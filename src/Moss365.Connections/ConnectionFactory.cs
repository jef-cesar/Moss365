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

    public ConnectionFactory(SignInStatus? signIn = null) => _signIn = signIn;

    /// <summary>Why Exchange could not use the same sign-in, or null.</summary>
    public string? ExchangeMessage => _token?.ExchangeMessage;

    public ISystemCheck? Check { get; } = new Check.SystemCheck();

    private IUpdateCheck? _updates;

    public IUpdateCheck? Updates => _updates ??= new Updates.GitHubUpdates(_http);

    public IAdConnection? Ad(ConnectionSettings settings, string? password)
        => OperatingSystem.IsWindows() && settings.AdFilled ? new AdLdap(settings.AdServer, settings.AdUser, password) : null;

    public IGraphConnection? Graph(ConnectionSettings settings)
    {
        if (!settings.GraphFilled)
        {
            return null;
        }

        var token = Token(settings);
        return new GraphConnection(new GraphClient(_http, token.TokenAsync), () => token.SignedInAs, () => token.AppRoles, token.TokenAsync, token.SilentAsync);
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

    private MsalTokenSource Token(ConnectionSettings settings)
    {
        var key = $"{settings.TenantId.Trim()}|{settings.ClientId.Trim()}|{settings.ManagementUpn.Trim()}";
        lock (_lock)
        {
            if (_token is null || _tokenKey != key)
            {
                _token = new MsalTokenSource(settings.TenantId, settings.ClientId, settings.ManagementUpn, _signIn);
                _tokenKey = key;
            }

            return _token;
        }
    }
}
