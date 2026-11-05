using System.Net;
using System.Text;
using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Connections.Ad;
using Moss365.Connections.Exchange;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

public class AdLdapMapperTests
{
    private static Dictionary<string, object?[]> K(params (string Name, object?[] Values)[] k)
        => k.ToDictionary(x => x.Name, x => x.Values, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void User()
    {
        var signIn = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var g = AdLdapMapper.ToUser(K(
            ("sAMAccountName", new object?[] { "an.peeters" }),
            ("distinguishedName", new object?[] { "CN=An Peeters,OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=test,DC=internal" }),
            ("userAccountControl", new object?[] { 0x200 | 0x10000 }),
            ("lastLogonTimestamp", new object?[] { signIn.ToFileTimeUtc() }),
            ("physicalDeliveryOfficeName", new object?[] { "gemeentehuis" }),
            ("l", new object?[] { "intranet" }),
            ("memberOf", new object?[] { "CN=a,DC=x", "CN=b,DC=x" })));

        Assert.True(g.Enabled);
        Assert.True(g.PasswordNeverExpires);
        Assert.Equal("GEMEENTEBESTUUR/GEBRUIKERS", g.OuPath);
        Assert.Equal("gemeentehuis", g.Office);
        Assert.Equal("intranet", g.City);
        Assert.Equal(signIn.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), g.LastLogonDate);
        Assert.Equal(2, g.MemberOr.Count);
        Assert.Equal(string.Empty, g.Department);
    }

    [Fact]
    public void DisabledAndNeverSignedIn()
    {
        var g = AdLdapMapper.ToUser(K(("userAccountControl", new object?[] { 0x202 }), ("lastLogonTimestamp", new object?[] { 0L })));
        Assert.False(g.Enabled);
        Assert.Equal(string.Empty, g.LastLogonDate);
    }

    [Theory]
    [InlineData(unchecked((int)0x80000002), "Security", "Global")]
    [InlineData(unchecked((int)0x80000004), "Security", "DomainLocal")]
    [InlineData(0x8, "Distribution", "Universal")]
    public void GroupKind(int groupType, string category, string scope)
    {
        var g = AdLdapMapper.ToGroup(K(("groupType", new object?[] { groupType }), ("member", new object?[] { "a", "b", "c" })));
        Assert.Equal(category, g.Category);
        Assert.Equal(scope, g.Scope);
        Assert.Equal(3, g.CountMembers);
    }

    [Fact]
    public void PathAndDomain()
    {
        Assert.Equal("DC=CONTOSO,DC=LOCAL", AdLdapMapper.DomainToDn("CONTOSO.LOCAL"));
        Assert.Equal("LDAP://10.0.0.1/CN=a\\/b,DC=x", AdLdapMapper.LdapPath("10.0.0.1", "CN=a/b,DC=x"));
        Assert.Equal("LDAP://DC=x", AdLdapMapper.LdapPath("", "DC=x"));
    }
}

/// <summary>Fake Graph: answers per method and path, and a list of all requests.</summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string FilePath, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _rules = new();

    public List<(HttpMethod Method, string Url, string? Content)> Requests { get; } = new();

    public FakeGraph Op(HttpMethod method, string pathBegin, object answer, HttpStatusCode status = HttpStatusCode.OK)
    {
        _rules.Add((method, pathBegin, _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(answer as string ?? JsonSerializer.Serialize(answer), Encoding.UTF8, "application/json"),
        }));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add((request.Method, url, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancel)));
        var path = url.Replace(GraphClient.Base, string.Empty);
        var rule = _rules.LastOrDefault(r => r.Method == request.Method && path.StartsWith(r.FilePath, StringComparison.Ordinal));
        return rule.Answer?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"niet gevonden: " + path + "\"}}"),
        };
    }
}

