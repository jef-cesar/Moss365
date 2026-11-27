using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Security;

/// <summary>An object in AD by its name and DN.</summary>
public sealed record AdObject(string Name, string Dn);

/// <summary>A user account with an SPN (kerberoasting): its encryption types (msDS-SupportedEncryptionTypes; null: not
/// set, so RC4 by default) and its SPNs.</summary>
public sealed record SpnAccount(string Name, string Dn, int? EncryptionTypes, IReadOnlyList<string> Spns)
{
    /// <summary>AES128 and AES256.</summary>
    public const int Aes = 0x18;

    /// <summary>DES (two kinds) and RC4.</summary>
    public const int Weak = 0x7;

    public bool AesOnly => EncryptionTypes is { } e && (e & Weak) == 0 && (e & Aes) != 0;

    /// <summary>The encryption types with only AES among the ciphers; other bits (such as FAST) stay as they are.</summary>
    public int AesValue => ((EncryptionTypes ?? 0) & ~Weak) | Aes;
}

/// <summary>An account with unconstrained Kerberos delegation (not a domain controller), with its SPNs: who uses it.</summary>
public sealed record DelegationAccount(string Name, string Dn, bool Computer, IReadOnlyList<string> Spns);

/// <summary>An administrator: a (nested) member of Domain Admins, Enterprise Admins, Schema Admins or Administrators.</summary>
public sealed record AdminAccount(string Name, string Dn, bool Enabled, bool ProtectedUsers, bool NotDelegated, IReadOnlyList<string> Spns, DateTimeOffset? PasswordSet, DateTimeOffset? LastLogon);

/// <summary>A direct member of a privileged group: a user, group or computer.</summary>
public sealed record GroupMember(string Name, string Dn, string Kind, bool Enabled);

/// <summary>A privileged group, found by its well-known RID (the names differ per language of the domain).</summary>
public sealed record PrivilegedGroup(string Rid, string Name, string Dn, IReadOnlyList<GroupMember> Members)
{
    /// <summary>Account, Server, Print and Backup Operators: empty in a domain managed with delegation.</summary>
    public bool Operators => Rid is "548" or "549" or "550" or "551";

    /// <summary>Schema Admins and Enterprise Admins: only filled while a change needs it.</summary>
    public bool Forest => Rid is "518" or "519";
}

/// <summary>
/// The advice for Active Directory that is carried out as a plan (R16, chapter 10): what each kind of fix does, the
/// plans themselves, and the check of the privileged groups. Low risk with undo is a plan right away; unconstrained
/// delegation and Protected Users are guided (U16): one account per plan, with what it affects first.
/// </summary>
public static class AdHardening
{
    // The kinds of fix on a line of a check (CheckRow.Fix); the account flags are the names of SecurityAdvice.AccountFlags
    public const string Protect = "protect";
    public const string RemoveMember = "remove-member";
    public const string AesOnly = "aes";
    public const string AdminCount = "admincount";
    public const string Quota = "quota";
    public const string RecycleBin = "recyclebin";

    /// <summary>A fix that opens a page instead of a plan (a library script), as "script:Name".</summary>
    public const string ScriptPrefix = "script:";

    /// <summary>The kinds of the action <see cref="ChangeAction.AdHardening"/>; the AD connection refuses any other.</summary>
    public static readonly IReadOnlyList<string> Kinds = new[] { "encryptionTypes", "adminCount", "machineAccountQuota", "recycleBin" };

    /// <summary>The fixes that are guided (U16): one account per plan, with the impact in the plan.</summary>
    public static bool Guided(string? fix) => fix is "TrustedForDelegation" or Protect;

    /// <summary>The check of the privileged groups: administrators outside Protected Users or not marked sensitive,
    /// disabled members, members of the operator groups, and Schema or Enterprise Admins that are not empty.</summary>
    public static SecurityCheck Groups(AdSecurityReading? r)
    {
        var title = Texts.T["security.check.ad-groups"];
        if (r is null || (r.Groups.Count == 0 && r.Admins.Count == 0))
        {
            return SecurityCheck.Unknown("ad-groepen", title, Texts.T["code.security-checks.not-checked-yet-ad"]);
        }

        var rows = new List<CheckRow>();
        foreach (var a in r.Admins.Where(a => a.Enabled && (!a.ProtectedUsers && r.ProtectedUsersDn is not null || !a.NotDelegated)))
        {
            var missing = new List<string>();
            if (!a.ProtectedUsers && r.ProtectedUsersDn is not null)
            {
                missing.Add(Texts.T["advice.ad-groups.not-protected"]);
            }

            if (!a.NotDelegated)
            {
                missing.Add(Texts.T["advice.ad-groups.not-sensitive"]);
            }

            // An administrator with SPNs runs a service: Protected Users would stop it; first a gMSA
            rows.Add(a.Spns.Count > 0
                ? new CheckRow(a.Name, Texts.T.Format("advice.ad-groups.service-admin", string.Join(", ", a.Spns.Take(3))), CheckStatus.Attention)
                : new CheckRow(a.Name, string.Join("; ", missing), CheckStatus.Attention)
                {
                    Fix = Protect,
                    FixData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["dn"] = a.Dn, ["pu"] = a.ProtectedUsers || r.ProtectedUsersDn is null ? string.Empty : r.ProtectedUsersDn,
                        ["gevoelig"] = a.NotDelegated ? "True" : "False",
                        ["wachtwoord"] = a.PasswordSet?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    },
                });
        }

