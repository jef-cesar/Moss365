using System.Net;
using Moss365.Connections.Graph;
using Moss365.Core.Changes;
using Moss365.Core.Devices;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17: actions on Intune devices through Graph; Intune is asked again before wiping.</summary>
public class DeviceConnectionTests
{
    private const string Beta = "https://graph.microsoft.com/beta";

    private static GraphConnection Make(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

    private static object Items(params object[] items) => new { value = items };

    [Fact]
    public async Task DeviceActionsGoToIntuneAndAPersonalDeviceIsNeverWiped()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Post, "/deviceManagement/managedDevices/d1/syncDevice", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Post, Beta + "/deviceManagement/managedDevices/d1/setDeviceName", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Get, "/deviceManagement/managedDevices/d1?", new { managedDeviceOwnerType = "company" })
            .Op(HttpMethod.Post, "/deviceManagement/managedDevices/d1/wipe", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Get, "/deviceManagement/managedDevices/d2?", new { managedDeviceOwnerType = "personal" })
            .Op(HttpMethod.Post, "/deviceManagement/managedDevices/d2/wipe", "", HttpStatusCode.NoContent);
        var device = new IntuneDevice("d1", "LAPTOP-1", "an@test.be", "Windows", "10.0.26100", "compliant", null, "company");
        var sync = DevicePlans.Action(device, "sync").Plan.Single();
        var rename = DevicePlans.Action(device, "rename", "LAPTOP-2").Plan.Single();
        var wipe = DevicePlans.Action(device, "wipe", typedName: "LAPTOP-1").Plan.Single();
        // The plan says company, but Intune says personal now: refused
        var personal = DevicePlans.Action(device with { Id = "d2" }, "wipe", typedName: "LAPTOP-1").Plan.Single();

        var r = await Make(fake).ExecuteAsync(new[] { sync, rename, wipe, personal }, CancellationToken.None);

