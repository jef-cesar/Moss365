using Bunit;
using Moss365.Core.Inventaris;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor 0.8: Entra-ID, profielen, bewerken, nieuwe groep, taken voor een selectie.</summary>
public sealed partial class PlanSchermTests
{
    [Fact]
    public void ElkObjectToontZijnEntraId()
    {
        var persoon = State.Data.ZoekPersoon("ad:cas.vos")!;
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:cas.vos"));
        Assert.Contains($"<code>{persoon.Entra!.Id}</code>", cut.Find(".kb-entra-id").InnerHtml);

        // Een groep die enkel in AD bestaat, heeft geen Entra-ID
        var adGroep = State.Data.Index.Groepen.First(g => g.Cloud is null);
        Assert.Null(State.Data.Index.EntraIdVan(adGroep.Ref));
        var cloud = State.Data.Index.Groepen.First(g => g.Cloud is not null);
        Assert.Equal(cloud.Cloud!.Id, State.Data.Index.EntraIdVan(cloud.Ref));

        // In de lijst als kolom die je zelf aanzet
        var lijst = RenderComponent<Gebruikers>();
        lijst.FindAll("button").First(b => b.TextContent == "Kolommen").Click();
        lijst.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("Entra-ID")).QuerySelector("input")!.Change(true);
        Assert.Contains(persoon.Entra.Id, lijst.Markup);
    }

    [Fact]
    public void EenLezerZietAllesMaarWijzigtNiets()
    {
        State.Config.ProfielenVerplicht = true;
        _fabriek.AppRollen = new[] { "Moss365.Lezer" };

        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        Assert.DoesNotContain("Wachtwoord opnieuw instellen", cut.Markup);
        Assert.DoesNotContain(">uitstroom<", cut.Markup);

        var lijst = RenderComponent<Gebruikers>();
        Assert.True(lijst.FindAll("button").First(b => b.TextContent == "Exporteren").HasAttribute("hidden"));

        // Een plan dat toch op het scherm komt: niets aan te vinken, met de reden
        var plan = new List<Moss365.Core.Wijzigingen.Wijziging>
        {
            new() { Systeem = Moss365.Core.Wijzigingen.Systeem.ActiveDirectory, Actie = Moss365.Core.Wijzigingen.Actie.AdAccountUitschakelen, Doel = "CN=x", Omschrijving = "uitschakelen" },
        };
        var weergave = RenderComponent<Moss365.UI.Componenten.PlanWeergave>(p => p.Add(x => x.Plan, plan).Add(x => x.Context, "test"));
        Assert.Contains("Niet voor jouw profiel", weergave.Markup);
        Assert.False(plan[0].Geselecteerd);

        // Organisatie: geen toegang; menu zonder Acties
        var layout = RenderMetLayout<Organisatie>("organisatie");
        Assert.Contains("Geen toegang", layout.Markup);
        Assert.Contains("Profiel: Lezer", layout.Markup);
        Assert.DoesNotContain(layout.FindAll("nav.kb-nav a"), a => a.TextContent.Trim().StartsWith("Uitstroom", StringComparison.Ordinal));
    }

    [Fact]
    public void HelpdeskZietEnkelDeEigenWijzigingenInHetLogboek()
    {
        State.Logboek.Schrijf(new Moss365.Core.Wijzigingen.LogRegel(DateTimeOffset.Now, "beheerder@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "eigen", "o", new Dictionary<string, string>(), "Gelukt", ""));
        State.Logboek.Schrijf(new Moss365.Core.Wijzigingen.LogRegel(DateTimeOffset.Now, "collega@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "andermans", "o", new Dictionary<string, string>(), "Gelukt", ""));
        _fabriek.AppRollen = new[] { "Moss365.Helpdesk" };

        var cut = RenderComponent<Logboek>();
        Assert.Contains("eigen", cut.Markup);
        Assert.DoesNotContain("andermans", cut.Markup);
        Assert.DoesNotContain("Gekozen wijzigingen ongedaan maken", cut.Markup);

        _fabriek.AppRollen = new[] { "Moss365.Auditor" };
        Assert.Contains("andermans", RenderComponent<Logboek>().Markup);
    }

    [Fact]
    public void ZonderProfielenMagIedereenAllesMetEenMelding()
    {
        var layout = RenderMetLayout<Gebruikers>("gebruikers");
        Assert.Contains("Profielen nog niet ingesteld", layout.Markup);
        Assert.True(State.Mag(Moss365.Core.Rechten.Recht.Beheren));
    }

    [Fact]
    public void GegevensVanEenGebruikerBewerken()
    {
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:cas.vos"));
        cut.FindAll("button").First(b => b.TextContent == "Bewerken").Click();
        cut.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Functie", StringComparison.Ordinal)).QuerySelector("input")!.Change("Maatschappelijk werker");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Functie: Maatschappelijk werker", cut.Find("section.kb-plan").TextContent);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Moss365.Core.Wijzigingen.Actie.AdKenmerkenZetten && w.P("title") == "Maatschappelijk werker"));
    }

    [Fact]
    public void NieuweGroepAanmakenVanuitDeLijst()
    {
        State.Config.GroepenOu = "GEMEENTEBESTUUR/GROEPEN";
        Assert.Contains("groepen/nieuw", RenderComponent<Groepen>().Markup);

        var cut = RenderComponent<NieuweGroep>();
        cut.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Naam", StringComparison.Ordinal)).QuerySelector("input")!.Change("gg_scherm_test");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("AD-beveiligingsgroep gg_scherm_test aanmaken", cut.Find("section.kb-plan").TextContent);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Moss365.Core.Wijzigingen.Actie.AdGroepAanmaken));
    }

    [Fact]
    public void TakenVoorDeGekozenGebruikersEnEenExtraBevestigingBijEenGrootPlan()
    {
        var cut = RenderComponent<Gebruikers>();
        Assert.DoesNotContain("Met de", cut.Markup);
        cut.FindAll("tbody td.kb-kies input")[0].Click();
        cut.FindAll("tbody td.kb-kies input")[1].Click();
        Assert.Contains("Met de 2 gekozen gebruikers", cut.Markup);

        cut.Find(".kb-bulk input[list]").Change("bibliotheek");
        cut.FindAll(".kb-bulk button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Kantoor wordt bibliotheek", cut.Markup);

        // Een groot plan (meer dan 50 wijzigingen) vraagt een extra vinkje
        var plan = Enumerable.Range(0, 51).Select(i => new Moss365.Core.Wijzigingen.Wijziging
        {
            Systeem = Moss365.Core.Wijzigingen.Systeem.ActiveDirectory, Actie = Moss365.Core.Wijzigingen.Actie.AdAccountOntgrendelen,
            Doel = $"CN=x{i}", Omschrijving = "ontgrendelen",
        }).ToList();
        var weergave = RenderComponent<Moss365.UI.Componenten.PlanWeergave>(p => p.Add(x => x.Plan, plan).Add(x => x.Context, "groot"));
        weergave.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        Assert.True(weergave.FindAll("button").First(b => b.TextContent == "Ja, uitvoeren").HasAttribute("disabled"));
        weergave.Find(".kb-groot-plan input").Change(true);
        Assert.False(weergave.FindAll("button").First(b => b.TextContent == "Ja, uitvoeren").HasAttribute("disabled"));
    }

    // ---------------------------------------------------------------- logs, bewaartermijn, automatisch vernieuwen (#17)

    private string TechnischLog()
    {
        var tekst = new System.Text.StringBuilder();
        foreach (var pad in Directory.Exists(State.LogMap) ? Directory.GetFiles(State.LogMap) : Array.Empty<string>())
        {
            using var f = new FileStream(pad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            tekst.Append(new StreamReader(f).ReadToEnd());
        }

        return tekst.ToString();
    }

    [Fact]
    public void HetTechnischLogBevatGeenGeheimen()
    {
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        cut.FindAll("button").First(b => b.TextContent == "Wachtwoord opnieuw instellen").Click();
        var wachtwoord = cut.Find(".kb-kopieer input").GetAttribute("value")!;
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        State.AdWachtwoord = "AD-Geheim-987!";
        WachtOpTaak(); // het object vernieuwen na het plan loopt nog als achtergrondtaak
        Assert.True(State.StartLaden(Moss365.UI.Services.LaadSoort.AdEnEntra));
        WachtOpTaak();

        var log = TechnischLog();
        Assert.Contains("gestart", log);
        Assert.Contains("Plan uitgevoerd", log);
        Assert.Contains("Live laden (AD en Entra) klaar", log);
        Assert.DoesNotContain(wachtwoord, log);
        Assert.DoesNotContain("AD-Geheim-987!", log);
        // Geen namen of accounts, enkel wat er gebeurde
        Assert.DoesNotContain("janssens", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportsEnAanmeldingenKomenInHetToegangslog()
    {
        RenderComponent<Gebruikers>().FindAll("button").First(b => b.TextContent == "Exporteren").Click();
        var export = Assert.Single(State.Databank.LeesToegang(10));
        Assert.Equal("Export", export.Wat);
        Assert.StartsWith("gebruikers: ", export.Details);

        var beveiliging = RenderComponent<Beveiliging>();
        Assert.Contains(State.Databank.LeesToegang(10), r => r.Wat == "Beveiliging bekeken");

        var logboek = RenderComponent<Logboek>();
        Assert.Contains("Toegangslog", logboek.Markup);
        Assert.Contains("Beveiliging bekeken", logboek.Markup);
    }

    [Fact]
    public void OudeLogboekregelsEerstArchiverenDanWeghalen()
    {
        State.Logboek.Schrijf(new Moss365.Core.Wijzigingen.LogRegel(DateTimeOffset.Now.AddYears(-3), "oud@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "heel oud", "o", new Dictionary<string, string>(), "Gelukt", ""));
        State.Logboek.Schrijf(new Moss365.Core.Wijzigingen.LogRegel(DateTimeOffset.Now, "nu@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "recent", "o", new Dictionary<string, string>(), "Gelukt", ""));

        var cut = RenderComponent<Logboek>();
        Assert.Contains("<strong>1</strong> regels ouder dan", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Uit de databank halen"); // eerst archiveren
        cut.FindAll("button").First(b => b.TextContent == "Archiveren naar CSV").Click();
        var csv = Assert.Single(Directory.GetFiles(State.Omgeving.ExportMap, "Logboek-archief-*.csv"));
        Assert.Contains("heel oud", File.ReadAllText(csv));
        Assert.DoesNotContain("recent", File.ReadAllText(csv));

        cut.FindAll("button").First(b => b.TextContent == "Uit de databank halen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, weghalen").Click();
        Assert.Equal(1, State.Databank.AantalLog());
        Assert.Equal("recent", State.Logboek.Recent(5).Single().DoelNaam);
    }

    [Fact]
    public async Task AutomatischVernieuwenEnkelAlsHetMagEnZonderAanmeldlink()
    {
        var nu = DateTime.Now;
        Assert.False(await State.AutoVernieuwenAsync(nu)); // staat uit

        State.Instellingen.AutoVernieuwen = true;
        Assert.False(await State.AutoVernieuwenAsync(nu)); // nog niet live geladen

        Assert.True(State.StartLaden(Moss365.UI.Services.LaadSoort.AdEnEntra));
        WachtOpTaak();
        Assert.False(await State.AutoVernieuwenAsync(DateTime.Now.AddMinutes(5))); // te kort geleden
        Assert.True(await State.AutoVernieuwenAsync(DateTime.Now.AddMinutes(16)));
        WachtOpTaak();
        Assert.Equal(2, _fabriek.AdBases.Count);
    }
}
