using Moss365.Core.Wijzigingen;
using Microsoft.Identity.Client;

namespace Moss365.Connections.Graph;

/// <summary>
/// Haalt tokens voor de aangemelde beheerder (gedelegeerde rechten van de app-registratie "Moss365"): voor
/// Microsoft Graph en, met dezelfde aanmelding, voor Exchange Online. De app opent zelf geen browser: de aanmeldlink
/// verschijnt in de app (<see cref="AanmeldStatus"/>) en de beheerder plakt hem in een browser naar keuze op deze pc.
/// Daarna worden de tokens stil vernieuwd zolang de app open is.
/// </summary>
public sealed class MsalTokenBron
{
    private static readonly string[] GraphScopes = { "https://graph.microsoft.com/.default" };
    private static readonly string[] ExchangeScopes = { "https://outlook.office365.com/.default" };

    private const string Gelukt = "<html><head><meta charset=\"utf-8\"><title>Moss365</title></head><body style=\"font-family:Segoe UI,sans-serif;color:#2d2c38;padding:3rem\">"
                                  + "<h1 style=\"color:#88719e\">Aangemeld bij Moss365</h1><p>Je mag dit tabblad sluiten en teruggaan naar de app.</p></body></html>";

    private readonly IPublicClientApplication _app;
    private readonly string? _loginHint;
    private readonly AanmeldStatus? _status;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private IAccount? _account;

    public MsalTokenBron(string tenantId, string clientId, string? loginHint, AanmeldStatus? status = null)
    {
        _app = PublicClientApplicationBuilder.Create(clientId.Trim())
            .WithTenantId(tenantId.Trim())
            .WithRedirectUri("http://localhost")
            .Build();
        _loginHint = string.IsNullOrWhiteSpace(loginHint) ? null : loginHint.Trim();
        _status = status;
    }

    public string? AangemeldAls => _account?.Username;

    /// <summary>De app-rollen (claim "roles" in het ID-token) van de laatste aanmelding; null als nog niet aangemeld.</summary>
    public IReadOnlyList<string>? AppRollen { get; private set; }

    private string Onthoud(AuthenticationResult resultaat)
    {
        _account = resultaat.Account;
        if (resultaat.ClaimsPrincipal is { } principal)
        {
            AppRollen = principal.FindAll("roles").Select(c => c.Value).ToList();
        }

        return resultaat.AccessToken;
    }

    /// <summary>Melding als Exchange niet met dezelfde aanmelding kon (bv. recht Exchange.Manage ontbreekt).</summary>
    public string? ExchangeMelding { get; private set; }

    public Task<string> TokenAsync(CancellationToken annuleren) => TokenVoorAsync(GraphScopes, annuleren);

    /// <summary>Een geldige aanmelding zonder aanmeldlink (stil vernieuwd), of false als je opnieuw moet aanmelden.</summary>
    public async Task<bool> StilAsync(CancellationToken annuleren)
    {
        try
        {
            await TokenVoorAsync(GraphScopes, annuleren, stil: true);
            return true;
        }
        catch (Exception ex) when (ex is MsalException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Token voor Exchange Online met dezelfde aanmelding, of null als de app-registratie dat (nog) niet toelaat.
    /// Dan meldt het Exchange-proces zelf aan.
    /// </summary>
    public async Task<string?> ExchangeTokenAsync(CancellationToken annuleren)
    {
        // Eerst Graph: dat toont zo nodig de aanmeldlink. Exchange daarna enkel stil, met dezelfde aanmelding.
        await TokenAsync(annuleren);
        try
        {
            var token = await TokenVoorAsync(ExchangeScopes, annuleren, stil: true);
            ExchangeMelding = null;
            return token;
        }
        catch (MsalException ex)
        {
            ExchangeMelding = "Exchange kon niet met dezelfde aanmelding (" + ex.ErrorCode + "). Draai Registreer-App.ps1 opnieuw om het recht Exchange.Manage toe te voegen; tot dan meldt Exchange apart aan.";
            return null;
        }
    }

    private async Task<string> TokenVoorAsync(string[] scopes, CancellationToken annuleren, bool stil = false)
    {
        await _slot.WaitAsync(annuleren);
        try
        {
            if (_account is not null)
            {
                try
                {
                    return Onthoud(await _app.AcquireTokenSilent(scopes, _account).ExecuteAsync(annuleren));
                }
                catch (MsalUiRequiredException) when (!stil)
                {
                    // Opnieuw interactief aanmelden (bv. sessie verlopen)
                }
            }

            if (stil)
            {
                throw new InvalidOperationException("Nog niet aangemeld.");
            }

            using var lopend = CancellationTokenSource.CreateLinkedTokenSource(annuleren);
            lopend.CancelAfter(TimeSpan.FromMinutes(10));
            var opties = new SystemWebViewOptions
            {
                HtmlMessageSuccess = Gelukt,
                OpenBrowserAsync = uri =>
                {
                    if (_status is null)
                    {
                        return SystemWebViewOptions.OpenWithChromeEdgeBrowserAsync(uri);
                    }

                    _status.Toon(uri, lopend);
                    return Task.CompletedTask;
                },
            };
            var opbouw = _app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(opties)
                .WithPrompt(Prompt.SelectAccount);
            if (_loginHint is not null)
            {
                opbouw = opbouw.WithLoginHint(_loginHint);
            }

            try
            {
                return Onthoud(await opbouw.ExecuteAsync(lopend.Token));
            }
            catch (OperationCanceledException) when (!annuleren.IsCancellationRequested)
            {
                throw new InvalidOperationException("Aanmelden geannuleerd of niet binnen 10 minuten afgerond.");
            }
            finally
            {
                _status?.Klaar();
            }
        }
        finally
        {
            _slot.Release();
        }
    }
}
