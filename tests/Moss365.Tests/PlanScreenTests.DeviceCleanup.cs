using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Devices;
using Moss365.Core.Setup;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17 (U18, U25, U26) on Toestellen: compliance per policy, the baseline policies and cleaning up old devices.</summary>
public sealed partial class PlanScreenTests
{
    private IRenderedComponent<DevicesPage> DevicesRead()
    {
        var cut = RenderComponent<DevicesPage>();
        cut.FindAll("button").First(b => b.TextContent == "Toestellen lezen").Click();
        cut.WaitForAssertion(() => Assert.NotNull(State.IntuneDevices), TimeSpan.FromSeconds(5));
        return cut;
    }

    private static void RunPlan(IRenderedFragment cut, string within)
    {
        cut.FindAll($"{within} button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        cut.FindAll($"{within} button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).Click();
    }

    [Fact]
    public void CompliancePerPolicyIsShownAndTheBaselinesArePlans()
    {
        EntraFilled();
        _factory.Compliance = new ComplianceReading(
            new[]
            {
                new CompliancePolicy("p1", "Basis Windows", "Windows", new[]
                {
                    new ComplianceDeviceStatus("LAPTOP-OUD", "cas@test.be", "nonCompliant", DateTimeOffset.Now.AddDays(-2)),
                    new ComplianceDeviceStatus("LAPTOP-1", "an.peeters@test.be", "compliant", DateTimeOffset.Now),
                }),
            },
            new[] { new ComplianceSetting("BitLocker vereist", "windows10AndLater", 1, 1, 0) }, false, null) { SettingsJson = "{\"secureByDefault\":false}" };
        var cut = DevicesRead();

        var card = cut.Find("#naleving");
        Assert.Contains("gelden nu als conform", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("iOS: 1 toestel(len) zonder enig nalevingsbeleid", card.TextContent, StringComparison.Ordinal); // GSM-BO
        Assert.Contains("BitLocker vereist", cut.Find("#nalevingsinstellingen").TextContent, StringComparison.Ordinal);
        cut.Find("#naleving button[aria-label='Toon de toestellen die Basis Windows niet halen']").Click();
        Assert.Contains("LAPTOP-OUD", cut.Find("#niet-conform").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("LAPTOP-1", cut.Find("#niet-conform").TextContent, StringComparison.Ordinal);

        // The baselines only with the part on
        Assert.Contains("Basisnalevingsbeleid", cut.Find("#naleving").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("#basisbeleid"));
        Assert.Empty(cut.FindAll("#standaard-niet-conform"));
        State.Config.PermissionParts.Add(PermissionParts.CompliancePolicies);
        cut.FindComponent<ComplianceCard>().Render();
        Assert.Equal(3, cut.FindAll("#basisbeleid button").Count);

        // Devices without a policy: not compliant, as a plan that names the devices that would fail
        cut.Find("#standaard-niet-conform").Click();
        Assert.Contains("Toestellen zonder nalevingsbeleid als niet conform markeren", cut.Find("#naleving").TextContent, StringComparison.Ordinal);
        Assert.Contains("GSM-BO", cut.Find("#naleving").TextContent, StringComparison.Ordinal);

        cut.FindAll("#basisbeleid button").First(b => b.TextContent == "Basisbeleid voor Windows plannen").Click();
        Assert.Contains("Nalevingsbeleid Moss365 basis Windows maken (nog niet toegewezen)", cut.Find("#naleving").TextContent, StringComparison.Ordinal);
        Assert.Contains("zonder toewijzing", cut.Find("#naleving").TextContent, StringComparison.Ordinal);
        RunPlan(cut, "#naleving");
        cut.WaitForAssertion(() => Assert.Equal(ChangeAction.IntuneCreateCompliancePolicy, Assert.Single(_factory.Executed).ChangeAction), TimeSpan.FromSeconds(5));

        // On a date in the past there is no compliance (it is not in the snapshots), and no plan for old devices
        Assert.NotNull(State.Compliance);
        Assert.Null(State.ShowAsOf(State.Database.SnapshotList().Last().Time));
        Assert.Null(State.Compliance);
        var past = RenderComponent<DevicesPage>();
        Assert.Contains("niet op een datum in het verleden", past.Find("#naleving").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(past.FindAll("#opkuis button"), b => b.TextContent.Contains("uitschakelen", StringComparison.Ordinal) || b.TextContent.Contains("verwijderen", StringComparison.Ordinal));
    }

    [Fact]
    public void OldDevicesAreCleanedUpAsPlansAndEntraOnlyWithThePart()
    {
        EntraFilled();
        _factory.ExtraEntraDevices.AddRange(new[]
        {
            new EntraDevice("o-oud", "e-oud", "PC-OUD", "Windows", "10.0.19045", true, DateTimeOffset.Now.AddDays(-120), "AzureAd"),
            new EntraDevice("o-uit", "e-uit", "PC-UIT", "Windows", "10.0.19045", false, DateTimeOffset.Now.AddDays(-200), "AzureAd"),
            new EntraDevice("o-hyb", "e-hyb", "PC-HYB", "Windows", "10.0.19045", true, DateTimeOffset.Now.AddDays(-120), "ServerAd"),
            new EntraDevice("o-ap", "e-ap", "PC-AP", "Windows", "10.0.26100", true, DateTimeOffset.Now.AddDays(-120), "AzureAd") { Autopilot = true },
        });
        _factory.ExtraIntuneDevices.Add(new IntuneDevice("m-uit", "PC-UIT", "cas@test.be", "Windows", "10.0.19045", "noncompliant", DateTimeOffset.Now.AddDays(-200), "company") { EntraDeviceId = "e-uit" });
        var cut = DevicesRead();

        Assert.Contains("PC-OUD", cut.Find("#uit-te-schakelen").TextContent, StringComparison.Ordinal);
        Assert.Contains("PC-UIT", cut.Find("#te-verwijderen").TextContent, StringComparison.Ordinal);
        Assert.Contains("PC-HYB", cut.Find("#blijven-staan").TextContent, StringComparison.Ordinal);
        Assert.Contains("PC-AP", cut.Find("#blijven-staan").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("PC-HYB", cut.Find("#uit-te-schakelen").TextContent, StringComparison.Ordinal);
        Assert.Contains("PC-UIT", cut.Find("#intune-oud").TextContent, StringComparison.Ordinal);

        // The part is off: the lists, an explanation, and no plan for Entra
        Assert.Contains("Oude toestellen opkuisen in Entra", cut.Find("#opkuis").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("#uit-te-schakelen input").HasAttribute("disabled"));
        Assert.DoesNotContain(cut.FindAll("#opkuis button"), b => b.TextContent.EndsWith("uitschakelen", StringComparison.Ordinal));

        // With the part on: disabling, and deleting with the record in Intune and the warning for BitLocker keys
        State.Config.PermissionParts.Add(PermissionParts.EntraDeviceCleanup);
        cut.FindComponent<DeviceCleanupCard>().Render();
        cut.Find("#uit-te-schakelen input[aria-label='PC-OUD']").Change(true);
        cut.FindAll("#opkuis button").First(b => b.TextContent == "1 toestel(len) uitschakelen").Click();
        Assert.Contains("PC-OUD uitschakelen in Entra (90 dagen niet gezien)", cut.Find("#opkuis").TextContent, StringComparison.Ordinal);

        cut.Find("#te-verwijderen input[aria-label='PC-UIT']").Change(true);
        cut.FindAll("#opkuis button").First(b => b.TextContent == "1 toestel(len) verwijderen").Click();
        cut.WaitForAssertion(() => Assert.Contains("2 BitLocker-herstelsleutel(s)", cut.Find("#opkuis").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Contains("Het record van PC-UIT verwijderen uit Intune", cut.Find("#opkuis").TextContent, StringComparison.Ordinal);
        Assert.Contains("bitlocker-count:e-uit", _factory.WasRead);
        RunPlan(cut, "#opkuis");
        cut.WaitForAssertion(() => Assert.Equal(new[] { ChangeAction.IntuneDeleteDevice, ChangeAction.GraphDeleteDevice }, _factory.Executed.Select(c => c.ChangeAction)), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ReadersSeeTheOldDevicesButMakeNoPlan()
    {
        EntraFilled();
        State.Config.PermissionParts.Add(PermissionParts.EntraDeviceCleanup);
        _factory.ExtraEntraDevices.Add(new EntraDevice("o-oud", "e-oud", "PC-OUD", "Windows", "10.0.19045", true, DateTimeOffset.Now.AddDays(-120), "AzureAd"));
        State.Config.ProfilesRequired = true;
        _factory.AppRoles = new[] { "Moss365.Helpdesk" };
        var cut = DevicesRead();

        Assert.Contains("PC-OUD", cut.Find("#uit-te-schakelen").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("#uit-te-schakelen input").HasAttribute("disabled"));
        Assert.DoesNotContain(cut.FindAll("#opkuis button"), b => b.TextContent.Contains("uitschakelen", StringComparison.Ordinal) || b.TextContent.Contains("verwijderen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheEngineRefusesTheChangesOfAPartThatIsOff()
    {
        EntraFilled();
        var disable = Assert.Single(DeviceCleanup.Disable(new[] { new EntraDevice("o9", "e9", "PC-9", "Windows", "10.0.26100", true, DateTimeOffset.Now.AddDays(-120), "AzureAd") },
            new[] { PermissionParts.EntraDeviceCleanup }).Plan);
        var policy = Assert.Single(ComplianceTemplates.Create(ComplianceTemplates.All[1], Array.Empty<CompliancePolicy>(), new[] { PermissionParts.CompliancePolicies }).Plan);
        var intune = Assert.Single(DeviceCleanup.DeleteIntune(new[] { Laptop() with { Id = "d9" } }).Plan);

        var results = await State.ExecuteAsync(new[] { disable, policy, intune }, "test", null);

        Assert.Equal(ResultStatus.Failed, results.Single(r => r.ChangeId == disable.Id).Status);
        Assert.Contains("Oude toestellen opkuisen in Entra", results.Single(r => r.ChangeId == disable.Id).Message, StringComparison.Ordinal);
        Assert.Equal(ResultStatus.Failed, results.Single(r => r.ChangeId == policy.Id).Status);
        Assert.Equal(intune.Id, Assert.Single(_factory.Executed).Id);

        State.Config.PermissionParts.Add(PermissionParts.EntraDeviceCleanup);
        var again = Assert.Single(DeviceCleanup.Disable(new[] { new EntraDevice("o9", "e9", "PC-9", "Windows", "10.0.26100", true, DateTimeOffset.Now.AddDays(-120), "AzureAd") },
            State.Config.PermissionParts).Plan);
        Assert.Equal(ResultStatus.Succeeded, Assert.Single(await State.ExecuteAsync(new[] { again }, "test", null)).Status);
    }
    [Fact]
    public void ThePanelOfADeviceShowsWhatItFailsAndLeadsToItsKeys()
    {
        EntraFilled();
        _factory.Compliance = new ComplianceReading(
            new[] { new CompliancePolicy("p1", "Basis Windows", "Windows", new[] { new ComplianceDeviceStatus("LAPTOP-1", "an.peeters@test.be", "nonCompliant", DateTimeOffset.Now) }) },
            Array.Empty<ComplianceSetting>(), true, null);
        var cut = DevicesRead();
        Assert.Contains("gelden als niet conform (aanbevolen)", cut.Find("#naleving").TextContent, StringComparison.Ordinal);

        cut.Find("#een-lijst button[aria-label='LAPTOP-1 openen']").Click();
        Assert.Contains("Haalt niet: Basis Windows", cut.Find("#toestel").TextContent, StringComparison.Ordinal);
        cut.FindAll("#toestel button").First(b => b.TextContent == "Instellingen lezen die het niet haalt").Click();
        cut.WaitForAssertion(() => Assert.Contains("BitLocker vereist", cut.Find("#toestel-naleving").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("Firewall", cut.Find("#toestel-naleving").TextContent, StringComparison.Ordinal);
        Assert.Equal("sleutels?toestel=LAPTOP-1", cut.Find("#toestel a[href^='sleutels']").GetAttribute("href"));

        // The keys page searches the device at once; showing a key still takes a click (and goes to the access log)
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("sleutels?toestel=PC-100");
        var keys = RenderComponent<Keys>();
        keys.WaitForAssertion(() => Assert.Contains("PC-100", keys.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("333333-444444", keys.Markup, StringComparison.Ordinal);
    }
}