public class GraphConnectionTests
{
    private static GraphConnection Make(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

    private static object Items(params object[] items) => new { value = items };

    [Fact]
    public async Task ReadsUsersGroupsMembersAndLicencesWithPaging()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/subscribedSkus", Items(new { skuId = "s1", skuPartNumber = "SPB", consumedUnits = 163, prepaidUnits = new { enabled = 163 } }))
            .Op(HttpMethod.Get, "/users?", new Dictionary<string, object>
            {
                ["value"] = new object[]
                {
                    new
                    {
                        id = "u1", userPrincipalName = "an@test.be", displayName = "An", accountEnabled = true, userType = "Member",
                        onPremisesSyncEnabled = true, onPremisesSamAccountName = "an.peeters", city = "intranet",
                        assignedLicenses = new[] { new { skuId = "s1" } },
                        signInActivity = new { lastSignInDateTime = "2026-10-01T08:00:00Z", lastNonInteractiveSignInDateTime = "2026-10-10T08:00:00Z" },
                    },
                },
                ["@odata.nextLink"] = GraphClient.Base + "/users-pagina2",
            })
            .Op(HttpMethod.Get, "/users-pagina2", Items(new { id = "u2", userPrincipalName = "gast#EXT#@test.onmicrosoft.com", userType = "Guest" }))
            .Op(HttpMethod.Get, "/groups?", Items(
                new { id = "g1", displayName = "SG-SM-info-Full", mailEnabled = true, securityEnabled = true, groupTypes = Array.Empty<string>(), mail = "sg@test.be" },
                new { id = "g2", displayName = "Alle gebruikers", mailEnabled = false, securityEnabled = true, groupTypes = new[] { "DynamicMembership" }, membershipRule = "All Users" }))
            .Op(HttpMethod.Get, "/groups/g1/members", Items(new { id = "u1" }))
            .Op(HttpMethod.Get, "/groups/g2/members", Items(new { id = "u1" }, new { id = "u2" }));

        var d = await Make(fake).ReadAsync(null, CancellationToken.None);

        Assert.Equal(2, d.Users.Count);
        var an = d.Users[0];
        Assert.Equal(new[] { "SPB" }, an.Licences);
        Assert.Equal("intranet", an.City);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), an.LastSignIn);
        Assert.True(d.Users[1].IsGuest);

        Assert.Equal(Moss365.Core.Inventory.CloudGroupType.MailSecurity, d.Groups[0].Type);
        Assert.Equal("sg@test.be", d.Groups[0].Mail);
        Assert.True(d.Groups[1].Dynamic);
        Assert.Equal(2, d.Groups[1].CountMembers);
        Assert.Equal(new[] { "g1", "g2" }, d.Membership["u1"].OrderBy(x => x));
        Assert.Equal(163, Assert.Single(d.Licences).Total);
        Assert.All(fake.Requests, v => Assert.Equal(HttpMethod.Get, v.Method));
    }

    [Fact]
    public async Task WithoutSignInDataContinueWithoutSignInActivity()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/subscribedSkus", Items())
            .Op(HttpMethod.Get, "/users?", Items(new { id = "u1", userPrincipalName = "an@test.be" }))
            .Op(HttpMethod.Get, "/users?$select=id,userPrincipalName,displayName,givenName,surname,mail,mailNickname,accountEnabled,userType,department,jobTitle,officeLocation,companyName,city,usageLocation,createdDateTime,onPremisesSyncEnabled,onPremisesSamAccountName,onPremisesDistinguishedName,assignedLicenses,signInActivity",
                "{\"error\":{\"code\":\"Authentication_RequestFromNonPremiumTenantOrB2CTenant\",\"message\":\"geen P1\"}}", HttpStatusCode.Forbidden)
            .Op(HttpMethod.Get, "/groups?", Items());

        var d = await Make(fake).ReadAsync(null, CancellationToken.None);
        Assert.Single(d.Users);
        Assert.Equal(2, fake.Requests.Count(v => v.Url.Contains("/users?")));
    }

    [Fact]
    public async Task AddGroupMemberWithUpnLookup()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/users/eva%40test.be", new { id = "u-eva" })
            .Op(HttpMethod.Post, "/groups/g1/members/$ref", "", HttpStatusCode.NoContent);
        var w = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphAddGroupMember, Target = "eva@test.be", Description = "x" };
        w.Parameters["groepId"] = "g1";

        var r = Assert.Single(await Make(fake).ExecuteAsync(new[] { w }, CancellationToken.None));

        Assert.Equal(ResultStatus.Succeeded, r.Status);
        var post = fake.Requests.Single(v => v.Method == HttpMethod.Post);
        Assert.EndsWith("/groups/g1/members/$ref", post.Url);
        Assert.Contains("directoryObjects/u-eva", post.Content);
    }

    [Fact]
    public async Task AlreadyMemberIsNoError()
    {
        var fake = new FakeGraph().Op(HttpMethod.Post, "/groups/g1/members/$ref",
            "{\"error\":{\"code\":\"Request_BadRequest\",\"message\":\"One or more added object references already exist for the following modified properties: 'members'.\"}}",
            HttpStatusCode.BadRequest);
        var w = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphAddGroupMember, Target = "u1", Description = "x" };
        w.Parameters["groepId"] = "g1";
        var r = Assert.Single(await Make(fake).ExecuteAsync(new[] { w }, CancellationToken.None));
        Assert.Equal(ResultStatus.Succeeded, r.Status);
        Assert.Equal("Was al lid.", r.Message);
    }

    [Fact]
    public async Task LicenceByNameAndUnknownUser()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/subscribedSkus", Items(new { skuId = "s-f3", skuPartNumber = "DESKLESSPACK" }))
            .Op(HttpMethod.Post, "/users/u1/assignLicense", "{}");
        var lic = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphAssignLicence, Target = "u1", Description = "x" };
        lic.Parameters["sku"] = "DESKLESSPACK";
        var unknown = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphRevokeSessions, Target = "weg@test.be", Description = "x" };

        var r = await Make(fake).ExecuteAsync(new[] { lic, unknown }, CancellationToken.None);

        Assert.Equal(ResultStatus.Succeeded, r[0].Status);
        Assert.Contains("\"skuId\":\"s-f3\"", fake.Requests.Single(v => v.Method == HttpMethod.Post).Content);
        Assert.Equal(ResultStatus.Failed, r[1].Status);
        Assert.Contains("bestaat (nog) niet", r[1].Message);
    }

    [Fact]
    public void AttributesAreTypedCorrectly()
    {
        var content = GraphConnection.AttributesContent(new Dictionary<string, string> { ["accountEnabled"] = "false", ["usageLocation"] = "BE", ["jobTitle"] = "" });
        Assert.Equal(false, content["accountEnabled"]);
        Assert.Equal("BE", content["usageLocation"]);
        Assert.Null(content["jobTitle"]);
    }
}

