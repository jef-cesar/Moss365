using System.Text.Json.Nodes;
using Bunit;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor 0.9: Conditional Access beheren.</summary>
public sealed partial class PlanSchermTests
{
    private IRenderedComponent<CaOverzicht> Beheren()
    {
        LaadMetCa();
        var cut = RenderComponent<CaOverzicht>();
        cut.FindAll("button").First(b => b.TextContent == "Beheren").Click();
        return cut;
    }

    [Fact]
    public void ZonderNoodaccountsZegtBeherenWatEerstMoet()
    {
        var cut = Beheren();
        Assert.Contains("geen noodaccounts", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Nieuw beleid").Click();
        cut.Find("select").Change("mfa-iedereen");
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));
    }

    [Fact]
    public void AanzettenVraagtEenExtraBevestigingEnGaatNaarGraph()
    {
        State.Config.Noodaccounts.Add("info@test.onmicrosoft.com");
        var cut = Beheren();
        Assert.Contains("Noodaccounts: Info", cut.Markup);

        // "Beheerders phishingbestendig" staat op alleen rapport; Aanzetten kan pas na het vinkje
        var rij = cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Beheerders phishingbestendig"));
        Assert.True(rij.QuerySelectorAll("button").First(b => b.TextContent == "Aanzetten").HasAttribute("disabled"));
        cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("effect in alleen rapport bekeken")).Change(true);
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Beheerders phishingbestendig"))
            .QuerySelectorAll("button").First(b => b.TextContent == "Aanzetten").Click();
        Assert.Contains("op aan zetten", cut.Markup);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        WachtOpTaak(); // Conditional Access wordt na het plan opnieuw gelezen

        var w = Assert.Single(_fabriek.Uitgevoerd, x => x.Actie == Actie.GraphCaBeleidWijzigen);
        Assert.Equal("ca-beheer", w.Doel);
        Assert.Equal("enabled", JsonNode.Parse(w.P("json"))!["state"]!.GetValue<string>());

        // Uitzetten kan altijd (een beleid uit sluit niemand buiten)
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Buitenland blokkeren"))
            .QuerySelectorAll("button").First(b => b.TextContent == "Uitzetten").Click();
        Assert.Contains("\"Buitenland blokkeren\" op uit zetten", cut.Markup);
    }

    [Fact]
    public void UitzonderingMetEinddatumStaatDaarnaKlaarBijGepland()
    {
        State.Config.Noodaccounts.Add("info@test.onmicrosoft.com");
        var cut = Beheren();
        cut.FindAll("button").First(b => b.TextContent == "Uitzonderingen").Click();
        cut.FindAll("select").First(s => s.QuerySelectorAll("option").Any(o => o.GetAttribute("value") == "ca-mfa")).Change("ca-mfa");
        Assert.Contains("Rechtstreeks uitgezonderd van \"MFA voor iedereen\" (1)", cut.Markup);
        Assert.Contains("noodaccount", cut.Find(".kb-lijst-plat").TextContent);
        Kies(cut, "Wie", "cas", "cas vos");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("cas vos uitzonderen van", cut.Markup);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        WachtOpTaak(); // Conditional Access wordt na het plan opnieuw gelezen

        var w = Assert.Single(_fabriek.Uitgevoerd, x => x.Actie == Actie.GraphCaBeleidWijzigen);
        Assert.Equal(new[] { "e-info", "e-cas" }, JsonNode.Parse(w.P("json"))!["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        var gepland = Assert.Single(State.Gepland.Lees(), a => a.Soort == GeplandSoort.CaUitzonderingEinde);
        Assert.Equal("ca-mfa", gepland.CaBeleidId);
        Assert.Equal(DateTime.Today.AddDays(14).Date, gepland.Wanneer.Date);
        Assert.Contains("Einde uitzondering op \"MFA voor iedereen\"", gepland.Titel);
    }

    [Fact]
    public void NieuwBeleidUitEenSjabloonEnEenBenoemdeLocatie()
    {
        State.Config.Noodaccounts.Add("info@test.onmicrosoft.com");
        var cut = Beheren();
        cut.FindAll("button").First(b => b.TextContent == "Nieuw beleid").Click();
        cut.FindAll("select").First(s => s.QuerySelectorAll("option").Any(o => o.GetAttribute("value") == "toestelcode-blokkeren")).Change("toestelcode-blokkeren");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("in alleen rapport", cut.Markup);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        WachtOpTaak(); // Conditional Access wordt na het plan opnieuw gelezen
        var nieuw = Assert.Single(_fabriek.Uitgevoerd, x => x.Actie == Actie.GraphCaBeleidMaken);
        Assert.Equal("enabledForReportingButNotEnforced", JsonNode.Parse(nieuw.P("json"))!["state"]!.GetValue<string>());

        cut.FindAll("button").First(b => b.TextContent == "Benoemde locaties").Click();
        Veld(cut, "Naam").Change("Bibliotheek");
        Veld(cut, "IP-bereiken", "textarea").Change("198.51.100.7/32\nfout");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("\"fout\" is geen IP-bereik", cut.Markup);
        Veld(cut, "IP-bereiken", "textarea").Change("198.51.100.7/32");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Nieuwe benoemde locatie \"Bibliotheek\" (vertrouwd)", cut.Markup);
    }
}
