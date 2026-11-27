using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Setup;

namespace Moss365.Core.Devices;

/// <summary>The status of one device for one compliance policy.</summary>
public sealed record ComplianceDeviceStatus(string Device, string User, string Status, DateTimeOffset? LastReported)
{
    public bool Failing => Status is "nonCompliant" or "error" or "conflict";
}

/// <summary>A compliance policy in Intune, with the status of its devices.</summary>
public sealed record CompliancePolicy(string Id, string Name, string Platform, IReadOnlyList<ComplianceDeviceStatus> Devices)
{
    public IReadOnlyList<ComplianceDeviceStatus> Failing => Devices.Where(d => d.Failing).ToList();
}

/// <summary>A setting of the compliance policies and on how many devices it fails (over all policies).</summary>
public sealed record ComplianceSetting(string Setting, string Platform, int Compliant, int NonCompliant, int Error);

/// <summary>The state of one setting of a compliance policy on one device.</summary>
public sealed record ComplianceSettingState(string Setting, string State)
{
    public bool Failing => State is "nonCompliant" or "error" or "conflict";
}

/// <summary>A compliance policy as one device has it (R17, the panel of a device): its state and the settings.</summary>
public sealed record DeviceComplianceState(string Policy, string State, IReadOnlyList<ComplianceSettingState> Settings)
{
    public IReadOnlyList<ComplianceSettingState> Failing => Settings.Where(s => s.Failing).ToList();
}

/// <summary>What was read of compliance (R17, U26): the policies, the settings, and whether a device without a policy
/// counts as not compliant (null: unknown).</summary>
public sealed record ComplianceReading(IReadOnlyList<CompliancePolicy> Policies, IReadOnlyList<ComplianceSetting> Settings, bool? NoPolicyIsNotCompliant, string? Note)
{
    /// <summary>The compliance settings of Intune as read (JSON), to change one of them and send the rest as it was.</summary>
    public string? SettingsJson { get; init; }
}

/// <summary>
/// Compliance per policy (R17): which devices fail which policy, on which setting, which platforms have no policy at
/// all, and whether devices without a policy count as compliant. "Compliant device" in Conditional Access only means
/// something with this.
/// </summary>
public static class ComplianceAnalysis
{
    /// <summary>The platform of a policy (its @odata.type) or of a device (its system), in a few words.</summary>
    public static string PlatformOf(string typeOrSystem)
    {
        var t = typeOrSystem.ToLowerInvariant();
        return t.Contains("windows", StringComparison.Ordinal) ? "Windows"
            : t.Contains("ios", StringComparison.Ordinal) || t.Contains("ipados", StringComparison.Ordinal) ? "iOS"
            : t.Contains("android", StringComparison.Ordinal) || t.Contains("aosp", StringComparison.Ordinal) ? "Android"
            : t.Contains("macos", StringComparison.Ordinal) ? "macOS"
            : t.Contains("linux", StringComparison.Ordinal) ? "Linux"
            : typeOrSystem;
    }

    /// <summary>The platforms of the devices in Intune that no compliance policy covers, with how many devices.</summary>
    public static IReadOnlyList<(string Platform, int Devices)> PlatformsWithoutPolicy(IEnumerable<IntuneDevice> devices, IEnumerable<CompliancePolicy> policies)
    {
        var covered = policies.Select(p => p.Platform).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return devices.GroupBy(d => PlatformOf(d.OperatingSystem), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && !covered.Contains(g.Key))
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>The policies one device fails, from the reading per policy (by the name of the device).</summary>
    public static IReadOnlyList<CompliancePolicy> FailedBy(ComplianceReading? reading, string deviceName)
        => reading?.Policies.Where(p => p.Failing.Any(d => string.Equals(d.Device, deviceName, StringComparison.OrdinalIgnoreCase))).ToList()
           ?? (IReadOnlyList<CompliancePolicy>)Array.Empty<CompliancePolicy>();

    /// <summary>The devices in Intune that no compliance policy covers (no status for them in any policy, or only "not
    /// applicable"): they count as compliant or not depending on the setting for devices without a policy.</summary>
    public static IReadOnlyList<IntuneDevice> WithoutPolicy(IEnumerable<IntuneDevice> devices, ComplianceReading reading)
    {
        var covered = reading.Policies.SelectMany(p => p.Devices).Where(d => d.Status is not ("notApplicable" or "notAssigned"))
            .Select(d => d.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return devices.Where(d => !covered.Contains(d.Name)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The plan that marks devices without a compliance policy as not compliant (U26), only with the part
    /// "baseline compliance policies" on, with a warning for the devices that will then fail; undo puts the settings back
    /// as they were read.</summary>
    public static PlanOutcome SecureByDefault(ComplianceReading reading, IEnumerable<IntuneDevice>? devices, IEnumerable<string>? parts)
    {
        if (!PermissionParts.IsOn(parts, PermissionParts.CompliancePolicies))
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["compliance.template.part-off"] });
        }

        if (reading.NoPolicyIsNotCompliant != false || reading.SettingsJson is not { } before || JsonNode.Parse(before) is not JsonObject settings)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["compliance.secure.cannot"] });
        }

        settings["secureByDefault"] = true;
        var without = WithoutPolicy(devices ?? Array.Empty<IntuneDevice>(), reading);
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.IntuneSetComplianceSettings,
                Target = "deviceManagement",
                TargetName = Texts.T["compliance.context"],
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = settings.ToJsonString() },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["json"] = before },
                Description = Texts.T["compliance.secure.plan"],
                CurrentValue = Texts.T["compliance.compliant"],
                NewValue = Texts.T["compliance.failing"],
                Warning = without.Count == 0
                    ? Texts.T["compliance.secure.warning.none"]
                    : Texts.T.Format("compliance.secure.warning", without.Count, string.Join(", ", without.Take(10).Select(d => d.Name)) + (without.Count > 10 ? ", ..." : string.Empty)),
            },
        }, Array.Empty<string>());
    }

    /// <summary>What to do, in words: devices without a policy that count as compliant, platforms without a policy.</summary>
    public static IReadOnlyList<string> Advice(ComplianceReading reading, IEnumerable<IntuneDevice>? devices)
    {
        var output = new List<string>();
        if (reading.NoPolicyIsNotCompliant == false)
        {
            output.Add(Texts.T["compliance.advice.secure-by-default"]);
        }

        foreach (var (platform, count) in PlatformsWithoutPolicy(devices ?? Array.Empty<IntuneDevice>(), reading.Policies))
        {
            output.Add(Texts.T.Format("compliance.advice.no-policy", platform, count));
        }

        return output;
    }
}

