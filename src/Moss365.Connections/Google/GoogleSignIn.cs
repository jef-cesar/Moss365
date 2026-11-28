using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Localization;

namespace Moss365.Connections.Google;

/// <summary>
/// Signing in to Google with the OAuth client of the own project (G1, U35): the authorization code flow for installed
/// apps, with PKCE and a loopback address (127.0.0.1 on a free port). The app shows the link like the sign-in at
/// Microsoft (<see cref="SignInStatus"/>); the browser comes back to the loopback address with the code. Only an account
/// of the domain of the tenant is accepted. The tokens stay in memory for the session and are never logged.
/// </summary>
public sealed class GoogleSignIn
{
    /// <summary>The reading scopes (G1) that every sign-in asks; a part adds its own (U40).</summary>
    public static IReadOnlyList<string> Scopes => Core.Setup.GoogleParts.BaseScopes;

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _domain;
    private readonly SignInStatus? _status;
    private readonly bool _withoutWindow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlyList<string> _scopes;
    private readonly ITokenCacheStore? _cache;
    private string? _access;
    private DateTimeOffset _expires;
    private string? _refresh;

    /// <param name="scopes">What to ask for; null = the reading scopes.</param>
    /// <param name="cache">Where to keep the refresh token ("stay signed in", encrypted with the Windows account), or null.</param>
    public GoogleSignIn(HttpClient http, string clientId, string clientSecret, string domain, SignInStatus? status, bool withoutWindow,
        IReadOnlyList<string>? scopes = null, ITokenCacheStore? cache = null)
    {
        _http = http;
        _clientId = clientId.Trim();
        _clientSecret = clientSecret.Trim();
        _domain = domain.Trim().ToLowerInvariant();
        _status = status;
        _withoutWindow = withoutWindow;
        _scopes = scopes ?? Scopes;
        _cache = cache;
        Restore();
    }

    /// <summary>The kept sign-in, when it was for the same client, domain and scopes (another part on means signing in
    /// again, with the new consent).</summary>
    private void Restore()
    {
        if (_cache?.Load() is not { } bytes)
        {
            return;
        }

        try
        {
            var kept = JsonSerializer.Deserialize<Kept>(bytes);
            if (kept is not null && kept.Client == _clientId && kept.Domain == _domain && kept.Scopes == string.Join(' ', _scopes) && kept.Refresh.Length > 0)
            {
                _refresh = kept.Refresh;
                SignedInAs = kept.Email;
            }
        }
        catch (JsonException)
        {
            _cache.Clear();
        }
    }

    private void Keep()
    {
        if (_cache is not null && _refresh is not null)
        {
            _cache.Save(JsonSerializer.SerializeToUtf8Bytes(new Kept(_clientId, _domain, string.Join(' ', _scopes), _refresh, SignedInAs)));
        }
    }

    private sealed record Kept(string Client, string Domain, string Scopes, string Refresh, string? Email);

    /// <summary>The address that signed in, or null.</summary>
    public string? SignedInAs { get; private set; }

    /// <summary>A valid access token: the one in memory, a refreshed one, or a new sign-in (never without a window).</summary>
    public async Task<string> TokenAsync(CancellationToken cancel)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (_access is not null && _expires > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _access;
            }

            if (_refresh is not null && await RefreshAsync(cancel))
            {
                return _access!;
            }

            if (_withoutWindow)
            {
                throw new InvalidOperationException(Texts.T["google.sign-in-needed"]);
            }

            await InteractiveAsync(cancel);
            return _access!;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InteractiveAsync(CancellationToken cancel)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            waiting.CancelAfter(TimeSpan.FromMinutes(10));
            _status?.Show(AuthorizeUrl(_clientId, redirect, Challenge(verifier), state, _domain, _scopes), waiting);
            var answer = await LoopbackAsync(listener, Texts.T["google.signed-in.page"], waiting.Token);
            if (answer.Error is { } error)
            {
                throw new InvalidOperationException(Texts.T.Format("google.sign-in-refused", error));
            }

