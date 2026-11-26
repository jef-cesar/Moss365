using System.Net;
using Moss365.Connections.Graph;
using Moss365.Core.Changes;
using Moss365.Core.Devices;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17: actions on Intune devices through Graph; Intune is asked again before wiping.</summary>
public class DeviceConnectionTests
{
    private const string Beta = "https://graph.microsoft.com/beta";

    private static GraphConnection Make(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

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
}
