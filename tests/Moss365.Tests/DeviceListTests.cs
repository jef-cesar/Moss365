using Moss365.Core.Devices;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17: one device list over AD, Entra and Intune.</summary>
public class DeviceListTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void OneListOverAdEntraAndIntune()
    {
        var ad = new[]
        {
            new AdComputer("PC-HYBRIDE", "CN=PC-HYBRIDE,DC=contoso,DC=local", "Windows 11 Pro", "10.0 (26100)", Now.AddDays(-1), true, null),
            new AdComputer("PC-ALLEEN", "CN=PC-ALLEEN,DC=contoso,DC=local", "Windows 11 Pro", "10.0 (26100)", Now.AddDays(-2), true, null),
        };
        var entra = new[]
        {
            new EntraDevice("o1", "dev-1", "PC-HYBRIDE", "Windows", "10.0.26100", true, Now.AddDays(-1), "ServerAd"),
            new EntraDevice("o2", "dev-2", "LAPTOP-1", "Windows", "10.0.26100", true, Now.AddDays(-1), "AzureAd"),
            new EntraDevice("o3", "dev-3", "LAPTOP-1", "Windows", "10.0.26100", true, Now.AddDays(-200), "AzureAd"),
        };
        var intune = new[]
        {
            new IntuneDevice("i1", "LAPTOP-1", "an@contoso.com", "Windows", "10.0.26100", "compliant", Now.AddDays(-1), "company") { EntraDeviceId = "dev-2" },
            new IntuneDevice("i2", "WEES", "bo@contoso.com", "Windows", "10.0.26100", "compliant", Now.AddDays(-1), "company") { EntraDeviceId = "weg" },
        };

        var list = DeviceAnalysis.Combine(ad, entra, intune, Now);

        var hybrid = list.Single(r => r.Name == "PC-HYBRIDE");
        Assert.NotNull(hybrid.Ad);
        Assert.Contains("not-managed", hybrid.Findings);
        Assert.Equal(new[] { "ad-only" }, list.Single(r => r.Name == "PC-ALLEEN").Findings);

        var laptops = list.Where(r => r.Name == "LAPTOP-1").ToList();
        Assert.Equal(2, laptops.Count);
        Assert.All(laptops, l => Assert.Contains("duplicate", l.Findings));
        Assert.Equal("i1", laptops.Single(l => l.Entra!.Id == "o2").Intune!.Id);
        Assert.Contains("stale", laptops.Single(l => l.Entra!.Id == "o3").Findings);
        Assert.Contains("intune-orphan", list.Single(r => r.Name == "WEES").Findings);

        // What was not read gives no findings about it
        Assert.DoesNotContain(DeviceAnalysis.Combine(ad, null, null, Now), r => r.Findings.Contains("ad-only"));
        Assert.DoesNotContain(DeviceAnalysis.Combine(null, entra, null, Now), r => r.Findings.Contains("not-managed"));
    }
}
