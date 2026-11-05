using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

public class NameRulesTests
{
    [Theory]
    [InlineData("Jan", "jan")]
    [InlineData("Van den Broeck", "vandenbroeck")]
    [InlineData("D'Hondt", "dhondt")]
    [InlineData("Beliën", "belien")]
    [InlineData("  Anne-Marie ", "anne-marie")]
    [InlineData("", "")]
    public void Normalise(string name, string expected) => Assert.Equal(expected, NameRules.Normalise(name));

    [Fact]
    public void LocalPartAndSam()
    {
        Assert.Equal("jan.peeters", NameRules.LocalPart("Jan", "Peeters"));
        Assert.Equal("anne-marie.vandenbroeck", NameRules.LocalPart("Anne-Marie", "Van den Broeck"));
        Assert.Equal("anne-marie.vandenbro", NameRules.Sam("anne-marie.vandenbroeck"));
        Assert.Equal(20, NameRules.Sam("anne-marie.vandenbroeck").Length);
        Assert.Equal("jan.peeters2", NameRules.SamWithNumber("jan.peeters", 2));
        Assert.Equal("anne-marie.vandenbr2", NameRules.SamWithNumber("anne-marie.vandenbroeck", 2));
    }

    [Fact]
    public void InitialPasswordIsStrongAndAlwaysDifferent()
    {
        var a = NameRules.InitialPassword();
        var b = NameRules.InitialPassword();
        Assert.Equal(16, a.Length);
        Assert.NotEqual(a, b);
        Assert.Contains(a, char.IsUpper);
        Assert.Contains(a, char.IsLower);
        Assert.Contains(a, char.IsDigit);
        Assert.Contains(a, c => !char.IsLetterOrDigit(c));
        Assert.DoesNotContain(a, c => "0O1lI".Contains(c));
    }
}

public class MirrorPlanTests
{
    [Fact]
    public void AdditionsTakeTheRightRoute()
    {
        var data = TestData.AppEnvironment();
        var an = data.SearchPerson("ad:an.peeters")!;
        var bo = data.SearchPerson("ad:bo.janssens")!;
        var rules = MirrorUser.Compute(data, TestData.Config(), an, bo);
        var plan = PlanBuilder.Mirror(data, rules, bo);

        var ad = Assert.Single(plan, w => w.ChangeAction == ChangeAction.AdAddGroupMember);
        Assert.Equal(bo.Ad!.DistinguishedName, ad.Target);
        Assert.Equal("gg_rww_foxbeleid", ad.P("groep"));
        Assert.EndsWith(TestData.Groups, ad.P("groepDn"));

        var exchange = plan.Where(w => w.ChangeAction == ChangeAction.ExchangeAddGroupMember).ToList();
        Assert.Equal(2, exchange.Count);
        Assert.Contains(exchange, w => w.P("groep") == "sg-sm-info-full@test.be" && w.Target == "bo.janssens@test.be");
        Assert.Contains(exchange, w => w.P("groep") == "gemeentehuis@test.be");

        Assert.DoesNotContain(plan, w => w.P("groep") is "Domain Admins" or "Alle gebruikers" or "DNST_CULTUUR");
    }

    [Fact]
    public void SynchronisedGroupBecomesTheAdGroupBehindItWithoutDuplicate()
    {
        var data = TestData.AppEnvironment();
        var an = data.SearchPerson("ad:an.peeters")!;
        var cas = data.SearchPerson("ad:cas.vos")!;
        var plan = PlanBuilder.Mirror(data, MirrorUser.Compute(data, TestData.Config(), an, cas), cas);

        // gg_rww_foxbeleid comes both from the AD side and via the synchronised cloud group: only once in the plan
        Assert.Single(plan, w => w.ChangeAction == ChangeAction.AdAddGroupMember && w.P("groep") == "gg_rww_foxbeleid");
        Assert.Contains(plan, w => w.ChangeAction == ChangeAction.AdAddGroupMember && w.P("groep") == "gg_wifiIntern");
        Assert.Contains(plan, w => w.ChangeAction == ChangeAction.GraphAddGroupMember && w.P("groepId") == "g-team" && w.Target == "e-cas");
    }
}

