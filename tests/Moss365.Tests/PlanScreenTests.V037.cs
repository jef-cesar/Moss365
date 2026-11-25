using Bunit;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Reporting;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CheckStatus = Moss365.Core.Security.CheckStatus;
using SecurityCheck = Moss365.Core.Security.SecurityCheck;

namespace Moss365.Tests;

/// <summary>0.37 (R14): reporting and working as a team.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void PeriodsOfAReport()
    {
        var q = ReportPeriod.LastQuarter(new DateOnly(2026, 2, 10));
        Assert.Equal(new DateOnly(2025, 10, 1), q.From);
        Assert.Equal(new DateOnly(2025, 12, 31), q.To);
        Assert.Equal("Q4 2025", q.Name);
        var m = ReportPeriod.LastMonth(new DateOnly(2026, 3, 31));
        Assert.Equal(new DateOnly(2026, 2, 28), m.To);
    }

    [Fact]
    public void AReportComesFromTheHistoryAlsoForThePast()
    {
        var period = ReportPeriod.Quarter(2026, 1);
        var history = new Dictionary<string, IReadOnlyList<(DateOnly, int)>>
        {
            [Kpis.Active.Key] = new[] { (new DateOnly(2025, 12, 20), 100), (new DateOnly(2026, 2, 1), 104), (new DateOnly(2026, 4, 2), 110) },
            [Kpis.Inactive90.Key] = new[] { (new DateOnly(2026, 1, 15), 7), (new DateOnly(2026, 3, 30), 9) },
        };
        var log = new[]
        {
            new LogEntry(new DateTimeOffset(2026, 2, 3, 10, 0, 0, TimeSpan.Zero), "admin", "t", "AD", "AdUnlockAccount", "x", "x", "d", new Dictionary<string, string>(), "Succeeded", ""),
            new LogEntry(new DateTimeOffset(2026, 2, 4, 10, 0, 0, TimeSpan.Zero), "admin", "t", "AD", "AdUnlockAccount", "x", "x", "d", new Dictionary<string, string>(), "Failed", "fout"),
            new LogEntry(new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero), "admin", "t", "AD", "AdMove", "x", "x", "d", new Dictionary<string, string>(), "Succeeded", ""),
        };
        var report = PeriodReport.Build("Contoso", period, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), "admin", history, log,
            Array.Empty<QualityRow>(), Standards.Evaluate(new Dictionary<string, SecurityCheck>()));
        var active = Assert.Single(report.Kpis, k => k.Key == Kpis.Active.Key);
        Assert.Equal(100, active.Start);
        Assert.Equal(104, active.End);
        var inactive = Assert.Single(report.Kpis, k => k.Key == Kpis.Inactive90.Key);
        Assert.Equal((7, 9), (inactive.Start, inactive.End)); // no value yet at the start: the first one of the period
        var change = Assert.Single(report.Changes);
        Assert.Equal(("AdUnlockAccount", 1, 1), (change.Action, change.Succeeded, change.Failed));
        Assert.Empty(report.Notes);

        var html = report.ToHtml();
        Assert.Contains("Q1 2026", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NIS2", html, StringComparison.Ordinal);
        var csv = report.ToCsv();
        Assert.Contains("AdUnlockAccount;1;1", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCsvNeverHoldsAFormula()
    {
        var report = new PeriodReport("T", ReportPeriod.Quarter(2026, 1), DateTimeOffset.Now, "a",
            new[] { new KpiRow("k", "=HYPERLINK(\"x\")", 1, 2, false) }, Array.Empty<ChangeRow>(), Array.Empty<QualityRow>(), Array.Empty<StandardRow>(), Array.Empty<string>());
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", report.ToCsv(), StringComparison.Ordinal);
    }

    [Fact]
    public void StandardsTakeTheWorstCheck()
    {
        var results = new Dictionary<string, SecurityCheck>
        {
            ["zwakke-mfa"] = new("zwakke-mfa", "MFA", CheckStatus.Problem, "3 zonder", Array.Empty<Core.Security.CheckRow>()),
            ["rollen"] = new("rollen", "Rollen", CheckStatus.Ok, "ok", Array.Empty<Core.Security.CheckRow>()),
        };
        var rows = Standards.Evaluate(results);
        Assert.Equal(CheckStatus.Problem, rows.Single(r => r.Control.Id == "21.2.j").Status);
        Assert.Equal(CheckStatus.Problem, rows.Single(r => r.Control.Standard == Standards.Cis && r.Control.Id == "6").Status);
        Assert.Equal(CheckStatus.Unknown, rows.Single(r => r.Control.Id == "21.2.d").Status);

        // Checks that do not exist for the tenant (only AD) are left out
        var adOnly = Standards.Evaluate(results, new[] { "meldingen", "ad-beveiliging", "gpo" });
        Assert.Equal(CheckStatus.Unknown, adOnly.Single(r => r.Control.Id == "21.2.j").Status);
        Assert.Empty(adOnly.Single(r => r.Control.Id == "21.2.j").Results);
    }

    [Fact]
    public void DataQualityPerDepartment()
    {
        var rows = DataQuality.PerDepartment(State.KpiSource);
        var total = rows[^1];
        Assert.Equal("Totaal", total.Department);
        Assert.Equal(State.KpiSource.Active.Count, total.People);
        Assert.True(rows.Take(rows.Count - 1).Select(r => r.Score).SequenceEqual(rows.Take(rows.Count - 1).Select(r => r.Score).OrderBy(s => s)));
        var p = new Person { Key = "x", Entra = new EntraUser { Id = "x", JobTitle = "Onthaal", Department = "Burgerzaken" } };
        Assert.Equal(new[] { "kantoor", "manager", "telefoon" }, DataQuality.Missing(p));
    }

    private InventoryData WithOldGuest()
    {
        var d = State.Data;
        var entra = d.EntraUsers.Select(e => e.Id == "e-gast" ? e with { CreatedDateTime = DateTimeOffset.Now.AddMonths(-20).ToString("o"), AccountEnabled = true } : e).ToList();
        return d with { EntraUsers = entra, Persons = InventoryLoader.LinkPersons(d.AdUsers, entra) };
    }

    [Fact]
    public void GuestsAreRenewedEachYear()
    {
        var d = WithOldGuest();
        var due = Assert.Single(GuestRenewal.Due(d, Array.Empty<GuestReview>(), DateTimeOffset.Now));
        Assert.Contains("nooit bevestigd", due.Reason, StringComparison.Ordinal);

        // Without a sponsor no mail; with one, one mail per sponsor
        Assert.Single(GuestRenewal.AskSponsors(new[] { due }, GuestRenewal.DefaultQuestion).Errors);
        var reviews = GuestRenewal.SetSponsor(Array.Empty<GuestReview>(), "e-gast", "an@test.be");
        var withSponsor = Assert.Single(GuestRenewal.Due(d, reviews, DateTimeOffset.Now));
        var mail = Assert.Single(GuestRenewal.AskSponsors(new[] { withSponsor }, GuestRenewal.DefaultQuestion).Plan);
        Assert.Equal(ChangeAction.GraphSendMail, mail.ChangeAction);
        Assert.Contains(withSponsor.Guest.Name, mail.P("tekst"), StringComparison.Ordinal);

        // Confirmed: no longer due; it survives a round trip through the database text
        reviews = GuestRenewal.Read(GuestRenewal.Write(GuestRenewal.Confirm(reviews, "e-gast", "admin", DateTimeOffset.Now)));
        Assert.Empty(GuestRenewal.Due(d, reviews, DateTimeOffset.Now));

        // Disabling is a plan that can be undone, never a delete
        var disable = Assert.Single(GuestRenewal.Disable(new[] { due.Guest }).Plan);
        Assert.Equal(ChangeAction.GraphSetAttributes, disable.ChangeAction);
        Assert.Equal("true", disable.Previous!["accountEnabled"]);
    }

    [Fact]
    public void ARequestIsApprovedAndFillsInTheNewUserForm()
    {
        var profile = new JobProfile { Name = "Onthaal", Department = "Burgerzaken" };
        State.Config.JobProfiles.Add(profile);
        var cut = RenderComponent<RequestsPage>();
        cut.FindAll("button").First(b => b.TextContent == "Aanvraag indienen").Click();
        Assert.Contains("Vul voornaam en familienaam in.", cut.Markup, StringComparison.Ordinal);

        var request = new StaffRequest { Kind = RequestKind.Onboarding, GivenName = "Lies", Surname = "Peeters", JobProfile = profile.Id, Date = new DateOnly(2027, 1, 4) };
        Assert.Empty(StaffRequests.Validate(request, State.Config));
        State.AddRequest(request);
        cut.Render();
        Assert.Contains("Nieuw: Lies Peeters", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Goedkeuren").Click();

        var stored = Assert.Single(State.Requests());
        Assert.Equal(RequestStatus.Approved, stored.Status);
        Assert.Equal(State.User, stored.RequestedBy);
        Assert.Contains(State.ChangeLog.Recent(10), r => r.ChangeAction == "Request" && r.TargetName == "Nieuw: Lies Peeters");
        var form = State.PageState.Form<NewUserRequest>("nieuw", "nieuw", "Nieuwe gebruiker");
        Assert.Equal(("Lies", "Peeters", profile.Id), (form.GivenName, form.Surname, form.JobProfile));
        Assert.EndsWith("/nieuw", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceHealthOnTheStartScreen()
    {
        EntraFilled();
        var cut = RenderComponent<ServiceHealthCard>();
        cut.FindAll("button").First(b => b.TextContent == "Status lezen").Click();
        cut.WaitForAssertion(() => Assert.Contains("Exchange Online", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Contains("1 dienst(en) met een probleem", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("wordt onderzocht", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Oude verificatie stopt", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("Alle diensten van Microsoft 365 zijn in orde.", ServiceHealth.Summary(new ServiceHealthReading(
            new[] { new ServiceStatus("Teams", "serviceOperational") }, Array.Empty<ServiceIssue>(), Array.Empty<ServiceMessage>())));
    }

    [Fact]
    public void TheReportScreenMakesAndSavesAReport()
    {
        State.Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today.AddDays(-200)), new Dictionary<string, int> { [Kpis.Active.Key] = 3 });
        var cut = RenderComponent<ReportsPage>();
        cut.FindAll("button").First(b => b.TextContent == "Rapport maken").Click();
        Assert.Contains("Rapport Q", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("21.2.j", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Opslaan als HTML", StringComparison.Ordinal)).Click();
        var file = Assert.Single(Directory.GetFiles(State.ExportFolder, "rapport-*.html"));
        Assert.Contains("Datakwaliteit", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains(State.Database.ReadAccess(10), a => a.What == "Rapport opgeslagen");
    }

    [Fact]
    public void TenantsAreComparedWithoutIds()
    {
        static InventoryData With(params CaPolicy[] policies) => new() { ConditionalAccess = new CaData(policies, Array.Empty<NamedLocation>(), Array.Empty<AuthenticationStrength>(), new Dictionary<string, string>(), false) };
        var a = With(new CaPolicy { Id = "1", Name = "MFA voor iedereen", Status = CaStatus.On, UsersIn = new[] { "All" }, Checks = new[] { "mfa" }, GroupsOff = new[] { "g-a" } },
                     new CaPolicy { Id = "2", Name = "Oude verificatie blokkeren", Status = CaStatus.On, ClientApps = new[] { "exchangeActiveSync", "other" }, Checks = new[] { "block" } });
        var b = With(new CaPolicy { Id = "9", Name = "MFA voor iedereen", Status = CaStatus.On, UsersIn = new[] { "All" }, Checks = new[] { "mfa" }, GroupsOff = new[] { "g-b" } });
        var differences = TenantComparison.Compare(a, new OrganisationConfig(), b, new OrganisationConfig());
        var only = Assert.Single(differences);
        Assert.Equal("Oude verificatie blokkeren", only.Item);
        Assert.Equal(TenantComparison.Missing, only.Other);

        var cb = new OrganisationConfig { PasswordMaxAgeDays = 90 };
        Assert.Contains(TenantComparison.Compare(a, new OrganisationConfig(), a, cb), d => d.Item.StartsWith("Wachtwoord", StringComparison.Ordinal));
    }

    [Fact]
    public void TheNewPagesOfR14HaveARole()
    {
        Assert.Equal(Core.Permissions.Permission.InviteGuests, Access.ForPath("gastenverlenging"));
        Assert.Equal(Core.Permissions.Permission.Settings, Access.ForPath("tenantvergelijking"));
        Assert.False(Access.FitsTenant("gastenverlenging", true, false));
        foreach (var page in new[] { "rapporten", "aanvragen", "gastenverlenging", "tenantvergelijking" })
        {
            Assert.Contains(MenuModel.AllPages, p => p.Href == page);
        }
    }
}
