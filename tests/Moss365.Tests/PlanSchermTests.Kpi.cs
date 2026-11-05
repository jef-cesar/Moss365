using System.Text.RegularExpressions;
using Bunit;
using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>#21: elke KPI-tegel is klikbaar en de pagina erachter toont precies de rijen achter het getal.</summary>
public sealed partial class PlanSchermTests
{
    private void LaadMetBeveiliging()
    {
        _fabriek.Beveiliging = new BeveiligingGegevens(
            new[]
            {
                new MfaRegistratie("e-an", "an.peeters@test.be", true, true, new[] { "microsoftAuthenticatorPush" }, "microsoftAuthenticatorPush", true),
                new MfaRegistratie("e-bo", "bo.janssens@test.be", false, false, new[] { "email" }, "", true),
            },
            new[] { new RolLid("Global Administrator", "e-bo", "BO JANSSENS", "bo.janssens@test.be", "user") });
        Assert.True(State.StartLaden(LaadSoort.AdEnEntra));
        WachtOpTaak();
    }

    private IRenderedFragment OpenLink(string link)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(link);
        var pagina = link.Split('?')[0];
        return pagina switch
        {
            "gebruikers" => RenderComponent<Gebruikers>(),
            "postvakken" => RenderComponent<Postvakken>(),
            "groepen" => RenderComponent<Groepen>(),
            "rooms" => RenderComponent<Rooms>(),
            "logboek" => RenderComponent<Logboek>(),
            "conditional-access" => RenderComponent<CaOverzicht>(),
            _ => throw new InvalidOperationException("Onbekende pagina " + pagina),
        };
    }

    /// <summary>Alle tegels en staven met een KPI-link: (link, getal op de tegel).</summary>
    private static List<(string Link, int Getal)> KpiLinks(IRenderedFragment cut)
    {
        var tegels = cut.FindAll("a.kb-tegel")
            .Where(a => a.GetAttribute("href")!.Contains("kpi=", StringComparison.Ordinal))
            .Select(a => (a.GetAttribute("href")!, int.Parse(a.QuerySelector(".kb-tegel-getal")!.TextContent.Trim())));
        var staven = cut.FindAll("li.kb-staaf")
            .Where(li => li.QuerySelector("a")?.GetAttribute("href")?.Contains("kpi=", StringComparison.Ordinal) == true)
            .Select(li => (li.QuerySelector("a")!.GetAttribute("href")!, int.Parse(li.QuerySelector(".kb-staaf-getal")!.TextContent.Trim().Split(' ')[0])));
        // De legende van een verdeling en de tabel onder een kolomgrafiek
        var legende = cut.FindAll("ul.kb-legende li")
            .Where(li => li.QuerySelector("a")?.GetAttribute("href")?.Contains("kpi=", StringComparison.Ordinal) == true)
            .Select(li => (li.QuerySelector("a")!.GetAttribute("href")!, int.Parse(li.QuerySelector("strong")!.TextContent.Trim())));
        var tabellen = cut.FindAll("details.kb-als-tabel tbody tr")
            .Where(tr => tr.QuerySelector("a")?.GetAttribute("href")?.Contains("kpi=", StringComparison.Ordinal) == true)
            .Select(tr => (tr.QuerySelector("a")!.GetAttribute("href")!, int.Parse(tr.QuerySelectorAll("td")[1].TextContent.Trim())));
        return tegels.Concat(staven).Concat(legende).Concat(tabellen).ToList();
    }

    [Fact]
    public void ElkeKpiTegelToontPreciesDeRijenErachter()
    {
        LaadMetBeveiliging();
        foreach (var (status, dagen) in new[] { ("Gelukt", 1), ("Gelukt", 2), ("Mislukt", 3), ("Mislukt", 20) })
        {
            State.Logboek.Schrijf(new LogRegel(DateTimeOffset.Now.AddDays(-dagen), State.Gebruiker, "test", "AD", "AdKenmerkenZetten", "CN=x", "x", "kenmerk",
                new Dictionary<string, string>(), status, "", Guid.NewGuid(), Guid.NewGuid()));
        }

        var bronnen = new IRenderedFragment[] { RenderComponent<Statistieken>(), RenderComponent<Start>(), RenderComponent<Beveiliging>(), RenderComponent<CaOverzicht>() };

        // Elke tegel is klikbaar: geen tegel zonder link meer op deze pagina's (behalve de keuzeknoppen van een lijst)
        foreach (var b in bronnen)
        {
            Assert.Empty(b.FindAll("div.kb-tegel"));
        }

        var links = bronnen.SelectMany(KpiLinks).Distinct().ToList();
        Assert.True(links.Count >= 30, $"maar {links.Count} KPI-links");
        Assert.Contains(links, l => l.Link == Kpis.Uitgeschakeld.Link);
        Assert.Contains(links, l => l.Link == Kpis.ZonderMfa.Link && l.Getal == 1);
        Assert.Contains(links, l => l.Link == Kpis.Gelukt7.Link && l.Getal == 2);
        Assert.Contains(links, l => l.Link == Kpis.Mislukt7.Link && l.Getal == 1);
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=afdeling%3A", StringComparison.Ordinal));
        Assert.Contains(links, l => l.Link == Kpis.MfaStatus("nee").Link && l.Getal == 1);
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=aanmelding%3A", StringComparison.Ordinal));
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=kantoor%3A", StringComparison.Ordinal));
        Assert.Contains(links, l => l.Link.StartsWith("gebruikers?kpi=gastdomein%3A", StringComparison.Ordinal));

        // Licenties: het getal op de staaf komt van Microsoft (toegewezen), de lijst van de geladen accounts
        foreach (var (link, getal) in links.Where(l => !l.Link.Contains("licentie%3A", StringComparison.Ordinal)))
        {
            var pagina = OpenLink(link);
            var kpi = Kpis.Zoek(Uri.UnescapeDataString(link.Split("kpi=")[1]))!;
            var lijst = kpi.Lijst switch { KpiLijst.CaBeleiden => "ca-beleiden", KpiLijst.CaControle => "ca-controle", _ => kpi.Pagina };
            var rijen = pagina.FindAll($"table[data-lijst='{lijst}'] tbody tr").Count;
            Assert.True(getal == rijen, $"{link}: tegel {getal}, lijst {rijen}");
            if (getal > 0)
            {
                var filter = pagina.Find(".kb-kpi-filter").TextContent;
                Assert.Contains(kpi.Label, filter);
                Assert.Matches(new Regex($@":\s*{getal} rij"), filter);
            }
        }
    }

    [Fact]
    public void GrafiekenMetDeAfdelingsfilterEnHetVerloop()
    {
        LaadMetBeveiliging();

        // Na het live laden staat de stand van vandaag in de databank: enkel aantallen per KPI
        var stand = State.Databank.LeesKpiStand(DateOnly.FromDateTime(DateTime.Today));
        Assert.Equal(Kpis.Personen(Kpis.Actief, State.KpiBron).Count, Assert.Single(stand[Kpis.Actief.Sleutel]).Waarde);
        Assert.DoesNotContain(stand.Keys, k => k.Contains('@') || k.Contains("janssens", StringComparison.OrdinalIgnoreCase));
        State.Databank.BewaarKpiStand(DateOnly.FromDateTime(DateTime.Today.AddDays(-40)), new Dictionary<string, int> { [Kpis.Actief.Sleutel] = 1 });

        State.Logboek.Schrijf(new LogRegel(DateTimeOffset.Now, State.Gebruiker, "test", "AD", "AdKenmerkenZetten", "CN=x", "x", "kenmerk",
            new Dictionary<string, string>(), "Gelukt", ""));

        var cut = RenderComponent<Statistieken>();
        var actief = Kpis.Personen(Kpis.Actief, State.KpiBron).Count;
        Assert.Contains("Nog niets in deze periode", cut.Markup); // de testgegevens hebben geen aanmaakdatums: geen lege grafiek
        Assert.Contains($"▲ {actief - 1} t.o.v. 30 dagen", cut.Markup);
        Assert.NotEmpty(cut.FindAll(".kb-verloop-lijn"));
        var weken = Assert.Single(cut.FindAll("svg.kb-kolomgrafiek"));
        Assert.Equal(12, weken.QuerySelectorAll("rect.kb-raak").Length);
        Assert.Contains("Gelukt", cut.Find("details.kb-als-tabel").TextContent);

        // Afdelingsfilter: de grafieken tellen enkel die afdeling, de tegels blijven alles tellen
        var afdeling = cut.Find("select[aria-label='Filter op afdeling']");
        var keuze = afdeling.QuerySelectorAll("option").Select(o => o.GetAttribute("value")!).First(v => v.Length > 0);
        afdeling.Change(keuze);
        var link = cut.FindAll("ul.kb-legende li a").First(a => a.GetAttribute("href")!.Contains("aanmelding", StringComparison.Ordinal)).GetAttribute("href")!;
        Assert.Contains("afdeling-alle%3A", link);
        var kpi = Kpis.Zoek(Uri.UnescapeDataString(link.Split("kpi=")[1]))!;
        Assert.All(Kpis.Personen(kpi, State.KpiBron), p => Assert.Equal(keuze, State.Config.AfdelingVoor(p.Dienst)?.Naam ?? Statistiek.GeenAfdeling));
        Assert.Contains(cut.FindAll("a.kb-tegel"), a => a.GetAttribute("href") == Kpis.Actief.Link && a.TextContent.Contains(actief.ToString()));

        // De lijst achter een deel met de filter
        var lijst = OpenLink(link);
        Assert.Equal(Kpis.Personen(kpi, State.KpiBron).Count, lijst.FindAll("table[data-lijst='gebruikers'] tbody tr").Count);
        Assert.Contains("afdeling " + keuze, lijst.Find(".kb-kpi-filter").TextContent);
    }

    [Fact]
    public void KpiFilterWistDeAndereFiltersEnBlijftNaTerug()
    {
        // Eerst eigen filters op de lijst: die mogen het aantal achter de tegel niet verkleinen
        State.TabelSessie("gebruikers").Filter("dienst").Tekst = "bestaat-niet";

        var nav = Services.GetRequiredService<NavigationManager>();
        var g = State.Geschiedenis;
        var cut = OpenLink(Kpis.Uitgeschakeld.Link);
        g.Bezocht(nav.Uri);
        var verwacht = Kpis.Personen(Kpis.Uitgeschakeld, State.KpiBron).Count;
        Assert.True(verwacht > 0);
        Assert.Equal(verwacht, cut.FindAll("tbody tr").Count);
        Assert.Contains("oud account", cut.Markup);
        Assert.DoesNotContain("Gasten tonen", cut.Markup); // de gewone filters gelden niet zolang de KPI-filter actief is

        // Zoeken binnen de KPI-rijen kan nog
        cut.Find("input.kb-breed").Input("niemand-zo");
        Assert.Contains("0 na je eigen filters", cut.Find(".kb-kpi-filter").TextContent);
        cut.Find("input.kb-breed").Input(string.Empty);

        // Naar een andere pagina en met Terug terug: de filter blijft
        nav.NavigateTo("statistieken");
        g.Bezocht(nav.Uri);
        nav.NavigateTo(g.Terug()!);
        Assert.True(g.IsSprongNaar(nav.Uri));
        var terug = RenderComponent<Gebruikers>();
        Assert.Contains("Gefilterd op", terug.Markup);

        // Wissen: de gewone lijst (actieve gebruikers) komt terug
        terug.FindAll("button").First(b => b.TextContent == "Filter wissen").Click();
        Assert.DoesNotContain("Gefilterd op", terug.Markup);
        Assert.Contains("Gasten tonen", terug.Markup);
        Assert.DoesNotContain("oud account", terug.Markup);
    }
}