public class RoomPlanTests
{
    [Fact]
    public void OnlyWhatDiffers()
    {
        var data = TestData.AppEnvironment();
        var a = data.Rooms.Single(r => r.Smtp == "rm-vz-a@test.be");
        var plan = PlanBuilder.Room(a, RoomDesired.FromDefault(new RoomDefault()));

        var processing = Assert.Single(plan, w => w.ChangeAction == ChangeAction.ExchangeRoomCalendarProcessing);
        Assert.Equal(new[] { "BookingWindowInDays", "DeleteSubject" }, processing.Parameters.Keys.OrderBy(k => k));
        Assert.Equal("180", processing.P("BookingWindowInDays"));
        Assert.Equal("False", processing.P("DeleteSubject"));

        var permission = Assert.Single(plan, w => w.ChangeAction == ChangeAction.ExchangeCalendarPermission);
        Assert.Equal("LimitedDetails", permission.CurrentValue);
        Assert.Equal("Reviewer", permission.P("AccessRights"));
        Assert.DoesNotContain(plan, w => w.ChangeAction == ChangeAction.ExchangeRoomPlace);
    }

    [Fact]
    public void RoomAlreadyCorrectGivesNoPlan()
    {
        var b = TestData.AppEnvironment().Rooms.Single(r => r.Smtp == "rm-vz-b@test.be");
        Assert.Empty(PlanBuilder.Room(b, RoomDesired.FromDefault(new RoomDefault())));
        Assert.Empty(PlanBuilder.RoomDeviations(b, new RoomDefault()));
    }

    [Fact]
    public void ChangePlace()
    {
        var b = TestData.AppEnvironment().Rooms.Single(r => r.Smtp == "rm-vz-b@test.be");
        var w = Assert.Single(PlanBuilder.Room(b, new RoomDesired { Floor = "1", Capacity = 8, Building = "Gemeentehuis" }));
        Assert.Equal(ChangeAction.ExchangeRoomPlace, w.ChangeAction);
        Assert.Equal("1", Assert.Single(w.Parameters).Value);
    }

    [Fact]
    public void ShowDeviations()
    {
        var a = TestData.AppEnvironment().Rooms.Single(r => r.Smtp == "rm-vz-a@test.be");
        var deviations = PlanBuilder.RoomDeviations(a, new RoomDefault());
        Assert.Contains(deviations, x => x.StartsWith("Boekingsvenster: 600"));
        Assert.Contains(deviations, x => x.StartsWith("Standaardrecht agenda: LimitedDetails"));
        Assert.Equal(3, deviations.Count);
    }
}

public class NewUserPlanTests
{
    private static NewUserRequest Request(string givenName = "Eva", string surname = "Smet") => new()
    {
        Population = "Gemeente",
        GivenName = givenName,
        Surname = surname,
        Department = "Cultuur",
        Office = "gemeentehuis",
        JobTitle = "Medewerker",
        LicenceSku = "DESKLESSPACK",
    };