public class ExchangeConnectionTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-exo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>Pretends PowerShell runs: reads the commands and writes results via a function.</summary>
    private sealed class FakePowerShell : IPowerShellStarter
    {
        private readonly Func<JsonElement, object> _answer;

        public FakePowerShell(Func<JsonElement, object> answer) => _answer = answer;

        public List<IReadOnlyList<string>> Calls { get; } = new();
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = new();
        public string? ScriptContent { get; private set; }

        public Task<(int Code, string Errors)> StartAsync(string script, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel)
        {
            Calls.Add(arguments);
            Environments.Add(environment ?? new Dictionary<string, string>());
            ScriptContent = File.ReadAllText(script);
            var input = arguments[arguments.ToList().IndexOf("-Opdrachten") + 1];
            var output = arguments[arguments.ToList().IndexOf("-Uitvoer") + 1];
            using var doc = JsonDocument.Parse(File.ReadAllText(input));
            var results = doc.RootElement.EnumerateArray().Select(_answer).ToList();
            File.WriteAllText(output, JsonSerializer.Serialize(results));
            return Task.FromResult((0, string.Empty));
        }
    }

    [Fact]
    public void ChangesBecomeCommands()
    {
        var member = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeAddGroupMember, Target = "eva@test.be", Description = "x" };
        member.Parameters["groep"] = "sg@test.be";
        var o = ExchangeConnection.ToCommand(member);
        Assert.Equal("LidToevoegen", o.Op);
        Assert.Equal("sg@test.be", o.Params["Identity"]);
        Assert.Equal("eva@test.be", o.Params["Member"]);

        var room = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeRoomCalendarProcessing, Target = "rm@test.be", Description = "x" };
        room.Parameters["BookingWindowInDays"] = "180";
        var r = ExchangeConnection.ToCommand(room);
        Assert.Equal("Agendaverwerking", r.Op);
        Assert.Equal("rm@test.be", r.Params["Identity"]);
        Assert.Equal("180", r.Params["BookingWindowInDays"]);

        Assert.Throws<InvalidOperationException>(() => ExchangeConnection.ToCommand(
            new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.AdDisableAccount, Target = "x", Description = "x" }));
    }

    [Fact]
    public async Task RunsInAProcessAndReadsTheResults()
    {
        var fake = new FakePowerShell(o => new
        {
            id = o.GetProperty("id").GetString(),
            status = o.GetProperty("params").GetProperty("Member").GetString() == "fout@test.be" ? "Mislukt" : "Gelukt",
            melding = "",
            data = (object?)null,
        });
        var connection = new ExchangeConnection("beheer@test.be", fake, _folder);
        var w1 = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeAddGroupMember, Target = "eva@test.be", Description = "x" };
        var w2 = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeAddGroupMember, Target = "fout@test.be", Description = "x" };

        var r = await connection.ExecuteAsync(new[] { w1, w2 }, CancellationToken.None);

        Assert.Single(fake.Calls);
        Assert.Contains("beheer@test.be", fake.Calls[0]);
        Assert.Contains("$Toegestaan", fake.ScriptContent);
        Assert.Equal(ResultStatus.Succeeded, r[0].Status);
        Assert.Equal(ResultStatus.Failed, r[1].Status);
        Assert.Empty(Directory.GetFiles(_folder, "*.json"));
    }

    [Fact]
    public async Task TokenGoesViaTheEnvironmentNotTheCommandLine()
    {
        var fake = new FakePowerShell(o => new { id = o.GetProperty("id").GetString(), status = "Gelukt", melding = "", data = (object?)null });
        var connection = new ExchangeConnection(null, fake, _folder, token: _ => Task.FromResult<string?>("geheim-token"), signedInAs: () => "beheer@test.be");
        var w = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeAddGroupMember, Target = "eva@test.be", Description = "x" };

        await connection.ExecuteAsync(new[] { w }, CancellationToken.None);

        Assert.Equal("geheim-token", fake.Environments[0][ExchangeConnection.TokenVariable]);
        Assert.DoesNotContain(fake.Calls[0], a => a.Contains("geheim-token"));
        Assert.Contains("beheer@test.be", fake.Calls[0]);
        Assert.Empty(Directory.GetFiles(_folder, "*.json"));

        // Without a token: no environment variable
        var without = new FakePowerShell(o => new { id = o.GetProperty("id").GetString(), status = "Gelukt", melding = "", data = (object?)null });
        await new ExchangeConnection(null, without, _folder, token: _ => Task.FromResult<string?>(null)).ExecuteAsync(new[] { w }, CancellationToken.None);
        Assert.False(without.Environments[0].ContainsKey(ExchangeConnection.TokenVariable));
    }

    [Fact]
    public void NewChangesBecomeCommands()
    {
        Change W(ChangeAction a, string target, params (string, string)[] p)
        {
            var w = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = a, Target = target, Description = "x" };
            foreach (var (k, v) in p) w.Parameters[k] = v;
            return w;
        }

        var permission = ExchangeConnection.ToCommand(W(ChangeAction.ExchangeRemoveMailboxPermission, "info@test.be", ("Recht", "SendAs"), ("Trustee", "bo@test.be")));
        Assert.Equal(("RechtVerwijderen", "info@test.be", "SendAs", "bo@test.be"), (permission.Op, permission.Params["Identity"], permission.Params["Recht"], permission.Params["Trustee"]));

        var configure = ExchangeConnection.ToCommand(W(ChangeAction.ExchangeUpdateMailbox, "info@test.be", ("ForwardingSmtpAddress", ""), ("onbekend", "x")));
        Assert.Equal("PostvakInstellen", configure.Op);
        Assert.Equal("", configure.Params["ForwardingSmtpAddress"]);
        Assert.False(configure.Params.ContainsKey("onbekend"));

        var newValue = ExchangeConnection.ToCommand(W(ChangeAction.ExchangeCreateSharedMailbox, "jeugd@test.be", ("Name", "Jeugd"), ("DisplayName", "Jeugd"), ("Alias", "jeugd")));
        Assert.Equal(("PostvakAanmaken", "jeugd@test.be"), (newValue.Op, newValue.Params["PrimarySmtpAddress"]));

        Assert.Equal("GroepAanmaken", ExchangeConnection.ToCommand(W(ChangeAction.ExchangeCreateGroup, "sg@test.be", ("Name", "SG"))).Op);
        Assert.Equal("Beheerder", ExchangeConnection.ToCommand(W(ChangeAction.ExchangeGroupManager, "sg@test.be", ("Toevoegen", "an@test.be"))).Op);
        Assert.Equal("GroepInstellen", ExchangeConnection.ToCommand(W(ChangeAction.ExchangeUpdateGroup, "sg@test.be", ("DisplayName", "SG"))).Op);
        Assert.Equal("AutoAntwoord", ExchangeConnection.ToCommand(W(ChangeAction.ExchangeAutoReply, "info@test.be", ("AutoReplyState", "Disabled"))).Op);
    }

    [Fact]
    public async Task ReadMailboxes()
    {
        var fake = new FakePowerShell(o => new
        {
            id = "postvakken",
            status = "Gelukt",
            melding = "",
            data = new
            {
                Postvakken = new[] { new { Type = "SharedMailbox", DisplayName = "Info", Smtp = "info@test.be", Hidden = "True", DoorsturenNaar = "x@fabrikam.com", DoorsturenEnBewaren = "True", AutoAntwoord = "Disabled" } },
                Rechten = new[] { new { Postvak = "info@test.be", Recht = "FullAccess,SendAs", Trustee = "sg@test.be" } },
                Groepen = new[] { new { Type = "MailUniversalSecurityGroup", DisplayName = "SG", Smtp = "sg@test.be", ManagedBy = "an|bo", Leden = new[] { new { Smtp = "an@test.be", Naam = "An" } } } },
            },
        });
        var g = await new ExchangeConnection(null, fake, _folder).ReadMailboxesAsync(null, CancellationToken.None);
        var p = Assert.Single(g.Mailboxes);
        Assert.True(p.Hidden);
        Assert.Equal("x@fabrikam.com", p.ForwardingTo);
        Assert.True(p.SaveForwardingAnd);
        Assert.Equal("Disabled", p.AutoReply);
        Assert.Equal(new[] { "FullAccess", "SendAs" }, g.Permissions.Select(r => r.Permission));
        var groupValue = Assert.Single(g.Groups);
        Assert.Equal(new[] { "an", "bo" }, groupValue.ManagedBy);
        Assert.Equal(1, groupValue.CountMembers);
        Assert.Equal("an@test.be", Assert.Single(groupValue.Members).Smtp);
    }

    [Fact]
    public async Task ReadRooms()
    {
        var fake = new FakePowerShell(o => new
        {
            id = "rooms",
            status = "Gelukt",
            melding = "",
            data = new[] { new { Smtp = "rm@test.be", DisplayName = "Zaal", Capaciteit = 8, BookingWindowInDays = "180", AllowConflicts = "False", AgendaRechten = "Default=Reviewer" } },
        });
        var rooms = await new ExchangeConnection(null, fake, _folder).ReadRoomsAsync(null, CancellationToken.None);
        var room = Assert.Single(rooms);
        Assert.Equal(8, room.Capacity);
        Assert.Equal(180, room.BookingWindowInDays);
        Assert.Equal("Reviewer", room.DefaultCalendarPermission);
    }

    [Fact]
    public async Task WithoutResultFileAClearError()
    {
        var connection = new ExchangeConnection(null, new EmptyPowerShell(), _folder);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => connection.ReadRoomsAsync(null, CancellationToken.None));
        Assert.Contains("module ontbreekt", ex.Message);
    }

    private sealed class EmptyPowerShell : IPowerShellStarter
    {
        public Task<(int Code, string Errors)> StartAsync(string script, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel)
            => Task.FromResult((1, "module ontbreekt"));
    }

    [Fact]
    public void OnlyResultWithoutList()
    {
        var r = Assert.Single(ExchangeConnection.ReadResults("﻿{\"id\":\"a\",\"status\":\"Gelukt\",\"melding\":\"\",\"data\":null}"));
        Assert.Equal("a", r.Id);
        Assert.Null(r.Data);
    }
}

