using Bunit;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Licences;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.35 (R12): licences and costs.</summary>
public sealed partial class PlanScreenTests
{
    private InventoryData WithLicences()
    {
        var d = State.Data;
        var entra = d.EntraUsers.Select(e => e.Id switch
        {
            "e-oud" => e with { Licences = new[] { "SPB" } },                                      // disabled in AD
            "e-an" => e with { Licences = new[] { "SPE_E3", "ENTERPRISEPACK" }, LastSignIn = DateTime.Now.ToString("o") }, // duplicate
            "e-bo" => e with { Licences = new[] { "DESKLESSPACK" }, LastSignIn = DateTime.Now.AddDays(-200).ToString("o") }, // not used
            "e-cas" => e with { Licences = new[] { "SPB" }, LastSignIn = DateTime.Now.ToString("o") },
            _ => e,
        }).ToList();
        var ad = d.AdUsers.Select(a => a.SamAccountName == "bo.janssens" ? a with { LastLogonDate = DateTime.Now.AddDays(-200).ToString("yyyy-MM-dd HH:mm:ss") } : a).ToList();
        var licences = d.Licences.Concat(new[] { new Licence("sku-e3", "SPE_E3", 1, 1), new Licence("sku-o3", "ENTERPRISEPACK", 1, 1) }).ToList();
        return d with { AdUsers = ad, EntraUsers = entra, Persons = InventoryLoader.LinkPersons(ad, entra), Licences = licences };
    }

    [Fact]
    public void LicencesThatBringNothingAreFound()
    {
        var d = WithLicences();
        var wasted = LicenceAnalysis.Wasted(d, DateTime.Now);
        Assert.Contains(wasted, w => w.Kind == WasteKind.Disabled && w.Person.Entra?.Id == "e-oud");
        var duplicate = Assert.Single(wasted, w => w.Kind == WasteKind.Duplicate);
        Assert.Equal(new[] { "ENTERPRISEPACK" }, duplicate.Skus);
        Assert.Contains(wasted, w => w.Kind == WasteKind.Unused && w.Person.Entra?.Id == "e-bo");
        Assert.DoesNotContain(wasted, w => w.Person.Entra?.Id == "e-cas");

        // A duplicate plan removes only the licence that is already included
        var plan = LicenceAnalysis.Remove(d, new[] { duplicate }).Plan;
        var change = Assert.Single(plan);
        Assert.Equal(ChangeAction.GraphRemoveLicences, change.ChangeAction);
        Assert.Equal("sku-o3", change.P("skuIds"));
        var all = Assert.Single(LicenceAnalysis.Remove(d, wasted.Where(w => w.Kind == WasteKind.Disabled)).Plan);
        Assert.Equal(string.Empty, all.P("skuIds"));
        Assert.NotNull(all.Warning);
    }

    [Fact]
    public void CostsPerMonthAndSavings()
    {
        var d = WithLicences();
        State.Config.LicencePrices["SPB"] = 20.6m;
        State.Config.LicencePrices["ENTERPRISEPACK"] = 23m;
        State.Config.LicencePrices["DESKLESSPACK"] = 7.5m;
        var cas = d.Persons.First(p => p.Entra?.Id == "e-cas");
        Assert.Equal(20.6m, LicenceAnalysis.MonthlyCost(cas, State.Config));
        var wasted = LicenceAnalysis.Wasted(d, DateTime.Now);
        Assert.Equal(20.6m + 23m + 7.5m, LicenceAnalysis.Savings(wasted, State.Config));
        var perDepartment = LicenceAnalysis.CostPer(d, State.Config, p => p.Department);
        Assert.Contains(perDepartment, r => r.Name == "gezinszorg" && r.Cost == 20.6m);
        Assert.Contains(Kpis.Fixed, k => k.Key == "licentie-verspild");
    }

    [Fact]
    public void LicencesAgainstTheJobProfile()
    {
        var d = WithLicences();
        var profile = JobProfilePlans.FromPerson(d, State.Config, d.Persons.First(p => p.Entra?.Id == "e-cas"), "Zorg");
        profile.JobTitle = null;
        State.Config.JobProfiles.Add(profile);
        var assignments = new Dictionary<string, ProfileAssignment> { [d.Persons.First(p => p.Entra?.Id == "e-bo").Key] = new(profile.Id, 1, DateTimeOffset.Now, "test") };
        var row = Assert.Single(LicenceAnalysis.ByProfile(d, State.Config, assignments));
        Assert.Equal(new[] { "SPB" }, row.Missing);
        Assert.Equal(new[] { "DESKLESSPACK" }, row.Extra);
    }

    [Fact]
    public void TheLicencesScreenSetsPricesAndPlansRemoval()
    {
        var cut = RenderComponent<LicencesPage>();
        Assert.Contains("Abonnementen", cut.Markup, StringComparison.Ordinal);
        cut.Find("input[aria-label='Prijs SPB']").Change("20,60");
        Assert.Equal(20.6m, State.Config.LicencePrices["SPB"]);
        Assert.Contains("Licenties die niets opleveren", cut.Markup, StringComparison.Ordinal);
    }
}
