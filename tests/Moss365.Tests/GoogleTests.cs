using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Moss365.Connections.Google;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>G1: signing in to Google (installed app, PKCE, loopback) and reading the Directory API, with a fake Google.</summary>
public sealed class GoogleTests
{
    /// <summary>Answers per URL; remembers what was asked (never a real request).</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body, string? Bearer)> Asked { get; } = new();
        public Func<HttpRequestMessage, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.NotFound, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Asked)
            {
                Asked.Add((request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.Parameter));
            }

            var (status, text) = Answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static string IdToken(string email, string? hd)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = hd is null ? $"{{\"email\":\"{email}\"}}" : $"{{\"email\":\"{email}\",\"hd\":\"{hd}\"}}";
        return $"{Part("{\"alg\":\"none\"}")}.{Part(payload)}.";
    }

    [Fact]
    public void PkceAndTheLinkFollowTheStandard()
    {
        // RFC 7636, appendix B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleSignIn.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var link = GoogleSignIn.AuthorizeUrl("client-1", "http://127.0.0.1:5000", "challenge", "state-1", "example.com");
        var query = GoogleSignIn.Query(link.PathAndQuery);
        Assert.Equal("accounts.google.com", link.Host);
        Assert.Equal(("S256", "challenge", "state-1", "example.com", "code"), (query["code_challenge_method"], query["code_challenge"], query["state"], query["hd"], query["response_type"]));
        Assert.Equal(string.Join(' ', GoogleSignIn.Scopes), query["scope"]);
        Assert.All(GoogleSignIn.Scopes.Where(s => s.Contains("admin.directory", StringComparison.Ordinal)), s => Assert.EndsWith(".readonly", s, StringComparison.Ordinal)); // G1 only reads

        Assert.Equal(("an@example.com", "example.com"), GoogleSignIn.Identity(IdToken("an@example.com", "example.com")));
        Assert.Equal((null, null), GoogleSignIn.Identity("not-a-token"));
        Assert.True(GoogleSignIn.BelongsToDomain("an@example.com", "example.com", "example.com"));
        Assert.True(GoogleSignIn.BelongsToDomain("an@school.example.com", null, "example.com"));
        Assert.False(GoogleSignIn.BelongsToDomain("an@example.net", "example.net", "example.com"));
        Assert.False(GoogleSignIn.BelongsToDomain("an@fabrikam.com", null, "kam.com")); // only the domain itself or a subdomain
    }

    [Fact]
    public async Task TheLoopbackAnswersTheBrowserAndIgnoresOtherRequests()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var waiting = GoogleSignIn.LoopbackAsync(listener, "Klaar", CancellationToken.None);
            Assert.Equal("404", (await Browser(port, "/favicon.ico")).Split(' ')[1]);
            var page = await Browser(port, "/?state=s%201&code=4%2F0abc");
            Assert.Contains("200 OK", page, StringComparison.Ordinal);
            Assert.Contains("Klaar", page, StringComparison.Ordinal);
            Assert.Equal(("4/0abc", "s 1", (string?)null), await waiting);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> Browser(int port, string path)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"));
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    /// <summary>The whole sign-in: the link in the bar, the browser comes back with the code, the code becomes a token.</summary>
    private static async Task<(GoogleSignIn SignIn, FakeGoogle Google, Exception? Error)> SignInAsync(string email, string? hd)
    {
        var google = new FakeGoogle
        {
            Answer = (r, body) => r.RequestUri!.Host == "oauth2.googleapis.com"
                ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { access_token = "token-1", expires_in = 3600, refresh_token = "refresh-1", id_token = IdToken(email, hd) }))
                : (HttpStatusCode.NotFound, "{}"),
        };
        var status = new SignInStatus();
        status.Changed += () =>
        {
            if (status.Link is not { } link)
            {
                return;
            }

            var query = GoogleSignIn.Query(link.PathAndQuery);
            var port = new Uri(query["redirect_uri"]).Port;
            _ = Browser(port, $"/?code=code-1&state={Uri.EscapeDataString(query["state"])}");
        };
        var signIn = new GoogleSignIn(new HttpClient(google), "client-1", "secret-1", "example.com", status, withoutWindow: false);
        try
        {
            await signIn.TokenAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            return (signIn, google, null);
        }
        catch (InvalidOperationException ex)
        {
            return (signIn, google, ex);
        }
    }

    [Fact]
    public async Task ASignInOfTheDomainGivesATokenAndOneOfAnotherDomainIsRefused()
    {
        var (signIn, google, error) = await SignInAsync("admin@example.com", "example.com");
        Assert.Null(error);
        Assert.Equal("admin@example.com", signIn.SignedInAs);
        var token = Assert.Single(google.Asked);
        Assert.Contains("grant_type=authorization_code", token.Body, StringComparison.Ordinal);
        Assert.Contains("code=code-1", token.Body, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", token.Body, StringComparison.Ordinal);
        Assert.Equal("token-1", await signIn.TokenAsync(CancellationToken.None)); // kept in memory: no second request
        Assert.Single(google.Asked);

        var (other, _, refused) = await SignInAsync("admin@example.net", "example.net");
        Assert.NotNull(refused);
        Assert.Null(other.SignedInAs);
    }

    [Fact]
    public async Task WithoutAWindowGoogleIsNeverSignedIn()
    {
        var signIn = new GoogleSignIn(new HttpClient(new FakeGoogle()), "client-1", "secret-1", "example.com", new SignInStatus(), withoutWindow: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.TokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UsersGroupsAndUnitsAreReadOverAllPages()
    {
        var google = new FakeGoogle
        {
            Answer = (r, _) =>
            {
                var url = r.RequestUri!.ToString();
                if (url.Contains("/users?", StringComparison.Ordinal))
                {
                    return url.Contains("pageToken=p2", StringComparison.Ordinal)
                        ? (HttpStatusCode.OK, """{"users":[{"id":"2","primaryEmail":"bo@example.com","name":{"fullName":"Bo"},"suspended":true,"lastLoginTime":"1970-01-01T00:00:00.000Z"}]}""")
                        : (HttpStatusCode.OK, """
                            {"users":[{"id":"1","primaryEmail":"an@example.com","aliases":["an.peeters@example.com"],
                              "name":{"fullName":"An Peeters","givenName":"An","familyName":"Peeters"},"orgUnitPath":"/Sales",
                              "isAdmin":true,"isEnrolledIn2Sv":true,"isEnforcedIn2Sv":false,"lastLoginTime":"2026-11-20T08:00:00.000Z",
                              "creationTime":"2024-01-01T00:00:00.000Z","externalIds":[{"type":"organization","value":"E-1001"}],
                              "organizations":[{"department":"Cultuur","primary":true}]}],"nextPageToken":"p2"}
                            """);
                }

                if (url.Contains("/orgunits", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK, """{"organizationUnits":[{"orgUnitPath":"/Sales","orgUnitId":"id:03ph8","name":"Sales","parentOrgUnitPath":"/"}]}""");
                }

                if (url.Contains("/resources/calendars", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK, """{"items":[{"resourceId":"r1","resourceEmail":"zaal-1@example.com","resourceName":"Zaal 1","resourceCategory":"CONFERENCE_ROOM","capacity":8,"floorName":"2"}]}""");
                }

                if (url.Contains("/roleassignments", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK, """
                        {"items":[{"roleId":"91","assignedTo":"1","assigneeType":"user","scopeType":"CUSTOMER"},
                                  {"roleId":"92","assignedTo":"2","assigneeType":"user","scopeType":"ORG_UNIT","orgUnitId":"id:03ph8"},
                                  {"roleId":"92","assignedTo":"g1","assigneeType":"group","scopeType":"CUSTOMER"}]}
                        """);
                }

                if (url.Contains("/roles?", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK, """{"items":[{"roleId":"91","roleName":"_SEED_ADMIN_ROLE","isSuperAdminRole":true},{"roleId":"92","roleName":"_HELP_DESK_ADMIN_ROLE"}]}""");
                }

                return url.Contains("/groups?", StringComparison.Ordinal)
                    ? (HttpStatusCode.OK, """{"groups":[{"id":"g1","email":"team@example.com","name":"Team","directMembersCount":"12","adminCreated":true}]}""")
                    : (HttpStatusCode.NotFound, "{}");
            },
        };
        var connection = new GoogleConnection(new HttpClient(google), _ => Task.FromResult("token-1"));
        var data = await connection.ReadAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "an@example.com", "bo@example.com" }, data.Users.Select(u => u.PrimaryEmail));
        var an = data.Users[0];
        Assert.Equal(("An Peeters", "/Sales", "E-1001", "Cultuur"), (an.Name, an.OrgUnitPath, an.EmployeeId, an.Department));
        Assert.True(an.IsAdmin && an.TwoStepEnrolled && !an.TwoStepEnforced);
        Assert.Equal(new[] { "an.peeters@example.com" }, an.Aliases);
        Assert.Equal(string.Empty, data.Users[1].LastLogin); // 1970 is "never"
        Assert.True(data.Users[1].Suspended);
        Assert.Equal(("team@example.com", 12), (data.Groups[0].Email, data.Groups[0].CountMembers));
        Assert.Equal("/Sales", Assert.Single(data.OrgUnits).Path);
        Assert.Equal(("Zaal 1", 8, "CONFERENCE_ROOM"), (data.Resources![0].Name, data.Resources[0].Capacity, data.Resources[0].Category));
        Assert.Equal(2, data.Roles!.Count); // the role of a group is not one person
        Assert.True(data.Roles.Single(r => r.UserId == "1").SuperAdmin);
        Assert.Equal("/Sales", data.Roles.Single(r => r.UserId == "2").Scope);
        Assert.All(google.Asked, a => Assert.Equal("token-1", a.Bearer));
        Assert.All(google.Asked, a => Assert.Equal(HttpMethod.Get, a.Method)); // only reading

        // Stored and read back, and gone for a tenant without Google
        var d = LiveLoader.WithGoogle(InventoryLoader.Build(TestData.Files()), data);
        var back = InventoryLoader.Build(Snapshots.Tables(InventoryWriter.Files(d, InventoryComponent.Google)));
        Assert.Equal(JsonSerializer.Serialize(d.GoogleUsers), JsonSerializer.Serialize(back.GoogleUsers));
        Assert.Equal(d.GoogleGroups.Select(g => (g.Id, g.Email, g.CountMembers)), back.GoogleGroups.Select(g => (g.Id, g.Email, g.CountMembers)));
        Assert.Equal(d.GoogleOrgUnits, back.GoogleOrgUnits);
        Assert.Equal(d.GoogleResources, back.GoogleResources);
        Assert.Equal(d.GoogleRoles, back.GoogleRoles);
        Assert.Empty(d.WithoutGoogle().GoogleGroups);
        Assert.Equal(Platform.GoogleWorkspace, Platforms.OfTable(InventoryLoader.GoogleUsersFile));
    }

    [Fact]
    public async Task AMissingRightForResourcesOrRolesLeavesTheRest()
    {
        var google = new FakeGoogle
        {
            Answer = (r, _) => r.RequestUri!.ToString() switch
            {
                var u when u.Contains("/users?", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"users":[{"id":"1","primaryEmail":"an@example.com"}]}"""),
                var u when u.Contains("/orgunits", StringComparison.Ordinal) => (HttpStatusCode.OK, "{}"),
                var u when u.Contains("/groups?", StringComparison.Ordinal) => (HttpStatusCode.OK, "{}"),
                _ => (HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Request had insufficient authentication scopes."}}"""),
            },
        };
        var notes = new List<string>();
        var data = await new GoogleConnection(new HttpClient(google), _ => Task.FromResult("token-1")).ReadAsync(new Progress(notes), CancellationToken.None);
        Assert.Single(data.Users);
        Assert.Null(data.Resources);
        Assert.Null(data.Roles);
        Assert.Contains(notes, n => n.Contains("insufficient", StringComparison.Ordinal));

        // Not read = what there was stays
        var before = InventoryLoader.Build(TestData.Files()) with { GoogleResources = new[] { new GoogleResource { Id = "r1", Name = "Zaal" } } };
        Assert.Single(LiveLoader.WithGoogle(before, data).GoogleResources);
    }

    private sealed class Progress(List<string> notes) : IProgress<string>
    {
        public void Report(string value) => notes.Add(value);
    }

    [Fact]
    public async Task AnErrorOfGoogleSaysWhatToCheck()
    {
        var google = new FakeGoogle { Answer = (_, _) => (HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"Not Authorized to access this resource/api"}}""") };
        var connection = new GoogleConnection(new HttpClient(google), _ => Task.FromResult("token-1"));
        var error = await Assert.ThrowsAsync<GoogleError>(() => connection.ReadAsync(null, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.Contains("Not Authorized", error.Message, StringComparison.Ordinal);
        Assert.Contains("Admin SDK", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", error.Message, StringComparison.Ordinal);
    }
}
