using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Security;

/// <summary>One change made outside Moss365 since the last visit (R18): its kind (<see cref="SinceLastVisit.Kinds"/>),
/// when (null when only the snapshots know it), what and who.</summary>
public sealed record VisitChange(string Kind, DateTimeOffset? Time, string What, string Who);

/// <summary>
/// What changed outside Moss365 since you last opened the tenant (R18): new administrators (privileged AD groups from
/// the snapshots, admin roles from the audit log kept locally), changed Conditional Access, new app consents and
/// sign-ins of emergency accounts. What Moss365 did itself (the change log) is left out. The alerts of a server, on the
/// Desktop, without a server.
/// </summary>
public static class SinceLastVisit
{
    public static IReadOnlyList<string> Kinds { get; } = new[] { "admin", "policy", "consent", "emergency" };

    /// <summary>An audit line and a change of Moss365 on the same object this close in time are the same change.</summary>
    public static readonly TimeSpan SameChange = TimeSpan.FromMinutes(15);

    /// <summary>The kind of an audit line that matters here, or null.</summary>
    public static string? KindOf(AuditEvent a)
    {
        if (!string.Equals(a.Result, "success", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(a.Category, "RoleManagement", StringComparison.OrdinalIgnoreCase)
            && a.Activity.StartsWith("Add", StringComparison.OrdinalIgnoreCase) && a.Activity.Contains("member", StringComparison.OrdinalIgnoreCase))
        {
            return "admin";
        }

        if (string.Equals(a.Service, "Conditional Access", StringComparison.OrdinalIgnoreCase)
            || a.Activity.Contains("conditional access", StringComparison.OrdinalIgnoreCase) || a.Activity.Contains("named location", StringComparison.OrdinalIgnoreCase))
        {
            return "policy";
        }

        return a.Activity.StartsWith("Consent to application", StringComparison.OrdinalIgnoreCase)
               || a.Activity.StartsWith("Add delegated permission grant", StringComparison.OrdinalIgnoreCase)
               || a.Activity.StartsWith("Add app role assignment to service principal", StringComparison.OrdinalIgnoreCase)
            ? "consent"
            : null;
    }

    /// <param name="since">The last visit.</param>
    /// <param name="before">The organisation at the last visit (the snapshot), or null.</param>
    /// <param name="now">The organisation now.</param>
    /// <param name="audits">The audit log kept locally since the last visit.</param>
    /// <param name="signIns">The sign-ins kept locally since the last visit.</param>
    /// <param name="ownChanges">The change log since the last visit: what Moss365 did.</param>
    public static IReadOnlyList<VisitChange> Compute(DateTimeOffset since, InventoryData? before, InventoryData now, IEnumerable<AuditEvent> audits,
        IEnumerable<KeptSignIn> signIns, IEnumerable<LogEntry> ownChanges)
    {
        var own = ownChanges.Where(r => r.Time >= since - SameChange && r.Succeeded).ToList();
        var output = new List<VisitChange>();

        foreach (var a in audits.Where(a => a.Time >= since))
        {
            if (KindOf(a) is { } kind && !own.Any(r => SameObject(r, a) && (r.Time - a.Time).Duration() <= SameChange))
            {
                output.Add(new(kind, a.Time, Texts.T.Format("visit.audit", a.Activity, a.TargetName), a.By));
            }
        }

        // AD: who is in a privileged group now and was not at the last visit (only when that snapshot had AD)
        if (before is { AdUsers.Count: > 0 })
        {
            var then = before.AdUsers.Where(u => u.SamAccountName.Length > 0).GroupBy(u => u.SamAccountName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().MemberOr, StringComparer.OrdinalIgnoreCase);
            foreach (var user in now.AdUsers)
            {
                var previous = then.GetValueOrDefault(user.SamAccountName) ?? Array.Empty<string>();
                foreach (var dn in user.MemberOr.Except(previous, StringComparer.OrdinalIgnoreCase))
                {
                    // A group that was not read itself still counts by its name, as on the user page
                    var group = now.AdGroupsByDn.TryGetValue(dn, out var g) ? g : new AdGroup { Name = DnHelp.Name(dn), DistinguishedName = dn, OuPath = DnHelp.OuPathOfObject(dn) };
                    if (MirrorUser.IsPrivileged(group)
                        && !own.Any(r => r.ChangeAction == nameof(ChangeAction.AdAddGroupMember) && string.Equals(r.Parameters.GetValueOrDefault("groepDn"), dn, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(r.Target, user.DistinguishedName, StringComparison.OrdinalIgnoreCase)))
                    {
                        output.Add(new("admin", null, Texts.T.Format("visit.ad-group", user.DisplayName.Length > 0 ? user.DisplayName : user.SamAccountName, group.Name), string.Empty));
                    }
                }
            }
        }

        foreach (var k in signIns.Where(k => k.SignIn.Time >= since && k.Reasons.Contains("emergency")))
        {
            output.Add(new("emergency", k.SignIn.Time, Texts.T.Format("visit.emergency", k.SignIn.Upn, k.SignIn.App, k.SignIn.Location), k.SignIn.Upn));
        }

        return output.OrderByDescending(c => c.Time ?? DateTimeOffset.MaxValue).ToList();
    }

    private static bool SameObject(LogEntry r, AuditEvent a)
        => (a.TargetId.Length > 0 && string.Equals(r.Target, a.TargetId, StringComparison.OrdinalIgnoreCase))
           || (a.TargetName.Length > 0 && (string.Equals(r.TargetName, a.TargetName, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Target, a.TargetName, StringComparison.OrdinalIgnoreCase)));
}
