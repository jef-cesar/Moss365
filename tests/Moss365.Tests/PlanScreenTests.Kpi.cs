using System.Text.RegularExpressions;
using Bunit;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>#21: every KPI tile is clickable and the page behind it shows exactly the rows behind the number.</summary>
public sealed partial class PlanScreenTests
{
    private void LoadWithSecurity()
    {
        _factory.Security = new SecurityData(
            new[]
            {
                new MfaRegistration("e-an", "an.peeters@test.be", true, true, new[] { "microsoftAuthenticatorPush" }, "microsoftAuthenticatorPush", true),
                new MfaRegistration("e-bo", "bo.janssens@test.be", false, false, new[] { "email" }, "", true),
            },
            new[] { new RoleMember("Global Administrator", "e-bo", "BO JANSSENS", "bo.janssens@test.be", "user") });
        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
    }

    private IRenderedFragment OpenLink(string link)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(link);
        var currentPage = link.Split('?')[0];
        return currentPage switch
        {
            "gebruikers" => RenderComponent<Users>(),
            "postvakken" => RenderComponent<Mailboxes>(),
            "groepen" => RenderComponent<Groups>(),
            "rooms" => RenderComponent<Rooms>(),
            "logboek" => RenderComponent<ChangeLog>(),
            "conditional-access" => RenderComponent<CaOverview>(),
            _ => throw new InvalidOperationException("Onbekende pagina " + currentPage),
        };
    }

    /// <summary>All tiles and bars with a KPI link: (link, number on the tile).</summary>
    private static List<(string Link, int Number)> KpiLinks(IRenderedFragment cut)
    {
        var tiles = cut.FindAll("a.kb-tegel")
            .Where(a => a.GetAttribute("href")!.Contains("kpi=", StringComparison.Ordinal))
            .Select(a => (a.GetAttribute("href")!, int.Parse(a.QuerySelector(".kb-tegel-getal")!.TextContent.Trim())));
        var bars = cut.FindAll("li.kb-staaf")
            .Where(li => li.QuerySelector("a")?.GetAttribute("href")?.Contains("kpi=", StringComparison.Ordinal) == true)
            .Select(li => (li.QuerySelector("a")!.GetAttribute("href")!, int.Parse(li.QuerySelector(".kb-staaf-getal")!.TextContent.Trim().Split(' ')[0])));
        // The legend of a distribution and the table under a column chart
        var legend = cut.FindAll("ul.kb-legende li")
            .Where(li => li.QuerySelector("a")?.GetAttribute("href")?.Contains("kpi=", StringComparison.Ordinal) == true)
            .Select(li => (li.QuerySelector("a")!.GetAttribute("href")!, int.Parse(li.QuerySelector("strong")!.TextContent.Trim())));
        var tables = cut.FindAll("details.kb-als-tabel tbody tr")
            .Where(tr => tr.QuerySelector("a")?.GetAttribute("href")?.Contains("kpi=", StringComparison.Ordinal) == true)
            .Select(tr => (tr.QuerySelector("a")!.GetAttribute("href")!, int.Parse(tr.QuerySelectorAll("td")[1].TextContent.Trim())));
        return tiles.Concat(bars).Concat(legend).Concat(tables).ToList();
    }

    [Fact]
    public void EveryKpiTileShowsExactlyTheRowsBehindIt()
    {
        LoadWithSecurity();
        foreach (var (status, days) in new[] { ("Gelukt", 1), ("Gelukt", 2), ("Mislukt", 3), ("Mislukt", 20) })
        {
            State.ChangeLog.Write(new LogEntry(DateTimeOffset.Now.AddDays(-days), State.User, "test", "AD", "AdKenmerkenZetten", "CN=x", "x", "kenmerk",
                new Dictionary<string, string>(), status, "", Guid.NewGuid(), Guid.NewGuid()));
        }

        var sources = new IRenderedFragment[] { RenderComponent<Statistics>(), RenderComponent<Start>(), RenderComponent<Security>(), RenderComponent<CaOverview>() };

        // Every tile is clickable: no tile without a link on these pages any more (except the choice buttons of a list)
        foreach (var b in sources)
        {
            Assert.Empty(b.FindAll("div.kb-tegel"));
        }

        var links = sources.SelectMany(KpiLinks).Distinct().ToList();
        Assert.True(links.Count >= 30, $"maar {links.Count} KPI-links");
        Assert.Contains(links, l => l.Link == Kpis.Disabled.Link);
        Assert.Contains(links, l => l.Link == Kpis.WithoutMfa.Link && l.Number == 1);
        Assert.Contains(links, l => l.Link == Kpis.Succeeded7.Link && l.Number == 2);
        Assert.Contains(links, l => l.Link == Kpis.Failed7.Link && l.Number == 1);
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=afdeling%3A", StringComparison.Ordinal));
        Assert.Contains(links, l => l.Link == Kpis.MfaStatus("nee").Link && l.Number == 1);
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=aanmelding%3A", StringComparison.Ordinal));
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=kantoor%3A", StringComparison.Ordinal));
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=gastdomein%3A", StringComparison.Ordinal));

        // Licences: the number on the bar comes from Microsoft (assigned), the list from the loaded accounts
        foreach (var (link, number) in links.Where(l => !l.Link.Contains("licentie%3A", StringComparison.Ordinal)))
        {
            var currentPage = OpenLink(link);
            var kpi = Kpis.Find(Uri.UnescapeDataString(link.Split("kpi=")[1]))!;
            var list = kpi.Items switch { KpiList.CaPolicies => "ca-beleiden", KpiList.CaCheck => "ca-controle", _ => kpi.Page };
            var rows = currentPage.FindAll($"table[data-lijst='{list}'] tbody tr").Count;
            Assert.True(number == rows, $"{link}: tegel {number}, lijst {rows}");
            if (number > 0)
            {
                var filter = currentPage.Find(".kb-kpi-filter").TextContent;
                Assert.Contains(kpi.Label, filter);
                Assert.Matches(new Regex($@":\s*{number} rij"), filter);
            }
        }
    }

    [Fact]
    public void ChartsWithTheDivisionFilterAndTheTrend()
    {
        LoadWithSecurity();

        // After loading live, today's snapshot is in the database: only counts per KPI
        var snapshot = State.Database.ReadKpiSnapshot(DateOnly.FromDateTime(DateTime.Today));
        Assert.Equal(Kpis.Persons(Kpis.Active, State.KpiSource).Count, Assert.Single(snapshot[Kpis.Active.Key]).Value);
        Assert.DoesNotContain(snapshot.Keys, k => k.Contains('@') || k.Contains("janssens", StringComparison.OrdinalIgnoreCase));
        State.Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Today.AddDays(-40)), new Dictionary<string, int> { [Kpis.Active.Key] = 1 });

        State.ChangeLog.Write(new LogEntry(DateTimeOffset.Now, State.User, "test", "AD", "AdKenmerkenZetten", "CN=x", "x", "kenmerk",
            new Dictionary<string, string>(), "Gelukt", ""));

        var cut = RenderComponent<Statistics>();
        var active = Kpis.Persons(Kpis.Active, State.KpiSource).Count;
        Assert.Contains("Nog niets in deze periode", cut.Markup); // the test data has no creation dates: no empty chart
        Assert.Contains($"▲ {active - 1} t.o.v. 30 dagen", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".kb-verloop-lijn"));
        var weeks = Assert.Single(cut.FindAll("svg.kb-kolomgrafiek"));
        Assert.Equal(12, weeks.QuerySelectorAll("rect.kb-raak").Length);
        Assert.Contains("Gelukt", cut.Find("details.kb-als-tabel").TextContent);

        // Division filter: the charts count only that division, the tiles keep counting everything
        var division = cut.Find("select[aria-label='Filter op afdeling']");
        var choice = division.QuerySelectorAll("option").Select(o => o.GetAttribute("value")!).First(v => v.Length > 0);
        division.Change(choice);
        var link = cut.FindAll("ul.kb-legende li a").First(a => a.GetAttribute("href")!.Contains("aanmelding", StringComparison.Ordinal)).GetAttribute("href")!;
        Assert.Contains("afdeling-alle%3A", link);
        var kpi = Kpis.Find(Uri.UnescapeDataString(link.Split("kpi=")[1]))!;
        Assert.All(Kpis.Persons(kpi, State.KpiSource), p => Assert.Equal(choice, State.Config.DivisionFor(p.Department)?.Name ?? Statistic.NoDivision));
        Assert.Contains(cut.FindAll("a.kb-tegel"), a => a.GetAttribute("href") == Kpis.Active.Link && a.TextContent.Contains(active.ToString()));

        // The list behind a part with the filter
        var list = OpenLink(link);
        Assert.Equal(Kpis.Persons(kpi, State.KpiSource).Count, list.FindAll("table[data-lijst='gebruikers'] tbody tr").Count);
        Assert.Contains("afdeling " + choice, list.Find(".kb-kpi-filter").TextContent);
    }

    [Fact]
    public void KpiFilterClearsTheOtherFiltersAndStaysAfterBack()
    {
        // Own filters on the list first: they must not make the number behind the tile smaller
        State.TableSession("gebruikers").Filter("dienst").Text = "bestaat-niet";

        var nav = Services.GetRequiredService<NavigationManager>();
        var g = State.History;
        var cut = OpenLink(Kpis.Disabled.Link);
        g.Visited(nav.Uri);
        var expected = Kpis.Persons(Kpis.Disabled, State.KpiSource).Count;
        Assert.True(expected > 0);
        Assert.Equal(expected, cut.FindAll("tbody tr").Count);
        Assert.Contains("oud account", cut.Markup);
        Assert.DoesNotContain("Gasten tonen", cut.Markup); // the normal filters do not apply while the KPI filter is active

        // Searching within the KPI rows still works
        cut.Find("input.kb-breed").Input("niemand-zo");
        Assert.Contains("0 na je eigen filters", cut.Find(".kb-kpi-filter").TextContent);
        cut.Find("input.kb-breed").Input(string.Empty);

        // To another page and back with Back: the filter stays
        nav.NavigateTo("statistieken");
        g.Visited(nav.Uri);
        nav.NavigateTo(g.Back()!);
        Assert.True(g.IsJumpTo(nav.Uri));
        var back = RenderComponent<Users>();
        Assert.Contains("Gefilterd op", back.Markup);

        // Clear: the normal list (active users) comes back
        back.FindAll("button").First(b => b.TextContent == "Filter wissen").Click();
        Assert.DoesNotContain("Gefilterd op", back.Markup);
        Assert.Contains("Gasten tonen", back.Markup);
        Assert.DoesNotContain("oud account", back.Markup);
    }
}
