using Moss365.Core.Changes;
using Moss365.Core.Localization;

namespace Moss365.Core.Devices;

/// <summary>Safe actions change nothing on the device for the user; management changes its name or removes company data; destructive ones wipe it.</summary>
public enum DeviceActionLevel
{
    Safe,
    Management,
    Destructive,
}

/// <summary>An action on an Intune device (R17, U17), its level and whether it is for Windows only.</summary>
public sealed record DeviceActionInfo(string Key, DeviceActionLevel Level, bool WindowsOnly = false);

/// <summary>
/// Actions on Intune devices as plans (R17, U17): safe (sync, restart, rotate the LAPS password or the BitLocker key,
/// a Defender scan; also for the Helpdesk), management (rename, retire) and destructive (wipe, fresh start: the
/// Administrator only, confirmed by typing the device name, never for a personal device). See <see cref="DeviceSafety"/>.
/// </summary>
public static class DevicePlans
{
    /// <summary>Windows allows at most 15 characters, letters, digits and hyphens, for a device name.</summary>
    public const int MaxNameLength = 15;

    public static IReadOnlyList<DeviceActionInfo> Actions { get; } = new DeviceActionInfo[]
    {
        new("sync", DeviceActionLevel.Safe),
        new("reboot", DeviceActionLevel.Safe),
        new("rotateLaps", DeviceActionLevel.Safe, WindowsOnly: true),
        new("rotateBitLocker", DeviceActionLevel.Safe, WindowsOnly: true),
        new("defenderScan", DeviceActionLevel.Safe, WindowsOnly: true),
        new("rename", DeviceActionLevel.Management),
        new("retire", DeviceActionLevel.Management),
        new("primaryUser", DeviceActionLevel.Management),
        new("wipe", DeviceActionLevel.Destructive),
        new("freshStart", DeviceActionLevel.Destructive, WindowsOnly: true),
    };

    public static DeviceActionInfo? Info(string? key) => Actions.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>The level of a change, or null when it is not an action on a device.</summary>
    public static DeviceActionLevel? LevelOf(Change w) => w.ChangeAction == ChangeAction.IntuneDeviceAction ? Info(w.P("actie"))?.Level : null;

    public static bool Windows(IntuneDevice device) => device.OperatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>Can this action be done on this device at all (Windows only, never wipe a personal device)?</summary>
    public static bool Fits(IntuneDevice device, DeviceActionInfo info)
        => (!info.WindowsOnly || Windows(device)) && !(info.Level == DeviceActionLevel.Destructive && device.Personal);

