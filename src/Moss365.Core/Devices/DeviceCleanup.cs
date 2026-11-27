using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Setup;

namespace Moss365.Core.Devices;

/// <summary>
/// Cleaning up old devices (R17, U18, U25): a device in Entra not seen for 90 days is first disabled (undo possible); only
/// after 90 days disabled does a separate plan delete its record in Entra and in Intune, with a warning when BitLocker
/// keys are still attached (they go with the record). Entra needs the part "clean up old devices in Entra"; without it
/// Moss365 only cleans up Intune records not seen for 180 days, and shows the list for Entra.
/// </summary>
public static class DeviceCleanup
{
    public const int StaleDays = 90;
    public const int DisabledDays = 90;
    public const int IntuneDays = 180;

    /// <summary>A device Moss365 may clean up in Entra: not hybrid joined (cleaned up in AD) and not Autopilot.</summary>
    public static bool InScope(EntraDevice d) => !d.Hybrid && !d.Autopilot;

    private static bool Stale(EntraDevice d, DateTimeOffset now, int days) => d.LastSignIn is not { } s || s < now.AddDays(-days);

    /// <summary>Enabled devices in Entra not seen for 90 days.</summary>
    public static IReadOnlyList<EntraDevice> ToDisable(IEnumerable<EntraDevice> devices, DateTimeOffset now)
        => devices.Where(d => InScope(d) && d.Enabled && Stale(d, now, StaleDays)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Old devices Moss365 leaves alone, with the reason: hybrid joined (disable the computer in AD) or Autopilot.</summary>
    public static IReadOnlyList<(EntraDevice Device, string Why)> LeftAlone(IEnumerable<EntraDevice> devices, DateTimeOffset now)
        => devices.Where(d => !InScope(d) && Stale(d, now, StaleDays))
            .Select(d => (d, d.Hybrid ? Texts.T["device.cleanup.hybrid"] : Texts.T["device.cleanup.autopilot"]))
            .OrderBy(x => x.d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>When Moss365 disabled a device (the change log), or null.</summary>
    public static DateTimeOffset? DisabledOn(string deviceObjectId, IEnumerable<LogEntry> changeLog)
        => changeLog.Where(r => r.ChangeAction == nameof(ChangeAction.GraphSetDeviceEnabled) && r.Succeeded
                                && string.Equals(r.Target, deviceObjectId, StringComparison.OrdinalIgnoreCase) && r.Parameters.GetValueOrDefault("aan") == "False")
            .Select(r => (DateTimeOffset?)r.Time).OrderByDescending(t => t).FirstOrDefault();

    /// <summary>Disabled devices that may go: disabled by Moss365 at least 90 days ago, or, disabled outside Moss365, not
    /// seen for 90 + 90 days.</summary>
    public static IReadOnlyList<EntraDevice> ToDelete(IEnumerable<EntraDevice> devices, IEnumerable<LogEntry> changeLog, DateTimeOffset now)
    {
        var log = changeLog.ToList();
        return devices.Where(d => InScope(d) && !d.Enabled && (DisabledOn(d.Id, log) is { } on ? on <= now.AddDays(-DisabledDays) : Stale(d, now, StaleDays + DisabledDays)))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Records in Intune not seen for 180 days (also without the Entra part).</summary>
    public static IReadOnlyList<IntuneDevice> IntuneStale(IEnumerable<IntuneDevice> devices, DateTimeOffset now)
        => devices.Where(d => d.LastSync is not { } s || s < now.AddDays(-IntuneDays)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static PlanOutcome Disable(IEnumerable<EntraDevice> chosen, IEnumerable<string>? parts)
    {
        if (!PermissionParts.IsOn(parts, PermissionParts.EntraDeviceCleanup))
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["device.cleanup.part-off"] });
        }

        return PlanOutcome.Of(chosen.Where(d => d.Enabled && InScope(d)).Select(d => new Change
        {
            TargetSystem = TargetSystem.Graph,
            ChangeAction = ChangeAction.GraphSetDeviceEnabled,
            Target = d.Id,
            TargetName = d.Name,
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["aan"] = "False", ["deviceId"] = d.DeviceId },
            Previous = new(StringComparer.OrdinalIgnoreCase) { ["aan"] = "True" },
            Description = Texts.T.Format("device.cleanup.disable", d.Name),
        }).ToList(), Array.Empty<string>());
    }

    /// <summary>Deletes the record in Intune (when there is one) and then the one in Entra; a warning per device whose
    /// BitLocker keys would go with it.</summary>
    public static PlanOutcome Delete(IEnumerable<EntraDevice> chosen, IEnumerable<IntuneDevice>? intune, IReadOnlyDictionary<string, int> bitLockerKeys, IEnumerable<string>? parts)
    {
        if (!PermissionParts.IsOn(parts, PermissionParts.EntraDeviceCleanup))
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["device.cleanup.part-off"] });
        }

        var managed = (intune ?? Array.Empty<IntuneDevice>()).Where(m => m.EntraDeviceId.Length > 0).ToLookup(m => m.EntraDeviceId, StringComparer.OrdinalIgnoreCase);
        var plan = new List<Change>();
        foreach (var d in chosen.Where(d => !d.Enabled && InScope(d)))
        {
            Change? last = null;
            foreach (var m in managed[d.DeviceId])
            {
                last = IntuneDelete(m, last?.Id);
                plan.Add(last);
            }

            var keys = bitLockerKeys.GetValueOrDefault(d.DeviceId);
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphDeleteDevice,
                Target = d.Id,
                TargetName = d.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["deviceId"] = d.DeviceId, ["sleutels"] = keys.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                Description = Texts.T.Format("device.cleanup.delete", d.Name),
                Warning = keys > 0 ? Texts.T.Format("device.cleanup.bitlocker-warning", keys) : Texts.T["device.action.cannot-undo"],
                DependentOf = last?.Id,
            });
        }

        return PlanOutcome.Of(plan, Array.Empty<string>());
    }

    /// <summary>Deletes records in Intune that were not seen for 180 days (no Entra part needed).</summary>
    public static PlanOutcome DeleteIntune(IEnumerable<IntuneDevice> chosen) => PlanOutcome.Of(chosen.Select(m => IntuneDelete(m, null)).ToList(), Array.Empty<string>());

    private static Change IntuneDelete(IntuneDevice m, Guid? after) => new()
    {
        TargetSystem = TargetSystem.Graph,
        ChangeAction = ChangeAction.IntuneDeleteDevice,
        Target = m.Id,
        TargetName = m.Name,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["toestel"] = m.Name, ["eigendom"] = m.Ownership },
        Description = Texts.T.Format("device.cleanup.delete-intune", m.Name),
        Warning = Texts.T["device.action.cannot-undo"],
        DependentOf = after,
    };
}
