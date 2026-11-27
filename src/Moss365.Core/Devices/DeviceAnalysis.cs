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
public sealed record IntuneDevice(string Id, string Name, string Owner, string OperatingSystem, string Version, string Compliance, DateTimeOffset? LastSync, string Ownership)
{
    public string Serial { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Manufacturer { get; init; } = string.Empty;

    public bool? Encrypted { get; init; }

    public DateTimeOffset? Enrolled { get; init; }

    /// <summary>The deviceId of its record in Entra (not the object id), to find that record.</summary>
    public string EntraDeviceId { get; init; } = string.Empty;

    /// <summary>A personal device (bring your own): only retire, never wipe (R17).</summary>
    public bool Personal => string.Equals(Ownership, "personal", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A device in Entra ID: joined (AzureAd), hybrid joined (ServerAd) or registered (Workplace) (R17).</summary>
public sealed record EntraDevice(string Id, string DeviceId, string Name, string OperatingSystem, string Version, bool Enabled, DateTimeOffset? LastSignIn, string TrustType)
{
    /// <summary>Registered with Windows Autopilot (a physical id [ZTDID]): never disabled or deleted, it could no longer be
    /// provisioned (R17, U18). Not kept in the snapshots: on a date in the past nothing is written anyway.</summary>
    public bool Autopilot { get; init; }

    /// <summary>Hybrid joined: managed in AD, cleaned up there (Entra Connect follows).</summary>
    public bool Hybrid => string.Equals(TrustType, "ServerAd", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One device over AD, Entra and Intune (R17), with what deserves a look (keys of <see cref="DeviceAnalysis.Findings"/>).</summary>
public sealed record DeviceRecord(string Name, AdComputer? Ad, EntraDevice? Entra, IntuneDevice? Intune, IReadOnlyList<string> Findings)
{
    public DateTimeOffset? LastSeen => new[] { Ad?.LastLogon, Entra?.LastSignIn, Intune?.LastSync }.Where(d => d is not null).DefaultIfEmpty(null).Max();
}

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

    /// <summary>Is this system (with its version) no longer supported by Microsoft? From the life cycle data (R17).</summary>
    public static bool Outdated(string os) => Lifecycle.Of(os, string.Empty, DateTimeOffset.Now).Status == SupportStatus.Ended;

    /// <summary>What a device in the one list can have: in AD but not in Entra, not in Intune, only in Intune, more than one
    /// record in Entra, not seen for 90 days.</summary>
    public static IReadOnlyList<string> Findings { get; } = new[] { "ad-only", "not-managed", "intune-orphan", "duplicate", "stale" };

    /// <summary>
    /// One list over AD, Entra and Intune (R17): Intune and Entra by the Entra device id, AD and Entra by name for a
    /// hybrid joined device. A source that was not read (null) gives no findings about it.
    /// </summary>
    public static IReadOnlyList<DeviceRecord> Combine(IReadOnlyList<AdComputer>? ad, IReadOnlyList<EntraDevice>? entra, IReadOnlyList<IntuneDevice>? intune, DateTimeOffset now, int staleDays = 90)
    {
        var entraList = entra ?? Array.Empty<EntraDevice>();
        var intuneList = intune ?? Array.Empty<IntuneDevice>();
        var intuneByEntra = new Dictionary<string, IntuneDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in intuneList.Where(i => i.EntraDeviceId.Length > 0))
        {
            intuneByEntra.TryAdd(i.EntraDeviceId, i);
        }

        var adByName = new Dictionary<string, AdComputer>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ad ?? Array.Empty<AdComputer>())
        {
            adByName.TryAdd(c.Name, c);
        }

        var duplicates = entraList.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedIntune = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedAd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Name, AdComputer? Ad, EntraDevice? Entra, IntuneDevice? Intune)>();
        foreach (var e in entraList)
        {
            var i = e.DeviceId.Length > 0 ? intuneByEntra.GetValueOrDefault(e.DeviceId) : null;
            if (i is not null)
            {
                usedIntune.Add(i.Id);
            }

            AdComputer? c = null;
            if (string.Equals(e.TrustType, "ServerAd", StringComparison.OrdinalIgnoreCase) && adByName.TryGetValue(e.Name, out var computer) && usedAd.Add(computer.Name))
            {
                c = computer;
            }

            found.Add((e.Name, c, e, i));
        }

        found.AddRange(intuneList.Where(i => !usedIntune.Contains(i.Id)).Select(i => (i.Name, (AdComputer?)null, (EntraDevice?)null, (IntuneDevice?)i)));
        found.AddRange((ad ?? Array.Empty<AdComputer>()).Where(c => !usedAd.Contains(c.Name)).Select(c => (c.Name, (AdComputer?)c, (EntraDevice?)null, (IntuneDevice?)null)));

        return found.Select(f =>
        {
            var points = new List<string>();
            if (entra is not null && f.Ad is { Enabled: true } && f.Entra is null)
            {
                points.Add("ad-only");
            }

            if (intune is not null && f.Entra is { Enabled: true } && f.Intune is null)
            {
                points.Add("not-managed");
            }

            if (entra is not null && f.Intune is not null && f.Entra is null)
            {
                points.Add("intune-orphan");
            }

            if (f.Entra is not null && duplicates.Contains(f.Name))
            {
                points.Add("duplicate");
            }

            var record = new DeviceRecord(f.Name, f.Ad, f.Entra, f.Intune, points);
            if ((f.Ad?.Enabled ?? f.Entra?.Enabled ?? true) && (record.LastSeen is not { } seen || seen < now.AddDays(-staleDays)))
            {
                points.Add("stale");
            }

            return record;
        }).OrderByDescending(r => r.Findings.Count).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

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
