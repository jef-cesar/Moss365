using Bunit;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests with bUnit, on a temporary inventory folder with made-up data.</summary>
public sealed class UiTests : TestContext, IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "kb-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FixedEnvironment _environment;

    public UiTests()
    {
        var folder = Path.Combine(_base, "Inventaris", "Inventaris-20261016-1343");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, InventoryLoader.AdUsers),
            "\"OU\";\"SamAccountName\";\"UserPrincipalName\";\"DisplayName\";\"Department\";\"EmailAddress\";\"Enabled\";\"DistinguishedName\"\n" +
            $"\"{TestData.Municipality}\";\"an.peeters\";\"an.peeters@test.be\";\"An Peeters\";\"cultuur\";\"an.peeters@test.be\";\"True\";\"{TestData.Dn("an.peeters", TestData.Municipality)}\"\n" +
            $"\"{TestData.SocialWelfare}\";\"cas.vos\";\"cas.vos@test.be\";\"Cas Vos\";\"gezinszorg\";\"cas.vos@test.be\";\"True\";\"{TestData.Dn("cas.vos", TestData.SocialWelfare)}\"\n" +
            $"\"{TestData.SocialWelfare}\";\"dina.nieuw\";\"dina.nieuw@test.be\";\"Dina Nieuw\";\"strijkatelier\";\"dina.nieuw@test.be\";\"True\";\"{TestData.Dn("dina.nieuw", TestData.SocialWelfare)}\"\n");
        File.WriteAllText(Path.Combine(folder, InventoryLoader.EntraUsers),
            "\"Id\";\"UserPrincipalName\";\"DisplayName\";\"Department\";\"AccountEnabled\";\"UserType\";\"OnPremisesSyncEnabled\";\"OnPremisesSamAccountName\"\n" +
            "\"e-an\";\"an.peeters@test.be\";\"An Peeters\";\"vrije tijd\";\"True\";\"Member\";\"True\";\"an.peeters\"\n");

        var defaultValue = Path.Combine(_base, "standaard.json");
        ConfigStorage.Save(TestData.Config(), defaultValue);
        _environment = new FixedEnvironment(Path.Combine(_base, "Inventaris"), defaultValue, Path.Combine(_base, "eigen", "organisatie.json"));
        Services.AddSingleton<IAppEnvironment>(_environment);
        Services.AddSingleton<ManagementState>();
    }

    void IDisposable.Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    [Fact]
    public void OverviewShowsTheSources()
    {
        var cut = RenderComponent<Start>();
        Assert.Contains("Inventaris-20261016-1343", cut.Markup);
        Assert.Contains("niet gevonden", cut.Markup);
        Assert.Contains("nog zonder afdeling", cut.Markup);
    }

    [Fact]
    public void SearchUsers()
    {
        var cut = RenderComponent<Users>();
        Assert.Equal(3, cut.FindAll("tbody tr").Count);

        cut.Find("input.kb-breed").Input("gezinszorg");
        var row = Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("Cas Vos", row.TextContent);
        Assert.Contains("ad%3Acas.vos", row.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void DetailMarksDifferencesBetweenAdAndEntra()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:an.peeters"));
        Assert.Contains("An Peeters", cut.Find("h1").TextContent);
        var difference = cut.FindAll("tr.kb-verschil").Select(r => r.QuerySelector("th")!.TextContent).ToList();
        Assert.Contains("Dienst (Department)", difference);
        Assert.DoesNotContain("UPN", difference);
    }

    [Fact]
    public void CreateAndSaveOrganisationLink()
    {
        var cut = RenderComponent<Organisation>();
        Assert.True(cut.Find("button.kb-primair").HasAttribute("disabled"));

        cut.Find("select[aria-label='Afdeling voor gezinszorg']").Change("Burger en welzijn");
        var save = cut.Find("button.kb-primair");
        Assert.False(save.HasAttribute("disabled"));
        save.Click();

        Assert.Contains("Bewaard.", cut.Markup);
        var (stored, source, _) = ConfigStorage.LoadSafe(_environment.ConfigDefaultPath,
            new Moss365.Core.Storage.DatabaseText(Services.GetRequiredService<ManagementState>().Database, Moss365.Core.Storage.Documents.Organisation), ManagementState.OwnConfigName);
        Assert.Equal(ManagementState.OwnConfigName, source);
        Assert.Equal("Burger en welzijn", stored.DivisionFor("gezinszorg")!.Name);

        // The bundled list stays untouched
        var (defaultValue, _) = ConfigStorage.Load(_environment.ConfigDefaultPath, null);
        Assert.Null(defaultValue.DivisionFor("gezinszorg"));
    }

    [Fact]
    public void OrganisationShowsUnknownDepartmentsAndAddsThem()
    {
        var cut = RenderComponent<Organisation>();
        Assert.Contains("\"strijkatelier\"", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Toevoegen" && b.ParentElement!.ParentElement!.TextContent.Contains("strijkatelier")).Click();
        Assert.NotNull(Services.GetRequiredService<ManagementState>().Config.SearchDepartment("strijkatelier"));
        Assert.Contains("niet bewaard", cut.Markup);
    }

    [Fact]
    public void DuplicateDepartmentBlocksSave()
    {
        var cut = RenderComponent<Organisation>();
        var input = cut.FindAll("input.kb-invoer").First(i => i.GetAttribute("value") == "gezinszorg");
        input.Change("Cultuur ");
        Assert.Contains("staat 2 keer in de lijst", cut.Markup);
        Assert.True(cut.Find("button.kb-primair").HasAttribute("disabled"));
    }

    [Fact]
    public void MirrorViaQueryParameters()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Acas.vos");
        var cut = RenderComponent<Mirror>();
        Assert.Contains("An Peeters", cut.Markup);
        Assert.Contains("Cas Vos", cut.Markup);
        Assert.Contains("Plan maken", cut.Markup);
        Assert.DoesNotContain("binnenkort", cut.Markup);
    }
}
