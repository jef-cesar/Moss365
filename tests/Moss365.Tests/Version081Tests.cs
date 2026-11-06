using System.Net;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.Connections.Ad;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.8.1: phone, mobile and manager; tasks for a selection (administrator, schedule offboarding).</summary>
public sealed class Version081Tests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-v081-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    /// <summary>The made-up environment with phone, mobile and manager: an is the manager of bo (AD) and of info
    /// (cloud).</summary>
    private static InventoryData WithManagers()
    {
        var b = TestData.Files();
        var an = b[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "an.peeters");
        var bo = b[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "bo.janssens");
        an["OfficePhone"] = "03 000 00 01";
        an["MobilePhone"] = "0470 00 00 01";
        bo["Manager"] = an["DistinguishedName"];
        var info = b[InventoryLoader.EntraUsers].Single(r => r["Id"] == "e-info");
        info["BusinessPhone"] = "03 000 00 09";
        info["ManagerId"] = "e-an";
        return InventoryLoader.Build(b);
    }

    private static LogEntry Logged(Change w) => new(DateTimeOffset.Now, "t", "c", w.TargetSystem.ToString(), w.ChangeAction.ToString(), w.Target, w.TargetName,
        w.Description, w.Parameters, "Gelukt", "", Guid.NewGuid(), w.Id, w.Previous);

    // ---------------------------------------------------------------- phone, mobile, manager

    [Fact]
    public void PhoneMobileAndManagerComeFromTheInventoryAndGoBack()
    {
        var d = WithManagers();
        var an = d.SearchPerson("ad:an.peeters")!;
        var bo = d.SearchPerson("ad:bo.janssens")!;
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        Assert.Equal("03 000 00 01", an.Ad!.Phone);
        Assert.Equal("0470 00 00 01", an.Ad.Mobile);
        Assert.Same(an, AttributePlans.ManagerOf(d, bo));
        Assert.Same(an, AttributePlans.ManagerOf(d, info));
        Assert.Null(AttributePlans.ManagerOf(d, an));
        Assert.Equal(an.Key, UserDesired.Of(bo, d).Manager);
        Assert.Equal(string.Empty, UserDesired.Of(an, d).Manager);
        Assert.Null(UserDesired.Of(an).Manager); // without data: do not change

        var folder = InventoryWriter.Save(d, _folder, InventoryComponent.Ad | InventoryComponent.Entra, new DateTime(2026, 10, 31, 9, 0, 0));
        var back = InventoryLoader.Load(InventoryLoader.SearchFolders(_folder));
        Assert.True(Directory.Exists(folder));
        Assert.Equal("0470 00 00 01", back.SearchPerson("ad:an.peeters")!.Ad!.Mobile);
        Assert.Equal(an.Ad.DistinguishedName, back.SearchPerson("ad:bo.janssens")!.Ad!.Manager);
        var infoBack = back.Persons.Single(p => p.Entra?.Id == "e-info");
        Assert.Equal("e-an", infoBack.Entra!.ManagerId);
        Assert.Equal("03 000 00 09", infoBack.Entra.Phone);
    }

    [Fact]
    public void LdapReadsPhoneMobileAndManager()
    {
        var g = AdLdapMapper.ToUser(new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = new object?[] { "an.peeters" },
            ["telephoneNumber"] = new object?[] { "03 000 00 01" },
            ["mobile"] = new object?[] { "0470 00 00 01" },
            ["manager"] = new object?[] { "CN=Bo Janssens,OU=GEBRUIKERS,DC=test,DC=internal" },
        });
        Assert.Equal("03 000 00 01", g.Phone);
        Assert.Equal("0470 00 00 01", g.Mobile);
        Assert.Equal("CN=Bo Janssens,OU=GEBRUIKERS,DC=test,DC=internal", g.Manager);
        Assert.Contains("manager", AdLdapMapper.UserAttributes, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void InAdTheManagerIsAnAttributeWithThePreviousValue()
    {
        var d = WithManagers();
        var bo = d.SearchPerson("ad:bo.janssens")!;
        var cas = d.SearchPerson("ad:cas.vos")!;
        var desired = UserDesired.Of(bo, d);
        desired.Manager = cas.Key;
        desired.Phone = "03 000 00 02";

        var w = Assert.Single(AttributePlans.User(d, TestData.Config(), bo, desired).Plan);
        Assert.Equal(ChangeAction.AdSetAttributes, w.ChangeAction);
        Assert.Equal(cas.Ad!.DistinguishedName, w.P("manager"));
        Assert.Equal("03 000 00 02", w.P("telephoneNumber"));
        Assert.Equal(d.SearchPerson("ad:an.peeters")!.Ad!.DistinguishedName, w.Previous!["manager"]);
        Assert.Contains("Manager: " + cas.Name, w.NewValue);

        // No manager any more: empty (the AD writer clears the attribute)
        desired.Manager = string.Empty;
        desired.Phone = bo.Ad!.Phone;
        Assert.Equal(string.Empty, Assert.Single(AttributePlans.User(d, TestData.Config(), bo, desired).Plan).P("manager"));

        // Someone without an AD account cannot be a manager in AD
        desired.Manager = d.Persons.Single(p => p.Entra?.Id == "e-info").Key;
        var error = AttributePlans.User(d, TestData.Config(), bo, desired);
        Assert.DoesNotContain(error.Plan, w => w.Parameters.ContainsKey("manager"));
        Assert.Contains("geen AD-account", string.Join(" ", error.Errors.Concat(error.Plan.Select(w => w.Warning ?? ""))));
    }

    [Fact]
    public void InTheCloudTheManagerIsASeparateCommandThatCanBeUndone()
    {
        var d = WithManagers();
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        var bo = d.SearchPerson("ad:bo.janssens")!;
        var desired = UserDesired.Of(info, d);
        Assert.Equal("03 000 00 09", desired.Phone);
        desired.Manager = bo.Key;
        desired.Mobile = "0470 00 00 09";

        var u = AttributePlans.User(d, TestData.Config(), info, desired);
        Assert.Equal(2, u.Plan.Count);
        Assert.Equal(ChangeAction.GraphSetAttributes, u.Plan[0].ChangeAction);
        Assert.Equal("0470 00 00 09", u.Plan[0].P("mobilePhone"));
        var m = u.Plan[1];
        Assert.Equal(ChangeAction.GraphSetManager, m.ChangeAction);
        Assert.Equal("e-info", m.Target);
        Assert.Equal("e-bo", m.P("managerId"));
        Assert.Equal("e-an", m.Previous!["managerId"]);
        Assert.Null(Undo.WhyNot(Logged(m)));

        var back = Assert.Single(Undo.Plan(new[] { Logged(m) }).Plan);
        Assert.Equal(ChangeAction.GraphSetManager, back.ChangeAction);
        Assert.Equal("e-an", back.P("managerId"));

        // Only the manager: one command
        var only = UserDesired.Of(info, d);
        only.Manager = string.Empty;
        var w = Assert.Single(AttributePlans.User(d, TestData.Config(), info, only).Plan);
        Assert.Equal(string.Empty, w.P("managerId"));
    }

    [Fact]
    public void BusinessPhonesIsAListInGraph()
    {
        var content = GraphConnection.AttributesContent(new Dictionary<string, string> { ["businessPhones"] = "03 000 00 01", ["mobilePhone"] = "", ["jobTitle"] = "x" });
        Assert.Equal(new[] { "03 000 00 01" }, content["businessPhones"]);
        Assert.Null(content["mobilePhone"]);
        Assert.Empty((string[])GraphConnection.AttributesContent(new Dictionary<string, string> { ["businessPhones"] = "" })["businessPhones"]!);
    }

    private static GraphConnection Graph(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

    private static Change ManagerCommand(string managerId) => new()
    {
        TargetSystem = TargetSystem.Graph,
        ChangeAction = ChangeAction.GraphSetManager,
        Target = "e-info",
        Description = "Manager zetten",
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = managerId },
    };

    [Fact]
    public async Task GraphSetsAndClearsTheManager()
    {
        var fake = new FakeGraph().Op(HttpMethod.Put, "/users/e-info/manager/$ref", "", HttpStatusCode.NoContent);
        var r = Assert.Single(await Graph(fake).ExecuteAsync(new[] { ManagerCommand("e-bo") }, CancellationToken.None));
        Assert.Equal(ResultStatus.Succeeded, r.Status);
        var put = fake.Requests.Single(v => v.Method == HttpMethod.Put);
        Assert.Contains("/users/e-bo", put.Content);
        Assert.Contains("@odata.id", put.Content);

        // Clear: DELETE; already having no manager (404) is not an error
        var clear = new FakeGraph();
        var r2 = Assert.Single(await Graph(clear).ExecuteAsync(new[] { ManagerCommand("") }, CancellationToken.None));
        Assert.Equal(ResultStatus.Succeeded, r2.Status);
        Assert.Equal(HttpMethod.Delete, Assert.Single(clear.Requests).Method);
        Assert.Contains("geen manager", r2.Message);
    }

    // ---------------------------------------------------------------- calendar of a room (#20)

    [Fact]
    public void CalendarPeriodPerWeekOrChosen()
    {
        var wednesday = new DateTime(2026, 11, 4, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 11, 2), CalendarPeriod.Monday(wednesday));
        Assert.Equal(new DateTime(2026, 11, 2), CalendarPeriod.Monday(new DateTime(2026, 11, 2)));
        Assert.Equal(new DateTime(2026, 10, 26), CalendarPeriod.Monday(new DateTime(2026, 11, 1))); // Sunday belongs to the week before

        var (start, until, error) = CalendarPeriod.Compute(CalendarChoice.NextWeek, wednesday);
        Assert.Null(error);
        Assert.Equal(new DateTime(2026, 11, 9), start);
        Assert.Equal(new DateTime(2026, 11, 16), until);

        var selected = CalendarPeriod.Compute(CalendarChoice.Selected, wednesday, new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4));
        Assert.Equal(TimeSpan.FromDays(1), selected.Until - selected.Of); // the end date counts
        Assert.Contains("hoogstens", CalendarPeriod.Compute(CalendarChoice.Selected, wednesday, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).Error);
        Assert.NotNull(CalendarPeriod.Compute(CalendarChoice.Selected, wednesday).Error);
    }

    [Fact]
    public async Task GraphReadsTheCalendarWithPagesAndSaysWhenNotAllowed()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/users/rm-vz-a%40test.be/calendarView", new Dictionary<string, object>
            {
                ["value"] = new object[]
                {
                    new
                    {
                        subject = "Organisator Een", isAllDay = false, sensitivity = "normal", showAs = "busy", isCancelled = false,
                        organizer = new { emailAddress = new { name = "Organisator Een", address = "een@test.be" } },
                        start = new { dateTime = "2026-11-03T08:00:00.0000000", timeZone = "UTC" },
                        end = new { dateTime = "2026-11-03T09:00:00.0000000", timeZone = "UTC" },
                    },
                },
                ["@odata.nextLink"] = GraphClient.Base + "/agenda-pagina2",
            })
            .Op(HttpMethod.Get, "/agenda-pagina2", new
            {
                value = new object[]
                {
                    new
                    {
                        subject = "", isAllDay = true, sensitivity = "private", showAs = "oof", isCancelled = false,
                        start = new { dateTime = "2026-11-05T00:00:00.0000000", timeZone = "UTC" },
                        end = new { dateTime = "2026-11-06T00:00:00.0000000", timeZone = "UTC" },
                    },
                },
            });
        var start = new DateTime(2026, 11, 2);
        var calendar = await Graph(fake).ReadCalendarAsync("rm-vz-a@test.be", start, start.AddDays(7), CancellationToken.None);

        Assert.Equal(2, calendar.Count);
        Assert.Equal(new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc).ToLocalTime(), calendar[0].Begin);
        Assert.Equal("een@test.be", calendar[0].OrganiserAddress);
        Assert.True(calendar[1].WholeDay);
        Assert.Equal(new DateTime(2026, 11, 5), calendar[1].Begin); // a whole day does not shift with the time zone
        Assert.Equal("Privé", calendar[1].SubjectText);
        Assert.Equal("afwezig", calendar[1].StatusText);
        var url = fake.Requests[0].Url;
        Assert.Contains("startDateTime=" + start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), url);
        Assert.All(fake.Requests, v => Assert.Equal(HttpMethod.Get, v.Method));

        var noPermission = new FakeGraph().Op(HttpMethod.Get, "/users/rm-vz-a%40test.be/calendarView",
            "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access is denied.\"}}", HttpStatusCode.Forbidden);
        var error = await Assert.ThrowsAsync<NoPermissionError>(() => Graph(noPermission).ReadCalendarAsync("rm-vz-a@test.be", start, start.AddDays(7), CancellationToken.None));
        Assert.Contains("Access is denied", error.Message);
    }

    // ---------------------------------------------------------------- KPI's (#21)

    [Fact]
    public void EveryKpiHasAUniqueKeyAndIsFound()
    {
        Assert.Equal(Kpis.Fixed.Count, Kpis.Fixed.Select(k => k.Key).Distinct().Count());
        Assert.All(Kpis.Fixed, k => Assert.Same(k, Kpis.Find(k.Key)));
        Assert.Equal("gebruikers?kpi=afdeling%3AVrije%20tijd", Kpis.Division("Vrije tijd").Link);
        Assert.Equal("Vrije tijd", Kpis.Find("afdeling:Vrije tijd")!.Value);
        Assert.Null(Kpis.Find("bestaat-niet"));
        Assert.Null(Kpis.Find("afdeling:"));
    }

    [Fact]
    public void TheKpisCountLikeTheStatistics()
    {
        var d = WithManagers();
        var c = TestData.Config();
        var now = new DateTime(2026, 10, 31, 12, 0, 0);
        var s = Statistic.Compute(d, c, now);
        var b = new KpiSource(d, c, now);
        int N(Kpi k) => Kpis.Count(k, b);

        Assert.Equal(s.Active, N(Kpis.Active));
        Assert.Equal(s.Disabled, N(Kpis.Disabled));
        Assert.Equal(s.Guests, N(Kpis.Guests));
        Assert.Equal(s.AdAndEntra, N(Kpis.AdAndEntra));
        Assert.Equal(s.OnlyAd, N(Kpis.OnlyAd));
        Assert.Equal(s.OnlyEntra, N(Kpis.OnlyEntra));
        Assert.Equal(s.MailboxAccounts, N(Kpis.MailboxAccounts));
        Assert.Equal(s.Inactive90, N(Kpis.Inactive90));
        Assert.Equal(s.Inactive180, N(Kpis.Inactive180));
        Assert.Equal(s.NeverSignedIn, N(Kpis.NeverSignedIn));
        Assert.Equal(s.PasswordExpiresNever, N(Kpis.PasswordExpiresNever));
        Assert.Equal(s.PrivilegedMembers, N(Kpis.Privileged));
        Assert.Equal(s.WithoutAccessGroup, N(Kpis.WithoutAccessGroup));
        Assert.Equal(s.EmptyGroups, N(Kpis.EmptyGroups));
        Assert.Equal(s.DynamicGroups, N(Kpis.DynamicGroups));
        Assert.Equal(s.RoomsWithDeviation, N(Kpis.RoomsWithDeviation));
        Assert.All(s.PerDivision, st => Assert.Equal(st.Count, N(Kpis.Find(Uri.UnescapeDataString(st.Link!.Split("kpi=")[1]))!)));
        Assert.All(s.PermissionModel, st => Assert.Equal(st.Count, N(Kpis.PermissionModel(st.Name))));
        Assert.All(s.GroupsPerKind, st => Assert.Equal(st.Count, N(Kpis.GroupKind(st.Name))));
    }

    // ---------------------------------------------------------------- statistics in BI style

    private static InventoryData WithDates(DateTime now)
    {
        var b = TestData.Files();
        string D(int days) => now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
        var ad = b[InventoryLoader.AdUsers];
        ad.Single(r => r["SamAccountName"] == "an.peeters")["LastLogonDate"] = D(5);
        ad.Single(r => r["SamAccountName"] == "an.peeters")["PasswordLastSet"] = D(10);
        ad.Single(r => r["SamAccountName"] == "an.peeters")["WhenCreated"] = D(3);
        ad.Single(r => r["SamAccountName"] == "bo.janssens")["LastLogonDate"] = D(45);
        ad.Single(r => r["SamAccountName"] == "bo.janssens")["PasswordLastSet"] = D(400);
        ad.Single(r => r["SamAccountName"] == "bo.janssens")["Office"] = "bibliotheek";
        ad.Single(r => r["SamAccountName"] == "cas.vos")["LastLogonDate"] = D(200);
        ad.Single(r => r["SamAccountName"] == "cas.vos")["PasswordLastSet"] = D(900);
        return InventoryLoader.Build(b);
    }

    [Fact]
    public void DistributionsAddUpToTheActiveEmployees()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0);
        var b = new KpiSource(WithDates(now), TestData.Config(), now);
        var active = b.Active.Count;

        var signIn = BiStatistics.SignInRecency(b);
        Assert.Equal(active, signIn.Sum(x => x.Count));
        Assert.Equal(new[] { "schaal-1", "schaal-2", "schaal-3", "schaal-4", "neutraal" }, signIn.Select(x => x.Colour));
        Assert.Contains(Kpis.Persons(signIn[0].Kpi!, b), p => p.Key == "ad:an.peeters");
        Assert.Contains(Kpis.Persons(signIn[1].Kpi!, b), p => p.Key == "ad:bo.janssens");
        Assert.Contains(Kpis.Persons(signIn[3].Kpi!, b), p => p.Key == "ad:cas.vos");

        var password = BiStatistics.PasswordAge(b);
        Assert.Equal(b.Active.Count(p => p.Ad is not null), password.Sum(x => x.Count));
        Assert.Equal(new[] { 1, 0, 1, 1 }, password.Take(4).Select(x => x.Count));

        var mfa = BiStatistics.Mfa(b);
        Assert.Equal(active, mfa.Sum(x => x.Count));
        Assert.Equal(active, mfa.Single(x => x.Kpi!.Key == "mfa:onbekend").Count); // no security data: unknown, never "in order"

        var offices = BiStatistics.PerOffice(b);
        Assert.Equal(active, offices.Sum(x => x.Count));
        Assert.Contains(offices, k => k.Name == "bibliotheek" && k.Count == 1);

        var months = BiStatistics.CreatedPerMonth(b);
        Assert.Equal(12, months.Count);
        Assert.Equal("aangemaakt:2026-10", months[^1].Kpi!.Key);
        Assert.True(months[^1].Total >= 1);
    }

    [Fact]
    public void TheDivisionFilterIsACompositeKpi()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0);
        var b = new KpiSource(WithDates(now), TestData.Config(), now);
        string DivisionOf(Person p) => b.Config.DivisionFor(p.Department)?.Name ?? Statistic.NoDivision;
        var division = DivisionOf(b.Data.SearchPerson("ad:cas.vos")!);
        var share = BiStatistics.SignInRecency(b, division);
        Assert.All(share, d => Assert.EndsWith("|afdeling-alle:" + division, d.Kpi!.Key));
        Assert.Equal(b.Active.Count(p => DivisionOf(p) == division), share.Sum(d => d.Count));
        Assert.True(share.Sum(d => d.Count) < b.Active.Count);
        var k = share[3].Kpi!;
        Assert.Equal(KpiList.Users, k.Items);
        Assert.Contains(" · afdeling " + division, k.Label);
        Assert.Same(Kpis.Find(k.Key)!.Key, k.Key);
        Assert.Null(Kpis.Find("aanmelding:0-30|rechtstreeks")); // no user list: no composition
    }

    [Fact]
    public void GuestsPerDomainAndChangeLogPerWeek()
    {
        var d = TestData.AppEnvironment();
        var b = new KpiSource(d, TestData.Config(), DateTime.Now);
        var guest = d.Persons.Single(p => p.IsGuest);
        Assert.Equal("example.net", Kpis.GuestDomain(guest));
        var domain = Assert.Single(BiStatistics.GuestsPerDomain(b));
        Assert.Equal(1, domain.Count);
        Assert.Same(guest, Assert.Single(Kpis.Persons(domain.Kpi!, b)));

        var now = new DateTime(2026, 10, 31, 12, 0, 0); // Saturday, week 44
        LogEntry R(DateTime t, string status) => new(new DateTimeOffset(t), "t", "c", "AD", "x", "d", "d", "o", new Dictionary<string, string>(), status, "");
        var weeks = BiStatistics.ChangeLogPerWeek(new[] { R(now.AddHours(-1), "Gelukt"), R(now.AddDays(-1), "Mislukt"), R(now.AddDays(-8), "Gelukt"), R(now.AddDays(-200), "Gelukt") }, now);
        Assert.Equal(12, weeks.Count);
        Assert.Equal("w44", weeks[^1].Label);
        Assert.Equal(new[] { 1, 1 }, weeks[^1].Values);
        Assert.Equal(new[] { 1, 0 }, weeks[^2].Values);
        Assert.Equal(3, weeks.Sum(w => w.Total)); // 200 days ago falls outside it
    }

    [Fact]
    public void TheStatePerDayInTheDatabaseAndTheDifference()
    {
        Directory.CreateDirectory(_folder);
        var db = new Moss365.Core.Storage.Database(Path.Combine(_folder, "moss365.db"));
        Assert.Equal(Moss365.Core.Storage.Database.Migrations.Count, db.Version);
        var today = new DateOnly(2026, 10, 31);
        db.SaveKpiSnapshot(today.AddDays(-35), new Dictionary<string, int> { ["actief"] = 150 });
        db.SaveKpiSnapshot(today.AddDays(-10), new Dictionary<string, int> { ["actief"] = 155 });
        db.SaveKpiSnapshot(today, new Dictionary<string, int> { ["actief"] = 157, ["gasten"] = 4 });
        db.SaveKpiSnapshot(today, new Dictionary<string, int> { ["actief"] = 158 }); // the same day: replace

        var snapshot = db.ReadKpiSnapshot(today.AddDays(-90));
        Assert.Equal(new[] { 150, 155, 158 }, snapshot["actief"].Select(p => p.Value));
        var trend = new KpiTrend(Kpis.Active, snapshot["actief"]);
        Assert.Equal(8, trend.Difference(30, 158));
        Assert.Null(new KpiTrend(Kpis.Guests, snapshot["gasten"]).Difference(30, 4));
        Assert.Single(db.ReadKpiSnapshot(today)["actief"]);

        var b = new KpiSource(TestData.AppEnvironment(), TestData.Config(), DateTime.Now);
        var day = BiStatistics.Snapshot(b);
        Assert.DoesNotContain(day.Keys, k => k.StartsWith("gelukt", StringComparison.Ordinal) || k.StartsWith("mislukt", StringComparison.Ordinal));
        Assert.All(BiStatistics.WithTrend, k => Assert.True(day.ContainsKey(k.Key)));
    }

    // ---------------------------------------------------------------- tasks for a selection (#22)

    [Fact]
    public void AdministratorOfAllSelectedGroups()
    {
        var d = TestData.AppEnvironment();
        var an = d.SearchPerson("ad:an.peeters")!;
        var groups = d.Index.Groups.Where(g => g.Name is "gg_eigen" or "gg_wifiIntern").ToList();
        var u = BulkPlans.AdministratorForGroups(d, groups, an);
        Assert.Equal(2, u.Plan.Count);
        Assert.All(u.Plan, w =>
        {
            Assert.Equal(ChangeAction.AdSetAttributes, w.ChangeAction);
            Assert.Equal(an.Ad!.DistinguishedName, w.P("managedBy"));
        });
    }

    [Fact]
    public void ScheduleOffboardingForTheSelectedButNeverForGuests()
    {
        var d = TestData.AppEnvironment();
        var guest = d.Persons.Single(p => p.IsGuest);
        var persons = new[] { d.SearchPerson("ad:an.peeters")!, d.SearchPerson("ad:cas.vos")!, guest };
        var whenValue = new DateTimeOffset(2026, 11, 30, 17, 0, 0, TimeSpan.FromHours(1));

        var scheduled = BulkPlans.ScheduleOffboarding(persons, whenValue, "beheerder@test.be");
        Assert.Equal(2, scheduled.Count);
        Assert.All(scheduled, g =>
        {
            Assert.Equal(ScheduledKind.Offboarding, g.Kind);
            Assert.Equal(whenValue, g.When);
            Assert.Equal(new DateOnly(2026, 11, 30), g.Options!.Date);
        });
        Assert.DoesNotContain(scheduled, g => g.PersonKey == guest.Key);
    }
}