        Assert.Equal(new[] { ResultStatus.Succeeded, ResultStatus.Succeeded, ResultStatus.Succeeded, ResultStatus.Failed }, r.Select(x => x.Status));
        Assert.Contains("\"deviceName\":\"LAPTOP-2\"", fake.Requests.Single(v => v.Url.EndsWith("/setDeviceName", StringComparison.Ordinal)).Content, StringComparison.Ordinal);
        Assert.Contains("\"keepUserData\":false", fake.Requests.Single(v => v.Url.EndsWith("/d1/wipe", StringComparison.Ordinal)).Content, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.Requests, v => v.Url.EndsWith("/d2/wipe", StringComparison.Ordinal));
        Assert.Contains("persoonlijk toestel", r[3].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewPrimaryUserGoesToTheBetaOfIntune()
    {
        var fake = new FakeGraph().Op(HttpMethod.Post, Beta + "/deviceManagement/managedDevices/d1/users/$ref", "", HttpStatusCode.NoContent);
        var device = new IntuneDevice("d1", "LAPTOP-1", "an@test.be", "Windows", "10.0.26100", "compliant", null, "company");
        var change = DevicePlans.PrimaryUser(device, new Moss365.Core.Inventory.EntraUser { Id = "u-bo", UserPrincipalName = "bo@test.be" }, "u-an").Plan.Single();

        var r = Assert.Single(await Make(fake).ExecuteAsync(new[] { change }, CancellationToken.None));

        Assert.Equal(ResultStatus.Succeeded, r.Status);
        Assert.Contains("\"@odata.id\":\"https://graph.microsoft.com/beta/users/u-bo\"", fake.Requests.Single().Content, StringComparison.Ordinal);
    }
    [Fact]
    public async Task ComplianceIsReadPerPolicyInBatches()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/deviceManagement/deviceCompliancePolicies", Items(
                new Dictionary<string, object> { ["id"] = "p1", ["displayName"] = "Basis Windows", ["@odata.type"] = "#microsoft.graph.windows10CompliancePolicy" },
                new Dictionary<string, object> { ["id"] = "p2", ["displayName"] = "Basis iOS", ["@odata.type"] = "#microsoft.graph.iosCompliancePolicy" }))
            .Op(HttpMethod.Get, "/deviceManagement/deviceCompliancePolicies/p1/deviceStatuses", Items(
                new { deviceDisplayName = "LAPTOP-1", userPrincipalName = "an@test.be", status = "nonCompliant", lastReportedDateTime = "2026-11-20T10:00:00Z" },
                new { deviceDisplayName = "LAPTOP-2", userPrincipalName = "bo@test.be", status = "compliant", lastReportedDateTime = "2026-11-21T10:00:00Z" }))
            .Op(HttpMethod.Get, "/deviceManagement/deviceCompliancePolicies/p2/deviceStatuses", Items())
            .Op(HttpMethod.Get, "/deviceManagement/deviceCompliancePolicySettingStateSummaries", Items(
                new { settingName = "BitLocker vereist", platformType = "windows10AndLater", compliantDeviceCount = 1, nonCompliantDeviceCount = 1, errorDeviceCount = 0 }))
            .Op(HttpMethod.Get, "/deviceManagement?", new { settings = new { deviceComplianceCheckinThresholdDays = 30, secureByDefault = false } })
            .Op(HttpMethod.Patch, "/deviceManagement", "", HttpStatusCode.NoContent);

        var reading = await Make(fake).ReadComplianceAsync(CancellationToken.None);

        Assert.NotNull(reading);
        Assert.Null(reading!.Note);
        Assert.Equal(new[] { ("Basis Windows", "Windows", 2, 1), ("Basis iOS", "iOS", 0, 0) }, reading.Policies.Select(p => (p.Name, p.Platform, p.Devices.Count, p.Failing.Count)));
        var failing = Assert.Single(reading.Policies[0].Failing);
        Assert.Equal(("LAPTOP-1", "an@test.be", new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero)), (failing.Device, failing.User, failing.LastReported!.Value));
        Assert.Equal(("BitLocker vereist", "windows10AndLater", 1), (reading.Settings.Single().Setting, reading.Settings.Single().Platform, reading.Settings.Single().NonCompliant));
        Assert.False(reading.NoPolicyIsNotCompliant);
        Assert.Single(fake.Requests, r => r.Url.EndsWith("/$batch", StringComparison.Ordinal));

        // Marking devices without a policy as not compliant sends all the settings as read, with that one changed
        var change = ComplianceAnalysis.SecureByDefault(reading, null, new[] { PermissionParts.CompliancePolicies }).Plan.Single();
        var r = Assert.Single(await Make(fake).ExecuteAsync(new[] { change }, CancellationToken.None));
        Assert.Equal(ResultStatus.Succeeded, r.Status);
        Assert.Equal("{\"settings\":{\"deviceComplianceCheckinThresholdDays\":30,\"secureByDefault\":true}}", fake.Requests.Single(v => v.Method == HttpMethod.Patch).Content);
    }

    [Fact]
    public async Task ComplianceWithoutPermissionIsSaidNotHidden()
    {
        var fake = new FakeGraph().Op(HttpMethod.Get, "/deviceManagement/deviceCompliancePolicies", new { error = new { code = "Forbidden", message = "Geen recht" } }, HttpStatusCode.Forbidden);

        var reading = await Make(fake).ReadComplianceAsync(CancellationToken.None);

        Assert.Empty(reading!.Policies);
        Assert.Null(reading.NoPolicyIsNotCompliant);
        Assert.StartsWith("Naleving per beleid niet gelezen", reading.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutopilotAndHybridDevicesAreRecognisedInEntra()
    {
        var fake = new FakeGraph().Op(HttpMethod.Get, "/devices?", Items(
            new { id = "o1", deviceId = "dev-1", displayName = "PC-1", operatingSystem = "Windows", operatingSystemVersion = "10.0.26100", accountEnabled = true,
                  approximateLastSignInDateTime = "2026-01-01T00:00:00Z", trustType = "AzureAd", physicalIds = new[] { "[ZTDID]:abc", "[HWID]:h:1" } },
            new { id = "o2", deviceId = "dev-2", displayName = "PC-2", operatingSystem = "Windows", operatingSystemVersion = "10.0.26100", accountEnabled = false,
                  approximateLastSignInDateTime = "2026-01-01T00:00:00Z", trustType = "ServerAd", physicalIds = Array.Empty<string>() }));

        var devices = await Make(fake).ReadEntraDevicesAsync(CancellationToken.None);

        Assert.Equal(new[] { (true, false, true), (false, true, false) }, devices!.Select(d => (d.Autopilot, d.Hybrid, d.Enabled)));
        Assert.Contains("physicalIds", fake.Requests.Single().Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BitLockerKeysAreCountedNeverRead()
    {
        var fake = new FakeGraph().Op(HttpMethod.Get, "/informationProtection/bitlocker/recoveryKeys?", Items(new { id = "k1", deviceId = "dev-1" }, new { id = "k2", deviceId = "dev-1" }));

        Assert.Equal(2, await Make(fake).CountBitLockerKeysAsync("dev-1", CancellationToken.None));
        Assert.DoesNotContain(fake.Requests, r => r.Url.Contains("select=key", StringComparison.Ordinal));
        Assert.Null(await Make(new FakeGraph()).CountBitLockerKeysAsync("dev-1", CancellationToken.None)); // not found: unknown
    }

    [Fact]
    public async Task EntraIsAskedAgainBeforeADeviceIsDisabledOrDeleted()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/devices/o1?", new { accountEnabled = true, trustType = "AzureAd", physicalIds = Array.Empty<string>() })
            .Op(HttpMethod.Patch, "/devices/o1", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Get, "/devices/o3?", new { accountEnabled = true, trustType = "ServerAd", physicalIds = Array.Empty<string>() })
            .Op(HttpMethod.Patch, "/devices/o3", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Get, "/devices/o5?", new { accountEnabled = false, trustType = "AzureAd", physicalIds = new[] { "[ZTDID]:abc" } })
            .Op(HttpMethod.Get, "/devices/o6?", new { accountEnabled = true, trustType = "AzureAd", physicalIds = Array.Empty<string>() })
            .Op(HttpMethod.Get, "/devices/o7?", new { accountEnabled = false, trustType = "AzureAd", physicalIds = new[] { "[USER-GID]:x" } })
            .Op(HttpMethod.Delete, "/devices/", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Delete, "/deviceManagement/managedDevices/m7", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Post, "/deviceManagement/deviceCompliancePolicies", new { id = "nieuw-beleid" }, HttpStatusCode.Created);
        string[] on = { PermissionParts.EntraDeviceCleanup, PermissionParts.CompliancePolicies };
        static EntraDevice D(string id, bool enabled) => new(id, "dev-" + id, "PC-" + id, "Windows", "10.0.26100", enabled, DateTimeOffset.Now.AddDays(-200), "AzureAd");
        var disable = DeviceCleanup.Disable(new[] { D("o1", true) }, on).Plan.Single();
        // The plan says joined, Entra says hybrid now (a plan of earlier): refused
        var hybrid = DeviceCleanup.Disable(new[] { D("o3", true) }, on).Plan.Single();
        var autopilot = DeviceCleanup.Delete(new[] { D("o5", false) }, null, new Dictionary<string, int>(), on).Plan.Single();
        var stillOn = DeviceCleanup.Delete(new[] { D("o6", false) }, null, new Dictionary<string, int>(), on).Plan.Single();
        var gone = DeviceCleanup.Delete(new[] { D("o7", false) },
            new[] { new IntuneDevice("m7", "PC-o7", "an@test.be", "Windows", "10.0.26100", "compliant", null, "company") { EntraDeviceId = "dev-o7" } }, new Dictionary<string, int>(), on).Plan;
        var policy = ComplianceTemplates.Create(ComplianceTemplates.All[0], Array.Empty<CompliancePolicy>(), on).Plan.Single();

        var r = await Make(fake).ExecuteAsync(new[] { disable, hybrid, autopilot, stillOn }.Concat(gone).Append(policy).ToList(), CancellationToken.None);

        Assert.Equal(new[] { ResultStatus.Succeeded, ResultStatus.Failed, ResultStatus.Failed, ResultStatus.Failed, ResultStatus.Succeeded, ResultStatus.Succeeded, ResultStatus.Succeeded },
            r.Select(x => x.Status));
        Assert.Contains("\"accountEnabled\":false", fake.Requests.Single(v => v.Method == HttpMethod.Patch).Content, StringComparison.Ordinal);
        Assert.Contains("Hybride", r[1].Message, StringComparison.Ordinal);
        Assert.Contains("Autopilot", r[2].Message, StringComparison.Ordinal);
        Assert.Contains("eerst uit", r[3].Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "/deviceManagement/managedDevices/m7", "/devices/o7" },
            fake.Requests.Where(v => v.Method == HttpMethod.Delete).Select(v => v.Url.Replace(GraphClient.Base, string.Empty, StringComparison.Ordinal)));
        Assert.Contains("windows10CompliancePolicy", fake.Requests.Single(v => v.Method == HttpMethod.Post).Content, StringComparison.Ordinal);
    }
    [Fact]
    public async Task TheSettingsADeviceFailsComeFromTheBetaOfIntune()
    {
        var fake = new FakeGraph().Op(HttpMethod.Get, Beta + "/deviceManagement/managedDevices/d1/deviceCompliancePolicyStates", Items(
            new { displayName = "Basis Windows", state = "nonCompliant", settingStates = new object[]
            {
                new { setting = "Windows10CompliancePolicy.BitLockerEnabled", settingName = "BitLocker vereist", state = "nonCompliant" },
                new { setting = "Windows10CompliancePolicy.ActiveFirewallRequired", settingName = "", state = "compliant" },
            } }));

        var states = await Make(fake).ReadDeviceComplianceAsync("d1", CancellationToken.None);

        var policy = Assert.Single(states!);
        Assert.Equal(("Basis Windows", "nonCompliant"), (policy.Policy, policy.State));
        Assert.Equal("BitLocker vereist", Assert.Single(policy.Failing).Setting);
        Assert.Equal("Windows10CompliancePolicy.ActiveFirewallRequired", policy.Settings[1].Setting); // without a name, the setting itself
        Assert.Null(await Make(new FakeGraph()).ReadDeviceComplianceAsync("d1", CancellationToken.None));
    }
}
