using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Moss365.Connections.Google;
using Moss365.Core.Changes;
using Moss365.Core.Google;
using Moss365.Core.Inventory;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>G2, part Gmail settings (U34): the service account, its tokens and the plans for the Gmail settings.</summary>
public sealed class GmailTests
{
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Bearer)> Asked { get; } = new();
        public Func<HttpRequestMessage, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Asked.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), body, request.Headers.Authorization?.Parameter));
            var (status, text) = Answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly RSA Rsa = RSA.Create(2048);

    /// <summary>A key file as Google gives it, with a key made for this test.</summary>
    private static string KeyFile(string tokenUri = GmailKey.GoogleTokenUri, string type = "service_account") => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = type,
        ["project_id"] = "moss365-test",
        ["private_key_id"] = "kid-1",
        ["private_key"] = Rsa.ExportPkcs8PrivateKeyPem(),
        ["client_email"] = "moss365-gmail@example.com",
        ["client_id"] = "1234567890",
        ["token_uri"] = tokenUri,
    });

    private static readonly GoogleUser An = new() { Id = "g-an", PrimaryEmail = "an@example.com", Name = "An Peeters" };

    private static readonly string[] Domains = { "example.com" };

    private static GmailSettings Settings(bool forwarding = false) => new(
        new GmailForwarding(forwarding, forwarding ? "bo@example.com" : string.Empty, forwarding ? "archive" : string.Empty),
        new GmailVacation(false, string.Empty, string.Empty, false, null, null),
        new[] { "cas@example.com" },
        Array.Empty<GmailFilter>(),
        "<b>An</b>",
        new[] { "bo@example.com" });

    private static LogEntry Log(Change c) => new(DateTimeOffset.Now, "beheerder", "test", c.TargetSystem.ToString(), c.ChangeAction.ToString(), c.Target, c.TargetName,
        c.Description, c.Parameters, "Succeeded", string.Empty, Previous: c.Previous);

    [Fact]
    public void OnlyAKeyOfAServiceAccountForTheTokensOfGoogleIsAccepted()
    {
        var (key, error) = GmailKey.Parse(KeyFile());
        Assert.Null(error);
        Assert.Equal(("1234567890", "kid-1", "moss365-test"), (key!.ClientId, key.KeyId, key.ProjectId));
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(key.Info), StringComparison.Ordinal);
        Assert.NotNull(GmailKey.Parse(KeyFile(type: "authorized_user")).Error);
        Assert.NotNull(GmailKey.Parse(KeyFile(tokenUri: "https://token.fabrikam.com/token")).Error);
        Assert.NotNull(GmailKey.Parse("geen json").Error);
        Assert.NotNull(GmailKey.Parse("{\"type\":\"service_account\"}").Error);
    }

    [Fact]
    public async Task TheTokenIsAJwtSignedForOneUserWithOnlyTheGmailScopes()
    {
        var google = new FakeGoogle { Answer = (_, _) => (HttpStatusCode.OK, """{"access_token":"gmail-token-1","expires_in":3600}""") };
        var tokens = new GmailTokens(new HttpClient(google), GmailKey.Parse(KeyFile()).Key!);
        var jwt = tokens.Assertion("an@example.com").Split('.');
        static byte[] Bytes(string part) => Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '='));
        Assert.True(Rsa.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Bytes(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var header = JsonDocument.Parse(Bytes(jwt[0]));
        Assert.Equal(("RS256", "kid-1"), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("kid").GetString()));
        using var claims = JsonDocument.Parse(Bytes(jwt[1]));
        var c = claims.RootElement;
        Assert.Equal(("an@example.com", GmailKey.GoogleTokenUri, string.Join(' ', GoogleParts.GmailScopes)), (c.GetProperty("sub").GetString(), c.GetProperty("aud").GetString(), c.GetProperty("scope").GetString()));
        Assert.Equal(3600, c.GetProperty("exp").GetInt64() - c.GetProperty("iat").GetInt64());

        // One request per user per hour; the assertion only goes to Google
        Assert.Equal("gmail-token-1", await tokens.TokenAsync("an@example.com", CancellationToken.None));
        Assert.Equal("gmail-token-1", await tokens.TokenAsync("An@Example.com", CancellationToken.None));
        Assert.Equal(GmailKey.GoogleTokenUri, Assert.Single(google.Asked).Url);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", google.Asked[0].Body, StringComparison.Ordinal);

        // Without the domain-wide delegation Google says unauthorized_client: the message names the client id to add
        var refused = new GmailTokens(new HttpClient(new FakeGoogle { Answer = (_, _) => (HttpStatusCode.Unauthorized, """{"error":"unauthorized_client"}""") }), GmailKey.Parse(KeyFile()).Key!);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.TokenAsync("an@example.com", CancellationToken.None));
        Assert.Contains("1234567890", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GmailIsReadAndWrittenWithTheTokenOfTheServiceAccountForThatUser()
    {
        var google = new FakeGoogle
        {
            Answer = (r, _) => (r.Method.Method, r.RequestUri!.AbsolutePath) switch
            {
                ("GET", var p) when p.EndsWith("/autoForwarding", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"enabled":true,"emailAddress":"x@fabrikam.com","disposition":"archive"}"""),
                ("GET", var p) when p.EndsWith("/vacation", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"enableAutoReply":true,"responseSubject":"Afwezig","responseBodyPlainText":"Tot maandag","restrictToDomain":true,"startTime":"1767225600000"}"""),
                ("GET", var p) when p.EndsWith("/delegates", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"delegates":[{"delegateEmail":"cas@example.com"}]}"""),
                ("GET", var p) when p.EndsWith("/filters", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"filter":[{"id":"f1","criteria":{"from":"baas@example.com"},"action":{"forward":"x@fabrikam.com","removeLabelIds":["INBOX"]}}]}"""),
                ("GET", var p) when p.EndsWith("/forwardingAddresses", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"forwardingAddresses":[{"forwardingEmail":"x@fabrikam.com","verificationStatus":"accepted"}]}"""),
                ("GET", var p) when p.Contains("/forwardingAddresses/", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"forwardingEmail":"y@fabrikam.com","verificationStatus":"pending"}"""),
                ("GET", _) => (HttpStatusCode.OK, """{"signature":"<b>An</b>"}"""),
                ("POST", var p) when p.EndsWith("/forwardingAddresses", StringComparison.Ordinal) => (HttpStatusCode.Conflict, """{"error":{"code":409,"message":"exists"}}"""),
                _ => (HttpStatusCode.OK, "{}"),
            },
        };
        var asked = new List<string>();
        var connection = new GoogleConnection(new HttpClient(google), _ => Task.FromResult("admin-token"))
        {
            GmailToken = (user, _) => { asked.Add(user); return Task.FromResult("sa-token-" + user); },
        };
        var settings = await connection.ReadGmailAsync("an@example.com", CancellationToken.None);
        Assert.Equal(("x@fabrikam.com", "archive", "Tot maandag", true), (settings!.Forwarding.Address, settings.Forwarding.Disposition, settings.Vacation.Text, settings.Vacation.OnlyInternal));
        Assert.Equal(1767225600000, settings.Vacation.Start);
        Assert.Equal("cas@example.com", Assert.Single(settings.Delegates));
        var filter = Assert.Single(settings.Filters);
        Assert.True(filter.Forwards);
        Assert.Contains("baas@example.com", filter.Summary, StringComparison.Ordinal);
        Assert.All(google.Asked, a => Assert.Equal("sa-token-an@example.com", a.Bearer));

        // Forwarding to an address that did not confirm yet: refused with what to do, nothing turned on
        var forward = Step(ChangeAction.GmailSetForwarding, ("adres", "y@fabrikam.com"), ("disposition", "leaveInInbox"));
        var result = Assert.Single(await connection.ExecuteAsync(new[] { forward }, CancellationToken.None));
        Assert.Equal(ResultStatus.Failed, result.Status);
        Assert.Contains("y@fabrikam.com", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(google.Asked, a => a.Method == HttpMethod.Put);

        var results = await connection.ExecuteAsync(new[]
        {
            Step(ChangeAction.GmailSetForwarding, ("adres", ""), ("disposition", "leaveInInbox")),
            Step(ChangeAction.GmailSetVacation, ("aan", "True"), ("onderwerp", "Weg"), ("tekst", "Tot later"), ("alleenIntern", "False"), ("start", "1767225600000"), ("einde", "")),
            Step(ChangeAction.GmailRemoveFilter, ("id", "f1")),
            Step(ChangeAction.GmailSetSignature, ("handtekening", "<i>An</i>")),
        }, CancellationToken.None);
        Assert.All(results, r => Assert.Equal(ResultStatus.Succeeded, r.Status));
        Assert.Contains(google.Asked, a => a.Method == HttpMethod.Put && a.Url.EndsWith("/autoForwarding", StringComparison.Ordinal) && a.Body == "{\"enabled\":false}");
        var vacation = google.Asked.Single(a => a.Method == HttpMethod.Put && a.Url.EndsWith("/vacation", StringComparison.Ordinal));
        Assert.Contains("\"startTime\":\"1767225600000\"", vacation.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("endTime", vacation.Body, StringComparison.Ordinal);
        Assert.Contains(google.Asked, a => a.Method == HttpMethod.Delete && a.Url.EndsWith("/filters/f1", StringComparison.Ordinal));
        Assert.Contains(google.Asked, a => a.Method == HttpMethod.Patch && a.Url.EndsWith("/sendAs/an@example.com", StringComparison.Ordinal));
        Assert.All(asked, u => Assert.Equal("an@example.com", u));

        // Without the service account nothing is read
        Assert.Null(await new GoogleConnection(new HttpClient(google), _ => Task.FromResult("admin-token")).ReadGmailAsync("an@example.com", CancellationToken.None));
    }

    private static Change Step(ChangeAction action, params (string, string)[] parameters) => new()
    {
        TargetSystem = TargetSystem.Google, ChangeAction = action, Target = "an@example.com", Description = "x",
        Parameters = parameters.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void EveryGmailSettingIsAPlanWithWhatWasBefore()
    {
        var on = Assert.Single(GmailPlans.Forwarding(An, Settings(), "bo@example.com", "archive", Domains).Plan);
        Assert.Equal(("bo@example.com", "archive", "", "leaveInInbox"), (on.P("adres"), on.P("disposition"), on.Previous!["adres"], on.Previous["disposition"]));
        Assert.Null(on.Warning);
        Assert.NotNull(GmailPlans.Forwarding(An, Settings(), "x@fabrikam.com", "leaveInInbox", Domains).Plan[0].Warning);
        Assert.Single(GmailPlans.Forwarding(An, Settings(), "an@example.com", "leaveInInbox", Domains).Errors);
        Assert.Empty(GmailPlans.Forwarding(An, Settings(true), "bo@example.com", "archive", Domains).Plan);
        var off = Assert.Single(GmailPlans.Forwarding(An, Settings(true), "", "archive", Domains).Plan);
        var back = Assert.Single(Undo.Plan(new[] { Log(off) }).Plan);
        Assert.Equal(("bo@example.com", "archive"), (back.P("adres"), back.P("disposition")));

        var vacation = Assert.Single(GmailPlans.Vacation(An, Settings(), new GmailVacationDesired(true, "Afwezig", "Tot maandag", true, new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31))).Plan);
        Assert.Equal(("True", "False"), (vacation.P("aan"), vacation.Previous!["aan"]));
        Assert.True(long.Parse(vacation.P("einde"), System.Globalization.CultureInfo.InvariantCulture) > long.Parse(vacation.P("start"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Single(GmailPlans.Vacation(An, Settings(), new GmailVacationDesired(true, "", "", false, null, null)).Errors);
        Assert.Single(GmailPlans.Vacation(An, Settings(), new GmailVacationDesired(true, "x", "", false, new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 1))).Errors);
        Assert.Empty(GmailPlans.Vacation(An, Settings(), new GmailVacationDesired(false, "", "", false, null, null)).Plan);

        Assert.Single(GmailPlans.AddDelegate(An, Settings(), "x@fabrikam.com", Domains).Errors);
        Assert.Single(GmailPlans.AddDelegate(An, Settings(), "cas@example.com", Domains).Errors);
        var delegateStep = Assert.Single(GmailPlans.AddDelegate(An, Settings(), "bo@example.com", Domains).Plan);
        Assert.Equal(ChangeAction.GmailRemoveDelegate, Assert.Single(Undo.Plan(new[] { Log(delegateStep) }).Plan).ChangeAction);

        var signature = Assert.Single(GmailPlans.Signature(An, Settings(), "<i>An</i>").Plan);
        Assert.Equal("<b>An</b>", signature.Previous!["handtekening"]);
        Assert.Empty(GmailPlans.Signature(An, Settings(), " <b>An</b> ").Plan);

        using var doc = JsonDocument.Parse("""{"criteria":{"from":"baas@example.com"},"action":{"forward":"x@fabrikam.com"}}""");
        var filter = GmailPlans.Filter("f1", doc.RootElement.GetProperty("criteria"), doc.RootElement.GetProperty("action"));
        var remove = GmailPlans.RemoveFilter(An, filter);
        Assert.NotNull(remove.Warning);
        var again = Assert.Single(Undo.Plan(new[] { Log(remove) }).Plan);
        Assert.Equal(ChangeAction.GmailAddFilter, again.ChangeAction);
        Assert.Contains("x@fabrikam.com", again.P("json"), StringComparison.Ordinal);

        // The engine refuses every Gmail step with the part off
        Assert.NotNull(GoogleParts.Refusal(on, new[] { GoogleParts.Licences }));
        Assert.Null(GoogleParts.Refusal(on, new[] { GoogleParts.GmailSettings }));
        Assert.NotNull(GmailPlans.PartOff(Array.Empty<string>()));
    }
}
