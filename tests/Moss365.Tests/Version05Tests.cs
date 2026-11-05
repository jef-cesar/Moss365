using System.Net;
using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.Connections.Ad;
using Moss365.Connections.Graph;
using Moss365.Connections.Updates;
using Xunit;

namespace Moss365.Tests;

/// <summary>Core and connections for 0.5: export, versions, helpdesk, change log and undo, scheduling, cleanup,
/// security, delta.</summary>
public sealed class Version05Tests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-v05-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ---------------------------------------------------------------- export and versions

    [Fact]
    public void CsvForExcelRunsNoFormulas()
    {
        var text = CsvWriter.Text(new[] { "A", "B" }, new[] { new[] { "=HYPERLINK(\"x\")", "-5" }, new[] { "@som", "gewoon \"tekst\"" } }, forExcel: true);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        Assert.Contains("\"-5\"", text);
        Assert.Contains("\"'@som\"", text);
        Assert.Contains("\"gewoon \"\"tekst\"\"\"", text);

        // The inventory (which the app reads back itself) stays unchanged
        Assert.Contains("\"=HYPERLINK", CsvWriter.Text(new[] { "A" }, new[] { new[] { "=HYPERLINK(\"x\")" } }));
        Assert.Equal("Opkuis-Medewerkers-180d", CsvWriter.FileName("Opkuis-Medewerkers-180d"));
        Assert.Equal("a-b-c", CsvWriter.FileName("a/b:c"));
    }

    [Theory]
    [InlineData("v0.5.1", "0.5.0", true)]
    [InlineData("0.6", "0.5.9", true)]
    [InlineData("v0.5.0", "0.5.0", false)]
    [InlineData("0.4.9", "0.5.0", false)]
    [InlineData("v1.0.0+abc", "0.9.0", true)]
    [InlineData("onzin", "0.5.0", false)]
    public void CompareVersions(string candidate, string current, bool newer)
        => Assert.Equal(newer, Versions.IsNewer(candidate, current));

    [Fact]
    public async Task FindUpdateOnGitHub()
    {
        var found = await new GitHubUpdates(new HttpClient(new FakeHttp(HttpStatusCode.OK, """{"tag_name":"v0.6.0","html_url":"https://github.com/x/y/releases/tag/v0.6.0"}""")), "x/y")
            .CheckAsync(CancellationToken.None);
        Assert.Equal("0.6.0", found.NewestVersion);
        Assert.True(found.IsNewer("0.5.0"));
        Assert.EndsWith("/tag/v0.6.0", found.Link);

        var privateValue = await new GitHubUpdates(new HttpClient(new FakeHttp(HttpStatusCode.NotFound, "{}")), "x/y").CheckAsync(CancellationToken.None);
        Assert.Null(privateValue.NewestVersion);
        Assert.Contains("privé", privateValue.Message);
        Assert.Equal("https://github.com/x/y/releases/latest", privateValue.Link);
    }

    // ---------------------------------------------------------------- helpdesk

    [Fact]
    public void PasswordPlanIsOnlyInTheSecret()
    {
        var d = TestData.AppEnvironment();
        var bo = d.SearchPerson("ad:bo.janssens")!;
        Assert.Contains("minstens 12", Assert.Single(HelpdeskPlans.ConfigurePassword(d, bo, "kort", true, false).Errors));

        var plan = HelpdeskPlans.ConfigurePassword(d, bo, "Een-Lang-Wachtwoord-1", changeAtSignIn: false, unlock: true).Plan;
        Assert.Equal(new[] { ChangeAction.AdSetPassword, ChangeAction.AdUnlockAccount }, plan.Select(w => w.ChangeAction));
        Assert.Equal("Een-Lang-Wachtwoord-1", plan[0].Secret);
        Assert.Equal("False", plan[0].P("wijzigenBijAanmelding"));
        Assert.DoesNotContain(plan[0].Parameters.Values, v => v.Contains("Wachtwoord-1", StringComparison.Ordinal));
        Assert.Null(plan[0].Warning);

        // Privileged account: a warning; disabled: enabling is added (not ticked)
        Assert.Contains("Domain Admins", HelpdeskPlans.ConfigurePassword(d, d.SearchPerson("ad:an.peeters")!, "Een-Lang-Wachtwoord-1", true, false).Plan[0].Warning);
        var old = HelpdeskPlans.ConfigurePassword(d, d.SearchPerson("ad:oud.account")!, "Een-Lang-Wachtwoord-1", true, false).Plan;
        Assert.False(Assert.Single(old, w => w.ChangeAction == ChangeAction.AdEnableAccount).Selected);

        // A cloud account: not through the app
        var info = d.Persons.Single(p => p.Name == "Info");
        Assert.Contains("Entra-portaal", Assert.Single(HelpdeskPlans.ConfigurePassword(d, info, "Een-Lang-Wachtwoord-1", true, false).Errors));
    }

    [Fact]
    public void LockedFromLdap()
    {
        Dictionary<string, object?[]> K(params (string, object?)[] w) => w.ToDictionary(x => x.Item1, x => new[] { x.Item2 }, StringComparer.OrdinalIgnoreCase);
        Assert.True(AdLdapMapper.ToUser(K(("sAMAccountName", "a"), ("msDS-User-Account-Control-Computed", 0x10))).LockedOut);
        Assert.False(AdLdapMapper.ToUser(K(("sAMAccountName", "a"), ("msDS-User-Account-Control-Computed", 0), ("lockoutTime", 5L))).LockedOut);
        Assert.True(AdLdapMapper.ToUser(K(("sAMAccountName", "a"), ("lockoutTime", 132000000000000000L))).LockedOut);
        Assert.False(AdLdapMapper.ToUser(K(("sAMAccountName", "a"))).LockedOut);
    }

    // ---------------------------------------------------------------- change log and undo

    [Fact]
    public async Task ChangeLogKeepsPlanAndPreviousValuesButNoSecret()
    {
        var changeLog = new JsonlChangeLog(_folder);
        var plan = new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdSetPassword, Target = "CN=a,DC=t", Description = "w",
                Secret = "Geheim-Wachtwoord-123",
            },
            new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdSetAttributes, Target = "CN=a,DC=t", Description = "k",
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["description"] = "nieuw" },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["description"] = "oud" },
            },
        };
        await new PlanExecutor(new[] { new AllOk(TargetSystem.ActiveDirectory) }, changeLog, () => "test").ExecuteAsync(plan, "ctx");

        Assert.DoesNotContain("Geheim-Wachtwoord-123", File.ReadAllText(Directory.GetFiles(_folder).Single()));
        var rules = changeLog.Recent(10);
        Assert.Single(rules.Select(r => r.PlanId).Distinct());
        Assert.NotNull(rules[0].PlanId);
        Assert.Equal("oud", rules.Single(r => r.ChangeAction == nameof(ChangeAction.AdSetAttributes)).Previous!["description"]);

        // Older entries (from before 0.5) without the new fields stay readable
        File.AppendAllText(Path.Combine(_folder, "logboek-2025-01.jsonl"),
            """{"tijd":"2025-01-02T10:00:00+01:00","gebruiker":"x","context":"c","systeem":"AD","actie":"AdGroepLidToevoegen","doel":"d","doelNaam":"n","omschrijving":"o","parameters":{},"status":"Gelukt","melding":""}""" + "\n");
        var old = changeLog.Recent(10).Single(r => r.User == "x");
        Assert.Null(old.PlanId);
        Assert.True(Undo.CanReverse(old));
    }

    [Fact]
    public void UndoOffboardingInReverseOrder()
    {
        var d = TestData.AppEnvironment();
        var an = d.SearchPerson("ad:an.peeters")!;
        var plan = PlanBuilder.Offboarding(d, TestData.Config(), an, new OffboardingOptions { AdRemoveGroups = true, Date = new DateOnly(2026, 10, 31) })
            .Where(w => w.Selected && w.TargetSystem == TargetSystem.ActiveDirectory).ToList();
        var t = new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero);
        var log = plan.Select((w, i) => Rule(w, t.AddMilliseconds(i))).ToList();

        // The account is now in the offboarding OU
        var newDn = "CN=an.peeters,OU=disabled_users,OU=ARCHIVE,DC=test,DC=internal";
        var now = d with { AdUsers = d.AdUsers.Select(u => u.SamAccountName == "an.peeters" ? u with { DistinguishedName = newDn, Enabled = false } : u).ToList() };

        var back = Undo.Plan(log, now);
        Assert.Empty(back.NotReversible);
        Assert.Equal(ChangeAction.AdMove, back.Plan[0].ChangeAction);
        Assert.Equal(newDn, back.Plan[0].Target, ignoreCase: true);
        Assert.Equal(TestData.Municipality, back.Plan[0].P("doelOu"));

        // After moving back: the old DN for the groups
        var groups = back.Plan.Where(w => w.ChangeAction == ChangeAction.AdAddGroupMember).ToList();
        Assert.Equal(3, groups.Count);
        Assert.All(groups, w => Assert.Equal(TestData.Dn("an.peeters", TestData.Municipality), w.Target));
        var admins = groups.Single(w => w.P("groep") == "Domain Admins");
        Assert.False(admins.Selected);
        Assert.Contains("bevoorrechte groep", admins.Warning);

        var description = back.Plan.Single(w => w.ChangeAction == ChangeAction.AdSetAttributes);
        Assert.Equal(string.Empty, description.P("description"));
        Assert.Equal(ChangeAction.AdEnableAccount, back.Plan[^1].ChangeAction);
    }

    [Fact]
    public void NotEverythingCanBeUndone()
    {
        LogEntry R(ChangeAction a, string status = "Gelukt", string message = "", Dictionary<string, string>? p = null)
            => new(DateTimeOffset.Now, "x", "c", "s", a.ToString(), "doel", "naam", "o", p ?? new(), status, message);

        Assert.Contains("niets", Undo.WhyNot(R(ChangeAction.GraphAddGroupMember, message: "Was al lid.")));
        Assert.Contains("niet gelukt", Undo.WhyNot(R(ChangeAction.GraphAddGroupMember, "Mislukt")));
        Assert.Contains("wachtwoord", Undo.WhyNot(R(ChangeAction.AdSetPassword)));
        Assert.Contains("versie 0.5", Undo.WhyNot(R(ChangeAction.AdSetAttributes)));
        Assert.Contains("Gastgebruikers", Undo.WhyNot(R(ChangeAction.GraphInviteGuest)));

        var administrator = Assert.Single(Undo.Plan(new[] { R(ChangeAction.ExchangeGroupManager, p: new() { ["Toevoegen"] = "an@test.be" }) }).Plan);
        Assert.Equal("an@test.be", administrator.P("Verwijderen"));
        Assert.False(administrator.Parameters.ContainsKey("Toevoegen"));

        var permission = Assert.Single(Undo.Plan(new[] { R(ChangeAction.ExchangeRemoveMailboxPermission, p: new() { ["Recht"] = "SendAs", ["Trustee"] = "bo@test.be" }) }).Plan);
        Assert.Equal(ChangeAction.ExchangeAddMailboxPermission, permission.ChangeAction);
        Assert.Equal(TargetSystem.Exchange, permission.TargetSystem);
    }

    // ---------------------------------------------------------------- scheduling

    [Fact]
    public void ScheduledOffboardingSaveAndRecompute()
    {
        var d = TestData.AppEnvironment();
        var storage = new ScheduledStorage(Path.Combine(_folder, "gepland.json"));
        var whenValue = new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.FromHours(1));
        storage.Add(ScheduledPlans.Offboarding(d.SearchPerson("ad:bo.janssens")!, new OffboardingOptions { Date = new DateOnly(2026, 12, 31), RemoveLicences = true }, whenValue, "test"));

        var a = Assert.Single(storage.Read());
        Assert.Equal(whenValue, a.When);
        Assert.True(a.Options!.RemoveLicences);
        Assert.False(a.IsDue(whenValue.AddMinutes(-1)));
        Assert.True(a.IsDue(whenValue));

        var plan = ScheduledPlans.PlanFor(a, d, TestData.Config());
        Assert.Empty(plan.Errors);
        Assert.Contains(plan.Plan, w => w.ChangeAction == ChangeAction.AdDisableAccount && w.Selected);
        Assert.Contains("2026-12-31", plan.Plan.Single(w => w.ChangeAction == ChangeAction.AdSetAttributes).P("description"));

        // The user no longer exists
        var route = ScheduledPlans.PlanFor(a, d with { Persons = Array.Empty<Person>() }, TestData.Config());
        Assert.Contains("niet (meer)", Assert.Single(route.Errors));

        storage.Remove(a.Id);
        Assert.Empty(storage.Read());
    }

    // ---------------------------------------------------------------- cleanup

    [Fact]
    public void CleanupCandidatesAndPlan()
    {
        var d = TestData.AppEnvironment();
        var now = new DateTime(2027, 6, 1);
        var staff = CleanupPlans.Candidates(d, TestData.Config(), CleanupKind.Staff, 180, now);
        Assert.Contains(staff, k => k.Person.Key == "ad:cas.vos");
        Assert.DoesNotContain(staff, k => k.Person.Key == "ad:oud.account"); // already disabled
        Assert.DoesNotContain(staff, k => k.Person.IsGuest);
        Assert.True(staff.Single(k => k.Person.Key == "ad:an.peeters").Privileged);
        Assert.Empty(CleanupPlans.Candidates(d, TestData.Config(), CleanupKind.Staff, 365, new DateTime(2026, 10, 2)));

        var cas = staff.Single(k => k.Person.Key == "ad:cas.vos").Person;
        var plan = CleanupPlans.Plan(d, CleanupKind.Staff, new[] { cas }, new DateOnly(2027, 6, 1)).Plan;
        Assert.Equal(new[] { ChangeAction.AdDisableAccount, ChangeAction.AdSetAttributes }, plan.Select(w => w.ChangeAction));
        Assert.StartsWith("Uitgeschakeld bij opkuisronde 2027-06-01", plan[1].P("description"));
        Assert.Equal(string.Empty, plan[1].Previous!["description"]);

        var info = d.Persons.Single(p => p.Name == "Info");
        var cloud = Assert.Single(CleanupPlans.Plan(d, CleanupKind.Staff, new[] { info }, new DateOnly(2027, 6, 1)).Plan);
        Assert.Equal(ChangeAction.GraphSetAttributes, cloud.ChangeAction);
        Assert.Equal("false", cloud.P("accountEnabled"));

        var guest = d.Persons.Single(p => p.IsGuest);
        Assert.Equal(ChangeAction.GraphRemoveGuest, Assert.Single(CleanupPlans.Plan(d, CleanupKind.Guests, new[] { guest }, new DateOnly(2027, 6, 1)).Plan).ChangeAction);
    }

    // ---------------------------------------------------------------- security

    [Fact]
    public void SecurityFromGraphAndStatistics()
    {
        var mfa = GraphConnection.ToMfa(Json("""{"id":"e-bo","userPrincipalName":"bo@test.be","isMfaRegistered":false,"isMfaCapable":false,"isAdmin":true,"methodsRegistered":["email"],"defaultMfaMethod":"none"}"""));
        Assert.False(mfa.MfaRegistered);
        Assert.Equal(new[] { "email" }, mfa.Methods);
        Assert.Equal(string.Empty, mfa.DefaultMethod);

        var a = GraphConnection.ToSignIn(Json("""{"createdDateTime":"2026-10-20T08:00:00Z","userId":"e-bo","userPrincipalName":"bo@test.be","userDisplayName":"Bo","appDisplayName":"Office 365","ipAddress":"203.0.113.5","clientAppUsed":"Browser","location":{"city":"Gent","countryOrRegion":"BE"},"status":{"errorCode":50126,"failureReason":"Invalid username or password"}}"""));
        Assert.False(a.Succeeded);
        Assert.Equal("Gent, BE", a.Location);
        Assert.Equal(50126, a.ErrorCode);

        var d = TestData.AppEnvironment() with
        {
            Security = new SecurityData(
                new[] { mfa, new MfaRegistration("e-an", "an@test.be", true, true, new[] { "microsoftAuthenticatorPush", "mobilePhone" }, "microsoftAuthenticatorPush", true) },
                new[] { new RoleMember("Global Administrator", "e-bo", "Bo", "bo@test.be", "user"), new RoleMember("Directory Readers", "sp-1", "App", "", "servicePrincipal") }),
        };
        var b = Statistic.Compute(d, TestData.Config(), new DateTime(2026, 10, 24)).Security!;
        Assert.Equal(2, b.WithMfaData);
        Assert.Equal(1, b.WithoutMfa);
        Assert.Equal(1, b.WithAdminRole);
        Assert.Equal(1, b.GlobalAdministrators);
        Assert.Equal(1, b.CriticalWithoutMfa);
        Assert.Contains(b.Methods, s => s.Name == "Authenticator (melding)" && s.Count == 1);
    }

    // ---------------------------------------------------------------- quick load

    [Fact]
    public void AdApplyChanges()
    {
        const string ou = "OU=U,DC=t";
        const string ou2 = "OU=Weg,DC=t";
        AdUser U(string sam, params string[] memberOf) => new() { SamAccountName = sam, DistinguishedName = $"CN={sam},{ou}", OuPath = "U", MemberOr = memberOf };
        AdGroup G(string sam, params string[] members) => new() { SamAccountName = sam, Name = sam, DistinguishedName = $"CN={sam},{ou}", Members = members, MembersKnown = true, CountMembers = members.Length };

        var baseline = new AdBase(new AdSnapshot("dc1", 100),
            new[] { U("a", $"CN=g1,{ou}"), U("b", $"CN=g2,{ou}"), U("d", $"CN=g2,{ou}") },
            new[] { G("g1", $"CN=a,{ou}"), G("g2", $"CN=b,{ou}", $"CN=d,{ou}") });

        // g1 got b; c is new; d is deleted; b is moved (without b itself being changed)
        var (users, groups) = AdDelta.Apply(baseline,
            new[] { U("c") },
            new[] { G("g1", $"CN=a,{ou}", $"CN=b,{ou2}") },
            new Dictionary<string, string> { ["a"] = $"CN=a,{ou}", ["b"] = $"CN=b,{ou2}", ["c"] = $"CN=c,{ou}" },
            new Dictionary<string, string> { ["g1"] = $"CN=g1,{ou}", ["g2"] = $"CN=g2,{ou}" });

        Assert.Equal(new[] { "a", "b", "c" }, users.Select(u => u.SamAccountName).OrderBy(s => s));
        var b = users.Single(u => u.SamAccountName == "b");
        Assert.Equal($"CN=b,{ou2}", b.DistinguishedName);
        Assert.Equal("Weg", b.OuPath);
        Assert.Equal(new[] { $"CN=g1,{ou}", $"CN=g2,{ou}" }, b.MemberOr.OrderBy(x => x));

        var g2 = groups.Single(g => g.SamAccountName == "g2");
        Assert.Equal(new[] { $"CN=b,{ou2}" }, g2.Members);
        Assert.Equal(1, g2.CountMembers);
    }

    [Fact]
    public void GraphApplyGroupChanges()
    {
        var baseline = new GraphBase("link",
            new Dictionary<string, CloudGroup>
            {
                ["g1"] = new() { Id = "g1", DisplayName = "Een", Type = CloudGroupType.Microsoft365, CountMembers = 1 },
                ["g2"] = new() { Id = "g2", DisplayName = "Twee", CountMembers = 1 },
            },
            new Dictionary<string, IReadOnlySet<string>> { ["u1"] = new HashSet<string> { "g1", "g2" } });

        var items = new[]
        {
            GraphConnection.ToDeltaItem(Json("""{"id":"g1","displayName":"Een (nieuw)","members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u2"},{"@odata.type":"#microsoft.graph.user","id":"u1","@removed":{"reason":"deleted"}},{"@odata.type":"#microsoft.graph.device","id":"pc1"}]}""")),
            GraphConnection.ToDeltaItem(Json("""{"id":"g2","@removed":{"reason":"changed"}}""")),
            GraphConnection.ToDeltaItem(Json("""{"id":"g3","displayName":"Drie","groupTypes":[],"mailEnabled":false,"securityEnabled":true,"members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u1"}]}""")),
        };
        var (groups, member) = GraphDelta.Apply(baseline, items);

        var g1 = groups.Single(g => g.Id == "g1");
        Assert.Equal("Een (nieuw)", g1.DisplayName);
        Assert.Equal(CloudGroupType.Microsoft365, g1.Type); // kind not complete in the answer: stays
        Assert.Equal(1, g1.CountMembers);
        Assert.DoesNotContain(groups, g => g.Id == "g2");
        Assert.Equal(CloudGroupType.Security, groups.Single(g => g.Id == "g3").Type);
        Assert.Equal(new[] { "g3" }, member["u1"]);
        Assert.Equal(new[] { "g1" }, member["u2"]);
        Assert.False(member.ContainsKey("pc1"));
    }

    [Fact]
    public void AdStateAndDomainController()
    {
        Assert.Equal(new AdSnapshot("CN=NTDS,CN=DC1", 12345), AdLdapMapper.ToSnapshot("CN=NTDS,CN=DC1", "12345"));
        Assert.Null(AdLdapMapper.ToSnapshot("", "1"));
        Assert.Null(AdLdapMapper.ToSnapshot("dc", "geen getal"));
        Assert.True(AdLdapMapper.CanContinueFrom(new AdSnapshot("dc1", 100), new AdSnapshot("DC1", 150)));
        Assert.False(AdLdapMapper.CanContinueFrom(new AdSnapshot("dc1", 100), new AdSnapshot("dc2", 150)));
        Assert.False(AdLdapMapper.CanContinueFrom(new AdSnapshot("dc1", 100), new AdSnapshot("dc1", 50)));
    }

    // ---------------------------------------------------------------- 0.5.1: organisation and mailbox accounts

    [Fact]
    public void LoadOrganisationFallsBackToTheBundledOne()
    {
        Directory.CreateDirectory(_folder);
        var defaultValue = Path.Combine(_folder, "standaard.json");
        var own = Path.Combine(_folder, "eigen.json");

        var (_, source, message) = ConfigStorage.LoadSafe(defaultValue, own);
        Assert.Null(source);
        Assert.Contains("niet rechtstreeks vanuit de zip", message);

        ConfigStorage.Save(TestData.Config(), defaultValue);
        Assert.Equal((defaultValue, (string?)null), (ConfigStorage.LoadSafe(defaultValue, own).Source, ConfigStorage.LoadSafe(defaultValue, own).Warning));

        File.WriteAllText(own, "{ kapot");
        var broken = ConfigStorage.LoadSafe(defaultValue, own);
        Assert.Equal(defaultValue, broken.Source);
        Assert.NotEmpty(broken.Config.Populations);
        Assert.Contains("niet leesbaar", broken.Warning);

        File.WriteAllText(own, """{ "company": "Contoso" }""");
        var empty = ConfigStorage.LoadSafe(defaultValue, own);
        Assert.Equal(defaultValue, empty.Source);
        Assert.Contains("geen soorten", empty.Warning);

        var good = TestData.Config();
        good.Company = "Eigen";
        ConfigStorage.Save(good, own);
        var ownGood = ConfigStorage.LoadSafe(defaultValue, own);
        Assert.Equal((own, "Eigen", (string?)null), (ownGood.Source, ownGood.Config.Company, ownGood.Warning));
    }

    [Fact]
    public void MailboxAndRoomAccountsDoNotCountAsEmployee()
    {
        var files = TestData.Files();
        files[InventoryLoader.EntraUsers].Add(TestData.EntraUser("e-boek", "boekhouding@test.be", displayName: "Boekhouding"));
        files[InventoryLoader.EntraUsers].Add(TestData.EntraUser("e-zaal", "rm-vz-a@test.be", displayName: "Vergaderzaal A"));
        var d = InventoryLoader.Build(files);
        var without = Statistic.Compute(TestData.AppEnvironment(), TestData.Config(), new DateTime(2026, 10, 24));
        var withValue = Statistic.Compute(d, TestData.Config(), new DateTime(2026, 10, 24));

        Assert.Equal(without.Accounts, withValue.Accounts);
        Assert.Equal(without.NeverSignedIn, withValue.NeverSignedIn);
        Assert.Equal(2, withValue.MailboxAccounts);
        Assert.True(d.Index.IsMailboxAccount(d.Persons.Single(p => p.Name == "Boekhouding")));
        Assert.False(d.Index.IsMailboxAccount(d.SearchPerson("ad:an.peeters")!));
        Assert.DoesNotContain(CleanupPlans.Candidates(d, TestData.Config(), CleanupKind.Staff, 1, new DateTime(2030, 1, 1)),
            k => k.Person.Name is "Boekhouding" or "Vergaderzaal A");
    }

    // ---------------------------------------------------------------- helpers

    private static LogEntry Rule(Change w, DateTimeOffset t)
        => new(t, "test", "Uitstroom", w.SystemText, w.ChangeAction.ToString(), w.Target, w.TargetName, w.Description,
            new Dictionary<string, string>(w.Parameters), "Gelukt", string.Empty, Guid.Empty, w.Id, w.Previous);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private sealed class AllOk(TargetSystem system) : IExecutor
    {
        public TargetSystem TargetSystem => system;

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<ChangeResult>>(changes.Select(w => ChangeResult.Ok(w)).ToList());
    }

    private sealed class FakeHttp(HttpStatusCode status, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("Moss365", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
        }
    }
}
