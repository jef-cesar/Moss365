using System.Security.Cryptography;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Reporting;
using Moss365.Core.Security;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R19: the overview as the start screen, All tenants (only counts, nothing stored) and the board summary.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheOverviewShowsFourBlocksWithTheListsBehindThem()
    {
        State.Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today.AddDays(-30)), new Dictionary<string, int> { ["overzicht:reviews-due"] = 1 });
        var tiles = State.OverviewTiles(30);

        Assert.Equal(new[] { OverviewBlock.Security, OverviewBlock.Devices, OverviewBlock.Hygiene, OverviewBlock.Work }, tiles.Select(t => t.Block).Distinct().Order());
        var reviews = Assert.Single(tiles, t => t.Key == "reviews-due");
        Assert.Equal(("toegangsreviews", 1), (reviews.Link, reviews.Before));
        Assert.True(reviews.Value > 0 && reviews.Let); // never reviewed: due
        Assert.Contains(tiles, t => t.Key == "zonder-mfa" && t.Link.Contains("kpi=", StringComparison.Ordinal));
        Assert.Null(Assert.Single(tiles, t => t.Key == "devices-noncompliant").Value); // devices not read yet
        Assert.All(tiles, t => Assert.False(string.IsNullOrEmpty(t.Link)));

        var cut = RenderComponent<Start>();
        Assert.Equal(new[] { "blok-security", "blok-work", "blok-devices", "blok-hygiene" }, cut.FindAll("section[id^='blok-']").Select(s => s.Id));
        var tile = cut.Find("#tegel-reviews-due");
        Assert.Equal("toegangsreviews", tile.GetAttribute("href"));
        Assert.Contains("in 30 dagen", tile.TextContent, StringComparison.Ordinal);
        Assert.Contains("Toestellen verschijnen hier zodra ze ingelezen zijn", cut.Find("#blok-devices").TextContent, StringComparison.Ordinal);

        // Another period: the trend against 7 days ago (nothing kept then)
        cut.FindAll("#periode button").First(b => b.TextContent == "7 dagen geleden").Click();
        Assert.DoesNotContain("in 7 dagen", cut.Find("#tegel-reviews-due").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTileDrawsItsLineAndPasswordsThatExpireSoonAreWork()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        State.Database.SaveKpiSnapshot(today.AddDays(-40), new Dictionary<string, int> { ["overzicht:reviews-due"] = 9 }); // before the period
        State.Database.SaveKpiSnapshot(today.AddDays(-20), new Dictionary<string, int> { ["overzicht:reviews-due"] = 4 });
        State.Database.SaveKpiSnapshot(today.AddDays(-10), new Dictionary<string, int> { ["overzicht:reviews-due"] = 2 });

        var tile = State.OverviewTiles(30).Single(t => t.Key == "reviews-due");
        Assert.Equal(new[] { 4, 2 }, tile.History.Select(p => p.Value));
        var svg = RenderComponent<Start>().Find("#tegel-reviews-due svg");
        Assert.StartsWith("Verloop: van 4 op", svg.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.StartsWith("M", svg.QuerySelector("path")!.GetAttribute("d"), StringComparison.Ordinal);

        // Passwords that expire within 14 days: work, and only with a maximum age of passwords
        Assert.DoesNotContain(State.OverviewTiles(30), t => t.Key == "passwords-expiring");
        State.Config.PasswordMaxAgeDays = 90;
        var passwords = Assert.Single(State.OverviewTiles(30), t => t.Key == "passwords-expiring");
        Assert.Equal((OverviewBlock.Work, "wachtwoorden"), (passwords.Block, passwords.Link));
    }

    [Fact]
    public void TheLayoutFollowsTheRoleAndIsKeptPerPerson()
    {
        State.Config.ProfilesRequired = true;
        _factory.AppRoles = new[] { "Moss365.Helpdesk" };
        var cut = RenderComponent<Start>();
        Assert.Equal("blok-work", cut.FindAll("section[id^='blok-']").First().Id);

        cut.FindAll("#periode button").First(b => b.TextContent == "Indeling").Click();
        cut.Find("#indeling input[type=checkbox]").Change(false); // the first block (Werk) off
        cut.Find("#indeling button[aria-label='Beveiliging omhoog']").Click();
        cut = RenderComponent<Start>();
        Assert.DoesNotContain(cut.FindAll("section[id^='blok-']"), s => s.Id == "blok-work");
        Assert.Equal(OverviewBlock.Security, State.OverviewLayout.Order[1]);

        State.SaveOverviewLayout(null);
        Assert.Equal(Overview.DefaultLayout(State.Roles).Order, State.OverviewLayout.Order);
        Assert.Empty(State.OverviewLayout.Hidden);
    }

    [Fact]
    public void AReadKeepsTheCountsOfTheOverviewWithoutNames()
    {
        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        var kept = State.Database.ReadKpiSnapshot(DateOnly.FromDateTime(DateTime.Today));
        Assert.Contains("overzicht:reviews-due", kept.Keys);
        Assert.Contains("overzicht:data-quality", kept.Keys);
        Assert.DoesNotContain(kept.Keys, k => k.Contains('@') || k.Contains("peeters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheBoardSummaryIsOnePageWithoutNames()
    {
        State.Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today.AddDays(-30)), new Dictionary<string, int> { ["overzicht:reviews-due"] = 9 });
        var cut = RenderComponent<Start>();
        cut.FindAll("#periode button").First(b => b.TextContent == "Bestuursoverzicht opslaan").Click();

        var file = Directory.GetFiles(State.ExportFolder, "bestuursoverzicht-*.html").Single();
        var html = File.ReadAllText(file);
        Assert.Contains("<h1>Bestuursoverzicht Tenant</h1>", html, StringComparison.Ordinal);
        Assert.Contains("toegangsreviews te doen", html, StringComparison.Ordinal);
        Assert.Contains("in 30 dagen, beter", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Normen</h2>", html.Replace("<h2>Standaarden</h2>", "<h2>Normen</h2>", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("an.peeters", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(State.Database.ReadAccess(10), a => a.What == "Bestuursoverzicht opgeslagen");
        Assert.Contains(Path.GetFileName(file), cut.Markup, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> Fingerprint(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal))
            .ToDictionary(f => Path.GetRelativePath(root, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void AllTenantsShowsOnlyCountsAndStoresNothing()
    {
        var (hub, protector, environment) = EncryptedHub("alle-tenants");
        var first = hub.CurrentTenant;
        hub.Open(first).Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today), new Dictionary<string, int> { ["actief"] = 12, ["zonder-mfa"] = 3, ["overzicht:hardening"] = 74 });
        hub.Open(first).Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today.AddDays(-10)), new Dictionary<string, int> { ["actief"] = 10 });
        hub.Open(first).Database.SaveDocument("proef", "\"an.peeters@test.be\""); // a name in the tenant, never shown
        var second = hub.Add("Tweede organisatie");
        hub.Open(second).Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), new Dictionary<string, int> { ["actief"] = 5 });
        var third = hub.Add("Derde organisatie");
        var (key, _) = TenantHub.NewKey();
        hub.Encrypt(third, key, EncryptionMethod.WindowsAccount);
        hub.Dispose();

        // Another Windows user: the third tenant stays locked
        protector.User = 8;
        var later = Restart(environment, protector);
        var before = Fingerprint(environment.Root);
        var summaries = later.Summaries(new[] { "actief", "zonder-mfa", "overzicht:hardening" });

        var a = summaries.Single(s => s.Tenant.Id == first.Id);
        Assert.Equal((12, 3, 74), (a.Counts!.Values["actief"], a.Counts.Values["zonder-mfa"], a.Counts.Values["overzicht:hardening"]));
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), a.Counts.Date);
        Assert.Equal(5, summaries.Single(s => s.Tenant.Id == second.Id).Counts!.Values["actief"]);
        Assert.True(summaries.Single(s => s.Tenant.Id == third.Id).Locked);
        Assert.Empty(later.Opened); // no tenant was opened for it

        // The page needs only the hub (this test context has its services fixed already)
        using var context = new Bunit.TestContext();
        context.Services.AddSingleton(later);
        var cut = context.RenderComponent<AllTenantsPage>();
        Assert.Contains("Tweede organisatie", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("74%", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Vergrendeld", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("peeters", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@test.be", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("xport", StringComparison.Ordinal));

        // Nothing was written: every database and file is exactly as before
        Assert.Equal(before, Fingerprint(environment.Root));
        Assert.Empty(later.Opened);
        later.Dispose();
    }
}
