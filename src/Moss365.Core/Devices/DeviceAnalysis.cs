using Moss365.Core.Localization;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;

namespace Moss365.Core.Devices;

/// <summary>A computer in AD.</summary>
public sealed record AdComputer(string Name, string DistinguishedName, string OperatingSystem, string Version, DateTimeOffset? LastLogon, bool Enabled, DateTimeOffset? Created)
{
    public string OuPath => string.Join('/', DistinguishedName.Split(',').Skip(1).Where(p => p.StartsWith("OU=", StringComparison.OrdinalIgnoreCase)).Reverse().Select(p => p[3..]));
}

/// <summary>A device in Intune.</summary>
public sealed record IntuneDevice(string Id, string Name, string Owner, string OperatingSystem, string Version, string Compliance, DateTimeOffset? LastSync, string Ownership);

/// <summary>A Microsoft 365 group (or Team): owners and activity, read live.</summary>
public sealed record GroupActivity(string Id, string Name, bool IsTeam, int Owners, DateTimeOffset? LastActivity);

/// <summary>A SharePoint site: storage and activity (from the usage report).</summary>
public sealed record SiteUsage(string Url, string Owner, long StorageBytes, long Files, DateTimeOffset? LastActivity);

/// <summary>What was read for Teams and SharePoint; the sharing setting is the one of the tenant.</summary>
public sealed record CollaborationReading(IReadOnlyList<GroupActivity> Groups, IReadOnlyList<SiteUsage> Sites, string? Sharing, string? Note);

/// <summary>A Microsoft 365 group with what is worth a look.</summary>
public sealed record GroupFinding(string Id, string Name, bool IsTeam, int Owners, int Members, int Guests, DateTimeOffset? LastActivity, IReadOnlyList<string> Points);

/// <summary>
/// Devices, Teams and SharePoint (R13, 0.36): computers in AD that no longer sign in and their systems, devices in
/// Intune that are not compliant or not seen, Microsoft 365 groups and Teams without an owner, with guests or unused,
/// and SharePoint sites with their storage and activity. Disabling stale computers is a plan; nothing is deleted.
/// </summary>
public static class DeviceAnalysis
{
    /// <summary>Enabled computers that have not signed in for this many days (or never).</summary>
    public static IReadOnlyList<AdComputer> StaleComputers(IReadOnlyList<AdComputer> computers, DateTimeOffset now, int days = 90)
        => computers.Where(c => c.Enabled && (c.LastLogon is null || c.LastLogon < now.AddDays(-days)))
            .OrderBy(c => c.LastLogon ?? DateTimeOffset.MinValue).ToList();

    /// <summary>Systems and versions with their number of (enabled) computers.</summary>
    public static IReadOnlyList<(string System, int Count)> Systems(IReadOnlyList<AdComputer> computers)
        => computers.Where(c => c.Enabled)
            .GroupBy(c => $"{(c.OperatingSystem.Length > 0 ? c.OperatingSystem : "(onbekend)")} {c.Version}".Trim())
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();

    /// <summary>Is this system no longer supported by Microsoft?</summary>
    public static bool Outdated(string os)
        => new[] { "XP", "Vista", "Windows 7", "Windows 8", "2003", "2008", "2012" }.Any(o => os.Contains(o, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<IntuneDevice> NotCompliant(IReadOnlyList<IntuneDevice> devices)
        => devices.Where(d => d.Compliance is not ("compliant" or "unknown" or "")).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static IReadOnlyList<IntuneDevice> NotSeen(IReadOnlyList<IntuneDevice> devices, DateTimeOffset now, int days = 30)
        => devices.Where(d => d.LastSync is null || d.LastSync < now.AddDays(-days)).OrderBy(d => d.LastSync ?? DateTimeOffset.MinValue).ToList();

    /// <summary>The Microsoft 365 groups with what is worth a look: no owner, guests, no activity for 90 days.</summary>
    public static IReadOnlyList<GroupFinding> Groups(InventoryData d, CollaborationReading? reading, DateTimeOffset now)
    {
        var activity = (reading?.Groups ?? Array.Empty<GroupActivity>()).ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var guests = d.EntraUsers.Where(e => e.IsGuest).Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var members = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (member, groups) in d.CloudMembership)
        {
            foreach (var g in groups)
            {
                if (!members.TryGetValue(g, out var list))
                {
                    members[g] = list = new List<string>();
                }

                list.Add(member);
            }
        }

        var output = new List<GroupFinding>();
        foreach (var g in d.CloudGroups.Values.Where(g => g.Type == CloudGroupType.Microsoft365))
        {
            var all = members.GetValueOrDefault(g.Id) ?? new List<string>();
            var guestCount = all.Count(guests.Contains);
            var a = activity.GetValueOrDefault(g.Id);
            var points = new List<string>();
            if (a is { Owners: 0 })
            {
                points.Add(Texts.T["code.device-analysis.no-owner"]);
            }

            if (guestCount > 0)
            {
                points.Add(Texts.T.Format("code.device-analysis.guest-s", guestCount));
            }

            if (reading is not null && (a?.LastActivity is null || a.LastActivity < now.AddDays(-90)))
            {
                points.Add(a?.LastActivity is { } last ? Texts.T.Format("code.device-analysis.not-used-since", last.LocalDateTime) : Texts.T["code.device-analysis.no-activity-known"]);
            }

            output.Add(new GroupFinding(g.Id, g.DisplayName, a?.IsTeam ?? false, a?.Owners ?? -1, all.Count, guestCount, a?.LastActivity, points));
        }

        return output.OrderByDescending(f => f.Points.Count).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static IReadOnlyList<SiteUsage> UnusedSites(CollaborationReading reading, DateTimeOffset now, int days = 90)
        => reading.Sites.Where(s => s.LastActivity is null || s.LastActivity < now.AddDays(-days)).OrderByDescending(s => s.StorageBytes).ToList();

    /// <summary>The SharePoint sharing setting of the tenant in words, and whether it deserves attention.</summary>
    public static (string Text, bool Attention) Sharing(string? capability) => capability switch
    {
        "disabled" => (Texts.T["code.device-analysis.only-people-of-the-organisation"], false),
        "existingExternalUserSharingOnly" => (Texts.T["code.device-analysis.only-guests-already-in-the"], false),
        "externalUserSharingOnly" => (Texts.T["code.device-analysis.new-and-existing-guests-with"], false),
        "externalUserAndGuestSharing" => (Texts.T["code.device-analysis.everyone-also-with-a-link"], true),
        null or "" => (Texts.T["code.device-analysis.not-read"], false),
        _ => (capability, false),
    };

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} kB",
    };

    /// <summary>Disable stale computers in AD (as a plan; never deleted), optionally with the date in the description.</summary>
    public static PlanOutcome DisableComputers(IEnumerable<AdComputer> computers, DateOnly date)
        => PlanOutcome.Of(computers.Where(c => c.Enabled).Select(c => new Change
        {
            TargetSystem = TargetSystem.ActiveDirectory,
            ChangeAction = ChangeAction.AdDisableAccount,
            Target = c.DistinguishedName,
            TargetName = c.Name,
            Description = c.LastLogon is { } last ? Localization.Texts.T.Format("device.disable", c.Name, last.LocalDateTime) : Localization.Texts.T.Format("device.disable.never", c.Name),
            CurrentValue = Localization.Texts.T["device.enabled"],
            NewValue = Localization.Texts.T["device.disabled"],
        }));
}