            if (answer.Code is null || !string.Equals(answer.State, state, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(Texts.T["google.sign-in-wrong-answer"]);
            }

            await TokensAsync(new Dictionary<string, string>
            {
                ["code"] = answer.Code,
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier,
            }, cancel);
        }
        finally
        {
            listener.Stop();
            _status?.Done();
        }
    }

    private async Task<bool> RefreshAsync(CancellationToken cancel)
    {
        try
        {
            await TokensAsync(new Dictionary<string, string>
            {
                ["refresh_token"] = _refresh!,
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["grant_type"] = "refresh_token",
            }, cancel);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Revoked or expired: sign in again, and forget what was kept
            _refresh = null;
            _cache?.Clear();
            return false;
        }
    }

    private async Task TokensAsync(Dictionary<string, string> form, CancellationToken cancel)
    {
        using var content = new FormUrlEncodedContent(form);
        using var answer = await _http.PostAsync(TokenEndpoint, content, cancel);
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancel));
        var root = json.RootElement;
        if (!answer.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
        {
            // Only the code and the description of Google, never the request
            var code = root.TryGetProperty("error", out var e) ? e.ToString() : ((int)answer.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new InvalidOperationException(Texts.T.Format("google.token-refused", description is null ? code : $"{code}: {description}"));
        }

        var who = root.TryGetProperty("id_token", out var id) ? Identity(id.GetString() ?? string.Empty) : (null, null);
        if (who.Email is not null && !BelongsToDomain(who.Email, who.Domain, _domain))
        {
            throw new InvalidOperationException(Texts.T.Format("google.other-domain", _domain));
        }

        _access = access.GetString();
        _expires = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var s) && s.TryGetInt32(out var seconds) ? seconds : 3600);
        if (root.TryGetProperty("refresh_token", out var refresh))
        {
            _refresh = refresh.GetString();
        }

        SignedInAs = who.Email ?? SignedInAs;
        Keep();
    }

    /// <summary>The link to sign in: the client, the loopback address, the scopes, PKCE (S256), the state, and the
    /// domain as a hint (checked again in the answer).</summary>
    internal static Uri AuthorizeUrl(string clientId, string redirect, string challenge, string state, string domain, IReadOnlyList<string>? scopes = null)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes ?? Scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            ["prompt"] = "select_account",
            ["hd"] = domain,
        };
        return new Uri(AuthEndpoint + "?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")));
    }

    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The address and the domain (hd) in the id token; only to show and to check the domain, the token itself
    /// comes straight from Google over TLS.</summary>
    internal static (string? Email, string? Domain) Identity(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return (null, null);
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            string? Text(string name) => json.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
            return (Text("email"), Text("hd"));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>An account of the domain of the tenant (or of a subdomain of it).</summary>
    internal static bool BelongsToDomain(string email, string? hd, string domain)
    {
        var own = hd ?? email[(email.IndexOf('@') + 1)..];
        return string.Equals(own, domain, StringComparison.OrdinalIgnoreCase) || own.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Waits for the browser at the loopback address and answers it with a short page. Other requests (such as the icon)
    /// get a 404 and the wait goes on.
    /// </summary>
    internal static async Task<(string? Code, string? State, string? Error)> LoopbackAsync(TcpListener listener, string page, CancellationToken cancel)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancel);
            await using var stream = client.GetStream();
            // The whole request first: closing with unread data makes Windows reset the connection, and the browser
            // would not see the page
            var line = await RequestLineAsync(stream, cancel);
            var target = line.Split(' ') is [_, var t, ..] ? t : "/";
            var query = Query(target);
            var done = query.ContainsKey("code") || query.ContainsKey("error");
            var body = done ? $"<!doctype html><meta charset=\"utf-8\"><title>Moss365</title><p>{WebUtility.HtmlEncode(page)}</p>" : string.Empty;
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(done ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, cancel);
            await stream.WriteAsync(bytes, cancel);
            await stream.FlushAsync(cancel);
            client.Client.Shutdown(SocketShutdown.Send);
            if (done)
            {
                return (query.GetValueOrDefault("code"), query.GetValueOrDefault("state"), query.GetValueOrDefault("error"));
            }
        }
    }

    /// <summary>The first line of the request, after reading the request up to the empty line that ends its headers
    /// (at most 16 KB; a browser sends no body with a GET).</summary>
    private static async Task<string> RequestLineAsync(Stream stream, CancellationToken cancel)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16384 && await stream.ReadAsync(one, cancel) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                break;
            }

            if (bytes.Count >= 2 && bytes[^2] == '\n' && bytes[^1] == '\n')
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(bytes.ToArray());
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        return end < 0 ? text : text[..end];
    }

    internal static Dictionary<string, string> Query(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = target.IndexOf('?');
        if (start < 0)
        {
            return result;
        }

        foreach (var pair in target[(start + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var key = WebUtility.UrlDecode(i < 0 ? pair : pair[..i]);
            result[key] = i < 0 ? string.Empty : WebUtility.UrlDecode(pair[(i + 1)..]);
        }

        return result;
    }
}
