using Moss365.Core.Localization;
using Moss365.Core.Changes;
using Moss365.Connections.Ad;
using Moss365.Connections.Exchange;
using Moss365.Connections.Graph;

namespace Moss365.Connections;

/// <summary>Creates the real connections. The Graph token is kept while the app is open (one sign-in per
/// session).</summary>
public sealed class ConnectionFactory : IConnectionFactory
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly object _lock = new();
    private readonly SignInStatus? _signIn;
    private MsalTokenSource? _token;
    private string? _tokenKey;

    /// <summary>Reading without a window (R18): never a sign-in link, only the kept sign-in.</summary>
    private readonly bool _withoutWindow;

    public ConnectionFactory(SignInStatus? signIn = null, bool withoutWindow = false)
    {
        _signIn = signIn;
        _withoutWindow = withoutWindow;
    }

    /// <summary>Why Exchange could not use the same sign-in, or null.</summary>
    public string? ExchangeMessage => _token?.ExchangeMessage;

    public ISystemCheck? Check { get; } = new Check.SystemCheck();

    /// <summary>The MX check of where the mail lives (G0): DNS of this PC.</summary>
    public Moss365.Core.Mail.IMxLookup? Dns { get; } = new Dns.MxLookup();

    private IUpdateCheck? _updates;

    public IUpdateCheck? Updates => _updates ??= new Updates.GitHubUpdates(_http);

    public IAdConnection? Ad(ConnectionSettings settings, string? password)
        => OperatingSystem.IsWindows() && settings.AdFilled ? new AdLdap(settings.AdServer, settings.AdUser, password, new NetlogonShare(settings.AdServer, settings.NetlogonPath)) : null;

    public Core.LogonScripts.ILogonScriptShare? LogonScripts(ConnectionSettings settings)
        => OperatingSystem.IsWindows() && settings.AdFilled ? new NetlogonShare(settings.AdServer, settings.NetlogonPath) : null;

    public IGraphConnection? Graph(ConnectionSettings settings)
    {
        if (!settings.GraphFilled)
        {
            return null;
        }

        var token = Token(settings);
        return new GraphConnection(new GraphClient(_http, token.TokenAsync), () => token.SignedInAs, () => token.AppRoles, token.TokenAsync, token.SilentAsync);
    }

    private Google.GoogleSignIn? _google;
    private string? _googleKey;

    /// <summary>Google Workspace (G1): one sign-in per session and per client of the tenant.</summary>
    public IGoogleConnection? Google(ConnectionSettings settings)
    {
        if (!settings.GoogleFilled)
        {
            return null;
        }

        var scopes = Core.Setup.GoogleParts.Scopes(settings.GoogleParts);
        var key = $"{settings.GoogleDomain.Trim()}|{settings.GoogleClientId.Trim()}|{string.Join(' ', scopes)}|{settings.RememberSignIn}";
        Google.GoogleSignIn signIn;
        lock (_lock)
        {
            if (_google is null || _googleKey != key)
            {
                // "Stay signed in" (as for Microsoft): the refresh token encrypted with the Windows account
                var cache = settings.RememberSignIn && settings.GoogleTokenCache is { } store && OperatingSystem.IsWindows() ? new Check.ProtectedTokenStore(store) : null;
                _google = new Google.GoogleSignIn(_http, settings.GoogleClientId, settings.GoogleClientSecret, settings.GoogleDomain, _signIn, _withoutWindow, scopes, cache);
                _googleKey = key;
            }

            signIn = _google;
        }

        return new Google.GoogleConnection(_http, signIn.TokenAsync, () => signIn.SignedInAs, settings.GoogleParts, settings.GoogleDomain);
    }

    public IExchangeConnection? Exchange(ConnectionSettings settings)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // With a Tenant and Client ID: the same sign-in as Graph (link in the app); otherwise Exchange signs in by
        // itself
        var token = settings.GraphFilled ? Token(settings) : null;
        return new ExchangeConnection(settings.ManagementUpn, new PowerShellProcess(), token: token is null ? null : token.ExchangeTokenAsync,
            signedInAs: token is null ? null : () => token.SignedInAs);
    }

    public Core.Scripts.IScriptExecutor? Scripts(ConnectionSettings settings, string? adPassword)
    {
        var token = settings.GraphFilled ? Token(settings) : null;
        return new Scripts.ScriptExecutor(
            token is null ? null : token.TokenAsync,
            token is null ? null : token.ExchangeTokenAsync,
            () => token?.SignedInAs ?? (settings.ManagementUpn.Length > 0 ? settings.ManagementUpn : null),
            settings.AdServer, settings.AdUser, adPassword);
    }

    public IReadOnlyList<IExecutor> Executors(ConnectionSettings settings, string? adPassword)
    {
        var output = new List<IExecutor>();
        if (Ad(settings, adPassword) is IExecutor ad)
        {
            output.Add(ad);
        }

        if (Graph(settings) is IExecutor graph)
        {
            output.Add(graph);
        }

        if (Exchange(settings) is IExecutor exchange)
        {
            output.Add(exchange);
        }

        return output;
    }

    private TenantSetupConnection? _setup;

    public Core.Setup.ITenantSetupConnection? Setup(string tenantId)
    {
        var id = tenantId.Trim();
        if (!Core.Setup.TenantLookup.IsTenantId(id))
        {
            return null;
        }

        lock (_lock)
        {
            // One sign-in per tenant while the app is open
            if (_setup is null || !string.Equals(_setup.TenantId, id, StringComparison.OrdinalIgnoreCase))
            {
                _setup = new TenantSetupConnection(_http, id, _signIn);
            }

            return _setup;
        }
    }

    public async Task<Core.Setup.TenantLookupResult> FindTenantAsync(string domainOrId, CancellationToken cancel)
    {
        if (Core.Setup.TenantLookup.Clean(domainOrId) is not { } clean)
        {
            return new(null, Texts.T["code.connection-factory.enter-a-domain-e-g"]);
        }

        try
        {
            using var answer = await _http.GetAsync(Core.Setup.TenantLookup.MetadataUrl(clean), cancel);
            var text = await answer.Content.ReadAsStringAsync(cancel);
            return Core.Setup.TenantLookup.TenantIdFrom(text) is { } id
                ? new(id, null)
                : new(null, answer.StatusCode == System.Net.HttpStatusCode.BadRequest ? Texts.T.Format("code.connection-factory.microsoft-knows-no-tenant-with", clean) : Texts.T.Format("code.connection-factory.no-tenant-found", (int)answer.StatusCode));
        }
        catch (HttpRequestException ex)
        {
            return new(null, Texts.T.Format("code.connection-factory.login-microsoftonline-com-not-reachable", ex.Message));
        }
    }

    public Core.Scripts.IScriptLibrarySource? ScriptLibrary(string? folder)
        => string.IsNullOrWhiteSpace(folder) ? new Scripts.GitHubScriptSource(_http) : new Scripts.FolderScriptSource(folder.Trim());

    public Core.Setup.LocalDomain? FindLocalDomain() => Connections.Check.LocalDomainFinder.Find();

    private MsalTokenSource Token(ConnectionSettings settings)
    {
        var key = $"{settings.TenantId.Trim()}|{settings.ClientId.Trim()}|{settings.ManagementUpn.Trim()}|{settings.RememberSignIn}";
        lock (_lock)
        {
            if (_token is null || _tokenKey != key)
            {
                var cache = settings.RememberSignIn && settings.TokenCache is { } store && OperatingSystem.IsWindows() ? new Check.ProtectedTokenStore(store) : null;
                _token = new MsalTokenSource(settings.TenantId, settings.ClientId, settings.ManagementUpn, _signIn, cache: cache, silentOnly: _withoutWindow);
                _tokenKey = key;
            }

            return _token;
        }
    }
}
