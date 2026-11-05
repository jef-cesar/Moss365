using Bunit;
using Moss365.UI.Layout;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for 0.7: show that a screen is not saved or run yet (#19).</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>The whole app (menu, bar) with this screen in it, at this address.</summary>
    private IRenderedComponent<MainLayout> RenderWithLayout<TPage>(string address) where TPage : IComponent
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);
        return RenderComponent<MainLayout>(p => p.Add(x => x.Body, b =>
        {
            b.OpenComponent<TPage>(0);
            b.CloseComponent();
        }));
    }

    private static AngleSharp.Dom.IElement MenuItem(IRenderedComponent<MainLayout> cut, string text)
        => cut.FindAll("nav.kb-nav a").First(a => a.TextContent.Trim().StartsWith(text, StringComparison.Ordinal));

    [Fact]
    public void UnsavedOrganisationShowsTintBarAndDot()
    {
        var cut = RenderWithLayout<Organisation>("organisatie");
        Assert.Empty(cut.FindAll("main.kb-openstaand"));

        cut.Find("select[aria-label='Afdeling voor gezinszorg']").Change("Burger en welzijn");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("main.kb-openstaand")));
        Assert.Contains("Niet bewaard: 1 wijziging", cut.Find(".kb-openbalk").TextContent);
        Assert.Contains("●", MenuItem(cut, "Organisatie").TextContent);
        Assert.Contains("1 scherm niet bewaard", cut.Find(".kb-nav-openstaand").TextContent);

        // Save via the bar: everything gone, and the file is written
        cut.Find(".kb-openbalk button.kb-primair").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("main.kb-openstaand")));
        Assert.DoesNotContain("●", MenuItem(cut, "Organisatie").TextContent);
        Assert.True(File.Exists(State.AppEnvironment.ConfigUsersPath));
    }

    [Fact]
    public void LeavingWithUnsavedSettingsAsksConfirmation()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderWithLayout<Organisation>("organisatie");
        cut.Find("select[aria-label='Afdeling voor gezinszorg']").Change("Burger en welzijn");

        nav.NavigateTo("gebruikers");
        Assert.EndsWith("/organisatie", nav.Uri);
        cut.WaitForAssertion(() => Assert.Contains("Niet bewaard", cut.Find(".kb-dialoog").TextContent));

        // Stay: nothing changes
        cut.FindAll(".kb-dialoog button").First(b => b.TextContent == "Blijven").Click();
        Assert.Empty(cut.FindAll(".kb-dialoog"));
        Assert.Equal("Burger en welzijn", State.Config.DivisionFor("gezinszorg")?.Name);

        // Discard: back to what was saved, and only then gone
        nav.NavigateTo("gebruikers");
        cut.WaitForAssertion(() => cut.FindAll(".kb-dialoog button").First(b => b.TextContent == "Verwerpen").Click());
        Assert.EndsWith("/gebruikers", nav.Uri);
        Assert.NotEqual("Burger en welzijn", State.Config.DivisionFor("gezinszorg")?.Name);
        Assert.Equal(0, State.ConfigModifications);
        Assert.Empty(State.Pending.Everything);
    }

    [Fact]
    public void UnsavedConnectionsAskConfirmationAndSaveWithoutPassword()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        State.SaveSettings();
        var cut = RenderWithLayout<UI.Pages.Connections>("verbindingen");
        Assert.Empty(cut.FindAll("main.kb-openstaand"));

        cut.FindAll("input.kb-invoer").First(i => i.GetAttribute("placeholder")?.StartsWith("naam of IP-adres", StringComparison.Ordinal) == true).Change("dc2.test.internal");
        cut.WaitForAssertion(() => Assert.Contains("verbindingsinstellingen zijn aangepast", cut.Find(".kb-openbalk").TextContent));

        nav.NavigateTo("gebruikers");
        cut.WaitForAssertion(() => cut.FindAll(".kb-dialoog button").First(b => b.TextContent == "Bewaren").Click());
        Assert.EndsWith("/gebruikers", nav.Uri);
        Assert.False(State.SettingsChanged);
        Assert.Contains("dc2.test.internal", State.Database.ReadDocument("verbinding"));
    }

    [Fact]
    public void ReadyPlanShowsBarUntilRun()
    {
        var cut = RenderWithLayout<Mirror>("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
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
    public void DiscardPlanViaTheBar()
    {
        var cut = RenderWithLayout<Mirror>("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".kb-openbalk")));
        cut.FindAll(".kb-openbalk button").First(b => b.TextContent == "Verwerpen").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("main.kb-openstaand")));
        Assert.Empty(cut.FindAll("section.kb-plan"));
        Assert.Empty(_factory.Executed);
    }

    [Fact]
    public void FilledFormStaysVisibleInTheMenu()
    {
        var cut = RenderWithLayout<NewUser>("nieuw");
        Assert.DoesNotContain("●", MenuItem(cut, "Nieuwe gebruiker").TextContent);
        cut.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).Change("Testa");
        State.Pending.Reload(); // in the app moss365.js does this on every change of a field
        cut.WaitForAssertion(() => Assert.Contains("●", MenuItem(cut, "Nieuwe gebruiker").TextContent));
        Assert.Contains("Ingevuld, nog niet uitgevoerd", cut.Find(".kb-openbalk").TextContent);

        // Another screen: the dot stays, the bar belongs to New user
        var elsewhere = RenderWithLayout<Users>("gebruikers");
        Assert.Contains("●", MenuItem(elsewhere, "Nieuwe gebruiker").TextContent);
        Assert.Empty(elsewhere.FindAll(".kb-openbalk"));

        // Clear: gone
        var back = RenderWithLayout<NewUser>("nieuw");
        back.FindAll("button").First(b => b.TextContent == "Leegmaken").Click();
        State.Pending.Reload();
        back.WaitForAssertion(() => Assert.DoesNotContain("●", MenuItem(back, "Nieuwe gebruiker").TextContent));
    }

    [Fact]
    public void PendingLinksDetailPagesToTheirMenu()
    {
        Assert.Equal("groepen", Pending.MenuOf("groep/ad:x"));
        Assert.Equal("postvakken", Pending.MenuOf("/postvak/jeugd@test.be?x=1"));
        Assert.Equal("uitstroom", Pending.MenuOf("uitstroom/ad%3Abo.janssens"));
        Assert.Equal("uitstroom/ad:bo.janssens", Pending.Normalise("uitstroom/ad%3Abo.janssens#x"));
    }
}
