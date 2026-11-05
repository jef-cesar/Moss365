using Moss365.Core.Wijzigingen;
using Moss365.Connections.Ad;
using Moss365.Connections.Exchange;
using Moss365.Connections.Graph;

namespace Moss365.Connections;

/// <summary>Maakt de echte verbindingen. Het Graph-token blijft bewaard zolang de app open is (één aanmelding per sessie).</summary>
public sealed class VerbindingsFabriek : IVerbindingsFabriek
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly object _slot = new();
    private readonly AanmeldStatus? _aanmelding;
    private MsalTokenBron? _token;
    private string? _tokenSleutel;

    public VerbindingsFabriek(AanmeldStatus? aanmelding = null) => _aanmelding = aanmelding;

    /// <summary>Waarom Exchange niet met dezelfde aanmelding kon, of null.</summary>
    public string? ExchangeMelding => _token?.ExchangeMelding;

    public ISysteemControle? Controle { get; } = new Controle.SysteemControle();

    private IUpdateControle? _updates;

    public IUpdateControle? Updates => _updates ??= new Updates.GitHubUpdates(_http);

    public IAdVerbinding? Ad(VerbindingInstellingen instellingen, string? wachtwoord)
        => OperatingSystem.IsWindows() && instellingen.AdIngevuld ? new AdLdap(instellingen.AdServer, instellingen.AdGebruiker, wachtwoord) : null;

    public IGraphVerbinding? Graph(VerbindingInstellingen instellingen)
    {
        if (!instellingen.GraphIngevuld)
        {
            return null;
        }

        var token = Token(instellingen);
        return new GraphVerbinding(new GraphKlant(_http, token.TokenAsync), () => token.AangemeldAls, () => token.AppRollen, token.TokenAsync, token.StilAsync);
    }

    public IExchangeVerbinding? Exchange(VerbindingInstellingen instellingen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // Met Tenant en Client ID: dezelfde aanmelding als Graph (link in de app); anders meldt Exchange zelf aan
        var token = instellingen.GraphIngevuld ? Token(instellingen) : null;
        return new ExchangeVerbinding(instellingen.BeheerUpn, new PowerShellProces(), token: token is null ? null : token.ExchangeTokenAsync,
            aangemeldAls: token is null ? null : () => token.AangemeldAls);
    }

    public Core.Scripts.IScriptUitvoerder? Scripts(VerbindingInstellingen instellingen, string? adWachtwoord)
    {
        var token = instellingen.GraphIngevuld ? Token(instellingen) : null;
        return new Scripts.ScriptUitvoerder(
            token is null ? null : token.TokenAsync,
            token is null ? null : token.ExchangeTokenAsync,
            () => token?.AangemeldAls ?? (instellingen.BeheerUpn.Length > 0 ? instellingen.BeheerUpn : null),
            instellingen.AdServer, instellingen.AdGebruiker, adWachtwoord);
    }

    public IReadOnlyList<IUitvoerder> Uitvoerders(VerbindingInstellingen instellingen, string? adWachtwoord)
    {
        var uit = new List<IUitvoerder>();
        if (Ad(instellingen, adWachtwoord) is IUitvoerder ad)
        {
            uit.Add(ad);
        }

        if (Graph(instellingen) is IUitvoerder graph)
        {
            uit.Add(graph);
        }

        if (Exchange(instellingen) is IUitvoerder exchange)
        {
            uit.Add(exchange);
        }

        return uit;
    }

    private MsalTokenBron Token(VerbindingInstellingen instellingen)
    {
        var sleutel = $"{instellingen.TenantId.Trim()}|{instellingen.ClientId.Trim()}|{instellingen.BeheerUpn.Trim()}";
        lock (_slot)
        {
            if (_token is null || _tokenSleutel != sleutel)
            {
                _token = new MsalTokenBron(instellingen.TenantId, instellingen.ClientId, instellingen.BeheerUpn, _aanmelding);
                _tokenSleutel = sleutel;
            }

            return _token;
        }
    }
}
