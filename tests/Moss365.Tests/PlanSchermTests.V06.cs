using Bunit;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor 0.6: Conditional Access.</summary>
public sealed partial class PlanSchermTests
{
    private void LaadMetCa()
    {
        _fabriek.Ca = TestData.Ca(DateTimeOffset.Now);
        _fabriek.Aanmeldingen.Add(new Aanmelding(DateTimeOffset.Now.AddHours(-1), "e-an", "an.peeters@test.be", "an peeters", "Office 365",
            "203.0.113.5", "Gent, BE", 0, "", "Browser",
            new[] { new CaToepassing("ca-mfa", "MFA voor iedereen", "success"), new CaToepassing("ca-beheer", "Beheerders phishingbestendig", "reportOnlyFailure") }));
        Assert.True(State.StartLaden(LaadSoort.AdEnEntra));
        WachtOpTaak();
        Assert.All(State.Taak!.Stappen, s => Assert.Equal(StapStatus.Klaar, s.Status));
    }

    [Fact]
    public void ConditionalAccessZonderGegevensLegtUitHoe()
    {
        var cut = RenderComponent<CaOverzicht>();
        Assert.Contains("Policy.Read.All", cut.Markup);
    }

    [Fact]
    public void ConditionalAccessControleBeleidenEnEffect()
    {
        LaadMetCa();
        Assert.Equal(4, State.Data.ConditionalAccess!.Beleiden.Count);

        var cut = RenderComponent<CaOverzicht>();
        Assert.Contains("Medewerkers zonder MFA-beleid", cut.Markup);
        Assert.Contains("Noodaccounts", cut.Markup);
        Assert.Contains("aandacht", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent == "Beleiden").Click();
        Assert.Contains("Buitenland blokkeren", cut.Markup);
        Assert.Contains("Alleen rapport", cut.Markup);
        Assert.Contains("Niet vanaf België", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.StartsWith("Effect laden")).Click();
        cut.WaitForAssertion(() => Assert.Contains("1 aanmeldingen van de laatste 7 dagen", cut.Markup));
        Assert.Equal(1, State.CaEffect!.Single(e => e.BeleidId == "ca-beheer").ZouTegenhouden);

        cut.FindAll("button").First(b => b.TextContent == "Locaties en sterktes").Click();
        Assert.Contains("203.0.113.0/24", cut.Markup);
        Assert.Contains("Phishingbestendige MFA", cut.Markup);
    }

    [Fact]
    public void ConditionalAccessWatAlsVanafDeGebruiker()
    {
        LaadMetCa();

        var detail = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        Assert.Contains("uitgezonderd via de groep WKG_Eigen", detail.Markup);

        Services.GetRequiredService<NavigationManager>().NavigateTo("conditional-access?persoon=ad%3Abo.janssens");
        var cut = RenderComponent<CaOverzicht>();
        Assert.Contains("Geen enkel beleid is zeker van toepassing", cut.Markup);

        // In het buitenland: geblokkeerd
        cut.FindAll("select").Single(s => s.InnerHtml.Contains("buitenland")).Change(nameof(Core.Analyse.CaPlek.Buitenland));
        Assert.Contains("Geblokkeerd door Buitenland blokkeren", cut.Markup);
    }

    [Fact]
    public void ConditionalAccessBeleidDetail()
    {
        LaadMetCa();
        var cut = RenderComponent<CaBeleidDetail>(p => p.Add(x => x.Id, "ca-mfa"));
        Assert.Contains("Behalve leden van: WKG_Eigen", cut.Markup);
        Assert.Contains("Toegang mits MFA", cut.Markup);
        Assert.Contains("an peeters", cut.Markup);
        Assert.DoesNotContain("BO JANSSENS", cut.Markup);

        var aanmeldingen = RenderComponent<Moss365.UI.Componenten.AanmeldLijst>(p => p.Add(x => x.Aanmeldingen, _fabriek.Aanmeldingen));
        Assert.Contains("rapport: zou tegenhouden", aanmeldingen.Markup);

        var stat = RenderComponent<Statistieken>();
        Assert.Contains("medewerkers buiten elk MFA-beleid", stat.Markup);
    }
}
