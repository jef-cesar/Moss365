using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.Core.Changes;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.9: managing Conditional Access. Mostly the safety: emergency accounts, never lock yourself out,
/// report-only.</summary>
public sealed class Version09Tests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0);
    private const string Me = "an.peeters@test.be"; // Global Administrator in this test data
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-v09-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    /// <summary>A policy as Graph would return it (with the id and the JSON).</summary>
    private static CaPolicy WithJson(CaPolicy b)
    {
        var node = JsonNode.Parse(CaJson.ToGraph(b))!.AsObject();
        node["id"] = b.Id;
        node["createdDateTime"] = "2026-01-01T00:00:00Z";
        node["modifiedDateTime"] = "2026-02-01T00:00:00Z";
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return CaJson.Read(doc.RootElement);
    }

    private static InventoryData Data(params CaPolicy[] extra)
    {
        var ca = TestData.Ca(new DateTimeOffset(Now));
        return TestData.AppEnvironment() with
        {
            ConditionalAccess = ca with
            {
                Policies = ca.Policies.Concat(extra).Select(WithJson).ToList(),
                Locations = ca.Locations.Select(l => l with { Json = CaJson.LocationJson(l.Name, l.IsIp, l.Values, l.Trusted).Replace("{", $"{{\"id\":\"{l.Id}\",", StringComparison.Ordinal) }).ToList(),
            },
            Security = new SecurityData(Array.Empty<MfaRegistration>(),
                new[] { new RoleMember("Global Administrator", "e-an", "an peeters", Me, "user", CaData.RoleGlobalAdmin) }),
        };
    }

    /// <summary>The emergency account in the test data: the cloud-only account "Info".</summary>
    private static OrganisationConfig WithEmergency()
    {
        var c = TestData.Config();
        c.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        return c;
    }

    private static CaPolicy B(InventoryData d, string id) => d.ConditionalAccess!.Policies.Single(b => b.Id == id);

    private static JsonNode Json(Change w, string parameter = "json") => JsonNode.Parse(w.P(parameter))!;

    private static LogEntry Logged(Change w) => new(DateTimeOffset.Now, Me, "c", "Graph", w.ChangeAction.ToString(), w.Target, w.TargetName,
        w.Description, w.Parameters, "Gelukt", "", Guid.NewGuid(), w.Id, w.Previous);

    // ---------------------------------------------------------------- templates

    [Fact]
    public void WithoutEmergencyAccountsNoNewPolicy()
    {
        var u = CaTemplates.Plan("mfa-iedereen", new CaTemplateOptions(), Data(), TestData.Config(), Me);
        Assert.Empty(u.Plan);
        Assert.Contains("geen noodaccounts", u.Errors[0]);

        var c = TestData.Config();
        c.EmergencyAccounts.Add("bestaat.niet@test.onmicrosoft.com");
        Assert.Contains("bestaat.niet", CaTemplates.Plan("mfa-iedereen", new CaTemplateOptions(), Data(), c, Me).Errors[0]);
    }

    [Fact]
    public void ATemplateIsAlwaysReportOnlyWithTheEmergencyAccountsExcluded()
    {
        var d = Data();
        foreach (var s in CaTemplates.All.Where(s => s.Key != "landen-blokkeren"))
        {
            var u = CaTemplates.Plan(s.Key, new CaTemplateOptions(), d, WithEmergency(), Me);
            var w = Assert.Single(u.Plan);
            Assert.Equal(ChangeAction.GraphCreateCaPolicy, w.ChangeAction);
            var json = Json(w);
            Assert.Equal("enabledForReportingButNotEnforced", json["state"]!.GetValue<string>());
            Assert.Contains("e-info", json["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Null(CaSafety.Refusal(w, d, WithEmergency(), Me));
        }

        // A pilot group instead of everyone
        var pilot = Json(Assert.Single(CaTemplates.Plan("mfa-iedereen", new CaTemplateOptions("g-team"), d, WithEmergency(), Me).Plan));
        Assert.Equal(new[] { "g-team" }, pilot["conditions"]!["users"]!["includeGroups"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("All", pilot["conditions"]!["users"]!["includeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Phishing-resistant: the built-in strength, only with its id
        var management = Json(Assert.Single(CaTemplates.Plan("beheerders-phishingbestendig", new CaTemplateOptions(), d, WithEmergency(), Me).Plan));
        Assert.Equal(CaData.StrengthPhishingResistant, management["grantControls"]!["authenticationStrength"]!["id"]!.GetValue<string>());
        Assert.Contains(CaData.RoleGlobalAdmin, management["conditions"]!["users"]!["includeRoles"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Device code: the transfer method
        var code = Json(Assert.Single(CaTemplates.Plan("toestelcode-blokkeren", new CaTemplateOptions(), d, WithEmergency(), Me).Plan));
        Assert.Equal("deviceCodeFlow", code["conditions"]!["authenticationFlows"]!["transferMethods"]!.GetValue<string>());
    }

    [Fact]
    public void BlockCountriesAsksALocationWithHomeCountry()
    {
        var d = Data();
        Assert.Contains("Kies de benoemde locatie", CaTemplates.Plan("landen-blokkeren", new CaTemplateOptions(), d, WithEmergency(), Me).Errors[0]);
        Assert.Contains("Kies de benoemde locatie", CaTemplates.Plan("landen-blokkeren", new CaTemplateOptions(CountriesLocationId: "loc-kantoor"), d, WithEmergency(), Me).Errors[0]);
        var json = Json(Assert.Single(CaTemplates.Plan("landen-blokkeren", new CaTemplateOptions(CountriesLocationId: "loc-be"), d, WithEmergency(), Me).Plan));
        Assert.Equal("loc-be", json["conditions"]!["locations"]!["excludeLocations"]![0]!.GetValue<string>());

        // An existing name: not again
        var doubleValue = Data(new CaPolicy { Id = "x", Name = "MFA voor alle gebruikers", Status = CaStatus.Off, UsersIn = new[] { "All" }, AppsIn = new[] { "All" } });
        Assert.Contains("bestaat al", CaTemplates.Plan("mfa-iedereen", new CaTemplateOptions(), doubleValue, WithEmergency(), Me).Errors[0]);
    }

    // ---------------------------------------------------------------- enabling, and the checks

    [Fact]
    public void EnablingRequiresEmergencyAccountsExcluded()
    {
        var d = Data();

        // "Administrators phishing-resistant" (report) applies only to the role: the emergency account does not fall
        // under it
        var w = Assert.Single(CaPlans.Status(B(d, "ca-beheer"), CaStatus.On, d, WithEmergency(), Me).Plan);
        Assert.Equal("enabled", Json(w)["state"]!.GetValue<string>());
        Assert.Equal("enabledForReportingButNotEnforced", JsonNode.Parse(w.Previous!["json"])!["state"]!.GetValue<string>());
        Assert.Null(CaSafety.Refusal(w, d, WithEmergency(), Me));

        // "Block abroad" does not exclude the emergency account: report-only is allowed, enabling is not
        var country = B(d, "ca-land") with { Status = CaStatus.Report };
        var d2 = d with { ConditionalAccess = d.ConditionalAccess! with { Policies = d.ConditionalAccess.Policies.Select(b => b.Id == "ca-land" ? WithJson(country) : b).ToList() } };
        var enabled = CaPlans.Status(B(d2, "ca-land"), CaStatus.On, d2, WithEmergency(), Me);
        Assert.Empty(enabled.Plan);
        Assert.Contains("noodaccount", enabled.Errors[0]);
        Assert.Contains("Sluit het uit", enabled.Errors[0]);

        // Without emergency accounts in the configuration: never enable
        Assert.Contains("geen noodaccounts", CaPlans.Status(B(d, "ca-beheer"), CaStatus.On, d, TestData.Config(), Me).Errors[0]);

        // Turning off is always allowed
        Assert.Single(CaPlans.Status(B(d, "ca-land"), CaStatus.Off, d, TestData.Config(), Me).Plan);
    }

    [Fact]
    public void LockingYourselfOutIsRefused()
    {
        var all = new CaPolicy
        {
            Id = "ca-alles", Name = "Alles blokkeren", Status = CaStatus.Off, UsersIn = new[] { "All" }, UsersOff = new[] { "e-info" },
            AppsIn = new[] { "All" }, Checks = new[] { "block" },
        };
        var d = Data(all);
        var u = CaPlans.Status(B(d, "ca-alles"), CaStatus.On, d, WithEmergency(), Me);
        Assert.Empty(u.Plan);
        Assert.Contains("jouw eigen account", u.Errors[0]);

        // In report-only it is possible, with a warning
        var report = Assert.Single(CaPlans.Status(B(d, "ca-alles"), CaStatus.Report, d, WithEmergency(), Me).Plan);
        Assert.Contains("jouw eigen account", report.Warning);

        // Whoever uses the app without an Entra account: a warning, the app cannot check it
        var unknown = Assert.Single(CaPlans.Status(B(d, "ca-beheer"), CaStatus.On, d, WithEmergency(), "iemand@fabrikam.com").Plan);
        Assert.Contains("eigen account staat niet", unknown.Warning);
    }

    [Fact]
    public void TheEngineRefusesAlsoOnUndoAndWithoutData()
    {
        var d = Data();

        // A log entry of a policy that was turned off: undo turns it on again, but it does not exclude the emergency
        // account
        var output = Assert.Single(CaPlans.Status(B(d, "ca-land"), CaStatus.Off, d, WithEmergency(), Me).Plan);
        var back = Assert.Single(Undo.Plan(new[] { Logged(output) }).Plan);
        Assert.Equal("enabled", Json(back)["state"]!.GetValue<string>());
        Assert.Contains("noodaccount", CaSafety.Refusal(back, d, WithEmergency(), Me));

        // Not loaded: nothing to check, so refuse
        Assert.Contains("niet in de geladen gegevens", CaSafety.Refusal(back, TestData.AppEnvironment(), WithEmergency(), Me));

        // This check leaves other actions alone
        Assert.Null(CaSafety.Refusal(new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphAddGroupMember, Target = "x", Description = "x" }, d, WithEmergency(), Me));
    }

    [Fact]
    public async Task TheEngineNeverRunsARefusedChange()
    {
        var d = Data();
        var w = new Change
        {
            TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphUpdateCaPolicy, Target = "ca-land", TargetName = "Buitenland blokkeren", Description = "aan",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.StatusPatch(CaStatus.On) },
        };
        var executed = new List<Change>();
        var changeLog = new MemoryChangeLog();
        var r = await new PlanExecutor(new[] { new Counter(executed) }, changeLog, () => Me, x => CaSafety.Refusal(x, d, WithEmergency(), Me))
            .ExecuteAsync(new[] { w }, "test");
        Assert.Equal(ResultStatus.Failed, Assert.Single(r).Status);
        Assert.StartsWith("Geweigerd", r[0].Message);
        Assert.Empty(executed);
        Assert.Contains("Geweigerd", Assert.Single(changeLog.Rules).Message);
    }

    private sealed class Counter(List<Change> executed) : IExecutor
    {
        public TargetSystem TargetSystem => TargetSystem.Graph;

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> plan, CancellationToken cancel)
        {
            executed.AddRange(plan);
            return Task.FromResult<IReadOnlyList<ChangeResult>>(plan.Select(x => ChangeResult.Ok(x)).ToList());
        }
    }

    private sealed class MemoryChangeLog : IChangeLog
    {
        public List<LogEntry> Rules { get; } = new();

        public void Write(LogEntry rule) => Rules.Add(rule);

        public IReadOnlyList<LogEntry> Recent(int count) => Rules.TakeLast(count).Reverse().ToList();
    }

    // ---------------------------------------------------------------- exclusions

    [Fact]
    public void ExclusionWithEndDateAndBack()
    {
        var d = Data();
        var cas = d.SearchPerson("ad:cas.vos")!;

        // "Block abroad" is on and does not exclude the emergency account: the app refuses every change until the
        // emergency account is excluded first
        Assert.Contains("noodaccount", CaPlans.Exclusion(B(d, "ca-land"), cas, add: true, d, WithEmergency(), Me).Errors[0]);
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        var emergency = Assert.Single(CaPlans.Exclusion(B(d, "ca-land"), info, add: true, d, WithEmergency(), Me).Plan);
        d = d with { ConditionalAccess = d.ConditionalAccess! with { Policies = d.ConditionalAccess.Policies.Select(b => b.Id == "ca-land" ? CaJson.AfterPatch(b, emergency.P("json")) : b).ToList() } };

        var w = Assert.Single(CaPlans.Exclusion(B(d, "ca-land"), cas, add: true, d, WithEmergency(), Me).Plan);
        var users = Json(w)["conditions"]!["users"]!;
        Assert.Equal(new[] { "e-info", "e-cas" }, users["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { "All" }, users["includeUsers"]!.AsArray().Select(x => x!.GetValue<string>())); // the recording goes along, nothing gets lost
        Assert.Equal(new[] { "e-info" }, JsonNode.Parse(w.Previous!["json"])!["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Undo: the previous exclusions
        var back = Assert.Single(Undo.Plan(new[] { Logged(w) }).Plan);
        Assert.Equal(new[] { "e-info" }, Json(back)["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // The end is ready in Scheduled and is computed at the moment itself
        var policyAfter = CaJson.AfterPatch(B(d, "ca-land"), w.P("json"));
        var dAfter = d with { ConditionalAccess = d.ConditionalAccess! with { Policies = d.ConditionalAccess.Policies.Select(b => b.Id == "ca-land" ? policyAfter : b).ToList() } };
        var scheduled = ScheduledPlans.CaExclusionEnd(policyAfter, cas, DateTimeOffset.Now.AddDays(7), Me);
        Assert.Equal(ScheduledKind.CaExclusionEnd, scheduled.Kind);
        var plan = ScheduledPlans.PlanFor(scheduled, dAfter, WithEmergency());
        Assert.Empty(plan.Errors);
        Assert.Equal(new[] { "e-info" }, Json(Assert.Single(plan.Plan))["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Already gone: no plan, but the reason
        Assert.Contains("al niet meer uitgezonderd", ScheduledPlans.PlanFor(scheduled, d, WithEmergency()).Errors[0]);
    }

    [Fact]
    public void RemovingTheExclusionOfAnEmergencyAccountIsRefused()
    {
        var d = Data();
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        var u = CaPlans.Exclusion(B(d, "ca-mfa"), info, add: false, d, WithEmergency(), Me);
        Assert.Empty(u.Plan);
        Assert.Contains("noodaccount", u.Errors[0]);
        Assert.Contains("al uitgezonderd", CaPlans.Exclusion(B(d, "ca-mfa"), info, add: true).Errors[0]);
    }

    // ---------------------------------------------------------------- named locations

    [Fact]
    public void NamedLocationsWithValueCheck()
    {
        var d = Data();
        var error = CaPlans.Location(null, "Bibliotheek", isIp: true, new[] { "203.0.113.5", "300.1.1.1/24", "198.51.100.0/24" }, true, d);
        Assert.Empty(error.Plan);
        Assert.Equal(2, error.Errors.Count);
        Assert.True(CaPlans.IsCidr("2001:db8::/48"));
        Assert.False(CaPlans.IsCidr("10.0.0.0/0"));

        var newValue = Assert.Single(CaPlans.Location(null, "Bibliotheek", true, new[] { "198.51.100.0/24", "2001:db8::/48" }, true, d).Plan);
        Assert.Equal(ChangeAction.GraphCreateCaLocation, newValue.ChangeAction);
        var json = Json(newValue);
        Assert.Equal("#microsoft.graph.ipNamedLocation", json["@odata.type"]!.GetValue<string>());
        Assert.True(json["isTrusted"]!.GetValue<bool>());
        Assert.Equal(new[] { "#microsoft.graph.iPv4CidrRange", "#microsoft.graph.iPv6CidrRange" },
            json["ipRanges"]!.AsArray().Select(r => r!["@odata.type"]!.GetValue<string>()));

        // Changing countries: the policies that use the location are in the warning
        var be = d.ConditionalAccess!.Location("loc-be")!;
        var countries = Assert.Single(CaPlans.Location(be, "België", false, new[] { "be", "nl" }, false, d).Plan);
        Assert.Equal(ChangeAction.GraphUpdateCaLocation, countries.ChangeAction);
        Assert.Equal(new[] { "BE", "NL" }, Json(countries)["countriesAndRegions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("Buitenland blokkeren", countries.Warning);
        Assert.Equal(new[] { "BE" }, JsonNode.Parse(countries.Previous!["json"])!["countriesAndRegions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("geen landcode", CaPlans.Location(be, "België", false, new[] { "Belgie" }, false, d).Errors[0]);
        Assert.Contains("kan geen locatie met landen", CaPlans.Location(be, "België", true, new[] { "203.0.113.0/24" }, false, d).Errors[0]);
        Assert.Contains("bestaat al", CaPlans.Location(null, "gemeentehuis", true, new[] { "203.0.113.0/24" }, true, d).Errors[0]);
    }

    // ---------------------------------------------------------------- JSON

    [Fact]
    public void JsonForGraphWithoutWhatGraphKeepsItself()
    {
        const string read = """
        {"@odata.context":"x","id":"p1","templateId":null,"displayName":"Test","createdDateTime":"2026-01-01T00:00:00Z","modifiedDateTime":"2026-02-01T00:00:00Z",
         "state":"enabled","conditions":{"users":{"includeUsers":["All"],"excludeUsers":["e-info"],"includeGroups":[],"excludeGroups":[],"includeRoles":[],"excludeRoles":[]},
         "applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
         "grantControls":{"operator":"OR","builtInControls":[],"authenticationStrength":{"id":"00000000-0000-0000-0000-000000000004","displayName":"Phishing","allowedCombinations":["fido2"],"createdDateTime":"2020-01-01T00:00:00Z"}}}
        """;
        var writable = JsonNode.Parse(CaJson.Writable(read))!.AsObject();
        foreach (var field in new[] { "@odata.context", "id", "templateId", "createdDateTime", "modifiedDateTime" })
        {
            Assert.False(writable.ContainsKey(field), field);
        }

        Assert.Equal("{\"id\":\"00000000-0000-0000-0000-000000000004\"}", writable["grantControls"]!["authenticationStrength"]!.ToJsonString());

        using var doc = JsonDocument.Parse(read);
        var b = CaJson.Read(doc.RootElement);
        Assert.Equal(read, b.Json);
        var after = CaJson.AfterPatch(b, CaJson.ExclusionsPatch(b, new[] { "e-info", "e-cas" }));
        Assert.Equal(new[] { "e-info", "e-cas" }, after.UsersOff);
        Assert.Equal(new[] { "All" }, after.UsersIn);
        Assert.Equal(CaStatus.On, after.Status);
        Assert.Equal("{\"state\":\"enabled\"}", CaJson.Previous(read, CaJson.StatusPatch(CaStatus.Off)));
    }

    // ---------------------------------------------------------------- back-ups

    [Fact]
    public void BackupOnlyOnDifferenceAndRestore()
    {
        var d = Data();
        var ca = d.ConditionalAccess!;
        var first = CaBackup.Save(ca, _folder, Now);
        Assert.NotNull(first);
        Assert.Null(CaBackup.Save(ca, _folder, Now.AddHours(1))); // nothing changed

        // Someone turns off "Block abroad" and deletes "Block legacy"
        var country = WithJson(B(d, "ca-land") with { Status = CaStatus.Off });
        var now = ca with { Policies = ca.Policies.Where(b => b.Id != "ca-legacy").Select(b => b.Id == "ca-land" ? country : b).ToList() };
        Assert.NotNull(CaBackup.Save(now, _folder, Now.AddDays(1)));

        var list = CaBackup.Items(_folder);
        Assert.Equal(2, list.Count);
        Assert.Equal(Now.AddDays(1), list[0].Time);
        var old = CaBackup.Read(list[1].FilePath);
        Assert.Equal(4, old.Policies.Count);
        Assert.Equal(2, old.Locations.Count);

        var differences = CaComparison.Compare(old.Policies, now.Policies, CaReader.Names(d), now);
        Assert.Equal(2, differences.Count);
        Assert.Contains(differences, v => v.Kind == "verdwenen" && v.Name == "Verouderd blokkeren");
        Assert.Contains(differences, v => v.Kind == "gewijzigd" && v.Details.Any(x => x.Equals("status: aan → uit", StringComparison.OrdinalIgnoreCase)));

        // Restore: a changed policy with the full JSON from the backup (without what Graph keeps itself), a vanished
        // one created again
        var dNow = d with { ConditionalAccess = now };
        var oldCountry = old.Policies.Single(b => b.Id == "ca-land");
        // The emergency account falls under "Block abroad" (on): the app refuses to restore it to "on"
        Assert.Contains("noodaccount", CaPlans.Restore(oldCountry, B(dNow, "ca-land"), dNow, WithEmergency(), Me).Errors[0]);
        var w = Assert.Single(CaPlans.Restore(oldCountry with { Status = CaStatus.Report, Json = oldCountry.Json.Replace("\"enabled\"", "\"enabledForReportingButNotEnforced\"") },
            B(dNow, "ca-land"), dNow, WithEmergency(), Me).Plan);
        Assert.Equal(ChangeAction.GraphUpdateCaPolicy, w.ChangeAction);
        Assert.False(Json(w).AsObject().ContainsKey("id"));
        Assert.Equal("disabled", JsonNode.Parse(w.Previous!["json"])!["state"]!.GetValue<string>());

        var legacy = Assert.Single(CaPlans.Restore(old.Policies.Single(b => b.Id == "ca-legacy"), null, dNow, WithEmergency(), Me).Plan);
        Assert.Equal(ChangeAction.GraphCreateCaPolicy, legacy.ChangeAction);
        Assert.Equal("Verouderd blokkeren", Json(legacy)["displayName"]!.GetValue<string>());

        // A policy without JSON (not from Graph): no backup that would later lose something
        Assert.Null(CaBackup.Save(TestData.Ca(new DateTimeOffset(Now)), Path.Combine(_folder, "ander"), Now));
    }

    // ---------------------------------------------------------------- refreshing and Graph

    [Fact]
    public void AfterAConditionalAccessChangeCaIsReadAgain()
    {
        var d = Data();
        var w = Assert.Single(CaPlans.Status(B(d, "ca-land"), CaStatus.Off, d, WithEmergency(), Me).Plan);
        var targets = RefreshTargets.AfterPlan(d, new[] { w }, new[] { ChangeResult.Ok(w) });
        Assert.True(targets.ConditionalAccess);
        Assert.Empty(targets.EntraUsers);
        Assert.False(targets.Empty);
        Assert.Contains(Permissions(), r => r == Moss365.Core.Permissions.Permission.Manage);

        static IEnumerable<Moss365.Core.Permissions.Permission> Permissions()
            => new[] { ChangeAction.GraphCreateCaPolicy, ChangeAction.GraphUpdateCaPolicy, ChangeAction.GraphCreateCaLocation, ChangeAction.GraphUpdateCaLocation }
                .Select(Moss365.Core.Permissions.PermissionTable.ForAction).Distinct();
    }

    private static GraphConnection Graph(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

    [Fact]
    public async Task GraphCreatesAndUpdatesPoliciesAndLocations()
    {
        var d = Data();
        var newValue = Assert.Single(CaTemplates.Plan("mfa-iedereen", new CaTemplateOptions(), d, WithEmergency(), Me).Plan);
        var status = Assert.Single(CaPlans.Status(B(d, "ca-land"), CaStatus.Off, d, WithEmergency(), Me).Plan);
        var location = Assert.Single(CaPlans.Location(d.ConditionalAccess!.Location("loc-kantoor"), "Gemeentehuis", true, new[] { "203.0.113.0/25" }, true, d).Plan);
        var fake = new FakeGraph()
            .Op(HttpMethod.Post, "/identity/conditionalAccess/policies", new { id = "nieuw-id" }, HttpStatusCode.Created)
            .Op(HttpMethod.Patch, "/identity/conditionalAccess/policies/ca-land", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Patch, "/identity/conditionalAccess/namedLocations/loc-kantoor", "", HttpStatusCode.NoContent);

        var r = await Graph(fake).ExecuteAsync(new[] { newValue, status, location }, CancellationToken.None);
        Assert.All(r, x => Assert.Equal(ResultStatus.Succeeded, x.Status));
        Assert.Contains("nieuw-id", r[0].Message);
        var post = fake.Requests.Single(v => v.Method == HttpMethod.Post);
        Assert.Equal("enabledForReportingButNotEnforced", JsonNode.Parse(post.Content!)!["state"]!.GetValue<string>());
        Assert.Equal("{\"state\":\"disabled\"}", fake.Requests.Single(v => v.Url.EndsWith("/policies/ca-land", StringComparison.Ordinal)).Content);
        Assert.Contains("203.0.113.0/25", fake.Requests.Single(v => v.Url.Contains("namedLocations", StringComparison.Ordinal)).Content);
    }

    [Fact]
    public async Task GraphReadsThePoliciesWithTheirJsonAndTheAuditLog()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/identity/conditionalAccess/policies", new { value = new object[] { new { id = "p1", displayName = "Test", state = "disabled", conditions = new { users = new { includeUsers = new[] { "All" } } } } } })
            .Op(HttpMethod.Get, "/auditLogs/directoryAudits", new
            {
                value = new object[]
                {
                    new
                    {
                        activityDateTime = "2026-11-04T10:00:00Z", activityDisplayName = "Update conditional access policy", result = "success",
                        initiatedBy = new { user = new { userPrincipalName = Me } },
                        targetResources = new[] { new { id = "p1", displayName = "Test" } },
                    },
                },
            });
        var ca = await Graph(fake).ReadConditionalAccessAsync(null, CancellationToken.None);
        Assert.Contains("\"id\":\"p1\"", ca!.Policies[0].Json);

        var audit = Assert.Single(await Graph(fake).ReadCaAuditAsync(30, CancellationToken.None));
        Assert.Equal(Me, audit.Who);
        Assert.Equal("Test", audit.TargetName);
        Assert.Contains("loggedByService", Uri.UnescapeDataString(fake.Requests.Last().Url));

        var noPermission = new FakeGraph().Op(HttpMethod.Get, "/auditLogs/directoryAudits", "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"nee\"}}", HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<NoPermissionError>(() => Graph(noPermission).ReadCaAuditAsync(30, CancellationToken.None));
    }
}
