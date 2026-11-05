using Bunit;
using Moss365.UI.Layout;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor 0.7: zichtbaar maken dat een scherm nog niet bewaard of uitgevoerd is (#19).</summary>
public sealed partial class PlanSchermTests
{
    /// <summary>De hele app (menu, balk) met dit scherm erin, op dit adres.</summary>
    private IRenderedComponent<MainLayout> RenderMetLayout<TPagina>(string adres) where TPagina : IComponent
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo(adres);
        return RenderComponent<MainLayout>(p => p.Add(x => x.Body, b =>
        {
            b.OpenComponent<TPagina>(0);
            b.CloseComponent();
        }));
    }

    private static AngleSharp.Dom.IElement MenuItem(IRenderedComponent<MainLayout> cut, string tekst)
        => cut.FindAll("nav.kb-nav a").First(a => a.TextContent.Trim().StartsWith(tekst, StringComparison.Ordinal));

    [Fact]
    public void OrganisatieNietBewaardToontTintBalkEnBolletje()
    {
        var cut = RenderMetLayout<Organisatie>("organisatie");
        Assert.Empty(cut.FindAll("main.kb-openstaand"));

        cut.Find("select[aria-label='Afdeling voor gezinszorg']").Change("Burger en welzijn");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("main.kb-openstaand")));
        Assert.Contains("Niet bewaard: 1 wijziging", cut.Find(".kb-openbalk").TextContent);
        Assert.Contains("●", MenuItem(cut, "Organisatie").TextContent);
        Assert.Contains("1 scherm niet bewaard", cut.Find(".kb-nav-openstaand").TextContent);

        // Bewaren via de balk: alles weg, en het bestand is geschreven
        cut.Find(".kb-openbalk button.kb-primair").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("main.kb-openstaand")));
        Assert.DoesNotContain("●", MenuItem(cut, "Organisatie").TextContent);
        Assert.True(File.Exists(State.Omgeving.ConfigGebruikersPad));
    }

    [Fact]
    public void WeggaanMetNietBewaardeInstellingenVraagtBevestiging()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderMetLayout<Organisatie>("organisatie");
        cut.Find("select[aria-label='Afdeling voor gezinszorg']").Change("Burger en welzijn");

        nav.NavigateTo("gebruikers");
        Assert.EndsWith("/organisatie", nav.Uri);
        cut.WaitForAssertion(() => Assert.Contains("Niet bewaard", cut.Find(".kb-dialoog").TextContent));

        // Blijven: niets verandert
        cut.FindAll(".kb-dialoog button").First(b => b.TextContent == "Blijven").Click();
        Assert.Empty(cut.FindAll(".kb-dialoog"));
        Assert.Equal("Burger en welzijn", State.Config.AfdelingVoor("gezinszorg")?.Naam);

        // Verwerpen: terug naar wat bewaard was, en dan pas weg
        nav.NavigateTo("gebruikers");
        cut.WaitForAssertion(() => cut.FindAll(".kb-dialoog button").First(b => b.TextContent == "Verwerpen").Click());
        Assert.EndsWith("/gebruikers", nav.Uri);
        Assert.NotEqual("Burger en welzijn", State.Config.AfdelingVoor("gezinszorg")?.Naam);
        Assert.Equal(0, State.ConfigAanpassingen);
        Assert.Empty(State.Openstaand.Alles);
    }

    [Fact]
    public void VerbindingenNietBewaardVraagtBevestigingEnBewaartZonderWachtwoord()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        State.BewaarInstellingen();
        var cut = RenderMetLayout<UI.Pages.Verbindingen>("verbindingen");
        Assert.Empty(cut.FindAll("main.kb-openstaand"));

        cut.FindAll("input.kb-invoer").First(i => i.GetAttribute("placeholder")?.StartsWith("naam of IP-adres", StringComparison.Ordinal) == true).Change("dc2.test.internal");
        cut.WaitForAssertion(() => Assert.Contains("verbindingsinstellingen zijn aangepast", cut.Find(".kb-openbalk").TextContent));

        nav.NavigateTo("gebruikers");
        cut.WaitForAssertion(() => cut.FindAll(".kb-dialoog button").First(b => b.TextContent == "Bewaren").Click());
        Assert.EndsWith("/gebruikers", nav.Uri);
        Assert.False(State.InstellingenGewijzigd);
        Assert.Contains("dc2.test.internal", State.Databank.LeesDocument("verbinding"));
    }

    [Fact]
    public void PlanDatKlaarstaatToontBalkTotHetUitgevoerdIs()
    {
        var cut = RenderMetLayout<Spiegel>("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Plan klaar, nog niet uitgevoerd", cut.Find(".kb-openbalk").TextContent));
        Assert.Contains("●", MenuItem(cut, "Spiegelgebruiker").TextContent);
        Assert.Contains(cut.FindAll(".kb-openbalk button"), b => b.TextContent == "Naar het plan");

        cut.FindAll("section.kb-plan button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, uitvoeren").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("main.kb-openstaand")));
        Assert.DoesNotContain("●", MenuItem(cut, "Spiegelgebruiker").TextContent);
    }

    [Fact]
    public void PlanVerwerpenViaDeBalk()
    {
        var cut = RenderMetLayout<Spiegel>("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".kb-openbalk")));
        cut.FindAll(".kb-openbalk button").First(b => b.TextContent == "Verwerpen").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("main.kb-openstaand")));
        Assert.Empty(cut.FindAll("section.kb-plan"));
        Assert.Empty(_fabriek.Uitgevoerd);
    }

    [Fact]
    public void IngevuldFormulierBlijftZichtbaarInHetMenu()
    {
        var cut = RenderMetLayout<NieuweGebruiker>("nieuw");
        Assert.DoesNotContain("●", MenuItem(cut, "Nieuwe gebruiker").TextContent);
        cut.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).Change("Testa");
        State.Openstaand.Ververs(); // in de app doet moss365.js dat bij elke wijziging van een veld
        cut.WaitForAssertion(() => Assert.Contains("●", MenuItem(cut, "Nieuwe gebruiker").TextContent));
        Assert.Contains("Ingevuld, nog niet uitgevoerd", cut.Find(".kb-openbalk").TextContent);

        // Een ander scherm: het bolletje blijft, de balk hoort bij Nieuwe gebruiker
        var elders = RenderMetLayout<Gebruikers>("gebruikers");
        Assert.Contains("●", MenuItem(elders, "Nieuwe gebruiker").TextContent);
        Assert.Empty(elders.FindAll(".kb-openbalk"));

        // Leegmaken: weg
        var terug = RenderMetLayout<NieuweGebruiker>("nieuw");
        terug.FindAll("button").First(b => b.TextContent == "Leegmaken").Click();
        State.Openstaand.Ververs();
        terug.WaitForAssertion(() => Assert.DoesNotContain("●", MenuItem(terug, "Nieuwe gebruiker").TextContent));
    }

    [Fact]
    public void OpenstaandKoppeltDetailpaginasAanHunMenu()
    {
        Assert.Equal("groepen", Openstaand.MenuVan("groep/ad:x"));
        Assert.Equal("postvakken", Openstaand.MenuVan("/postvak/jeugd@test.be?x=1"));
        Assert.Equal("uitstroom", Openstaand.MenuVan("uitstroom/ad%3Abo.janssens"));
        Assert.Equal("uitstroom/ad:bo.janssens", Openstaand.Normaliseer("uitstroom/ad%3Abo.janssens#x"));
    }
}
