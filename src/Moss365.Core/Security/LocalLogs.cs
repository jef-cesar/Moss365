using Moss365.Core.Inventory;

namespace Moss365.Core.Security;

/// <summary>One line of the Entra directory audit log (auditLogs/directoryAudits).</summary>
public sealed record AuditEvent(
    string Id,
    DateTimeOffset Time,
    string Activity,
    string Category,
    string Service,
    string By,
    string TargetId,
    string TargetName,
    string Result);

/// <summary>A sign-in kept in the tenant database, with the reasons it was kept (<see cref="LocalLogs.Reasons"/>).</summary>
public sealed record KeptSignIn(SignInEvent SignIn, IReadOnlyList<string> Reasons);

/// <summary>What the last collection of the local logs did (document <see cref="LocalLogs.Document"/>).</summary>
public sealed record LocalLogsRun(DateTimeOffset Time, int SignIns, int Audits, string? Message);

/// <summary>
/// The sign-in and audit logs kept locally (R18, U19). Microsoft keeps them 7 to 30 days, depending on the licence; the
/// tenant database keeps failed, risky, administrator and emergency account sign-ins and the whole directory audit log
/// for <see cref="Storage.RetentionSettings.LocalLogDays"/> days. On by default, per tenant
/// (<see cref="Storage.RetentionSettings.KeepLocalLogs"/>).
/// </summary>
public static class LocalLogs
{
    public const string SignInKind = "signin";
    public const string AuditKind = "audit";

    /// <summary>The last collection, with its counts and a message when a part could not be read.</summary>
    public const string Document = "lokale-logboeken";

    /// <summary>Why a sign-in is kept: it failed, Entra saw a risk, an administrator signed in, an emergency account
    /// signed in.</summary>
    public static IReadOnlyList<string> Reasons { get; } = new[] { "failed", "risky", "admin", "emergency" };

    /// <summary>Microsoft keeps sign-ins for at most 30 days: never ask further back.</summary>
    public const int MaxDaysBack = 30;

    /// <summary>At most this many lines per kind in one collection (a first collection in a large tenant).</summary>
    public const int MaxPerRun = 20_000;

    /// <summary>Why this sign-in is kept; empty when it is not.</summary>
    public static IReadOnlyList<string> WhyKeep(SignInEvent e, IReadOnlySet<string> adminIds, IReadOnlySet<string> emergencyUpns)
    {
        var reasons = new List<string>();
        if (!e.Succeeded)
        {
            reasons.Add("failed");
        }

        if (e.Risky)
        {
            reasons.Add("risky");
        }

        if (e.UserId.Length > 0 && adminIds.Contains(e.UserId))
        {
            reasons.Add("admin");
        }

        if (e.Upn.Length > 0 && emergencyUpns.Contains(e.Upn))
        {
            reasons.Add("emergency");
        }

        return reasons;
    }

    /// <summary>The users with an admin role (from the last read of the security data).</summary>
    public static IReadOnlySet<string> Admins(SecurityData? security)
        => (security?.Roles ?? Array.Empty<RoleMember>()).Where(r => r.MemberId.Length > 0 && !string.Equals(r.MemberKind, "group", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.MemberId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Microsoft sometimes shows a line minutes to hours late: every collection asks this far back again (a
    /// line already kept is not kept twice).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(3);

    /// <summary>From when to ask: a little before the last line kept, but never more than <see cref="MaxDaysBack"/>
    /// days back.</summary>
    public static DateTimeOffset Since(DateTimeOffset? last, DateTimeOffset now)
        => last is { } l && l - Overlap > now.AddDays(-MaxDaysBack) ? l - Overlap : now.AddDays(-MaxDaysBack);

    /// <summary>The sign-ins to keep from a reading: every reason, once per sign-in (a sign-in without an id is
    /// skipped: it cannot be told apart from the next reading).</summary>
    public static IReadOnlyList<KeptSignIn> ToKeep(IEnumerable<SignInEvent> read, IReadOnlySet<string> adminIds, IReadOnlySet<string> emergencyUpns)
        => read.Where(e => e.Id.Length > 0).DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(e => new KeptSignIn(e, WhyKeep(e, adminIds, emergencyUpns)))
            .Where(k => k.Reasons.Count > 0)
            .ToList();

    /// <summary>Filters for the screen: a reason (null = all), text in the user, app, place or IP address.</summary>
    public static IEnumerable<KeptSignIn> Filter(IEnumerable<KeptSignIn> kept, string? reason, string? text)
        => kept.Where(k => (reason is null || k.Reasons.Contains(reason))
                           && (string.IsNullOrWhiteSpace(text) || new[] { k.SignIn.Upn, k.SignIn.Name, k.SignIn.App, k.SignIn.Location, k.SignIn.Ip }
                               .Any(v => v.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase))));

    public static IEnumerable<AuditEvent> Filter(IEnumerable<AuditEvent> audits, string? text)
        => string.IsNullOrWhiteSpace(text)
            ? audits
            : audits.Where(a => new[] { a.Activity, a.Category, a.Service, a.By, a.TargetName }.Any(v => v.Contains(text.Trim(), StringComparison.CurrentCultureIgnoreCase)));
}
