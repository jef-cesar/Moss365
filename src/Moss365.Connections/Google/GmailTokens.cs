using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moss365.Core.Google;
using Moss365.Core.Localization;
using Moss365.Core.Setup;

namespace Moss365.Connections.Google;

/// <summary>
/// Tokens of the service account for the part Gmail settings (G2, U34): the OAuth 2.0 flow for server to server with a
/// JWT signed with the private key (RS256), on behalf of one user of the domain (domain-wide delegation, the claim
/// <c>sub</c>), only for the Gmail scopes. A token lives in memory for at most an hour, per user; nothing is written to a
/// log.
/// </summary>
internal sealed class GmailTokens(HttpClient http, GmailKey key, Func<DateTimeOffset>? clock = null)
{
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Until)> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);

    public string KeyId => key.KeyId;

    public async Task<string> TokenAsync(string user, CancellationToken cancel)
    {
        if (_tokens.TryGetValue(user, out var kept) && kept.Until > _now().AddMinutes(2))
        {
            return kept.Token;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, GmailKey.GoogleTokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = Assertion(user),
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var answer = await http.SendAsync(request, cancel);
        var text = await answer.Content.ReadAsStringAsync(cancel);
        using var json = JsonDocument.Parse(text.Trim().Length == 0 ? "{}" : text);
        var root = json.RootElement;
        if (!answer.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var token))
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            // unauthorized_client: the client id of the service account is not (yet) in the domain-wide delegation
            throw new InvalidOperationException(error == "unauthorized_client"
                ? Texts.T.Format("gmail.delegation-missing", key.ClientId)
                : Texts.T.Format("gmail.token-refused", error ?? ((int)answer.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var seconds = root.TryGetProperty("expires_in", out var s) && s.TryGetInt32(out var n) ? n : 3600;
        var value = token.GetString() ?? string.Empty;
        _tokens[user] = (value, _now().AddSeconds(seconds));
        return value;
    }

    /// <summary>The signed JWT: who the service account is, for whom, which scopes, for an hour.</summary>
    internal string Assertion(string user)
    {
        var now = _now().ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = key.KeyId }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail,
            ["sub"] = user,
            ["scope"] = string.Join(' ', GoogleParts.GmailScopes),
            ["aud"] = GmailKey.GoogleTokenUri,
            ["iat"] = now,
            ["exp"] = now + 3600,
        }));
        var unsigned = $"{header}.{claims}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