/// <summary>A baseline compliance policy that Moss365 can make (U26), per platform.</summary>
public sealed record ComplianceTemplate(string Key, string Platform, string Name, string Json);

/// <summary>
/// Baseline compliance policies as templates (R17, U26), like the Conditional Access templates: made as a plan, not
/// assigned yet (you assign them to a group in Intune), and only for a tenant with the part "baseline compliance
/// policies" on. A device of an assigned group that fails a policy is marked not compliant after three days.
/// </summary>
public static class ComplianceTemplates
{
    private static JsonArray Block() => new()
    {
        new JsonObject
        {
            ["ruleName"] = "PasswordRequired",
            ["scheduledActionConfigurations"] = new JsonArray { new JsonObject { ["actionType"] = "block", ["gracePeriodHours"] = 72 } },
        },
    };

    public static IReadOnlyList<ComplianceTemplate> All
    {
        get
        {
            var windows = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.windows10CompliancePolicy",
                ["displayName"] = Texts.T["compliance.template.name.windows"],
                ["description"] = Texts.T["compliance.template.description"],
                ["bitLockerEnabled"] = true, ["secureBootEnabled"] = true, ["codeIntegrityEnabled"] = true, ["storageRequireEncryption"] = true,
                ["activeFirewallRequired"] = true, ["defenderEnabled"] = true, ["rtpEnabled"] = true, ["antivirusRequired"] = true, ["antiSpywareRequired"] = true,
                ["osMinimumVersion"] = "10.0.19045",
                ["scheduledActionsForRule"] = Block(),
            };
            var ios = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.iosCompliancePolicy",
                ["displayName"] = Texts.T["compliance.template.name.ios"],
                ["description"] = Texts.T["compliance.template.description"],
                ["passcodeRequired"] = true, ["passcodeMinimumLength"] = 6, ["passcodeBlockSimple"] = true, ["securityBlockJailbrokenDevices"] = true,
                ["osMinimumVersion"] = "17.0",
                ["scheduledActionsForRule"] = Block(),
            };
            var android = new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.androidWorkProfileCompliancePolicy",
                ["displayName"] = Texts.T["compliance.template.name.android"],
                ["description"] = Texts.T["compliance.template.description"],
                ["passwordRequired"] = true, ["passwordMinimumLength"] = 6, ["securityBlockJailbrokenDevices"] = true,
                ["securityRequireSafetyNetAttestationBasicIntegrity"] = true, ["osMinimumVersion"] = "12.0",
                ["scheduledActionsForRule"] = Block(),
            };
            return new[]
            {
                new ComplianceTemplate("windows", "Windows", (string)windows["displayName"]!, windows.ToJsonString()),
                new ComplianceTemplate("ios", "iOS", (string)ios["displayName"]!, ios.ToJsonString()),
                new ComplianceTemplate("android", "Android", (string)android["displayName"]!, android.ToJsonString()),
            };
        }
    }

    /// <summary>The plan that makes a template, or why not (the part is off, a policy with that name exists).</summary>
    public static PlanOutcome Create(ComplianceTemplate template, IEnumerable<CompliancePolicy> existing, IEnumerable<string>? parts)
    {
        if (!PermissionParts.IsOn(parts, PermissionParts.CompliancePolicies))
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["compliance.template.part-off"] });
        }

        if (existing.Any(p => string.Equals(p.Name, template.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T.Format("compliance.template.exists", template.Name) });
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.IntuneCreateCompliancePolicy,
                Target = template.Name,
                TargetName = template.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = template.Json, ["platform"] = template.Platform },
                Description = Texts.T.Format("compliance.template.plan", template.Name),
                Warning = Texts.T["compliance.template.not-assigned"],
            },
        }, Array.Empty<string>());
    }
}
