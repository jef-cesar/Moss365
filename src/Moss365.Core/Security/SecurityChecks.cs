using Moss365.Core.Localization;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

namespace Moss365.Core.Security;

/// <summary>Every check says OK, attention or problem, and "unknown" when the data is not enough (R11).</summary>
public enum CheckStatus
{
    Ok,
    Attention,
    Problem,
    Unknown,
}

public sealed record CheckRow(string Object, string Detail, CheckStatus Status, string? Href = null);

public sealed record SecurityCheck(string Key, string Title, CheckStatus Status, string Summary, IReadOnlyList<CheckRow> Rows)
{
    public static SecurityCheck Unknown(string key, string title, string why) => new(key, title, CheckStatus.Unknown, why, Array.Empty<CheckRow>());

    public string StatusText => Status switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Attention => Texts.T["security.status.attention"],
        CheckStatus.Problem => Texts.T["security.status.problem"],
        _ => Texts.T["word.unknown"],
    };
}

// ---------------------------------------------------------------------- what is read for the checks

public sealed record ForwardingRule(string Mailbox, string Kind, string Name, string Target);

public sealed record AppCredential(string App, string AppId, string Kind, DateTimeOffset End);

public sealed record AppPermission(string App, string AppId, string Permission, string Type, string ConsentType, string? Principal);

public sealed record AppsReading(IReadOnlyList<AppCredential> Credentials, IReadOnlyList<AppPermission> Permissions);

public sealed record AdSecurityReading(
    IReadOnlyList<string> SpnUsers,
    IReadOnlyList<string> UnconstrainedDelegation,
    IReadOnlyList<string> PasswordNotRequired,
    IReadOnlyList<string> ReversibleEncryption,
    DateTimeOffset? KrbtgtPasswordSet,
    IReadOnlyList<string> AdminCountOrphans,
    IReadOnlyList<(string Name, string OperatingSystem)> OldSystems);

public sealed record GpoInfo(string Id, string Name, bool UserDisabled, bool ComputerDisabled, int Version, IReadOnlyList<string> Links, DateTimeOffset? Changed);

public sealed record SecureScoreReading(IReadOnlyList<(DateTimeOffset Date, double Score, double Max)> History, IReadOnlyList<(string Title, double MaxScore, string Url)> Actions);

public sealed record RoleAssignmentInfo(string Role, string Principal, string Kind, DateTimeOffset? End);

public sealed record RiskyUser(string Upn, string Level, string State, DateTimeOffset? Updated);

/// <summary>
/// The security checks of R11 (0.34), from what was read: external forwarding, app secrets and permissions, alerts from
/// the snapshots, AD (light), Group Policy, Secure Score, admin roles and PIM, weak MFA and risky users.
/// </summary>
public static class SecurityChecks
{
    private static CheckStatus Worst(IEnumerable<CheckRow> rows)
        => rows.Select(r => r.Status).Where(s => s != CheckStatus.Ok).DefaultIfEmpty(CheckStatus.Ok).Max();

