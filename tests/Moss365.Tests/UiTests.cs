using Bunit;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests met bUnit, op een tijdelijke inventarismap met verzonnen gegevens.</summary>
public sealed class UiTests : TestContext, IDisposable
{
    private readonly string _basis = Path.Combine(Path.GetTempPath(), "kb-ui-" + Guid.NewGuid().ToString("N"));
    private readonly VasteOmgeving _omgeving;

    public UiTests()
    {
        var map = Path.Combine(_basis, "Inventaris", "Inventaris-20261016-1343");
        Directory.CreateDirectory(map);
        File.WriteAllText(Path.Combine(map, InventarisLader.AdGebruikers),
            "\"OU\";\"SamAccountName\";\"UserPrincipalName\";\"DisplayName\";\"Department\";\"EmailAddress\";\"Enabled\";\"DistinguishedName\"\n" +
            $"\"{TestData.Gemeente}\";\"an.peeters\";\"an.peeters@test.be\";\"An Peeters\";\"cultuur\";\"an.peeters@test.be\";\"True\";\"{TestData.Dn("an.peeters", TestData.Gemeente)}\"\n" +
            $"\"{TestData.Ocmw}\";\"cas.vos\";\"cas.vos@test.be\";\"Cas Vos\";\"gezinszorg\";\"cas.vos@test.be\";\"True\";\"{TestData.Dn("cas.vos", TestData.Ocmw)}\"\n" +
            $"\"{TestData.Ocmw}\";\"dina.nieuw\";\"dina.nieuw@test.be\";\"Dina Nieuw\";\"strijkatelier\";\"dina.nieuw@test.be\";\"True\";\"{TestData.Dn("dina.nieuw", TestData.Ocmw)}\"\n");
        File.WriteAllText(Path.Combine(map, InventarisLader.EntraGebruikers),
            "\"Id\";\"UserPrincipalName\";\"DisplayName\";\"Department\";\"AccountEnabled\";\"UserType\";\"OnPremisesSyncEnabled\";\"OnPremisesSamAccountName\"\n" +
            "\"e-an\";\"an.peeters@test.be\";\"An Peeters\";\"vrije tijd\";\"True\";\"Member\";\"True\";\"an.peeters\"\n");

        var standaard = Path.Combine(_basis, "standaard.json");
        ConfigOpslag.Bewaar(TestData.Config(), standaard);
        _omgeving = new VasteOmgeving(Path.Combine(_basis, "Inventaris"), standaard, Path.Combine(_basis, "eigen", "organisatie.json"));
        Services.AddSingleton<IOmgeving>(_omgeving);
        Services.AddSingleton<BeheerState>();
    }

    void IDisposable.Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_basis))
        {
            Directory.Delete(_basis, recursive: true);
        }
    }

    [Fact]
    public void OverzichtToontDeBronnen()
    {
        var cut = RenderComponent<Start>();
        Assert.Contains("Inventaris-20261016-1343", cut.Markup);
        Assert.Contains("niet gevonden", cut.Markup);
        Assert.Contains("nog zonder afdeling", cut.Markup);
    }

    [Fact]
    public void GebruikersZoeken()
    {
        var cut = RenderComponent<Gebruikers>();
        Assert.Equal(3, cut.FindAll("tbody tr").Count);

        cut.Find("input.kb-breed").Input("gezinszorg");
        var rij = Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Cas Vos", rij.TextContent);
        Assert.Contains("ad%3Acas.vos", rij.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void DetailMarkeertVerschillenTussenAdEnEntra()
    {
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:an.peeters"));
        Assert.Contains("An Peeters", cut.Find("h1").TextContent);
        var verschil = cut.FindAll("tr.kb-verschil").Select(r => r.QuerySelector("th")!.TextContent).ToList();
        Assert.Contains("Dienst (Department)", verschil);
        Assert.DoesNotContain("UPN", verschil);
    }

    [Fact]
    public void OrganisatieKoppelingMakenEnBewaren()
    {
        var cut = RenderComponent<Organisatie>();
        Assert.True(cut.Find("button.kb-primair").HasAttribute("disabled"));

        cut.Find("select[aria-label='Afdeling voor gezinszorg']").Change("Burger en welzijn");
        var bewaar = cut.Find("button.kb-primair");
        Assert.False(bewaar.HasAttribute("disabled"));
        bewaar.Click();

        Assert.Contains("Bewaard.", cut.Markup);
        var (opgeslagen, bron) = ConfigOpslag.Laad(_omgeving.ConfigStandaardPad, _omgeving.ConfigGebruikersPad);
        Assert.Equal(_omgeving.ConfigGebruikersPad, bron);
        Assert.Equal("Burger en welzijn", opgeslagen.AfdelingVoor("gezinszorg")!.Naam);

        // De meegeleverde lijst blijft onaangeroerd
        var (standaard, _) = ConfigOpslag.Laad(_omgeving.ConfigStandaardPad, null);
        Assert.Null(standaard.AfdelingVoor("gezinszorg"));
    }

    [Fact]
    public void OrganisatieToontOnbekendeDienstenEnVoegtZeToe()
    {
        var cut = RenderComponent<Organisatie>();
        Assert.Contains("\"strijkatelier\"", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Toevoegen" && b.ParentElement!.ParentElement!.TextContent.Contains("strijkatelier")).Click();
        Assert.NotNull(Services.GetRequiredService<BeheerState>().Config.ZoekDienst("strijkatelier"));
        Assert.Contains("niet bewaard", cut.Markup);
    }

    [Fact]
    public void DubbeleDienstBlokkeertBewaren()
    {
        var cut = RenderComponent<Organisatie>();
        var invoer = cut.FindAll("input.kb-invoer").First(i => i.GetAttribute("value") == "gezinszorg");
        invoer.Change("Cultuur ");
        Assert.Contains("staat 2 keer in de lijst", cut.Markup);
        Assert.True(cut.Find("button.kb-primair").HasAttribute("disabled"));
    }

    [Fact]
    public void SpiegelViaQueryParameters()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Acas.vos");
        var cut = RenderComponent<Spiegel>();
        Assert.Contains("An Peeters", cut.Markup);
        Assert.Contains("Cas Vos", cut.Markup);
        Assert.Contains("Plan maken", cut.Markup);
        Assert.DoesNotContain("binnenkort", cut.Markup);
    }
}
