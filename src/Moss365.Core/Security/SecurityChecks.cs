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
        CheckStatus.Attention => "aandacht",
        CheckStatus.Problem => "probleem",
        _ => "onbekend",
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

    /// <summary>Forwarding to an address outside the own domains (mailboxes and inbox rules).</summary>
    public static SecurityCheck Forwarding(IReadOnlyList<ForwardingRule>? rules, IReadOnlyCollection<string> ownDomains)
    {
        const string title = "Doorsturen naar buiten";
        if (rules is null)
        {
            return SecurityCheck.Unknown("doorsturen", title, "Nog niet nagekeken (Exchange Online).");
        }

        bool External(string target)
        {
            var t = target.Trim().ToLowerInvariant().Replace("smtp:", string.Empty, StringComparison.Ordinal);
            return t.Contains('@') && !ownDomains.Any(d => t.EndsWith("@" + d, StringComparison.OrdinalIgnoreCase) || t.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        }

        var rows = rules.Where(r => External(r.Target))
            .Select(r => new CheckRow(r.Mailbox, $"{(r.Kind == "regel" ? $"inboxregel \"{r.Name}\"" : "doorsturen op het postvak")} naar {r.Target}",
                r.Kind == "regel" ? CheckStatus.Problem : CheckStatus.Attention))
            .ToList();
        return new SecurityCheck("doorsturen", title, Worst(rows),
            rows.Count == 0 ? $"Niemand stuurt door naar buiten ({rules.Count} doorstuurregels, allemaal binnen de eigen domeinen)." : $"{rows.Count} doorsturing(en) naar buiten: kijk na of dat mag. Een onbekende inboxregel is een klassiek teken van een overgenomen account.",
            rows);
    }

    /// <summary>Secrets and certificates of app registrations that expired or expire within 30 days.</summary>
    public static SecurityCheck AppCredentials(AppsReading? r, DateTimeOffset now)
    {
        const string title = "Geheimen en certificaten van apps";
        if (r is null)
        {
            return SecurityCheck.Unknown("app-geheimen", title, "Nog niet nagekeken (Microsoft Graph).");
        }

        var rows = r.Credentials.Where(c => c.End <= now.AddDays(30))
            .OrderBy(c => c.End)
            .Select(c => new CheckRow(c.App, $"{c.Kind} {(c.End < now ? "verlopen op" : "verloopt op")} {c.End.LocalDateTime:dd/MM/yyyy}",
                c.End < now ? CheckStatus.Problem : CheckStatus.Attention))
            .ToList();
        return new SecurityCheck("app-geheimen", title, Worst(rows),
            rows.Count == 0 ? $"Geen geheim of certificaat verloopt binnen 30 dagen ({r.Credentials.Count} in totaal)." : $"{rows.Count} geheim(en) of certificaat/certificaten verlopen (bijna): een verlopen geheim breekt een koppeling.",
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
        const string title = "Brede app-rechten en toestemmingen van gebruikers";
        if (r is null)
        {
            return SecurityCheck.Unknown("app-rechten", title, "Nog niet nagekeken (Microsoft Graph).");
        }

        var rows = new List<CheckRow>();
        foreach (var p in r.Permissions.Where(p => p.Type == "Application" && BroadPermissions.Contains(p.Permission, StringComparer.OrdinalIgnoreCase)))
        {
            rows.Add(new CheckRow(p.App, $"toepassingsrecht {p.Permission} (zonder gebruiker, op alles)", CheckStatus.Attention));
        }

        foreach (var g in r.Permissions.Where(p => p.ConsentType == "Principal").GroupBy(p => p.App))
        {
            var broad = g.Any(p => BroadPermissions.Contains(p.Permission, StringComparer.OrdinalIgnoreCase));
            rows.Add(new CheckRow(g.Key, $"toestemming van {g.Select(p => p.Principal).Distinct().Count()} gebruiker(s): {string.Join(", ", g.Select(p => p.Permission).Distinct().Take(6))}",
                broad ? CheckStatus.Problem : CheckStatus.Attention));
        }

        return new SecurityCheck("app-rechten", title, Worst(rows),
            rows.Count == 0 ? "Geen brede toepassingsrechten en geen toestemmingen van gebruikers." : $"{rows.Count} app(s) om na te kijken: brede rechten zijn een risico als de app gelekt wordt.",
            rows);
    }

    /// <summary>The light AD security check: what an attacker looks for first.</summary>
    public static SecurityCheck AdSecurity(AdSecurityReading? r, DateTimeOffset now)
    {
        const string title = "AD-beveiliging";
        if (r is null)
        {
            return SecurityCheck.Unknown("ad-beveiliging", title, "Nog niet nagekeken (AD).");
        }

        var rows = new List<CheckRow>();
        rows.AddRange(r.SpnUsers.Select(u => new CheckRow(u, "gebruikersaccount met een SPN (kerberoasting): een lang, willekeurig wachtwoord of een gMSA", CheckStatus.Attention)));
        rows.AddRange(r.UnconstrainedDelegation.Select(u => new CheckRow(u, "onbeperkte Kerberos-delegatie", CheckStatus.Problem)));
        rows.AddRange(r.PasswordNotRequired.Select(u => new CheckRow(u, "\"wachtwoord niet vereist\" staat aan", CheckStatus.Problem)));
        rows.AddRange(r.ReversibleEncryption.Select(u => new CheckRow(u, "wachtwoord met omkeerbare versleuteling", CheckStatus.Problem)));
        rows.AddRange(r.AdminCountOrphans.Select(u => new CheckRow(u, "adminCount=1 maar niet (meer) in een bevoorrechte groep", CheckStatus.Attention)));
        rows.AddRange(r.OldSystems.Select(c => new CheckRow(c.Name, $"verouderd besturingssysteem: {c.OperatingSystem}", CheckStatus.Problem)));
        if (r.KrbtgtPasswordSet is { } k)
        {
            var days = (now - k).TotalDays;
            rows.Add(new CheckRow("krbtgt", $"wachtwoord {Math.Round(days)} dagen oud" + (days > 180 ? " (om de 180 dagen vernieuwen)" : string.Empty), days > 180 ? CheckStatus.Attention : CheckStatus.Ok));
        }

        var shown = rows.Where(x => x.Status != CheckStatus.Ok).ToList();
        return new SecurityCheck("ad-beveiliging", title, Worst(rows),
            shown.Count == 0 ? "Niets gevonden van wat een aanvaller eerst zoekt." : $"{shown.Count} punt(en) om na te kijken.", rows);
    }

    /// <summary>Group Policy: unlinked, empty and (partly) disabled policies.</summary>
    public static SecurityCheck Gpo(IReadOnlyList<GpoInfo>? gpos)
    {
        const string title = "Groepsbeleid (GPO)";
        if (gpos is null)
        {
            return SecurityCheck.Unknown("gpo", title, "Nog niet gelezen (AD).");
        }

        var rows = new List<CheckRow>();
        foreach (var g in gpos.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (g.Links.Count == 0)
            {
                rows.Add(new CheckRow(g.Name, "nergens gekoppeld", CheckStatus.Attention));
            }

            if (g.Version == 0)
            {
                rows.Add(new CheckRow(g.Name, "leeg (geen instellingen)", CheckStatus.Attention));
            }

            if (g.UserDisabled && g.ComputerDisabled)
            {
                rows.Add(new CheckRow(g.Name, "helemaal uitgeschakeld", CheckStatus.Attention));
            }
        }

        return new SecurityCheck("gpo", title, Worst(rows),
            rows.Count == 0 ? $"{gpos.Count} beleid(en), alle gekoppeld en met instellingen." : $"{gpos.Count} beleid(en); {rows.Count} punt(en) om op te ruimen.", rows);
    }

    public static SecurityCheck SecureScore(SecureScoreReading? r)
    {
        const string title = "Microsoft Secure Score";
        if (r is null || r.History.Count == 0)
        {
            return SecurityCheck.Unknown("secure-score", title, r is null ? "Nog niet gelezen (Microsoft Graph)." : "Geen score beschikbaar.");
        }

        var last = r.History.OrderBy(h => h.Date).Last();
        var first = r.History.OrderBy(h => h.Date).First();
        var percent = last.Max > 0 ? last.Score / last.Max * 100 : 0;
        var rows = r.Actions.OrderByDescending(a => a.MaxScore).Take(10)
            .Select(a => new CheckRow(a.Title, $"tot {a.MaxScore:0.#} punten", CheckStatus.Attention, a.Url)).ToList();
        var trend = last.Score - first.Score;
        return new SecurityCheck("secure-score", title, percent >= 70 ? CheckStatus.Ok : percent >= 40 ? CheckStatus.Attention : CheckStatus.Problem,
            $"{last.Score:0.#} van {last.Max:0.#} ({percent:0}%); {(trend >= 0 ? "+" : string.Empty)}{trend:0.#} sinds {first.Date.LocalDateTime:dd/MM/yyyy}. Hieronder de acties die het meest opleveren.",
            rows);
    }

    /// <summary>Admin roles: who has what permanently (attention for the critical roles), who is eligible.</summary>
    public static SecurityCheck Roles(IReadOnlyList<RoleAssignmentInfo>? roles)
    {
        const string title = "Beheerrollen en PIM";
        if (roles is null)
        {
            return SecurityCheck.Unknown("rollen", title, "Nog niet gelezen (Microsoft Graph; PIM vraagt Entra ID P2).");
        }

        var critical = CaTemplates.CriticalRoles.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = roles.OrderBy(r => r.Role).ThenBy(r => r.Principal)
            .Select(r => new CheckRow(r.Principal, $"{r.Role}: {r.Kind}" + (r.End is { } e ? $" tot {e.LocalDateTime:dd/MM/yyyy}" : string.Empty),
                r.Kind == "permanent" && critical.Contains(r.Role) ? CheckStatus.Attention : CheckStatus.Ok))
            .ToList();
        var permanent = rows.Count(r => r.Status == CheckStatus.Attention);
        var globals = roles.Count(r => r.Kind == "permanent" && r.Role == "Global Administrator");
        var status = globals > 5 ? CheckStatus.Problem : permanent > 0 ? CheckStatus.Attention : CheckStatus.Ok;
        return new SecurityCheck("rollen", title, status,
            $"{roles.Count} toewijzing(en); {permanent} permanent in een kritieke rol, {roles.Count(r => r.Kind == "in aanmerking")} in aanmerking (PIM)." + (globals > 5 ? $" {globals} permanente Global Administrators: hou het bij 2 tot 4." : string.Empty),
            rows);
    }

    /// <summary>Who only has weak MFA methods (SMS, phone call, e-mail) or none at all.</summary>
    public static SecurityCheck WeakMfa(InventoryData d)
    {
        const string title = "Zwakke MFA-methoden";
        if (d.Security is not { } s || s.Mfa.Count == 0)
        {
            return SecurityCheck.Unknown("zwakke-mfa", title, "Geen MFA-gegevens geladen (Beveiliging, live laden).");
        }

        var weak = new[] { "mobilePhone", "alternateMobilePhone", "officePhone", "email", "sms", "voice" };
        var rows = new List<CheckRow>();
        foreach (var m in s.Mfa)
        {
            var methods = m.Methods.Where(x => !x.Equals("password", StringComparison.OrdinalIgnoreCase)).ToList();
            if (methods.Count == 0)
            {
                rows.Add(new CheckRow(m.Upn, "geen MFA-methode", m.IsAdministrator ? CheckStatus.Problem : CheckStatus.Attention));
            }
            else if (methods.All(x => weak.Any(w => x.Contains(w, StringComparison.OrdinalIgnoreCase))))
            {
                rows.Add(new CheckRow(m.Upn, "enkel " + string.Join(", ", methods), m.IsAdministrator ? CheckStatus.Problem : CheckStatus.Attention));
            }
        }

        return new SecurityCheck("zwakke-mfa", title, Worst(rows),
            rows.Count == 0 ? "Iedereen heeft een sterkere methode dan sms of telefoon." : $"{rows.Count} account(s) zonder sterke methode: stap over naar Authenticator, passkeys of FIDO2.", rows);
    }

    public static SecurityCheck Risky(IReadOnlyList<RiskyUser>? users)
    {
        const string title = "Risicovolle gebruikers";
        if (users is null)
        {
            return SecurityCheck.Unknown("risico", title, "Niet gelezen: vraagt Entra ID P2 en het recht IdentityRiskyUser.Read.All.");
        }

        var open = users.Where(u => u.State is "atRisk" or "confirmedCompromised").ToList();
        var rows = open.Select(u => new CheckRow(u.Upn, $"risico {u.Level}, {u.State}" + (u.Updated is { } t ? $" ({t.LocalDateTime:dd/MM/yyyy})" : string.Empty),
            u.State == "confirmedCompromised" || u.Level == "high" ? CheckStatus.Problem : CheckStatus.Attention)).ToList();
        return new SecurityCheck("risico", title, Worst(rows),
            rows.Count == 0 ? "Geen gebruikers met een open risico." : $"{rows.Count} gebruiker(s) met een open risico: wachtwoord laten wijzigen en sessies intrekken.", rows);
    }

    /// <summary>
    /// Alerts from the snapshots: what changed between then and now, also outside Moss365. A new member of a
    /// privileged group or an admin role, a disabled account enabled again, a changed Conditional Access policy.
    /// </summary>
    public static SecurityCheck Alerts(InventoryData? before, InventoryData now, DateTimeOffset? since)
    {
        const string title = "Meldingen uit de momentopnames";
        if (before is null)
        {
            return SecurityCheck.Unknown("meldingen", title, "Er is nog geen eerdere momentopname om mee te vergelijken.");
        }

        var rows = new List<CheckRow>();
        var then = before.Index;
        foreach (var g in now.AdGroupsByDn.Values.Where(MirrorUser.IsPrivileged))
        {
            var old = before.AdGroupsByDn.GetValueOrDefault(g.DistinguishedName);
            var oldMembers = MembersOf(before, old);
            foreach (var m in MembersOf(now, g).Where(m => !oldMembers.Contains(m)))
            {
                rows.Add(new CheckRow(g.Name, $"nieuw lid: {Name(now, m)}", CheckStatus.Problem));
            }
        }

        if (now.Security is { } s && before.Security is { } bs)
        {
            var oldRoles = bs.Roles.Select(r => (r.Role, r.MemberId)).ToHashSet();
            foreach (var r in s.Roles.Where(r => !oldRoles.Contains((r.Role, r.MemberId))))
            {
                rows.Add(new CheckRow(r.Role, $"nieuw: {r.MemberName}", CheckStatus.Problem));
            }
        }

        foreach (var a in now.AdUsers.Where(a => a.Enabled))
        {
            if (before.AdUsers.FirstOrDefault(b => string.Equals(b.SamAccountName, a.SamAccountName, StringComparison.OrdinalIgnoreCase)) is { Enabled: false })
            {
                rows.Add(new CheckRow(a.SamAccountName, "uitgeschakeld account weer ingeschakeld (AD)", CheckStatus.Attention));
            }
        }

        foreach (var e in now.EntraUsers.Where(e => e.AccountEnabled && !e.OnPremisesSyncEnabled))
        {
            if (before.EntraUsers.FirstOrDefault(b => b.Id == e.Id) is { AccountEnabled: false })
            {
                rows.Add(new CheckRow(e.UserPrincipalName, "uitgeschakeld account weer ingeschakeld (Entra)", CheckStatus.Attention));
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
            rows.Count == 0 ? $"Niets opvallends veranderd sinds {since?.LocalDateTime:dd/MM/yyyy HH:mm}." : $"{rows.Count} wijziging(en) sinds {since?.LocalDateTime:dd/MM/yyyy HH:mm}, ook wat buiten Moss365 gebeurde.",
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