        foreach (var g in r.Groups)
        {
            foreach (var m in g.Members)
            {
                var (status, detail) = !m.Enabled && m.Kind != "group"
                    ? (CheckStatus.Problem, Texts.T.Format("advice.ad-groups.disabled-member", g.Name))
                    : g.Operators ? (CheckStatus.Attention, Texts.T.Format("advice.ad-groups.operator", g.Name))
                    : g.Forest ? (CheckStatus.Attention, Texts.T.Format("advice.ad-groups.forest", g.Name))
                    : (CheckStatus.Ok, string.Empty);
                if (status == CheckStatus.Ok)
                {
                    continue;
                }

                rows.Add(new CheckRow($"{m.Name} ({g.Name})", detail, status)
                {
                    Fix = RemoveMember,
                    FixData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = g.Dn, ["groep"] = g.Name, ["lidDn"] = m.Dn, ["lid"] = m.Name },
                });
            }
        }

        var shown = rows.Count(x => x.Status != CheckStatus.Ok);
        return new SecurityCheck("ad-groepen", title, rows.Select(x => x.Status).DefaultIfEmpty(CheckStatus.Ok).Max(),
            shown == 0 ? Texts.T["advice.ad-groups.none"] : Texts.T.Format("code.security-checks.point-s-to-check", shown), rows);
    }

    /// <summary>The plan for one line with a fix of this class (null when the fix is not one of them).</summary>
    public static PlanOutcome? Plan(CheckRow row, InventoryData data)
    {
        var d = row.FixData ?? new Dictionary<string, string>();
        string P(string k) => d.TryGetValue(k, out var v) ? v : string.Empty;
        switch (row.Fix)
        {
            case Protect:
                return ProtectPlan(row, P("dn"), P("pu"), P("gevoelig") == "True", P("wachtwoord"));
            case RemoveMember:
                return PlanOutcome.Of(new[]
                {
                    new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = ChangeAction.AdRemoveGroupMember,
                        Target = P("lidDn"),
                        TargetName = P("lid"),
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = P("groepDn"), ["groep"] = P("groep") },
                        Description = Texts.T.Format("advice.ad-groups.remove", P("lid"), P("groep")),
                        Warning = Texts.T["advice.ad-groups.remove-warning"],
                    },
                });
            case AesOnly:
                return Hardening(row, P("dn"), "encryptionTypes", P("nieuw"), P("was"), Texts.T.Format("advice.aes.plan", row.Object), Texts.T["advice.aes.warning"]);
            case AdminCount:
                return Hardening(row, P("dn"), "adminCount", string.Empty, string.Empty, Texts.T.Format("advice.admincount.plan", row.Object), Texts.T["advice.admincount.warning"]);
            case Quota:
                return Hardening(row, P("dn"), "machineAccountQuota", "0", P("was"), Texts.T["advice.quota.plan"], Texts.T["advice.quota.warning"]);
            case RecycleBin:
                return Hardening(row, P("dn"), "recycleBin", string.Empty, string.Empty, Texts.T["advice.recyclebin.plan"], Texts.T["advice.recyclebin.warning"]);
            default:
                return null;
        }
    }

    // Protected Users and "sensitive and cannot be delegated" (guided): the plan says what changes for the account
    private static PlanOutcome ProtectPlan(CheckRow row, string dn, string protectedUsers, bool sensitive, string passwordSet)
    {
        var plan = new List<Change>();
        var impact = Texts.T["advice.protect.impact"]
                     + (passwordSet.Length > 0 ? " " + Texts.T.Format("advice.protect.password", passwordSet) : string.Empty);
        if (protectedUsers.Length > 0)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdAddGroupMember,
                Target = dn,
                TargetName = row.Object,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = protectedUsers, ["groep"] = "Protected Users" },
                Description = Texts.T.Format("advice.protect.add", row.Object),
                Warning = impact,
            });
        }

        if (!sensitive)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdSetAccountFlag,
                Target = dn,
                TargetName = row.Object,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["vlag"] = "NotDelegated", ["aan"] = "True" },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["aan"] = "False" },
                Description = Texts.T.Format("advice.protect.sensitive", row.Object),
                CurrentValue = Texts.T["device.disabled"],
                NewValue = Texts.T["device.enabled"],
                Warning = protectedUsers.Length > 0 ? null : impact,
            });
        }

        return PlanOutcome.Of(plan, plan.Count == 0 ? new[] { Texts.T["advice.protect.done"] } : Array.Empty<string>());
    }

    private static PlanOutcome Hardening(CheckRow row, string dn, string kind, string value, string before, string description, string warning)
    {
        var change = new Change
        {
            TargetSystem = TargetSystem.ActiveDirectory,
            ChangeAction = ChangeAction.AdHardening,
            Target = dn,
            TargetName = row.Object,
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wat"] = kind },
            Description = description,
            Warning = warning,
        };
        switch (kind)
        {
            case "adminCount":
                change.Parameters["beschermd"] = "False";
                return PlanOutcome.Of(new[] { WithPrevious(change, new() { ["beschermd"] = "True" }) });
            case "recycleBin":
                return PlanOutcome.Of(new[] { change });
            default:
                change.Parameters["waarde"] = value;
                return PlanOutcome.Of(new[] { WithPrevious(change, new() { ["waarde"] = before }, before.Length == 0 ? Texts.T["undo.empty"] : before, value) });
        }
    }

    private static Change WithPrevious(Change c, Dictionary<string, string> previous, string? current = null, string? next = null) => new()
    {
        Id = c.Id,
        TargetSystem = c.TargetSystem,
        ChangeAction = c.ChangeAction,
        Target = c.Target,
        TargetName = c.TargetName,
        Parameters = c.Parameters,
        Description = c.Description,
        Warning = c.Warning,
        Previous = new Dictionary<string, string>(previous, StringComparer.OrdinalIgnoreCase),
        CurrentValue = current,
        NewValue = next,
    };
}