    [Fact]
    public void NamesOuAndAttributes()
    {
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), TestData.Config(), Request(), "Geheim!123");
        Assert.True(v.CanBeExecuted, string.Join("; ", v.Errors));
        Assert.Equal("eva.smet", v.Sam);
        Assert.Equal("eva.smet@test.be", v.Upn);
        Assert.Equal("CN=Eva Smet,OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=TEST,DC=INTERNAL", v.Dn);

        var create = v.Now[0];
        Assert.Equal(ChangeAction.AdCreateUser, create.ChangeAction);
        Assert.Equal("Geheim!123", create.Secret);
        Assert.Equal("cultuur", create.P("department"));
        Assert.Equal("Contoso", create.P("company"));
        Assert.Equal("gemeentehuis", create.P("physicalDeliveryOfficeName"));
        Assert.Equal("cultuur.bat", create.P("scriptPath"));
        Assert.Equal("OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=TEST,DC=INTERNAL", create.P("ou"));
        Assert.False(create.Parameters.ContainsKey("l"));
        Assert.DoesNotContain(create.Parameters.Values, p => p.Contains("Geheim"));
    }

    [Fact]
    public void DefaultGroupsOnlyIfTheyExistAndDependOnCreate()
    {
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), TestData.Config(), Request());
        var groupValue = Assert.Single(v.Now, w => w.ChangeAction == ChangeAction.AdAddGroupMember);
        Assert.Equal("gg_wifiIntern", groupValue.P("groep"));
        Assert.Equal(v.Now[0].Id, groupValue.DependentOf);
        Assert.Contains(v.Warnings, w => w.Contains("bestaat_niet"));
    }

    [Fact]
    public void AfterSynchronisationLicenceAndDivisionGroup()
    {
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), TestData.Config(), Request());
        Assert.Equal(new[] { ChangeAction.GraphSetAttributes, ChangeAction.GraphAssignLicence, ChangeAction.GraphAddGroupMember }, v.AfterSynchronisation.Select(w => w.ChangeAction));
        Assert.All(v.AfterSynchronisation, w => Assert.Equal("eva.smet@test.be", w.Target));
        Assert.Equal("sku-f3", v.AfterSynchronisation[1].P("skuId"));
        Assert.Equal("g-afd", v.AfterSynchronisation[2].P("groepId"));
    }

    [Fact]
    public void FullLicencePoolGivesAWarning()
    {
        var a = Request();
        a.LicenceSku = "SPB";
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), TestData.Config(), a);
        Assert.Contains(v.Warnings, w => w.Contains("geen vrije SPB"));
    }

    [Fact]
    public void ExistingNameGetsASequenceNumber()
    {
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), TestData.Config(), Request("An", "Peeters"));
        Assert.Equal("an.peeters2", v.Sam);
        Assert.Equal("an.peeters2@test.be", v.Upn);
        Assert.Contains(v.Warnings, w => w.Contains("bestaat al"));
    }

    [Fact]
    public void MirrorGroupsWithoutPrivilegedAndDynamic()
    {
        var data = TestData.AppEnvironment();
        var a = Request();
        a.Mirror = data.SearchPerson("ad:an.peeters");
        var v = PlanBuilder.NewUser(data, TestData.Config(), a);

        var adGroups = v.Now.Where(w => w.ChangeAction == ChangeAction.AdAddGroupMember).Select(w => w.P("groep")).ToList();
        Assert.Equal(new[] { "gg_rww_foxbeleid", "gg_wifiIntern" }, adGroups.OrderBy(g => g, StringComparer.Ordinal));
        Assert.DoesNotContain(v.AfterSynchronisation, w => w.P("groep") is "Alle gebruikers" or "Gesynchroniseerd");
        Assert.Contains(v.AfterSynchronisation, w => w.ChangeAction == ChangeAction.ExchangeAddGroupMember && w.P("groep") == "sg-sm-info-full@test.be");
        Assert.Contains(v.AfterSynchronisation, w => w.ChangeAction == ChangeAction.GraphAddGroupMember && w.P("groepId") == "g-team");
    }

    [Fact]
    public void VolunteerWithoutDepartmentWithCityAndOtherDomain()
    {
        var a = new NewUserRequest { Population = "Vrijwilliger", GivenName = "Piet", Surname = "Goossens" };
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), TestData.Config(), a);
        Assert.True(v.CanBeExecuted, string.Join("; ", v.Errors));
        Assert.Equal("piet.goossens@example.org", v.Upn);
        Assert.Equal("intranet", v.Now[0].P("l"));
        Assert.Empty(v.AfterSynchronisation);
    }

    [Fact]
    public void RequiredFieldsAndUnknownDomain()
    {
        var c = TestData.Config();
        c.AdDomain = null;
        var v = PlanBuilder.NewUser(TestData.AppEnvironment(), c, new NewUserRequest { Population = "Gemeente" });
        Assert.False(v.CanBeExecuted);
        Assert.Contains(v.Errors, f => f.Contains("Voornaam"));
        Assert.Contains(v.Errors, f => f.Contains("dienst"));
        Assert.Contains(v.Errors, f => f.Contains("kantoor"));
        Assert.Contains(v.Errors, f => f.Contains("domein"));
        Assert.Empty(v.Now);
    }
}

