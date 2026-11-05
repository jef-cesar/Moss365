using Bunit;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor 0.5: exporteren, helpdesk, ongedaan maken, inplannen, opkuis, beveiliging en snel laden.</summary>
public sealed partial class PlanSchermTests
{
    private string ExportMap => Path.Combine(_basis, "eigen", "export");

    [Fact]
    public void LijstExporterenNaarCsv()
    {
        var cut = RenderComponent<Gebruikers>();
        cut.FindAll("button").First(b => b.TextContent == "Exporteren").Click();

        var bestand = Assert.Single(Directory.GetFiles(ExportMap, "Gebruikers-*.csv"));
        var regels = File.ReadAllLines(bestand);
        Assert.Equal("\"Naam\";\"UPN\";\"Bron\";\"OU\";\"Dienst\";\"Afdeling\";\"Status\"", regels[0].TrimStart('﻿'));
        Assert.Equal(7, regels.Length); // kop + de 6 actieve gebruikers
        Assert.Contains("Tonen in Verkenner", cut.Markup);
    }

    [Fact]
    public void WachtwoordResettenZonderHetTeLoggen()
    {
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        cut.FindAll("button").First(b => b.TextContent == "Wachtwoord opnieuw instellen").Click();
        var wachtwoord = cut.Find(".kb-kopieer input").GetAttribute("value")!;
        Assert.Equal(16, wachtwoord.Length);

        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Nieuw wachtwoord instellen", cut.Find("section.kb-plan").TextContent);
        Assert.Contains("Account ontgrendelen", cut.Find("section.kb-plan").TextContent);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        var instellen = Assert.Single(_fabriek.Uitgevoerd, w => w.Actie == Actie.AdWachtwoordInstellen);
        Assert.Equal("True", instellen.P("wijzigenBijAanmelding"));
        Assert.DoesNotContain(instellen.Parameters.Values, v => v == wachtwoord);

        // Het wachtwoord staat klaar om door te geven, maar niet in het logboek
        Assert.Contains($"Nieuw wachtwoord voor", cut.Markup);
        Assert.Equal(wachtwoord, cut.Find(".kb-geheim .kb-kopieer input").GetAttribute("value"));
        var log = DatabankOpSchijf();
        Assert.Contains("AdWachtwoordInstellen", log);
        Assert.DoesNotContain(wachtwoord, log);
    }

    [Fact]
    public void WijzigingOngedaanMakenVanuitHetLogboek()
    {
        var groep = RenderComponent<GroepDetail>(p => p.Add(x => x.Sleutel, "cloud:g-team"));
        Kies(groep, "Lid toevoegen", "cas", "cas vos");
        Bevestig(groep);
        groep.WaitForAssertion(() => Assert.Contains("gelukt,", groep.Markup));
        WachtOpTaak();
        _fabriek.Uitgevoerd.Clear();

        var cut = RenderComponent<Logboek>();

        // Het vernieuwen op de achtergrond kan de pagina nog net opnieuw tekenen: aanvinken tot de knop het telt
        cut.WaitForAssertion(() =>
        {
            var vak = cut.Find("tbody td.kb-kies input:not([disabled])");
            if (!vak.HasAttribute("checked"))
            {
                vak.Click();
            }

            Assert.Contains(cut.FindAll("button"), b => b.TextContent.StartsWith("Gekozen wijzigingen ongedaan maken (1)", StringComparison.Ordinal));
        });
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Gekozen wijzigingen ongedaan maken (1)", StringComparison.Ordinal)).Click();
        Assert.Contains("Terug uit DNST_CULTUUR halen", cut.Find("section.kb-plan").TextContent);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        var terug = Assert.Single(_fabriek.Uitgevoerd);
        Assert.Equal(Actie.GraphGroepLidVerwijderen, terug.Actie);
        Assert.Equal("g-team", terug.P("groepId"));
        Assert.Contains(State.Logboek.Recent(10), r => r.Context.StartsWith("Ongedaan maken:", StringComparison.Ordinal));
    }

