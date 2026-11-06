using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Core.Permissions;

/// <summary>
/// The profiles of Moss365: app roles in the app registration, assigned via Entra groups. Roles add up: whoever has
/// Helpdesk and Reader may do everything of both.
/// </summary>
[Flags]
public enum Role
{
    No = 0,
    Reader = 1,
    Helpdesk = 2,
    Administrator = 4,
    Auditor = 8,
    AppAdmin = 16,

    /// <summary>While profiles are not required and the sign-in contains no role: everything, as before 0.8.</summary>
    Everything = Reader | Helpdesk | Administrator | Auditor | AppAdmin,
}

/// <summary>What someone may do in the app (the permission table from chapter 2 of the roadmap).</summary>
public enum Permission
{
    /// <summary>Search and view: users, groups, mailboxes, rooms, guests, compare, statistics.</summary>
    View,

    /// <summary>Compare with a mirror user (show only).</summary>
    CompareMirror,

    /// <summary>Export lists (goes to the access log).</summary>
    Export,

    /// <summary>Security: MFA, admin roles, privileged groups, Conditional Access.</summary>
    Security,

    /// <summary>Sign-ins (IP addresses, places); goes to the access log.</summary>
    SignIns,

    /// <summary>Change log: the own changes.</summary>
    ChangeLogOwn,

    /// <summary>Change log: all changes.</summary>
    ChangeLogAll,

    /// <summary>Reset the password, unlock, enable (normal accounts).</summary>
    Helpdesk,

    /// <summary>Add or remove a member in normal groups.</summary>
    GroupMembers,

    InviteGuests,

    /// <summary>Apply a mirror, new user, offboarding, scheduling, follow-up.</summary>
    Actions,

    /// <summary>Manage rooms, shared mailboxes, groups and users; cleanup, delete guests, undo.</summary>
    Manage,

    /// <summary>Privileged accounts and groups (Domain Admins, ...).</summary>
    Privileged,

    /// <summary>Scripts from the library and own scripts.</summary>
    Scripts,

    /// <summary>Organisation (choice lists and fixed rules).</summary>
    Settings,
}