public class OffboardingPlanTests
{
    [Fact]
    public void OrderAndDefaultChoices()
    {
        var data = TestData.AppEnvironment();
        var an = data.SearchPerson("ad:an.peeters")!;
        var plan = PlanBuilder.Offboarding(data, TestData.Config(), an, new OffboardingOptions { Date = new DateOnly(2026, 10, 16) });

        var ad = plan.Where(w => w.TargetSystem == TargetSystem.ActiveDirectory).ToList();
        Assert.Equal(ChangeAction.AdDisableAccount, ad[0].ChangeAction);
        Assert.Equal(ChangeAction.AdMove, ad[^1].ChangeAction);
        Assert.Equal("OU=disabled_users,OU=ARCHIVE,DC=TEST,DC=INTERNAL", ad[^1].P("doelOu"));
        Assert.True(ad[^1].Selected);

        var description = Assert.Single(ad, w => w.ChangeAction == ChangeAction.AdSetAttributes);
        Assert.StartsWith("Uit dienst 2026-10-16", description.P("description"));

        // Normal groups stay by default, privileged groups are always removed
        Assert.False(ad.Single(w => w.P("groep") == "gg_wifiIntern").Selected);
        Assert.True(ad.Single(w => w.P("groep") == "Domain Admins").Selected);

        Assert.True(plan.Single(w => w.ChangeAction == ChangeAction.GraphRevokeSessions).Selected);
        Assert.False(plan.Single(w => w.ChangeAction == ChangeAction.ExchangeConvertToSharedMailbox).Selected);
        Assert.DoesNotContain(plan, w => w.ChangeAction == ChangeAction.GraphSetAttributes);
        Assert.DoesNotContain(plan, w => w.P("groep") == "Alle gebruikers");
    }

    [Fact]
    public void CloudAccountWithoutAdIsDisabledInEntra()
    {
        var data = TestData.AppEnvironment();
        var info = data.SearchPerson("entra:e-info")!;
        var plan = PlanBuilder.Offboarding(data, TestData.Config(), info, new OffboardingOptions());
        var w = Assert.Single(plan, x => x.ChangeAction == ChangeAction.GraphSetAttributes);
        Assert.Equal("false", w.P("accountEnabled"));
        Assert.DoesNotContain(plan, x => x.TargetSystem == TargetSystem.ActiveDirectory);
    }
}