    /// <summary>
    /// A link that comes from read data (such as the actions of Secure Score) only as an absolute http or https address;
    /// anything else (javascript:, file:, a protocol of another app) gives no link, so that a click never starts it.
    /// </summary>
    public static string? WebLink(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? url : null;

    /// <summary>Forwarding to an address outside the own domains (mailboxes and inbox rules).</summary>
    public static SecurityCheck Forwarding(IReadOnlyList<ForwardingRule>? rules, IReadOnlyCollection<string> ownDomains)
    {
        var title = Texts.T["code.management-state-security-checks.forwarding-outside"];
        if (rules is null)
        {
            return SecurityCheck.Unknown("doorsturen", title, Texts.T["code.security-checks.not-checked-yet-exchange-online"]);
        }

        bool External(string target)
        {
            var t = target.Trim().ToLowerInvariant().Replace("smtp:", string.Empty, StringComparison.Ordinal);
            return t.Contains('@') && !ownDomains.Any(d => t.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase) || t.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }

        var rows = rules.Where(r => External(r.Target))
            .Select(r => new CheckRow(r.Mailbox, $"{(r.Kind == "regel" ? $"inboxregel \"{r.Name}\"" : Texts.T["code.security-checks.forwarding-on-the-mailbox"])} naar {r.Target}",
                r.Kind == "regel" ? CheckStatus.Problem : CheckStatus.Attention))
            .ToList();
        return new SecurityCheck("doorsturen", title, Worst(rows),
            rows.Count == 0 ? Texts.T.Format("code.security-checks.nobody-forwards-outside-forwarding-rules", rules.Count) : Texts.T.Format("code.security-checks.forwarding-s-outside-check-whether", rows.Count),
            rows);
    }

    /// <summary>Secrets and certificates of app registrations that expired or expire within 30 days.</summary>
    public static SecurityCheck AppCredentials(AppsReading? r, DateTimeOffset now)
    {
        var title = Texts.T["code.management-state-security-checks.secrets-and-certificates-of-apps"];
        if (r is null)
        {
            return SecurityCheck.Unknown("app-geheimen", title, Texts.T["code.security-checks.not-checked-yet-microsoft-graph"]);
        }

        var rows = r.Credentials.Where(c => c.End <= now.AddDays(30))
            .OrderBy(c => c.End)
            .Select(c => new CheckRow(c.App, $"{c.Kind} {(c.End < now ? "verlopen op" : "verloopt op")} {c.End.LocalDateTime:dd/MM/yyyy}",
                c.End < now ? CheckStatus.Problem : CheckStatus.Attention))
            .ToList();
        return new SecurityCheck("app-geheimen", title, Worst(rows),
            rows.Count == 0 ? Texts.T.Format("code.security-checks.no-secret-or-certificate-expires", r.Credentials.Count) : Texts.T.Format("code.security-checks.secret-s-or-certificate-s", rows.Count),
            rows);
    }

    /// <summary>Permissions that give an app a lot (application permissions on everything) and consents users gave themselves.</summary>
    public static readonly IReadOnlyList<string> BroadPermissions = new[]
    {
        "Directory.ReadWrite.All", "RoleManagement.ReadWrite.Directory", "AppRoleAssignment.ReadWrite.All", "Application.ReadWrite.All",
        "User.ReadWrite.All", "Group.ReadWrite.All", "Mail.ReadWrite", "Mail.Send", "Files.ReadWrite.All", "Sites.FullControl.All",
        "Sites.ReadWrite.All", "full_access_as_app", "MailboxSettings.ReadWrite", "Policy.ReadWrite.ConditionalAccess",
    };

    public static SecurityCheck AppPermissions(AppsReading? r)
    {
        var title = Texts.T["code.management-state-security-checks.broad-app-permissions-and-consents"];
        if (r is null)
        {
            return SecurityCheck.Unknown("app-rechten", title, Texts.T["code.security-checks.not-checked-yet-microsoft-graph"]);
        }

        var rows = new List<CheckRow>();
        foreach (var p in r.Permissions.Where(p => p.Type == "Application" && BroadPermissions.Contains(p.Permission, StringComparer.OrdinalIgnoreCase)))
        {
            rows.Add(new CheckRow(p.App, Texts.T.Format("code.security-checks.application-permission-without-a-user", p.Permission), CheckStatus.Attention));
        }

        foreach (var g in r.Permissions.Where(p => p.ConsentType == "Principal").GroupBy(p => p.App))
        {
            var broad = g.Any(p => BroadPermissions.Contains(p.Permission, StringComparer.OrdinalIgnoreCase));
            rows.Add(new CheckRow(g.Key, $"toestemming van {g.Select(p => p.Principal).Distinct().Count()} gebruiker(s): {string.Join(", ", g.Select(p => p.Permission).Distinct().Take(6))}",
                broad ? CheckStatus.Problem : CheckStatus.Attention));
        }

        return new SecurityCheck("app-rechten", title, Worst(rows),
            rows.Count == 0 ? Texts.T["code.security-checks.no-broad-application-permissions-and"] : Texts.T.Format("code.security-checks.app-s-to-check-broad", rows.Count),
            rows);
    }

    /// <summary>The light AD security check: what an attacker looks for first.</summary>
    public static SecurityCheck AdSecurity(AdSecurityReading? r, DateTimeOffset now)
    {
        var title = Texts.T["security.check.ad-security"];
        if (r is null)
        {
            return SecurityCheck.Unknown("ad-beveiliging", title, Texts.T["code.security-checks.not-checked-yet-ad"]);
        }

        var rows = new List<CheckRow>();
        rows.AddRange(r.SpnUsers.Select(u => new CheckRow(u, Texts.T["code.security-checks.user-account-with-an-spn"], CheckStatus.Attention)));
        rows.AddRange(r.UnconstrainedDelegation.Select(u => new CheckRow(u, Texts.T["code.security-checks.unconstrained-kerberos-delegation"], CheckStatus.Problem)));
        rows.AddRange(r.PasswordNotRequired.Select(u => new CheckRow(u, Texts.T["code.security-checks.password-not-required-is-on"], CheckStatus.Problem)));
        rows.AddRange(r.ReversibleEncryption.Select(u => new CheckRow(u, Texts.T["code.security-checks.password-with-reversible-encryption"], CheckStatus.Problem)));
        rows.AddRange(r.AdminCountOrphans.Select(u => new CheckRow(u, Texts.T["code.security-checks.admincount-1-but-not-any"], CheckStatus.Attention)));
        rows.AddRange(r.OldSystems.Select(c => new CheckRow(c.Name, Texts.T.Format("code.security-checks.outdated-operating-system", c.OperatingSystem), CheckStatus.Problem)));
        if (r.KrbtgtPasswordSet is { } k)
        {
            var days = (now - k).TotalDays;
            rows.Add(new CheckRow("krbtgt", Texts.T.Format("code.security-checks.password-days-old", Math.Round(days)) + (days > 180 ? (" " + Texts.T["code.security-checks.renew-every-180-days"]) : string.Empty), days > 180 ? CheckStatus.Attention : CheckStatus.Ok));
        }

        var shown = rows.Where(x => x.Status != CheckStatus.Ok).ToList();
        return new SecurityCheck("ad-beveiliging", title, Worst(rows),
            shown.Count == 0 ? Texts.T["code.security-checks.nothing-found-of-what-an"] : Texts.T.Format("code.security-checks.point-s-to-check", shown.Count), rows);
    }

    /// <summary>Group Policy: unlinked, empty and (partly) disabled policies.</summary>
    public static SecurityCheck Gpo(IReadOnlyList<GpoInfo>? gpos)
    {
        var title = Texts.T["security.check.group-policy-gpo"];
        if (gpos is null)
        {
            return SecurityCheck.Unknown("gpo", title, Texts.T["code.security-checks.not-read-yet-ad"]);
        }

        var rows = new List<CheckRow>();
        foreach (var g in gpos.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (g.Links.Count == 0)
            {
                rows.Add(new CheckRow(g.Name, Texts.T["code.security-checks.linked-nowhere"], CheckStatus.Attention));
            }

            if (g.Version == 0)
            {
                rows.Add(new CheckRow(g.Name, Texts.T["code.security-checks.empty-no-settings"], CheckStatus.Attention));
            }

            if (g.UserDisabled && g.ComputerDisabled)
            {
                rows.Add(new CheckRow(g.Name, Texts.T["code.security-checks.completely-disabled"], CheckStatus.Attention));
            }
        }

        return new SecurityCheck("gpo", title, Worst(rows),
            rows.Count == 0 ? Texts.T.Format("code.security-checks.policy-ies-all-linked-and", gpos.Count) : Texts.T.Format("code.security-checks.policy-ies-point-s-to", gpos.Count, rows.Count), rows);
    }

    public static SecurityCheck SecureScore(SecureScoreReading? r)
    {
        var title = Texts.T["security.check.microsoft-secure-score"];
        if (r is null || r.History.Count == 0)
        {
            return SecurityCheck.Unknown("secure-score", title, r is null ? Texts.T["code.security-checks.not-read-yet-microsoft-graph"] : Texts.T["security.noscore"]);
        }

        var last = r.History.OrderBy(h => h.Date).Last();
        var first = r.History.OrderBy(h => h.Date).First();
        var percent = last.Max > 0 ? last.Score / last.Max * 100 : 0;
        var rows = r.Actions.OrderByDescending(a => a.MaxScore).Take(10)
            .Select(a => new CheckRow(a.Title, Texts.T.Format("security.score.points", a.MaxScore), CheckStatus.Attention, WebLink(a.Url))).ToList();
        var trend = last.Score - first.Score;
        return new SecurityCheck("secure-score", title, percent >= 70 ? CheckStatus.Ok : percent >= 40 ? CheckStatus.Attention : CheckStatus.Problem,
            Texts.T.Format("security.score", last.Score, last.Max, percent, trend >= 0 ? "+" : string.Empty, trend, first.Date.LocalDateTime),
            rows);
    }

    /// <summary>Admin roles: who has what permanently (attention for the critical roles), who is eligible.</summary>
    public static SecurityCheck Roles(IReadOnlyList<RoleAssignmentInfo>? roles)
    {
        var title = Texts.T["code.management-state-security-checks.admin-roles-and-pim"];
        if (roles is null)
        {
            return SecurityCheck.Unknown("rollen", title, Texts.T["code.security-checks.not-read-yet-microsoft-graph-2"]);
        }

        var critical = CaTemplates.CriticalRoles.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = roles.OrderBy(r => r.Role).ThenBy(r => r.Principal)
            .Select(r => new CheckRow(r.Principal, $"{r.Role}: {r.Kind}" + (r.End is { } e ? Texts.T.Format("security.roles.until", e.LocalDateTime) : string.Empty),
                r.Kind == "permanent" && critical.Contains(r.Role) ? CheckStatus.Attention : CheckStatus.Ok))
            .ToList();
        var permanent = rows.Count(r => r.Status == CheckStatus.Attention);
        var globals = roles.Count(r => r.Kind == "permanent" && r.Role == "Global Administrator");
        var status = globals > 5 ? CheckStatus.Problem : permanent > 0 ? CheckStatus.Attention : CheckStatus.Ok;
        return new SecurityCheck("rollen", title, status,
            Texts.T.Format("security.roles", roles.Count, permanent, roles.Count(r => r.Kind == "in aanmerking"), globals > 5 ? Texts.T.Format("security.roles.globals", globals) : string.Empty),
            rows);
    }

    /// <summary>Who only has weak MFA methods (SMS, phone call, e-mail) or none at all.</summary>
    public static SecurityCheck WeakMfa(InventoryData d)
    {
        var title = Texts.T["security.check.weak-mfa-methods"];
        if (d.Security is not { } s || s.Mfa.Count == 0)
        {
            return SecurityCheck.Unknown("zwakke-mfa", title, Texts.T["security.nomfadata"]);
        }

        var weak = new[] { "mobilePhone", "alternateMobilePhone", "officePhone", "email", "sms", "voice" };
        var rows = new List<CheckRow>();
        foreach (var m in s.Mfa)
        {
            var methods = m.Methods.Where(x => !x.Equals("password", StringComparison.OrdinalIgnoreCase)).ToList();
            if (methods.Count == 0)
            {
                rows.Add(new CheckRow(m.Upn, Texts.T["code.security-checks.no-mfa-method"], m.IsAdministrator ? CheckStatus.Problem : CheckStatus.Attention));
            }
            else if (methods.All(x => weak.Any(w => x.Contains(w, StringComparison.OrdinalIgnoreCase))))
            {
                rows.Add(new CheckRow(m.Upn, Texts.T.Format("code.security-checks.only", string.Join(", ", methods)), m.IsAdministrator ? CheckStatus.Problem : CheckStatus.Attention));
            }
        }

        return new SecurityCheck("zwakke-mfa", title, Worst(rows),
            rows.Count == 0 ? Texts.T["code.security-checks.everyone-has-a-stronger-method"] : Texts.T.Format("code.security-checks.account-s-without-a-strong", rows.Count), rows);
    }

    public static SecurityCheck Risky(IReadOnlyList<RiskyUser>? users)
    {
        var title = Texts.T["security.check.risky-users"];
        if (users is null)
        {
            return SecurityCheck.Unknown("risico", title, Texts.T["code.security-checks.not-read-needs-entra-id"]);
        }

        var open = users.Where(u => u.State is "atRisk" or "confirmedCompromised").ToList();
        var rows = open.Select(u => new CheckRow(u.Upn, Texts.T.Format("code.security-checks.risk", u.Level, u.State) + (u.Updated is { } t ? $" ({t.LocalDateTime:dd/MM/yyyy})" : string.Empty),
            u.State == "confirmedCompromised" || u.Level == "high" ? CheckStatus.Problem : CheckStatus.Attention)).ToList();
        return new SecurityCheck("risico", title, Worst(rows),
            rows.Count == 0 ? Texts.T["code.security-checks.no-users-with-an-open"] : Texts.T.Format("code.security-checks.user-s-with-an-open", rows.Count), rows);
    }

    /// <summary>
    /// Alerts from the snapshots: what changed between then and now, also outside Moss365. A new member of a
    /// privileged group or an admin role, a disabled account enabled again, a changed Conditional Access policy.
    /// </summary>
    public static SecurityCheck Alerts(InventoryData? before, InventoryData now, DateTimeOffset? since)
    {
        var title = Texts.T["code.management-state-security-checks.alerts-from-the-snapshots"];
        if (before is null)
        {
            return SecurityCheck.Unknown("meldingen", title, Texts.T["code.security-checks.there-is-no-earlier-snapshot"]);
        }

        var rows = new List<CheckRow>();
        var then = before.Index;
        foreach (var g in now.AdGroupsByDn.Values.Where(MirrorUser.IsPrivileged))
        {
            var old = before.AdGroupsByDn.GetValueOrDefault(g.DistinguishedName);
            var oldMembers = MembersOf(before, old);
            foreach (var m in MembersOf(now, g).Where(m => !oldMembers.Contains(m)))
            {
                rows.Add(new CheckRow(g.Name, Texts.T.Format("code.security-checks.new-member", Name(now, m)), CheckStatus.Problem));
            }
        }

        if (now.Security is { } s && before.Security is { } bs)
        {
            var oldRoles = bs.Roles.Select(r => (r.Role, r.MemberId)).ToHashSet();
            foreach (var r in s.Roles.Where(r => !oldRoles.Contains((r.Role, r.MemberId))))
            {
                rows.Add(new CheckRow(r.Role, Texts.T.Format("code.security-checks.new", r.MemberName), CheckStatus.Problem));
            }
        }

        // Looked up by name and id instead of searching the whole earlier list per account (linear, not quadratic);
        // the first account with a name wins, as before
        var adBefore = new Dictionary<string, AdUser>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in before.AdUsers)
        {
            adBefore.TryAdd(b.SamAccountName ?? string.Empty, b);
        }

        foreach (var a in now.AdUsers.Where(a => a.Enabled))
        {
            if (adBefore.GetValueOrDefault(a.SamAccountName ?? string.Empty) is { Enabled: false })
            {
                rows.Add(new CheckRow(a.SamAccountName, Texts.T["code.security-checks.disabled-account-enabled-again-ad"], CheckStatus.Attention));
            }
        }

        var entraBefore = new Dictionary<string, EntraUser>(StringComparer.Ordinal);
        foreach (var b in before.EntraUsers)
        {
            entraBefore.TryAdd(b.Id ?? string.Empty, b);
        }

        foreach (var e in now.EntraUsers.Where(e => e.AccountEnabled && !e.OnPremisesSyncEnabled))
        {
            if (entraBefore.GetValueOrDefault(e.Id ?? string.Empty) is { AccountEnabled: false })
            {
                rows.Add(new CheckRow(e.UserPrincipalName, Texts.T["code.security-checks.disabled-account-enabled-again-entra"], CheckStatus.Attention));
            }
        }

        if (now.ConditionalAccess is { } ca && before.ConditionalAccess is { } bca)
        {
            foreach (var v in CaComparison.Compare(bca.Policies, ca.Policies, CaReader.Names(now), ca))
            {
                rows.Add(new CheckRow(v.Name, $"Conditional Access {v.Kind}: {string.Join("; ", v.Details.Take(3))}", CheckStatus.Attention));
            }
        }

        return new SecurityCheck("meldingen", title, Worst(rows),
            rows.Count == 0 ? Texts.T.Format("security.alerts.none", since?.LocalDateTime) : Texts.T.Format("security.alerts.some", rows.Count, since?.LocalDateTime),
            rows);
    }

    private static HashSet<string> MembersOf(InventoryData d, AdGroup? g)
    {
        if (g is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (g.MembersKnown)
        {
            return g.Members.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return d.AdUsers.Where(u => u.MemberOr.Contains(g.DistinguishedName, StringComparer.OrdinalIgnoreCase)).Select(u => u.DistinguishedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string Name(InventoryData d, string dn)
        => d.AdUsers.FirstOrDefault(u => string.Equals(u.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase))?.DisplayName is { Length: > 0 } n ? n : DnHelpName(dn);

    private static string DnHelpName(string dn) => dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? dn[3..].Split(',')[0] : dn;
}
