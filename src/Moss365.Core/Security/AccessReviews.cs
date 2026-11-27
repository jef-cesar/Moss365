using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Security;

public enum ReviewKind
{
    AdGroup,
    AdminRole,
    Mailbox,

    /// <summary>An Entra group that holds an admin role (role-assignable): its members have the role.</summary>
    EntraGroup,

    /// <summary>The permissions of an app (from the last check of app permissions).</summary>
    AppPermission,
}

/// <summary>The access reviews in a period, for the period report and the standards (R20): how many subjects were
/// reviewed, how many members kept and to be removed, and how many subjects are due now.</summary>
public sealed record ReviewSummary(int Reviewed, int Kept, int Removed, int Due, int Subjects)
{
    /// <summary>The result for the standards: unknown without subjects, attention while one is due.</summary>
    public CheckStatus Status => Subjects == 0 ? CheckStatus.Unknown : Due > 0 ? CheckStatus.Attention : CheckStatus.Ok;
}

/// <summary>One member of what is reviewed: its key (the DN in AD, the id in Entra, "permission|trustee" for a
/// mailbox), its name and a detail (the UPN, the permission).</summary>
public sealed record ReviewMember(string Key, string Name, string Detail);

/// <summary>What is reviewed (R20): a privileged AD group, an admin role in Entra or a shared mailbox, with who has
/// access, and who confirms it when nobody is chosen (the manager of the group).</summary>
public sealed record ReviewSubject(ReviewKind Kind, string Key, string Name, IReadOnlyList<ReviewMember> Members, string DefaultReviewer = "")
{
    public string Id => AccessReviews.SubjectId(Kind, Key);
}

/// <summary>The reviewer, the interval and the last review of one subject (document <see cref="AccessReviews.Document"/>).</summary>
public sealed record ReviewSetting(string Subject, string Reviewer = "", int Months = AccessReviews.DefaultMonths, DateTimeOffset? Reviewed = null, string ReviewedBy = "");

/// <summary>A subject with its setting and when its next review is due (null: never reviewed, so now).</summary>
public sealed record ReviewDue(ReviewSubject Subject, ReviewSetting Setting, DateTimeOffset? Due, bool Overdue);

/// <summary>What the reviewer decided about one member: keep it, or remove it.</summary>
public sealed record ReviewDecision(string MemberKey, bool Keep);

/// <summary>
/// Light access reviews (R20), like the renewal of guests: every quarter (adjustable per subject) the owner or a
/// reviewer you choose confirms who is in the privileged AD groups and the admin roles, and who has access to the
/// shared mailboxes. Asking is a plan of mails, removing is a plan, and every decision is a line in the change log
/// (action <see cref="LogAction"/>): evidence for NIS2 and the CIS Controls without an Entra ID Governance licence. Admin
/// roles are removed in Entra itself: Moss365 deliberately has no permission to change roles.
/// </summary>
public static class AccessReviews
{
    public const string Document = "toegangsreviews";

    /// <summary>The action of the change log lines with the decisions.</summary>
    public const string LogAction = "Review";

    public const int DefaultMonths = 3;