    /// <summary>
    /// The plan for one action on one device. A rename needs a valid new name; a destructive action needs the device
    /// name typed exactly, and is refused for a personal device.
    /// </summary>
    /// <summary>A new primary user (management): the device and its company apps follow that person. The previous
    /// primary user is kept for undo when known.</summary>
    public static PlanOutcome PrimaryUser(IntuneDevice device, Inventory.EntraUser user, string? previousUserId)
    {
        if (string.Equals(user.UserPrincipalName, device.Owner, StringComparison.OrdinalIgnoreCase))
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["device.action.primary-user-same"] });
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.IntuneDeviceAction,
                Target = device.Id,
                TargetName = device.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["actie"] = "primaryUser", ["toestel"] = device.Name, ["eigendom"] = device.Ownership, ["gebruiker"] = user.Id, ["gebruikerNaam"] = user.UserPrincipalName,
                },
                Previous = string.IsNullOrEmpty(previousUserId) ? null : new(StringComparer.OrdinalIgnoreCase) { ["gebruiker"] = previousUserId, ["gebruikerNaam"] = device.Owner },
                Description = Texts.T.Format("device.action.primaryUser", device.Name, user.UserPrincipalName),
                CurrentValue = device.Owner,
                NewValue = user.UserPrincipalName,
            },
        }, Array.Empty<string>());
    }

    public static PlanOutcome Action(IntuneDevice device, string action, string? newName = null, string? typedName = null)
    {
        if (Info(action) is not { } info || action == "primaryUser")
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["device.action.unknown"] });
        }

        var errors = new List<string>();
        if (info.WindowsOnly && !Windows(device))
        {
            errors.Add(Texts.T["device.action.windows-only"]);
        }

        if (info.Level == DeviceActionLevel.Destructive)
        {
            if (device.Personal)
            {
                errors.Add(Texts.T["device.safety.personal"]);
            }

            if (!string.Equals(typedName?.Trim(), device.Name, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Texts.T["device.safety.type-name"]);
            }
        }

        var name = newName?.Trim() ?? string.Empty;
        if (action == "rename" && (name.Length is 0 or > MaxNameLength || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || name.All(char.IsAsciiDigit)))
        {
            errors.Add(Texts.T.Format("device.action.rename-invalid", MaxNameLength));
        }

        if (errors.Count > 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["actie"] = action,
            ["toestel"] = device.Name,
            ["eigendom"] = device.Ownership,
        };
        if (action == "rename")
        {
            parameters["naam"] = name;
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.IntuneDeviceAction,
                Target = device.Id,
                TargetName = device.Name,
                Parameters = parameters,
                Previous = action == "rename" ? new(StringComparer.OrdinalIgnoreCase) { ["naam"] = device.Name } : null,
                Description = action == "rename" ? Texts.T.Format("device.action.rename", device.Name, name) : Texts.T.Format($"device.action.{action}", device.Name),
                CurrentValue = action == "rename" ? device.Name : null,
                NewValue = action == "rename" ? name : null,
                Warning = info.Level == DeviceActionLevel.Destructive ? Texts.T["device.action.cannot-undo"] : null,
            },
        }, Array.Empty<string>());
    }
}

/// <summary>The devices of a person and what offboarding does with them (R17): retire a personal device; a company device
/// is returned, and wiped on Toestellen once it is back (never in the offboarding plan itself).</summary>
public static class LeaverDevices
{
    /// <summary>The Intune devices whose primary user is this person (by UPN or address).</summary>
    public static IReadOnlyList<IntuneDevice> Of(IEnumerable<IntuneDevice> devices, Inventory.Person person)
    {
        var names = new[] { person.Entra?.UserPrincipalName, person.Entra?.Mail, person.Ad?.UserPrincipalName }
            .Where(n => !string.IsNullOrEmpty(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return devices.Where(d => names.Contains(d.Owner)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Retire for every personal device; a note per company device to return.</summary>
    public static (IReadOnlyList<Change> Plan, IReadOnlyList<string> Notes) Plan(IEnumerable<IntuneDevice> devices, Inventory.Person person)
    {
        var plan = new List<Change>();
        var notes = new List<string>();
        foreach (var d in Of(devices, person))
        {
            if (d.Personal)
            {
                plan.AddRange(DevicePlans.Action(d, "retire").Plan);
            }
            else
            {
                notes.Add(Texts.T.Format("device.leaver.return", d.Name));
            }
        }

        return (plan, notes);
    }
}

/// <summary>
/// The safeguards for actions on devices (R17, U17), in the planner and in the engine, like <c>CaSafety</c> for Conditional
/// Access: a destructive action never for a personal device, and never for more than one device in one plan.
/// </summary>
public static class DeviceSafety
{
    public static string? Refusal(Change w)
        => DevicePlans.LevelOf(w) == DeviceActionLevel.Destructive && string.Equals(w.P("eigendom"), "personal", StringComparison.OrdinalIgnoreCase)
            ? Texts.T["device.safety.personal"]
            : null;

    public static string? PlanRefusal(IEnumerable<Change> plan)
        => plan.Where(w => w.Selected && DevicePlans.LevelOf(w) == DeviceActionLevel.Destructive).Select(w => w.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            ? Texts.T["device.safety.one-device"]
            : null;
}
