using Moss365.Core.Changes;
using Moss365.Core.Devices;
using Moss365.Core.Permissions;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17 (U18, U25, U26): compliance per policy, the baseline policies and cleaning up old devices.</summary>
public class DeviceCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] CleanupOn = { PermissionParts.EntraDeviceCleanup };
    private static readonly string[] PoliciesOn = { PermissionParts.CompliancePolicies };

    private static EntraDevice Entra(string id, bool enabled, int daysAgo, string trust = "AzureAd", bool autopilot = false)
        => new(id, "dev-" + id, "PC-" + id, "Windows", "10.0.26100", enabled, Now.AddDays(-daysAgo), trust) { Autopilot = autopilot };

    private static IntuneDevice Intune(string id, string os, int daysAgo, string entraId = "")
        => new(id, "M-" + id, "an@test.be", os, "1", "compliant", Now.AddDays(-daysAgo), "company") { EntraDeviceId = entraId };

    private static LogEntry Log(Change c, int daysAgo = 0) => new(Now.AddDays(-daysAgo), "beheerder@test.be", "c", c.TargetSystem.ToString(), c.ChangeAction.ToString(),
        c.Target, c.TargetName, c.Description, c.Parameters, nameof(ResultStatus.Succeeded), string.Empty, Previous: c.Previous);

    private static LogEntry Disabled(EntraDevice d, int daysAgo) => Log(Assert.Single(DeviceCleanup.Disable(new[] { d with { Enabled = true } }, CleanupOn).Plan), daysAgo);

    [Fact]
    public void PlatformsWithoutAPolicyAndDevicesThatCountAsCompliantAreAdvice()
    {
        Assert.Equal("Windows", ComplianceAnalysis.PlatformOf("#microsoft.graph.windows10CompliancePolicy"));
        Assert.Equal("iOS", ComplianceAnalysis.PlatformOf("#microsoft.graph.iosCompliancePolicy"));
        Assert.Equal("iOS", ComplianceAnalysis.PlatformOf("iPadOS"));
        Assert.Equal("Android", ComplianceAnalysis.PlatformOf("androidWorkProfile"));
        Assert.Equal("macOS", ComplianceAnalysis.PlatformOf("#microsoft.graph.macOSCompliancePolicy"));

        var devices = new[] { Intune("1", "Windows", 1), Intune("2", "Windows", 1), Intune("3", "iOS", 1), Intune("4", "Android", 1), Intune("5", "Android", 1) };
        var policies = new[] { new CompliancePolicy("p1", "Basis", "Windows", Array.Empty<ComplianceDeviceStatus>()) };
        Assert.Equal(new[] { ("Android", 2), ("iOS", 1) }, ComplianceAnalysis.PlatformsWithoutPolicy(devices, policies));

        var open = new ComplianceReading(policies, Array.Empty<ComplianceSetting>(), false, null);
        var advice = ComplianceAnalysis.Advice(open, devices);
        Assert.Equal(3, advice.Count);
        Assert.Contains("gelden nu als conform", advice[0], StringComparison.Ordinal);
        Assert.Contains("Android: 2", advice[1], StringComparison.Ordinal);
        Assert.Equal(2, ComplianceAnalysis.Advice(open with { NoPolicyIsNotCompliant = true }, devices).Count);
        Assert.Equal(2, ComplianceAnalysis.Advice(open with { NoPolicyIsNotCompliant = null }, devices).Count);

        // A device fails a policy when it is not compliant, in error or in conflict
        var statuses = new[]
        {
            new ComplianceDeviceStatus("A", "a", "nonCompliant", null), new ComplianceDeviceStatus("B", "b", "compliant", null),
            new ComplianceDeviceStatus("C", "c", "error", null), new ComplianceDeviceStatus("D", "d", "notApplicable", null),
        };
        Assert.Equal(new[] { "A", "C" }, new CompliancePolicy("p", "x", "Windows", statuses).Failing.Select(s => s.Device));
    }

    [Fact]
    public void BaselinePoliciesAreOnlyMadeWithThePartOnAndNeverTwice()
    {
        Assert.Equal(new[] { "windows", "ios", "android" }, ComplianceTemplates.All.Select(t => t.Key));
        foreach (var t in ComplianceTemplates.All)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(t.Json)!;
            Assert.Equal(t.Name, (string?)json["displayName"]);
            Assert.StartsWith("#microsoft.graph.", (string?)json["@odata.type"], StringComparison.Ordinal);
            Assert.Equal("block", (string?)json["scheduledActionsForRule"]![0]!["scheduledActionConfigurations"]![0]!["actionType"]);
        }

        var windows = ComplianceTemplates.All[0];
        Assert.Contains("Basisnalevingsbeleid", Assert.Single(ComplianceTemplates.Create(windows, Array.Empty<CompliancePolicy>(), null).Errors), StringComparison.Ordinal);
        var change = Assert.Single(ComplianceTemplates.Create(windows, Array.Empty<CompliancePolicy>(), PoliciesOn).Plan);
        Assert.Equal((TargetSystem.Graph, ChangeAction.IntuneCreateCompliancePolicy, "Moss365 basis Windows"), (change.TargetSystem, change.ChangeAction, change.Target));
        Assert.Contains("\"bitLockerEnabled\":true", change.P("json"), StringComparison.Ordinal);
        Assert.Contains("toewijzing", change.Warning, StringComparison.Ordinal);
        var existing = new[] { new CompliancePolicy("p1", "moss365 BASIS windows", "Windows", Array.Empty<ComplianceDeviceStatus>()) };
        Assert.NotEmpty(ComplianceTemplates.Create(windows, existing, PoliciesOn).Errors);

        // The engine refuses the change of a part that is off, also when the plan was made by hand
        Assert.NotNull(PermissionParts.Refusal(change, null));
        Assert.NotNull(PermissionParts.Refusal(change, CleanupOn));
        Assert.Null(PermissionParts.Refusal(change, PoliciesOn));
        Assert.Equal(Permission.Manage, PermissionTable.ForAction(change.ChangeAction));
        Assert.Contains("Intune", Undo.WhyNot(Log(change)), StringComparison.Ordinal);
    }

    [Fact]
    public void OldDevicesInEntraAreFirstDisabledAndOnlyDeletedAfterNinetyDays()
    {
        var old = Entra("1", true, 100);
        var recent = Entra("2", true, 10);
        var hybrid = Entra("3", true, 100, "ServerAd");
        var autopilot = Entra("4", true, 100, autopilot: true);
        var disabledLongAgo = Entra("5", false, 100);
        var disabledRecently = Entra("6", false, 400);
        var disabledElsewhere = Entra("7", false, 200);
        var disabledElsewhereRecently = Entra("8", false, 100);
        var all = new[] { old, recent, hybrid, autopilot, disabledLongAgo, disabledRecently, disabledElsewhere, disabledElsewhereRecently };

        Assert.Equal(new[] { old }, DeviceCleanup.ToDisable(all, Now));
        var left = DeviceCleanup.LeftAlone(all, Now);
        Assert.Equal(new[] { "3", "4" }, left.Select(x => x.Device.Id));
        Assert.Contains("AD", left[0].Why, StringComparison.Ordinal);
        Assert.Contains("Autopilot", left[1].Why, StringComparison.Ordinal);

        // Disabled by Moss365 95 days ago may go; 30 days ago not yet, however long it was not seen; disabled elsewhere after 180 days
        var log = new[] { Disabled(disabledLongAgo, 95), Disabled(disabledRecently, 30) };
        Assert.Equal(Now.AddDays(-30), DeviceCleanup.DisabledOn(disabledRecently.Id, log));
        Assert.Equal(new[] { "5", "7" }, DeviceCleanup.ToDelete(all, log, Now).Select(d => d.Id));

        // Disabling: only with the part on, never a hybrid or Autopilot device, and it can be undone
        Assert.NotEmpty(DeviceCleanup.Disable(new[] { old }, null).Errors);
        var disable = Assert.Single(DeviceCleanup.Disable(new[] { old, hybrid, autopilot, recent with { Enabled = false } }, CleanupOn).Plan);
        Assert.Equal((ChangeAction.GraphSetDeviceEnabled, "1", "False", "True"), (disable.ChangeAction, disable.Target, disable.P("aan"), disable.Previous!["aan"]));
        var back = Assert.Single(Undo.Plan(new[] { Log(disable) }).Plan);
        Assert.Equal((ChangeAction.GraphSetDeviceEnabled, "True", TargetSystem.Graph), (back.ChangeAction, back.P("aan"), back.TargetSystem));

        // Deleting: the record in Intune first, then the one in Entra, with a warning for BitLocker keys
        var managed = Intune("m5", "Windows", 200, disabledLongAgo.DeviceId);
        var keys = new Dictionary<string, int> { [disabledLongAgo.DeviceId] = 2 };
        Assert.NotEmpty(DeviceCleanup.Delete(new[] { disabledLongAgo }, new[] { managed }, keys, null).Errors);
        var plan = DeviceCleanup.Delete(new[] { disabledLongAgo, disabledElsewhere, old, hybrid with { Enabled = false } }, new[] { managed }, keys, CleanupOn).Plan;
        Assert.Equal(new[] { ChangeAction.IntuneDeleteDevice, ChangeAction.GraphDeleteDevice, ChangeAction.GraphDeleteDevice }, plan.Select(c => c.ChangeAction));
        Assert.Equal(("m5", "5", "7"), (plan[0].Target, plan[1].Target, plan[2].Target));
        Assert.Equal(plan[0].Id, plan[1].DependentOf);
        Assert.Contains("2 BitLocker", plan[1].Warning, StringComparison.Ordinal);
        Assert.Equal("Dit kan niet ongedaan gemaakt worden.", plan[2].Warning);
        Assert.Null(plan[2].DependentOf);
        Assert.NotNull(Undo.WhyNot(Log(plan[1])));
        Assert.NotNull(Undo.WhyNot(Log(plan[0])));

        // The engine refuses Entra changes with the part off; Intune records need no part
        Assert.NotNull(PermissionParts.Refusal(disable, null));
        Assert.NotNull(PermissionParts.Refusal(plan[1], PoliciesOn));
        Assert.Null(PermissionParts.Refusal(plan[1], CleanupOn));
        Assert.Null(PermissionParts.Refusal(plan[0], null));
    }

    [Fact]
    public void RecordsInIntuneNotSeenForHalfAYearCanAlwaysGo()
    {
        var devices = new[] { Intune("a", "Windows", 200), Intune("b", "iOS", 10), Intune("c", "Android", 1) with { LastSync = null } };
        var stale = DeviceCleanup.IntuneStale(devices, Now);
        Assert.Equal(new[] { "a", "c" }, stale.Select(d => d.Id));
        var plan = DeviceCleanup.DeleteIntune(stale).Plan;
        Assert.All(plan, c => Assert.Equal((ChangeAction.IntuneDeleteDevice, "Dit kan niet ongedaan gemaakt worden."), (c.ChangeAction, c.Warning)));
        Assert.Equal(("M-a", "company"), (plan[0].P("toestel"), plan[0].P("eigendom")));
        Assert.Equal(Permission.Manage, PermissionTable.ForAction(ChangeAction.IntuneDeleteDevice));
        Assert.Null(DeviceSafety.Refusal(plan[0]));
    }
    [Fact]
    public void DevicesWithoutAPolicyAreMarkedNotCompliantOnlyAsAPlanWithAWarning()
    {
        var devices = new[] { Intune("1", "Windows", 1) with { Name = "PC-1" }, Intune("2", "Windows", 1) with { Name = "PC-2" }, Intune("3", "iOS", 1) with { Name = "GSM-3" } };
        var reading = new ComplianceReading(
            new[] { new CompliancePolicy("p1", "Basis", "Windows", new[] { new ComplianceDeviceStatus("PC-1", "a", "compliant", null), new ComplianceDeviceStatus("GSM-3", "c", "notApplicable", null) }) },
            Array.Empty<ComplianceSetting>(), false, null)
        { SettingsJson = "{\"deviceComplianceCheckinThresholdDays\":30,\"isScheduledActionEnabled\":true,\"secureByDefault\":false}" };
        Assert.Equal(new[] { "GSM-3", "PC-2" }, ComplianceAnalysis.WithoutPolicy(devices, reading).Select(d => d.Name));

        Assert.NotEmpty(ComplianceAnalysis.SecureByDefault(reading, devices, null).Errors);
        Assert.NotEmpty(ComplianceAnalysis.SecureByDefault(reading with { NoPolicyIsNotCompliant = true }, devices, PoliciesOn).Errors);
        Assert.NotEmpty(ComplianceAnalysis.SecureByDefault(reading with { NoPolicyIsNotCompliant = null }, devices, PoliciesOn).Errors);
        var change = Assert.Single(ComplianceAnalysis.SecureByDefault(reading, devices, PoliciesOn).Plan);
        Assert.Equal(ChangeAction.IntuneSetComplianceSettings, change.ChangeAction);
        Assert.Contains("\"secureByDefault\":true", change.P("json"), StringComparison.Ordinal);
        Assert.Contains("\"deviceComplianceCheckinThresholdDays\":30", change.P("json"), StringComparison.Ordinal); // the rest as read
        Assert.Contains("2 toestel(len) hebben geen nalevingsbeleid", change.Warning, StringComparison.Ordinal);
        Assert.Contains("GSM-3, PC-2", change.Warning, StringComparison.Ordinal);

        // Undo puts the settings back as read; the engine refuses it with the part off
        var back = Assert.Single(Undo.Plan(new[] { Log(change) }).Plan);
        Assert.Equal(reading.SettingsJson, back.P("json"));
        Assert.NotNull(PermissionParts.Refusal(change, null));
        Assert.Null(PermissionParts.Refusal(change, PoliciesOn));
    }
}
