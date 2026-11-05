using Moss365.Core.Changes;
using Microsoft.Identity.Client;

namespace Moss365.Connections.Graph;

/// <summary>
/// Gets tokens for the signed-in administrator (delegated permissions of the app registration "Moss365"): for Microsoft
/// Graph and, with the same sign-in, for Exchange Online. The app never opens a browser itself: the sign-in link
/// appears in the app (<see cref="SignInStatus"/>) and the administrator pastes it into a browser of choice on this PC.
/// After that the tokens are renewed silently while the app is open.
/// </summary>
public sealed class MsalTokenSource
{
    private static readonly string[] GraphScopes = { "https://graph.microsoft.com/.default" };
    private static readonly string[] ExchangeScopes = { "https://outlook.office365.com/.default" };

    private const string Succeeded = "<html><head><meta charset=\"utf-8\"><title>Moss365</title></head><body style=\"font-family:Segoe UI,sans-serif;color:#2d2c38;padding:3rem\">"
                                  + "<h1 style=\"color:#88719e\">Aangemeld bij Moss365</h1><p>Je mag dit tabblad sluiten en teruggaan naar de app.</p></body></html>";

    private readonly IPublicClientApplication _app;
    private readonly string? _loginHint;
    private readonly SignInStatus? _status;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IAccount? _account;

    public MsalTokenSource(string tenantId, string clientId, string? loginHint, SignInStatus? status = null)
    {
        _app = PublicClientApplicationBuilder.Create(clientId.Trim())
            .WithTenantId(tenantId.Trim())
            .WithRedirectUri("http://localhost")
            .Build();
        _loginHint = string.IsNullOrWhiteSpace(loginHint) ? null : loginHint.Trim();
        _status = status;
    }

    public string? SignedInAs => _account?.Username;

    /// <summary>The app roles (claim "roles" in the ID token) of the last sign-in; null when not signed in
    /// yet.</summary>
    public IReadOnlyList<string>? AppRoles { get; private set; }

    private string Remember(AuthenticationResult result)
    {
        _account = result.Account;
        if (result.ClaimsPrincipal is { } principal)
        {
            AppRoles = principal.FindAll("roles").Select(c => c.Value).ToList();
        }

        return result.AccessToken;
    }

    /// <summary>Message when Exchange could not use the same sign-in (e.g. the permission Exchange.Manage is
    /// missing).</summary>
    public string? ExchangeMessage { get; private set; }

    public Task<string> TokenAsync(CancellationToken cancel) => TokenForAsync(GraphScopes, cancel);

    /// <summary>A valid sign-in without a sign-in link (renewed silently), or false when you have to sign in
    /// again.</summary>
    public async Task<bool> SilentAsync(CancellationToken cancel)
    {
        try
        {
            await TokenForAsync(GraphScopes, cancel, silent: true);
            return true;
        }
        catch (Exception ex) when (ex is MsalException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Token for Exchange Online with the same sign-in, or null when the app registration does not allow that (yet).
    /// Then the Exchange process signs in by itself.
    /// </summary>
    public async Task<string?> ExchangeTokenAsync(CancellationToken cancel)
    {
        // Graph first: that shows the sign-in link if needed. Exchange after that only silently, with the same sign-in.
        await TokenAsync(cancel);
        try
        {
            var token = await TokenForAsync(ExchangeScopes, cancel, silent: true);
            ExchangeMessage = null;
            return token;
        }
        catch (MsalException ex)
        {
            ExchangeMessage = "Exchange kon niet met dezelfde aanmelding (" + ex.ErrorCode + "). Draai Register-App.ps1 opnieuw om het recht Exchange.Manage toe te voegen; tot dan meldt Exchange apart aan.";
            return null;
        }
    }

    private async Task<string> TokenForAsync(string[] scopes, CancellationToken cancel, bool silent = false)
    {
        await _lock.WaitAsync(cancel);
        try
        {
            if (_account is not null)
            {
                try
                {
                    return Remember(await _app.AcquireTokenSilent(scopes, _account).ExecuteAsync(cancel));
                }
                catch (MsalUiRequiredException) when (!silent)
                {
                    // Sign in interactively again (e.g. session expired)
                }
            }

            if (silent)
            {
                throw new InvalidOperationException("Nog niet aangemeld.");
            }

            using var running = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            running.CancelAfter(TimeSpan.FromMinutes(10));
            var options = new SystemWebViewOptions
            {
                HtmlMessageSuccess = Succeeded,
                OpenBrowserAsync = uri =>
                {
                    if (_status is null)
                    {
                        return SystemWebViewOptions.OpenWithChromeEdgeBrowserAsync(uri);
                    }

                    _status.Show(uri, running);
                    return Task.CompletedTask;
                },
            };
            var build = _app.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(options)
                .WithPrompt(Prompt.SelectAccount);
            if (_loginHint is not null)
            {
                build = build.WithLoginHint(_loginHint);
            }

            try
            {
                return Remember(await build.ExecuteAsync(running.Token));
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new InvalidOperationException("Aanmelden geannuleerd of niet binnen 10 minuten afgerond.");
            }
            finally
            {
                _status?.Done();
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