public class LiveLoaderTests
{
    private sealed class FakeAd : IAdConnection
    {
        public Task<AdData> ReadAsync(IProgress<string>? progress, CancellationToken cancel)
            => Task.FromResult(new AdData(
                new[] { new Moss365.Core.Inventory.AdUser { SamAccountName = "live.gebruiker", DistinguishedName = "CN=Live,DC=x", Enabled = true } },
                Array.Empty<Moss365.Core.Inventory.AdGroup>(),
                "DC=LIVE,DC=INTERNAL"));

        public Task<Moss365.Core.Inventory.AdUser?> ReadUserAsync(string samAccountName, CancellationToken cancel)
            => Task.FromResult<Moss365.Core.Inventory.AdUser?>(null);

        public Task<Moss365.Core.Inventory.AdGroup?> ReadGroupAsync(string samAccountName, CancellationToken cancel)
            => Task.FromResult<Moss365.Core.Inventory.AdGroup?>(null);
    }

    [Fact]
    public async Task ReplacesAdAndKeepsTheRest()
    {
        var current = TestData.AppEnvironment();
        var live = await LiveLoader.LoadAsync(new FakeAd(), null, current, null, CancellationToken.None);

        Assert.Equal("Live", live.Origin);
        Assert.Equal("DC=LIVE,DC=INTERNAL", live.DomainDn);
        Assert.Single(live.AdUsers);
        Assert.Equal(current.EntraUsers.Count, live.EntraUsers.Count);
        Assert.Equal(current.Rooms.Count, live.Rooms.Count);
        Assert.Contains(live.Sources, b => b.Folder!.StartsWith("live"));

        var withRooms = LiveLoader.WithRooms(live, Array.Empty<Moss365.Core.Inventory.Room>());
        Assert.Empty(withRooms.Rooms);
        Assert.Single(withRooms.AdUsers);
    }
}
