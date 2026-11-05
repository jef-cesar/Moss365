using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.Core.Permissions;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

/// <summary>Core for 0.8: profiles (roles, permission table, the engine refuses).</summary>
public sealed class Version08Tests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-v08-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private static Change W(ChangeAction action, string target = "CN=x,DC=test,DC=internal", Dictionary<string, string>? p = null) => new()
    {
        TargetSystem = action.ToString().StartsWith("Ad", StringComparison.Ordinal) ? TargetSystem.ActiveDirectory
            : action.ToString().StartsWith("Graph", StringComparison.Ordinal) ? TargetSystem.Graph : TargetSystem.Exchange,
        ChangeAction = action,
        Target = target,
        Description = action.ToString(),
        Parameters = p ?? new(StringComparer.OrdinalIgnoreCase),
    };

    public static IEnumerable<object[]> AllActions() => Enum.GetValues<ChangeAction>().Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(AllActions))]
    public void EveryActionHasAMinimumRole(ChangeAction action)
    {
        // Reader and Auditor never change anything; Administrator may do everything; AppAdmin only the settings of the
        // app
        Assert.NotNull(PermissionTable.Refusal(Role.Reader, W(action)));
        Assert.NotNull(PermissionTable.Refusal(Role.Auditor, W(action)));
        Assert.NotNull(PermissionTable.Refusal(Role.AppAdmin, W(action)));
        Assert.Null(PermissionTable.Refusal(Role.Administrator, W(action)));

        var helpdesk = action is ChangeAction.AdSetPassword or ChangeAction.AdUnlockAccount or ChangeAction.AdEnableAccount
            or ChangeAction.AdAddGroupMember or ChangeAction.AdRemoveGroupMember or ChangeAction.GraphAddGroupMember or ChangeAction.GraphRemoveGroupMember
            or ChangeAction.ExchangeAddGroupMember or ChangeAction.ExchangeRemoveGroupMember or ChangeAction.GraphInviteGuest;
        Assert.Equal(helpdesk, PermissionTable.Refusal(Role.Helpdesk, W(action)) is null);
    }

    [Fact]
    public void PrivilegedAlwaysNeedsAdministrator()
    {
        var d = TestData.AppEnvironment();
        var admin = d.AdUsers.Single(u => u.SamAccountName == "an.peeters"); // member of Domain Admins
        var plain = d.AdUsers.Single(u => u.SamAccountName == "bo.janssens");

        Assert.Contains("an.peeters", PermissionTable.Refusal(Role.Helpdesk, W(ChangeAction.AdSetPassword, admin.DistinguishedName), d));
        Assert.Null(PermissionTable.Refusal(Role.Helpdesk, W(ChangeAction.AdSetPassword, plain.DistinguishedName), d));
        Assert.Null(PermissionTable.Refusal(Role.Administrator, W(ChangeAction.AdSetPassword, admin.DistinguishedName), d));

        var da = d.AdGroupsByDn.Values.Single(g => g.Name == "Domain Admins");
        var member = W(ChangeAction.AdAddGroupMember, plain.DistinguishedName, new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = da.DistinguishedName, ["groep"] = da.Name });
        Assert.Contains("Domain Admins", PermissionTable.Refusal(Role.Helpdesk, member, d));
        Assert.Null(PermissionTable.Refusal(Role.Administrator, member, d));
    }

    [Fact]
    public void RolesFromTheSignInAndTheTransition()
    {
        Assert.Equal(Role.Helpdesk | Role.Reader, PermissionTable.Determine(new[] { "Moss365.Helpdesk", "Moss365.Lezer", "iets.anders" }, requiredValue: true));
        Assert.Equal(Role.Administrator | Role.AppAdmin, PermissionTable.Determine(new[] { "Moss365.Administrator", "Moss365.AppAdmin" }, requiredValue: true));
        Assert.Equal(Role.Everything, PermissionTable.Determine(null, requiredValue: false));
        Assert.Equal(Role.Everything, PermissionTable.Determine(Array.Empty<string>(), requiredValue: false));
        Assert.Equal(Role.Reader, PermissionTable.Determine(null, requiredValue: true));
        Assert.Equal(Role.Reader, PermissionTable.Determine(new[] { "iets.anders" }, requiredValue: true));
        Assert.Equal("Helpdesk of Administrator", PermissionTable.Text(PermissionTable.RolesFor(Permission.Helpdesk)));
        Assert.Equal(5, PermissionTable.AppRoles.Select(r => r.Id).Distinct().Count());
        Assert.True(PermissionTable.Can(Role.Auditor, Permission.ChangeLogAll));
        Assert.False(PermissionTable.Can(Role.Helpdesk, Permission.ChangeLogAll));
        Assert.False(PermissionTable.Can(Role.Administrator, Permission.Settings));
        Assert.True(PermissionTable.Can(Role.AppAdmin, Permission.Settings));
    }

    private sealed class Counter(TargetSystem system) : IExecutor
    {
        public List<Change> Done { get; } = new();
        public TargetSystem TargetSystem => system;

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
        {
            Done.AddRange(changes);
            return Task.FromResult<IReadOnlyList<ChangeResult>>(changes.Select(w => ChangeResult.Ok(w)).ToList());
        }
    }

    [Fact]
    public async Task TheEngineRefusesWhatTheProfileDoesNotAllowAndLogsTheAttempt()
    {
        var ad = new Counter(TargetSystem.ActiveDirectory);
        var changeLog = new JsonlChangeLog(_folder);
        var plan = new[] { W(ChangeAction.AdUnlockAccount), W(ChangeAction.AdDisableAccount) };
        var executor = new PlanExecutor(new[] { ad }, changeLog, () => "helpdesk@test.be", w => PermissionTable.Refusal(Role.Helpdesk, w));

        var r = await executor.ExecuteAsync(plan, "test");
        Assert.Equal(ResultStatus.Succeeded, r[0].Status);
        Assert.Equal(ResultStatus.Failed, r[1].Status);
        Assert.StartsWith("Geweigerd:", r[1].Message);
        Assert.Equal(new[] { ChangeAction.AdUnlockAccount }, ad.Done.Select(w => w.ChangeAction));
        Assert.Contains(changeLog.Recent(10), l => l.ChangeAction == nameof(ChangeAction.AdDisableAccount) && l.Status == nameof(ResultStatus.Failed) && l.Message.Contains("Geweigerd"));

        // A reader runs nothing, not even through a detour
        var reader = new PlanExecutor(new[] { ad }, changeLog, () => "lezer@test.be", w => PermissionTable.Refusal(Role.Reader, w));
        ad.Done.Clear();
        await reader.ExecuteAsync(Enum.GetValues<ChangeAction>().Where(a => a.ToString().StartsWith("Ad", StringComparison.Ordinal)).Select(a => W(a)).ToList(), "omweg");
        Assert.Empty(ad.Done);
    }

    // ---------------------------------------------------------------- editing data (#14)

    [Fact]
    public void ASynchronisedAccountChangesInAdWithThePreviousValues()
    {
        var d = TestData.AppEnvironment();
        var an = d.SearchPerson("ad:an.peeters")!;
        var desired = UserDesired.Of(an);
        desired.JobTitle = "Coordinator";
        desired.Office = "bibliotheek";
        desired.Department = "GEZINSZORG "; // spelled as in the organisation

        var u = AttributePlans.User(d, TestData.Config(), an, desired);
        var w = u.Plan[0];
        Assert.Equal(ChangeAction.AdSetAttributes, w.ChangeAction);
        Assert.Equal(an.Ad!.DistinguishedName, w.Target);
        Assert.Equal("Coordinator", w.P("title"));
        Assert.Equal("bibliotheek", w.P("physicalDeliveryOfficeName"));
        Assert.Equal("gezinszorg", w.P("department"));
        Assert.Equal("cultuur", w.Previous!["department"]);
        Assert.Contains("synchronisatie", w.Description);
        Assert.DoesNotContain("displayName", w.Parameters.Keys); // not changed: not in the plan

        // Another department = another division: the division group AFD_VRIJETIJD comes along in the plan (or the
        // reason why not)
        Assert.True(u.Plan.Skip(1).Any(x => x.P("groep") == "AFD_VRIJETIJD") || u.Errors.Any(f => f.Contains("AFD_VRIJETIJD")));

        // Undo puts the previous values back
        var back = Undo.Plan(new[] { new LogEntry(DateTimeOffset.Now, "t", "c", "AD", w.ChangeAction.ToString(), w.Target, w.TargetName, w.Description,
            w.Parameters, "Gelukt", "", Guid.NewGuid(), w.Id, w.Previous) });
        Assert.Equal("cultuur", Assert.Single(back.Plan).P("department"));
    }

    [Fact]
    public void ACloudAccountChangesViaGraph()
    {
        var d = TestData.AppEnvironment();
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        var u = AttributePlans.User(d, TestData.Config(), info, new UserDesired { JobTitle = "Algemeen adres", Description = "kan niet" });
        var w = Assert.Single(u.Plan);
        Assert.Equal(ChangeAction.GraphSetAttributes, w.ChangeAction);
        Assert.Equal("e-info", w.Target);
        Assert.Equal("Algemeen adres", w.P("jobTitle"));
        Assert.Contains("enkel in AD", w.Warning);

        Assert.Contains("niets gewijzigd", AttributePlans.User(d, TestData.Config(), info, UserDesired.Of(info)).Errors[0]);
    }

    [Fact]
    public void FixADifferenceInAd()
    {
        var d = TestData.AppEnvironment();
        var bo = d.SearchPerson("ad:bo.janssens")!; // department "cultuur " with a space
        var v = new Moss365.Core.Analysis.Difference(bo, Moss365.Core.Analysis.DifferenceKind.AttributeDiffers, "Dienst (Department)", "cultuur ", "cultuur", "");
        var w = Assert.Single(AttributePlans.Fix(d, TestData.Config(), v).Plan);
        Assert.Equal("cultuur", w.P("department"));
        Assert.Equal("cultuur ", w.Previous!["department"]);
    }

    // ---------------------------------------------------------------- new group (#13)

    private static Moss365.Core.Configuration.OrganisationConfig ConfigWithGroupsOu()
    {
        var c = TestData.Config();
        c.GroupsOu = "GEMEENTEBESTUUR/GROEPEN";
        return c;
    }

    [Fact]
    public void NewAdGroupWithManagerAndMembers()
    {
        var d = TestData.AppEnvironment();
        var u = NewGroupPlans.Plan(d, ConfigWithGroupsOu(), new NewGroupRequest
        {
            Kind = NewGroupKind.AdSecurity, Name = "gg_test_map", Description = "Rechten op de testmap",
            Administrator = "ad:an.peeters", Members = new() { "ad:bo.janssens", "ad:cas.vos" },
        });
        Assert.Empty(u.Errors);
        var create = u.Plan[0];
        Assert.Equal(ChangeAction.AdCreateGroup, create.ChangeAction);
        Assert.StartsWith("CN=gg_test_map,OU=GROEPEN,OU=GEMEENTEBESTUUR,", create.Target);
        Assert.Equal(NewGroupPlans.GlobalSecurity.ToString(System.Globalization.CultureInfo.InvariantCulture), create.P("groupType"));
        Assert.Equal(d.SearchPerson("ad:an.peeters")!.Ad!.DistinguishedName, create.P("managedBy"));
        Assert.Null(create.Warning);
        Assert.Equal(2, u.Plan.Count(w => w.ChangeAction == ChangeAction.AdAddGroupMember && w.DependentOf == create.Id && w.P("groepDn") == create.Target));
        Assert.Contains("Administrator", PermissionTable.Refusal(Role.Helpdesk, create));
    }

    [Fact]
    public void NewGroupIsChecked()
    {
        var d = TestData.AppEnvironment();
        var c = ConfigWithGroupsOu();
        NewGroupRequest A(string name, NewGroupKind kind = NewGroupKind.AdSecurity, string mail = "") => new() { Name = name, Kind = kind, Mail = mail };

        Assert.Contains("bestaat al", NewGroupPlans.Plan(d, c, A("gg_wifiIntern")).Errors[0]);
        Assert.Contains("bevoorrechte", NewGroupPlans.Plan(d, c, A("Test Admins")).Errors[0]);
        Assert.Contains("dynamische", NewGroupPlans.Plan(d, c, A("Alle gebruikers", NewGroupKind.CloudSecurity)).Errors.Last());
        Assert.Contains("al in gebruik", NewGroupPlans.Plan(d, c, A("WKG_Info", NewGroupKind.Microsoft365, "gemeentehuis@test.be")).Errors[0]);
        Assert.Contains("OU voor nieuwe groepen", NewGroupPlans.Plan(d, TestData.Config(), A("gg_nieuw")).Errors[0]);
        Assert.Contains("naamgeving", NewGroupPlans.Plan(d, c, A("Projectgroep")).Plan[0].Warning);
    }

    [Fact]
    public void NewCloudGroupAndMailGroup()
    {
        var d = TestData.AppEnvironment();
        var m365 = Assert.Single(NewGroupPlans.Plan(d, TestData.Config(), new NewGroupRequest
        {
            Kind = NewGroupKind.Microsoft365, Name = "WKG_Feest", Mail = "feest@test.be", Administrator = "ad:an.peeters", Members = new() { "ad:bo.janssens" },
        }).Plan);
        Assert.Equal(ChangeAction.GraphCreateGroup, m365.ChangeAction);
        Assert.Equal("M365", m365.P("soort"));
        Assert.Equal("feest", m365.P("mailNickname"));
        Assert.Equal("e-an", m365.P("eigenaar"));
        Assert.Equal("e-bo", m365.P("leden"));

        var dl = NewGroupPlans.Plan(d, TestData.Config(), new NewGroupRequest
        {
            Kind = NewGroupKind.Distribution, Name = "Personeelsfeest", Mail = "personeelsfeest@test.be", Members = new() { "ad:cas.vos" },
        }).Plan;
        Assert.Equal("Distribution", dl[0].P("Type"));
        Assert.Equal(ChangeAction.ExchangeAddGroupMember, dl[1].ChangeAction);
        Assert.Equal("personeelsfeest@test.be", dl[1].P("groep"));
        Assert.Equal(dl[0].Id, dl[1].DependentOf);
    }

    // ---------------------------------------------------------------- tasks for a selection (#10)

    [Fact]
    public void SelectedUsersIntoAGroupAndSetAnAttribute()
    {
        var d = TestData.AppEnvironment();
        var persons = new[] { "ad:an.peeters", "ad:bo.janssens", "ad:cas.vos" }.Select(k => d.SearchPerson(k)!).ToList();
        var groupValue = d.Index.Groups.Single(g => g.Name == "gg_eigen");

        var u = BulkPlans.UsersInGroup(d, persons, groupValue, add: true);
        Assert.Equal(2, u.Plan.Count); // bo is already a member
        Assert.Contains(u.Errors, f => f.Contains("al lid"));
        Assert.All(u.Plan, w => Assert.Equal(ChangeAction.AdAddGroupMember, w.ChangeAction));

        var k = BulkPlans.Attribute(d, TestData.Config(), persons, "Office", "bibliotheek");
        Assert.Equal(3, k.Plan.Count(w => w.ChangeAction == ChangeAction.AdSetAttributes && w.P("physicalDeliveryOfficeName") == "bibliotheek"));
    }

    [Fact]
    public void AccessToTheSelectedMailboxesViaTheAccessGroup()
    {
        var d = TestData.AppEnvironment();
        var info = d.Mailboxes.Single(p => p.Smtp == "info@test.be");
        var bo = d.SearchPerson("ad:bo.janssens")!;
        var u = BulkPlans.AccessToMailboxes(d, d.Mailboxes.Where(p => p.IsShared), bo);
        Assert.Contains(u.Plan, w => w.P("groepNaam").StartsWith("SG-SM-info", StringComparison.OrdinalIgnoreCase) || w.P("groep").StartsWith("SG-SM-info", StringComparison.OrdinalIgnoreCase));

        var hide = BulkPlans.Hide(new[] { info }, !info.Hidden);
        Assert.Single(hide.Plan);
        Assert.Contains("al", BulkPlans.Hide(new[] { info }, info.Hidden).Errors[0]);
    }

    // ---------------------------------------------------------------- database (#17)

    [Fact]
    public void TheOldFilesAreMigratedOnce()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "logboek"));
        var old = new JsonlChangeLog(Path.Combine(_folder, "logboek"));
        old.Write(new LogEntry(DateTimeOffset.Now.AddDays(-40), "a@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "eerste", "o", new Dictionary<string, string>(), "Gelukt", ""));
        old.Write(new LogEntry(DateTimeOffset.Now.AddDays(-1), "a@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "tweede", "o", new Dictionary<string, string>(), "Gelukt", ""));
        File.WriteAllText(Path.Combine(_folder, "gepland.json"), "[]");
        File.WriteAllText(Path.Combine(_folder, "opvolging.json"), "[]");
        File.WriteAllText(Path.Combine(_folder, "weergave.json"), "{\"Tabellen\":{\"groepen\":{\"Sorteer\":\"soort\"}}}");
        File.WriteAllText(Path.Combine(_folder, "verbinding.json"), "{\"AdServer\":\"dc.test.internal\"}");

        var db = new Database(Path.Combine(_folder, Database.FileName));
        var messages = Migrate.OfFiles(db, _folder);
        Assert.Contains(messages, m => m.Contains("2 regels"));
        Assert.Equal(new[] { "tweede", "eerste" }, db.ReadLog(10).Select(r => r.TargetName));
        Assert.Contains("dc.test.internal", db.ReadDocument("verbinding"));
        Assert.Contains("soort", db.ReadDocument("weergave"));
        Assert.True(File.Exists(Path.Combine(_folder, "gepland.json.bak")));
        Assert.False(File.Exists(Path.Combine(_folder, "gepland.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "logboek"), "*.jsonl"));

        // Start again: nothing twice
        Assert.Empty(Migrate.OfFiles(new Database(db.FilePath), _folder));
        Assert.Equal(2, db.CountLog());
    }

    [Fact]
    public void TheSchemaHasVersionsAndIsUpgraded()
    {
        var path = Path.Combine(_folder, "oud.db");
        Directory.CreateDirectory(_folder);
        // An empty database from "before" (version 0)
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
        }

        var db = new Database(path);
        Assert.Equal(Database.Migrations.Count, db.Version);
        db.SaveDocument("x", "1");
        db.SaveDocument("x", "2");
        Assert.Equal("2", db.ReadDocument("x"));

        // Open once more: nothing again, data stays
        Assert.Equal(Database.Migrations.Count, new Database(path).Version);
        Assert.Equal("2", new Database(path).ReadDocument("x"));

        db.WriteAccess(new AccessEntry(DateTimeOffset.Now, "a@test.be", "Export", "Gebruikers: 12 rijen"));
        Assert.Equal("Export", Assert.Single(db.ReadAccess(10)).What);
    }

    [Fact]
    public async Task TheChangeLogInTheDatabaseIsAppendOnly()
    {
        var db = new Database(Path.Combine(_folder, "log.db"));
        var log = new DatabaseChangeLog(db);
        var plan = new[] { W(ChangeAction.AdUnlockAccount) };
        plan[0].Secret = "Nooit-In-Het-Logboek-1";
        await new PlanExecutor(new[] { new Counter(TargetSystem.ActiveDirectory) }, log, () => "t").ExecuteAsync(plan, "ctx");
        Assert.Single(log.Recent(10));
        Assert.DoesNotContain("Nooit-In-Het-Logboek-1", db.ReadLog(10).Single().ToString());
        Assert.Equal(1, db.CountLog(DateTimeOffset.Now.AddMinutes(1)));
        Assert.Equal(0, db.CountLog(DateTimeOffset.Now.AddDays(-1)));
    }
}