public class PlanExecutorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeExecutor : IExecutor
    {
        public FakeExecutor(TargetSystem system, Func<Change, ChangeResult>? behaviour = null)
        {
            TargetSystem = system;
            Behaviour = behaviour ?? (w => ChangeResult.Ok(w));
        }

        public TargetSystem TargetSystem { get; }
        public Func<Change, ChangeResult> Behaviour { get; }
        public List<List<Change>> Calls { get; } = new();

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
        {
            Calls.Add(changes.ToList());
            return Task.FromResult<IReadOnlyList<ChangeResult>>(changes.Select(Behaviour).ToList());
        }
    }

    private static Change W(TargetSystem s, string target, Guid? start = null, bool selected = true) => new()
    {
        TargetSystem = s,
        ChangeAction = ChangeAction.AdSetAttributes,
        Target = target,
        Description = target,
        DependentOf = start,
        Selected = selected,
    };

    [Fact]
    public async Task BundlesPerSystemInOrderAndLogs()
    {
        var ad = new FakeExecutor(TargetSystem.ActiveDirectory);
        var ex = new FakeExecutor(TargetSystem.Exchange);
        var log = new JsonlChangeLog(_folder);
        var plan = new[] { W(TargetSystem.ActiveDirectory, "1"), W(TargetSystem.ActiveDirectory, "2"), W(TargetSystem.Exchange, "3"), W(TargetSystem.ActiveDirectory, "4") };
        plan[0].Secret = "NIET-LOGGEN";

        var r = await new PlanExecutor(new IExecutor[] { ad, ex }, log, () => "tester").ExecuteAsync(plan, "test");

        Assert.Equal(4, r.Count);
        Assert.All(r, x => Assert.Equal(ResultStatus.Succeeded, x.Status));
        Assert.Equal(new[] { 2, 1 }, ad.Calls.Select(a => a.Count));
        Assert.Single(ex.Calls);

        var rules = log.Recent(10);
        Assert.Equal(4, rules.Count);
        Assert.Equal("4", rules[0].Target);
        Assert.All(rules, x => Assert.Equal("tester", x.User));
        Assert.DoesNotContain("NIET-LOGGEN", File.ReadAllText(Directory.GetFiles(_folder).Single()));
    }

    [Fact]
    public async Task NotSelectedAndDependentSteps()
    {
        var ad = new FakeExecutor(TargetSystem.ActiveDirectory, w => w.Target == "maak" ? ChangeResult.Error(w, "kapot") : ChangeResult.Ok(w));
        var make = W(TargetSystem.ActiveDirectory, "maak");
        var output = W(TargetSystem.ActiveDirectory, "uit", selected: false);
        var plan = new[] { make, W(TargetSystem.ActiveDirectory, "groep", make.Id), output, W(TargetSystem.ActiveDirectory, "na-uit", output.Id), W(TargetSystem.ActiveDirectory, "los") };

        var r = await new PlanExecutor(new[] { ad }, new JsonlChangeLog(_folder), () => "t").ExecuteAsync(plan, "test");

        Assert.Equal(ResultStatus.Failed, r[0].Status);
        Assert.Equal(ResultStatus.Skipped, r[1].Status);
        Assert.Equal(ResultStatus.Skipped, r[2].Status);
        Assert.Equal(ResultStatus.Succeeded, r[3].Status);
        Assert.Equal(4, r.Count);
        Assert.DoesNotContain(ad.Calls.SelectMany(a => a), w => w.Target is "uit" or "groep" or "na-uit");
    }

    [Fact]
    public async Task WithoutConnectionOrOnAnExceptionFails()
    {
        var broken = new FakeExecutor(TargetSystem.Graph, _ => throw new InvalidOperationException("geen netwerk"));
        var plan = new[] { W(TargetSystem.Graph, "g"), W(TargetSystem.Exchange, "e") };
        var r = await new PlanExecutor(new[] { broken }, new JsonlChangeLog(_folder), () => "t").ExecuteAsync(plan, "test");
        Assert.Equal("geen netwerk", r[0].Message);
        Assert.Equal("Geen verbinding met dit systeem.", r[1].Message);
    }
}

public class FollowUpStorageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kb-opv-" + Guid.NewGuid().ToString("N"), "opvolging.json");

    public void Dispose()
    {
        var folder = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SavesTasksWithoutSecrets()
    {
        var storage = new FollowUpStorage(_path);
        Assert.Empty(storage.Read());
        var w = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphAssignLicence, Target = "eva@test.be", Description = "licentie", Secret = "geheim" };
        w.Parameters["sku"] = "SPB";
        storage.Add(new FollowUpTask { Upn = "eva@test.be", Description = "Eva", Changes = new() { w } });

        Assert.DoesNotContain("geheim", File.ReadAllText(_path));
        var task = Assert.Single(storage.Read());
        var back = Assert.Single(task.Changes);
        Assert.Equal(ChangeAction.GraphAssignLicence, back.ChangeAction);
        Assert.Equal("SPB", back.P("sku"));
        Assert.Equal(w.Id, back.Id);
    }
}