    [Fact]
    public void UitstroomInplannenEnLaterUitvoeren()
    {
        var uit = RenderComponent<Uitstroom>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        uit.FindAll("button").First(b => b.TextContent == "Inplannen").Click();
        Assert.Contains("Ingepland op", uit.Markup);
        var gepland = Assert.Single(State.Gepland.Lees());
        Assert.Equal(GeplandSoort.Uitstroom, gepland.Soort);
        Assert.Empty(_fabriek.Uitgevoerd);

        // Het moment is voorbij: het menu meldt het, de pagina toont het plan pas op vraag
        gepland.Wanneer = DateTimeOffset.Now.AddMinutes(-5);
        State.Gepland.Bewaar(new[] { gepland });
        var layout = RenderComponent<Moss365.UI.Layout.MainLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<p>x</p>")));
        Assert.Contains("1 geplande actie(s) klaar om uit te voeren", layout.Markup);

        var cut = RenderComponent<Gepland>();
        Assert.Contains("klaar om uit te voeren", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Plan tonen").Click();
        Assert.Contains("AD-account uitschakelen", cut.Find("section.kb-plan").TextContent);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("Uitgevoerd en uit Gepland gehaald", cut.Markup));
        Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.AdAccountUitschakelen);
        Assert.Empty(State.Gepland.Lees());
    }

    [Fact]
    public void InstroomInplannenMaaktHetWachtwoordPasBijHetUitvoeren()
    {
        var voorstel = PlanBouwer.NieuweGebruiker(State.Data, State.Config, new NieuweGebruikerAanvraag
        {
            Populatie = "Vrijwilliger", Voornaam = "Piet", Achternaam = "Goossens",
        });
        Assert.True(voorstel.KanUitgevoerdWorden, string.Join("; ", voorstel.Fouten));
        State.Inplannen(GeplandPlannen.Instroom(voorstel, DateTimeOffset.Now.AddMinutes(-1), "test"));
        var bewaard = State.Databank.LeesDocument("gepland")!;
        Assert.DoesNotContain(voorstel.Wachtwoord, DatabankOpSchijf());
        Assert.DoesNotContain(voorstel.Wachtwoord, bewaard);

        var cut = RenderComponent<Gepland>();
        Assert.True(cut.FindAll("button").Any(b => b.TextContent == "Plan tonen"), cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Plan tonen").Click();
        Assert.True(cut.FindAll("button").Any(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)), cut.Markup);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("Startwachtwoord", cut.Markup));
        var aanmaken = Assert.Single(_fabriek.Uitgevoerd, w => w.Actie == Actie.AdGebruikerAanmaken);
        Assert.Equal("piet.goossens", aanmaken.P("sAMAccountName"));
        Assert.NotEqual(voorstel.Wachtwoord, cut.Find(".kb-geheim .kb-kopieer input").GetAttribute("value"));
    }

    [Fact]
    public void OpkuisrondeMetRapport()
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("opkuis?dagen=1");
        var cut = RenderComponent<Opkuis>();
        Assert.Contains("cas vos", cut.Markup);
        Assert.DoesNotContain("oud account", cut.Markup); // al uitgeschakeld

        var rij = cut.FindAll("tbody tr").First(r => r.TextContent.Contains("cas vos"));
        rij.QuerySelector("td.kb-kies input")!.Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Plan: uitschakelen (1)", StringComparison.Ordinal)).Click();
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("Rapport bewaard", cut.Markup));

        Assert.Equal(new[] { Actie.AdAccountUitschakelen, Actie.AdKenmerkenZetten }, _fabriek.Uitgevoerd.Select(w => w.Actie));
        var rapport = File.ReadAllText(Assert.Single(Directory.GetFiles(ExportMap, "Opkuisronde-Medewerkers-*.csv")));
        Assert.Contains("\"cas vos\"", rapport);
        Assert.Contains("\"uitschakelen\";\"gelukt\"", rapport);
        Assert.Contains("\"behouden\"", rapport);
    }

    [Fact]
    public void BeveiligingMetMfaRollenEnAanmeldingen()
    {
        _fabriek.Beveiliging = new BeveiligingGegevens(
            new[]
            {
                new MfaRegistratie("e-an", "an.peeters@test.be", true, true, new[] { "microsoftAuthenticatorPush" }, "microsoftAuthenticatorPush", true),
                new MfaRegistratie("e-bo", "bo.janssens@test.be", false, false, new[] { "email" }, "", true),
            },
            new[] { new RolLid("Global Administrator", "e-bo", "BO JANSSENS", "bo.janssens@test.be", "user") });
        _fabriek.Aanmeldingen.Add(new Aanmelding(DateTimeOffset.Now.AddHours(-1), "e-bo", "bo.janssens@test.be", "BO JANSSENS", "Office 365",
            "203.0.113.5", "Gent, BE", 50126, "Invalid username or password", "Browser"));

        Assert.True(State.StartLaden(LaadSoort.AdEnEntra));
        WachtOpTaak();
        Assert.All(State.Taak!.Stappen, s => Assert.Equal(StapStatus.Klaar, s.Status));

        var cut = RenderComponent<Beveiliging>();
        Assert.Contains("actieve medewerkers zonder MFA (1)", cut.Markup.Replace("Actieve", "actieve"));
        Assert.Contains("Global Administrator", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Live laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("Invalid username or password", cut.Markup));

        var detail = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        Assert.Contains("geen MFA", detail.Markup);
        Assert.Contains("Global Administrator", detail.Markup);

        var stat = RenderComponent<Statistieken>();
        Assert.Contains("Global Administrators", stat.Markup);
        Assert.Contains("beheerders zonder MFA", stat.Markup);

        // Bewaard in de live inventaris en terug ingelezen
        var opnieuw = InventarisLader.Laad(new[] { State.BewaardeInventaris! });
        Assert.Equal(2, opnieuw.Beveiliging!.Mfa.Count);
        Assert.Equal("Global Administrator", Assert.Single(opnieuw.Beveiliging.Rollen).Rol);
    }

    [Fact]
    public void SnelLadenNaEenVolledigeLezingOokNaHerstart()
    {
        Assert.False(State.AdKanSnel);
        Assert.True(State.StartLaden(LaadSoort.AdEnEntra));
        WachtOpTaak();
        Assert.True(State.AdKanSnel);
        Assert.True(State.EntraKanSnel);

        Assert.True(State.StartLaden(LaadSoort.AdEnEntra));
        WachtOpTaak();
        Assert.Null(_fabriek.AdBases[0]);
        Assert.Equal(1001, _fabriek.AdBases[1]!.Stand.HoogsteUsn);
        Assert.Equal("https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=t1", _fabriek.GraphBases[1]!.DeltaLink);
        Assert.Contains(State.Data.Bronnen, b => b.Map?.Contains("enkel wijzigingen", StringComparison.Ordinal) == true);

        // Na een herstart: de live bewaarde inventaris heeft het startpunt mee
        var herstart = new BeheerState(Services.GetRequiredService<IOmgeving>(), _fabriek);
        Assert.True(herstart.AdKanSnel);
        Assert.True(herstart.EntraKanSnel);

        // Een andere (oudere) inventaris past niet bij dat startpunt
        herstart.KiesMap(herstart.Mappen.Single(m => m.EndsWith("20261016-1343", StringComparison.Ordinal)));
        Assert.False(herstart.AdKanSnel);
        Assert.False(herstart.EntraKanSnel);

        // Uitgezet: altijd alles lezen
        State.Instellingen.SnelLaden = false;
        Assert.False(State.AdKanSnel);
    }

    [Fact]
    public void ControleToontDeVersie()
    {
        var cut = RenderComponent<Controle>();
        Assert.Contains("Over Moss365", cut.Markup);
        Assert.Contains($"Versie <strong>{BeheerState.AppVersie}</strong>", cut.Markup);
        Assert.Contains("github.com/jef-cesar/Moss365/releases", cut.Markup);
    }

    [Fact]
    public void KapotteEigenOrganisatieGeeftEenZichtbareWaarschuwing()
    {
        Directory.CreateDirectory(Path.Combine(_basis, "eigen"));
        File.WriteAllText(Path.Combine(_basis, "eigen", "organisatie.json"), "{ kapot");
        State.LaadConfig();
        State.LaadInventaris(); // wist Fout, maar niet de waarschuwing over de organisatie

        Assert.NotEmpty(State.Config.Populaties);
        var layout = RenderComponent<Moss365.UI.Layout.MainLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<p>x</p>")));
        Assert.Contains("is niet leesbaar", layout.Markup);
        Assert.Contains("Gemeente", RenderComponent<NieuweGebruiker>().Markup);
    }

    private void WachtOpTaak()
    {
        var tot = DateTime.Now.AddSeconds(10);
        while (State.TaakBezig && DateTime.Now < tot)
        {
            Thread.Sleep(20);
        }

        Assert.False(State.TaakBezig);
    }
}