public static class PermissionTable
{
    /// <summary>The app roles as they are in the app registration (value in the token, and the fixed id).</summary>
    public static IReadOnlyList<(string Value, Role Role, string Group, string Id)> AppRoles { get; } = new[]
    {
        ("Moss365.Reader", Role.Reader, "GG-Moss365-Readers", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8001"),
        ("Moss365.Helpdesk", Role.Helpdesk, "GG-Moss365-Helpdesk", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8002"),
        ("Moss365.Administrator", Role.Administrator, "GG-Moss365-Administrators", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8003"),
        ("Moss365.Auditor", Role.Auditor, "GG-Moss365-Auditors", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8004"),
        ("Moss365.AppAdmin", Role.AppAdmin, "GG-Moss365-AppAdmins", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8005"),
    };

    /// <summary>Role values of app registrations made before 0.11; still accepted in the token.</summary>
    public static IReadOnlyDictionary<string, Role> LegacyRoleValues { get; } = new Dictionary<string, Role>(StringComparer.OrdinalIgnoreCase)
    {
        ["Moss365.Lezer"] = Role.Reader,
        ["Moss365.Beheerder"] = Role.Administrator,
        ["Moss365.Appbeheer"] = Role.AppAdmin,
    };

    private const Role ReadAll = Role.Reader | Role.Helpdesk | Role.Administrator | Role.Auditor;

    private static readonly Dictionary<Permission, Role> Who = new()
    {
        [Permission.View] = ReadAll,
        [Permission.CompareMirror] = Role.Reader | Role.Helpdesk | Role.Administrator,
        [Permission.Export] = Role.Helpdesk | Role.Administrator | Role.Auditor,
        [Permission.Security] = Role.Administrator | Role.Auditor,
        [Permission.SignIns] = Role.Administrator | Role.Auditor,
        [Permission.ChangeLogOwn] = Role.Helpdesk | Role.Administrator | Role.Auditor,
        [Permission.ChangeLogAll] = Role.Administrator | Role.Auditor,
        [Permission.Helpdesk] = Role.Helpdesk | Role.Administrator,
        [Permission.GroupMembers] = Role.Helpdesk | Role.Administrator,
        [Permission.InviteGuests] = Role.Helpdesk | Role.Administrator,
        [Permission.Actions] = Role.Administrator,
        [Permission.Manage] = Role.Administrator,
        [Permission.Privileged] = Role.Administrator,
        [Permission.Scripts] = Role.Administrator,
        [Permission.Settings] = Role.AppAdmin,
    };

    public static bool Can(Role roles, Permission permission) => (roles & Who[permission]) != 0;

    /// <summary>Which roles give a permission (for messages: "needs the profile Administrator").</summary>
    public static Role RolesFor(Permission permission) => Who[permission];

    /// <summary>The roles from the "roles" claims of the sign-in; unknown values do not count.</summary>
    public static Role FromClaims(IEnumerable<string> claims)
    {
        var role = Role.No;
        foreach (var c in claims)
        {
            var found = AppRoles.FirstOrDefault(r => string.Equals(r.Value, c, StringComparison.OrdinalIgnoreCase));
            role |= found.Role | LegacyRoleValues.GetValueOrDefault(c);
        }

        return role;
    }

    /// <summary>
    /// The roles the app works with. If the sign-in contains roles, those. If not: while profiles are not required,
    /// everything (as before 0.8, with a message); when they are required, read only (also before signing in).
    /// </summary>
    public static Role Determine(IEnumerable<string>? claims, bool requiredValue)
    {
        var fromToken = claims is null ? Role.No : FromClaims(claims);
        if (fromToken != Role.No)
        {
            return fromToken;
        }

        return requiredValue ? Role.Reader : Role.Everything;
    }

    /// <summary>The minimum permission for an action (apart from privileged targets, see
    /// <see cref="Refusal"/>).</summary>
    public static Permission ForAction(ChangeAction action) => action switch
    {
        ChangeAction.AdSetPassword or ChangeAction.AdUnlockAccount or ChangeAction.AdEnableAccount => Permission.Helpdesk,
        ChangeAction.AdAddGroupMember or ChangeAction.AdRemoveGroupMember
            or ChangeAction.GraphAddGroupMember or ChangeAction.GraphRemoveGroupMember
            or ChangeAction.ExchangeAddGroupMember or ChangeAction.ExchangeRemoveGroupMember => Permission.GroupMembers,
        ChangeAction.GraphInviteGuest => Permission.InviteGuests,
        ChangeAction.GraphCreateApplication or ChangeAction.GraphUpdateApplication or ChangeAction.GraphCreateServicePrincipal
            or ChangeAction.GraphUpdateServicePrincipal or ChangeAction.GraphGrantConsent or ChangeAction.GraphAssignAppRole => Permission.Settings,
        _ => Permission.Manage,
    };

    /// <summary>
    /// Why these roles may not run this change, or null when it is allowed. A privileged account (member of a
    /// privileged AD group) or a privileged group always needs the profile Administrator.
    /// </summary>
    public static string? Refusal(Role roles, Change w, InventoryData? data = null)
    {
        // The groups of the profiles (GG-Moss365-...) belong to the set-up of the app, like its app registration
        var permission = w.ChangeAction == ChangeAction.GraphCreateGroup && AppRoles.Any(a => string.Equals(a.Group, w.P("displayName"), StringComparison.OrdinalIgnoreCase))
            ? Permission.Settings
            : ForAction(w.ChangeAction);
        if (!Can(roles, permission))
        {
            return $"Je profiel ({Text(roles, "en")}) laat dit niet toe; het vraagt {Text(RolesFor(permission))}.";
        }

        if (data is not null && permission != Permission.Manage && !Can(roles, Permission.Privileged) && TouchesPrivileged(w, data) is { } what)
        {
            return $"{what} is bevoorrecht; dat vraagt het profiel Beheerder.";
        }

        return null;
    }

    private static string? TouchesPrivileged(Change w, InventoryData data)
    {
        if (w.Parameters.TryGetValue("groepDn", out var groupDn) && data.AdGroupsByDn.TryGetValue(groupDn, out var groupValue)
            && MirrorUser.IsPrivileged(groupValue))
        {
            return $"De groep {groupValue.Name}";
        }

        if (w.TargetSystem == TargetSystem.ActiveDirectory
            && data.AdUsers.FirstOrDefault(u => string.Equals(u.DistinguishedName, w.Target, StringComparison.OrdinalIgnoreCase)) is { } user
            && (user.MemberOr.Any(dn => data.AdGroupsByDn.TryGetValue(dn, out var g) && MirrorUser.IsPrivileged(g))
                || data.AdGroupsByDn.Values.Any(g => MirrorUser.IsPrivileged(g)
                                                      && g.Members.Contains(user.DistinguishedName, StringComparer.OrdinalIgnoreCase))))
        {
            return $"Het account {user.SamAccountName}";
        }

        return null;
    }

    /// <summary>Readable names, e.g. "Helpdesk of Administrator" (or with "en" for the own roles).</summary>
    public static string Text(Role roles, string conjunction = "of")
    {
        if (roles == Role.No)
        {
            return "geen profiel";
        }

        var names = Enum.GetValues<Role>().Where(r => r is not Role.No and not Role.Everything && roles.HasFlag(r)).Select(r => r.ToString()).ToList();
        return names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + $" {conjunction} " + names[^1];
    }
}