    public static IReadOnlyList<int> Intervals { get; } = new[] { 1, 3, 6, 12 };

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }.WithLegacyNames();

    public static string SubjectId(ReviewKind kind, string key) => $"{kind}:{key}";

    public static IReadOnlyList<ReviewSetting> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ReviewSetting>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<ReviewSetting>>(json, Json) ?? new List<ReviewSetting>();
        }
        catch (JsonException)
        {
            return Array.Empty<ReviewSetting>();
        }
    }

    public static string Write(IEnumerable<ReviewSetting> settings) => JsonSerializer.Serialize(settings.OrderBy(s => s.Subject, StringComparer.Ordinal).ToList(), Json);

    /// <summary>The key of the pseudo-check for the standards (NIS2 21.2.i, CIS 6).</summary>
    public const string CheckKey = "toegangsreviews";

    /// <summary>
    /// Everything to review in the data: privileged AD groups and admin roles with members, Entra groups that hold an
    /// admin role, shared mailboxes with FullAccess or SendAs, and, from the last check of app permissions, the apps
    /// with broad permissions or consents.
    /// </summary>
    public static IReadOnlyList<ReviewSubject> Subjects(InventoryData d, IReadOnlyDictionary<string, SecurityCheck>? checks = null)
    {
        var output = new List<ReviewSubject>();
        var usersByDn = d.AdUsers.GroupBy(u => u.DistinguishedName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var g in d.AdGroupsByDn.Values.Where(MirrorUser.IsPrivileged))
        {
            var dns = g.MembersKnown ? g.Members : d.AdUsers.Where(u => u.MemberOr.Contains(g.DistinguishedName, StringComparer.OrdinalIgnoreCase)).Select(u => u.DistinguishedName).ToList();
            var members = dns.Distinct(StringComparer.OrdinalIgnoreCase).Select(dn => usersByDn.TryGetValue(dn, out var u)
                    ? new ReviewMember(dn, u.DisplayName.Length > 0 ? u.DisplayName : u.SamAccountName, u.UserPrincipalName)
                    : new ReviewMember(dn, d.AdGroupsByDn.TryGetValue(dn, out var nested) ? nested.Name : DnHelp.Name(dn), DnHelp.OuPathOfObject(dn)))
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (members.Count > 0)
            {
                var manager = g.ManagedBy.Length > 0 && usersByDn.TryGetValue(g.ManagedBy, out var m) ? First(m.EmailAddress, m.UserPrincipalName) : string.Empty;
                output.Add(new ReviewSubject(ReviewKind.AdGroup, g.DistinguishedName, g.Name, members, manager));
            }
        }

        var roles = d.Security?.Roles ?? Array.Empty<RoleMember>();
        var users = d.EntraUsers.GroupBy(u => u.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in roles.Where(r => string.Equals(r.MemberKind, "group", StringComparison.OrdinalIgnoreCase)).GroupBy(r => r.MemberId, StringComparer.OrdinalIgnoreCase))
        {
            var name = d.CloudGroups.TryGetValue(group.Key, out var cloud) ? cloud.DisplayName : group.First().MemberName;
            var members = d.CloudMembership.Where(kv => kv.Value.Contains(group.Key)).Select(kv => kv.Key)
                .Select(id => users.TryGetValue(id, out var u) ? new ReviewMember(id, u.DisplayName, u.UserPrincipalName) : new ReviewMember(id, id, string.Empty))
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            output.Add(new ReviewSubject(ReviewKind.EntraGroup, group.Key, $"{name} ({string.Join(", ", group.Select(r => r.Role).Distinct(StringComparer.OrdinalIgnoreCase))})", members));
        }

        if (checks?.GetValueOrDefault("app-rechten") is { } apps)
        {
            foreach (var app in apps.Rows.Where(r => r.Status is not CheckStatus.Ok).GroupBy(r => r.Object, StringComparer.OrdinalIgnoreCase))
            {
                output.Add(new ReviewSubject(ReviewKind.AppPermission, app.Key, app.Key,
                    app.Select(r => r.Detail).Distinct(StringComparer.Ordinal).Select(detail => new ReviewMember(detail, detail, string.Empty)).ToList()));
            }
        }

        foreach (var role in roles.Where(r => !string.Equals(r.MemberKind, "group", StringComparison.OrdinalIgnoreCase)).GroupBy(r => r.Role, StringComparer.OrdinalIgnoreCase))
        {
            output.Add(new ReviewSubject(ReviewKind.AdminRole, role.Key, role.Key,
                role.DistinctBy(r => r.MemberId, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new ReviewMember(r.MemberId, r.MemberName, First(r.MemberUpn, r.MemberKind)))
                    .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
        }

        foreach (var mailbox in d.MailboxPermissions.GroupBy(p => p.Mailbox, StringComparer.OrdinalIgnoreCase))
        {
            var name = d.Mailboxes.FirstOrDefault(m => string.Equals(m.Smtp, mailbox.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName;
            output.Add(new ReviewSubject(ReviewKind.Mailbox, mailbox.Key, string.IsNullOrEmpty(name) ? mailbox.Key : name,
                mailbox.DistinctBy(p => $"{p.Permission}|{p.Trustee}", StringComparer.OrdinalIgnoreCase)
                    .Select(p => new ReviewMember($"{p.Permission}|{p.Trustee}", p.Trustee, p.Permission))
                    .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
        }

        return output.OrderBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>When each subject is due: never reviewed is due now; overdue when the interval has passed.</summary>
    public static IReadOnlyList<ReviewDue> Due(IReadOnlyList<ReviewSubject> subjects, IReadOnlyList<ReviewSetting> settings, DateTimeOffset now)
    {
        var byId = settings.GroupBy(s => s.Subject, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        return subjects.Select(s =>
        {
            var setting = byId.GetValueOrDefault(s.Id) ?? new ReviewSetting(s.Id, s.DefaultReviewer);
            var due = setting.Reviewed?.AddMonths(Math.Max(1, setting.Months));
            return new ReviewDue(s, setting, due, due is null || due < now);
        }).ToList();
    }

    /// <summary>A setting changed (reviewer, interval) or added.</summary>
    public static IReadOnlyList<ReviewSetting> With(IReadOnlyList<ReviewSetting> settings, ReviewSetting changed)
        => settings.Where(s => !string.Equals(s.Subject, changed.Subject, StringComparison.OrdinalIgnoreCase))
            .Append(changed with { Reviewer = changed.Reviewer.Trim(), Months = Intervals.Contains(changed.Months) ? changed.Months : DefaultMonths }).ToList();

    /// <summary>
    /// The change log lines of a finished review, one per member: kept, or to be removed (that is a separate plan).
    /// Every member needs a decision.
    /// </summary>
    public static IReadOnlyList<LogEntry> LogLines(ReviewSubject s, IReadOnlyCollection<ReviewDecision> decisions, string user, string reviewer, DateTimeOffset now)
    {
        var byKey = decisions.GroupBy(x => x.MemberKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Keep, StringComparer.OrdinalIgnoreCase);
        if (s.Members.Any(m => !byKey.ContainsKey(m.Key)))
        {
            throw new InvalidOperationException(Texts.T["access-reviews.decide-all"]);
        }

        return s.Members.Select(m => new LogEntry(now, user, Texts.T.Format("access-reviews.context", s.Name), "Moss365", LogAction, s.Id, s.Name,
            Texts.T.Format(byKey[m.Key] ? "access-reviews.kept" : "access-reviews.to-remove", m.Name, s.Name),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["soort"] = s.Kind.ToString(),
                ["lid"] = m.Key,
                ["naam"] = m.Name,
                ["besluit"] = byKey[m.Key] ? "keep" : "remove",
                ["beoordelaar"] = reviewer,
            },
            "Succeeded", string.Empty)).ToList();
    }

    /// <summary>
    /// The plan that removes what was not kept: members of an AD group and permissions on a mailbox. Members of an
    /// admin role are an error with the way to do it in Entra.
    /// </summary>
    public static PlanOutcome Removals(ReviewSubject s, IEnumerable<ReviewDecision> decisions)
    {
        var remove = decisions.Where(x => !x.Keep).Select(x => x.MemberKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var m in s.Members.Where(m => remove.Contains(m.Key)))
        {
            switch (s.Kind)
            {
                case ReviewKind.AdGroup:
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = ChangeAction.AdRemoveGroupMember,
                        Target = m.Key,
                        TargetName = m.Name,
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = s.Key, ["groep"] = s.Name },
                        Description = Texts.T.Format("code.plan-builder.remove-from-ad-group", s.Name) + " " + Texts.T["code.plan-builder.privileged"],
                    });
                    break;
                case ReviewKind.Mailbox:
                    var parts = m.Key.Split('|', 2);
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeRemoveMailboxPermission,
                        Target = s.Key,
                        TargetName = s.Name,
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["Recht"] = parts[0], ["Trustee"] = parts.Length > 1 ? parts[1] : m.Name },
                        Description = Texts.T.Format("access-reviews.remove-permission", m.Detail, m.Name),
                    });
                    break;
                case ReviewKind.EntraGroup:
                    errors.Add(Texts.T.Format("access-reviews.group-in-entra", m.Name, s.Name));
                    break;
                case ReviewKind.AppPermission:
                    errors.Add(Texts.T.Format("access-reviews.app-in-entra", m.Name, s.Name));
                    break;
                default:
                    errors.Add(Texts.T.Format("access-reviews.role-in-entra", m.Name, s.Name));
                    break;
            }
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>The default question in the language of the interface; {lijst} stays (the list per reviewer).</summary>
    public static string DefaultQuestion => Texts.T.Format("access-reviews.mail.text", "{lijst}");

    /// <summary>One mail per reviewer with what to review and who has access; a subject without a reviewer is an
    /// error.</summary>
    public static PlanOutcome AskReviewers(IEnumerable<ReviewDue> due, string template)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var group in due.GroupBy(r => r.Setting.Reviewer.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0 || !group.Key.Contains('@'))
            {
                errors.AddRange(group.Select(r => Texts.T.Format("access-reviews.no-reviewer", r.Subject.Name)));
                continue;
            }

            var list = string.Join("\n\n", group.Select(r => r.Subject.Name + ":\n" + string.Join("\n", r.Subject.Members.Select(m => $"- {m.Name} ({m.Detail})"))));
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphSendMail,
                Target = group.Key,
                TargetName = group.Key,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["onderwerp"] = Texts.T["access-reviews.mail.subject"], ["tekst"] = template.Replace("{lijst}", list, StringComparison.OrdinalIgnoreCase) },
                Description = Texts.T.Format("access-reviews.ask", group.Count()),
                NewValue = group.Key,
            });
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>The reviews of a period: the decisions of the change log in it, and what is due now.</summary>
    public static ReviewSummary Summary(IEnumerable<LogEntry> changeLog, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<ReviewDue> due)
    {
        var lines = changeLog.Where(r => r.ChangeAction == LogAction && r.Time >= from && r.Time <= to).ToList();
        return new ReviewSummary(lines.Select(r => r.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            lines.Count(r => r.Parameters.GetValueOrDefault("besluit") == "keep"), lines.Count(r => r.Parameters.GetValueOrDefault("besluit") == "remove"),
            due.Count(r => r.Overdue), due.Count);
    }

    /// <summary>The reviews as a check, for the standards: attention while a review is due.</summary>
    public static SecurityCheck AsCheck(ReviewSummary summary)
        => new(CheckKey, Texts.T["menu.toegangsreviews"], summary.Status, Texts.T.Format("access-reviews.standard", summary.Due, summary.Subjects), Array.Empty<CheckRow>());

    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
