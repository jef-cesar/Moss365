using System.Globalization;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

/// <summary>One bar in a list (name, count, optionally a total to compare with).</summary>
public sealed record Bar(string Name, int Count, int? Total = null, string? Link = null)
{
    public double Part => Total is > 0 ? (double)Count / Total.Value : 0;
}

/// <summary>The indicators for the Statistics screen, computed from the loaded data.</summary>
public sealed record Statistic(
    int Accounts,
    int Active,
    int Disabled,
    int AdAndEntra,
    int OnlyAd,
    int OnlyEntra,
    int Guests,
    int Inactive90,
    int Inactive180,
    int NeverSignedIn,
    int PasswordExpiresNever,
    int PrivilegedMembers,
    IReadOnlyList<Bar> Licences,
    IReadOnlyList<Bar> PerDivision,
    int Groups,
    int EmptyGroups,
    int DynamicGroups,
    IReadOnlyList<Bar> GroupsPerKind,
    int SharedMailboxes,
    IReadOnlyList<Bar> PermissionModel,
    int DirectPermissions,
    int WithoutAccessGroup,
    int Rooms,
    int RoomsWithDeviation,
    IReadOnlyList<Bar> DifferencesAdEntra,
    SecurityStatistics? Security = null,
    int MailboxAccounts = 0)
{
    /// <summary>Name of the group of employees without a division in the bars per division.</summary>
    public const string NoDivision = "(geen afdeling)";

    /// <summary>Licences of which less than 5% (or at most 2) are free.</summary>
    public static bool AlmostFull(Bar licence) => licence.Total is > 0 && licence.Total.Value - licence.Count <= Math.Max(2, licence.Total.Value / 20);

    public static Statistic Compute(InventoryData d, OrganisationConfig config, DateTime now)
    {
        // Mailbox and room accounts are not employees: nobody signs in with them
        var staff = d.Persons.Where(p => !p.IsGuest && !d.Index.IsMailboxAccount(p)).ToList();
        var active = staff.Where(p => p.Enabled).ToList();
        var last = active.Select(LastSignIn).ToList();

        var index = d.Index;
        var shared = d.Mailboxes.Where(p => p.IsShared).ToList();
        var model = shared.Select(p => Kpis.PermissionModelOf(d, p)).ToList();

        var groups = index.Groups.ToList();
        var differences = d.HasAd && d.HasEntra ? AdEntraComparison.Compare(d, config) : Array.Empty<Difference>();

        return new Statistic(
            Accounts: staff.Count,
            Active: active.Count,
            Disabled: staff.Count - active.Count,
            AdAndEntra: staff.Count(p => p.Ad is not null && p.Entra is not null),
            OnlyAd: staff.Count(p => p.Ad is not null && p.Entra is null),
            OnlyEntra: staff.Count(p => p.Ad is null && p.Entra is not null),
            Guests: d.Persons.Count(p => p.IsGuest),
            Inactive90: last.Count(l => l is { } x && x < now.AddDays(-90)),
            Inactive180: last.Count(l => l is { } x && x < now.AddDays(-180)),
            NeverSignedIn: last.Count(l => l is null),
            PasswordExpiresNever: active.Count(p => p.Ad?.PasswordNeverExpires == true),
            PrivilegedMembers: active.Count(p => d.AdGroupsOf(p).Any(MirrorUser.IsPrivileged)),
            Licences: d.Licences.Where(l => l.Total > 0)
                .OrderByDescending(l => l.Total).Select(l => new Bar(l.SkuPartNumber, l.Assigned, l.Total, Kpis.Licence(l.SkuPartNumber).Link)).ToList(),
            PerDivision: active.GroupBy(p => config.DivisionFor(p.Department)?.Name ?? NoDivision)
                .Select(g => new Bar(Kpis.Display(g.Key), g.Count(), Link: Kpis.Division(g.Key).Link)).OrderByDescending(s => s.Count).ToList(),
            Groups: groups.Count,
            EmptyGroups: groups.Count(g => !g.Dynamic && (g.Cloud?.CountMembers ?? g.Exo?.CountMembers ?? g.Ad?.CountMembers) == 0),
            DynamicGroups: groups.Count(g => g.Dynamic),
            GroupsPerKind: groups.GroupBy(g => g.KindKey).Select(g => new Bar(Group.KindTextOf(g.Key), g.Count(), Link: Kpis.GroupKind(g.Key).Link)).OrderByDescending(s => s.Count).ToList(),
            SharedMailboxes: shared.Count,
            PermissionModel: Kpis.PermissionModels.Select(m => new Bar(Kpis.Display(m), model.Count(x => x == m), shared.Count, Kpis.PermissionModel(m).Link)).ToList(),
            DirectPermissions: index.Permissions.Count(r => r.Direct),
            WithoutAccessGroup: shared.Count(p => index.AccessGroupOf(p) is null),
            Rooms: d.Rooms.Count,
            RoomsWithDeviation: d.Rooms.Count(r => Changes.PlanBuilder.RoomDeviations(r, config.Rooms.Default).Count > 0),
            DifferencesAdEntra: differences.GroupBy(v => v.KindText).Select(g => new Bar(g.Key, g.Count())).OrderByDescending(s => s.Count).ToList(),
            Security: d.Security is { } b ? SecurityStatistics.Compute(b, active) : null,
            MailboxAccounts: d.Persons.Count(p => !p.IsGuest && d.Index.IsMailboxAccount(p)));
    }

    /// <summary>The most recent sign-in from AD and Entra; null = never (or unknown).</summary>
    public static DateTime? LastSignIn(Person p)
    {
        DateTime? best = null;
        foreach (var text in new[] { p.Ad?.LastLogonDate, p.Entra?.LastSignIn })
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && (best is null || d > best))
            {
                best = d;
            }
        }

        return best;
    }
}

/// <summary>Indicators about MFA and admin roles (only when the security data is loaded).</summary>
public sealed record SecurityStatistics(
    bool MfaKnown,
    bool RolesKnown,
    int WithMfaData,
    int WithoutMfa,
    int WithAdminRole,
    int GlobalAdministrators,
    int CriticalWithoutMfa,
    IReadOnlyList<Bar> Roles,
    IReadOnlyList<Bar> Methods)
{
    public static SecurityStatistics Compute(SecurityData b, IReadOnlyList<Person> activeStaff)
    {
        var registrations = activeStaff.Select(p => b.MfaOf(p.Entra?.Id)).Where(m => m is not null).Select(m => m!).ToList();
        var withoutMfa = registrations.Where(m => !m.MfaRegistered).Select(m => m.UserId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usersInRole = b.Roles.Where(r => r.MemberKind is "user" or "").ToList();
        return new SecurityStatistics(
            b.MfaKnown,
            b.RolesKnown,
            registrations.Count,
            withoutMfa.Count,
            usersInRole.Select(r => r.MemberId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            usersInRole.Count(r => r.Role == "Global Administrator"),
            usersInRole.Where(r => SecurityData.IsCriticalRole(r.Role) && b.MfaOf(r.MemberId) is { MfaRegistered: false })
                .Select(r => r.MemberId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            b.Roles.GroupBy(r => r.Role).Select(g => new Bar(g.Key, g.Count(), Link: "beveiliging")).OrderByDescending(s => s.Count).ToList(),
            registrations.SelectMany(m => m.Methods.Select(MethodGroup).Distinct()).GroupBy(n => n)
                .Select(g => new Bar(MethodName(g.Key), g.Count(), registrations.Count, Kpis.Method(g.Key).Link))
                .OrderByDescending(s => s.Count).ToList());
    }

    /// <summary>The method as it counts in the statistics: the passkeys count together with the FIDO2 keys.</summary>
    public static string MethodGroup(string method) => method is "passKeyDeviceBound" or "passKeyDeviceBoundAuthenticator" ? "fido2SecurityKey" : method;

    /// <summary>Readable name of an MFA method from the report, in the language of the interface.</summary>
    public static string MethodName(string method) => MethodGroup(method) switch
    {
        "microsoftAuthenticatorPush" => Localization.Texts.T["mfa-method.authenticator-push"],
        "microsoftAuthenticatorPasswordless" => Localization.Texts.T["mfa-method.authenticator-passwordless"],
        "softwareOneTimePasscode" => Localization.Texts.T["mfa-method.software-otp"],
        "hardwareOneTimePasscode" => Localization.Texts.T["mfa-method.hardware-otp"],
        "mobilePhone" => Localization.Texts.T["mfa-method.mobile-phone"],
        "alternateMobilePhone" => Localization.Texts.T["mfa-method.alternate-mobile"],
        "officePhone" => Localization.Texts.T["mfa-method.office-phone"],
        "email" => Localization.Texts.T["mfa-method.email"],
        "fido2SecurityKey" => Localization.Texts.T["mfa-method.fido2"],
        "temporaryAccessPass" => Localization.Texts.T["mfa-method.tap"],
        "securityQuestion" => Localization.Texts.T["mfa-method.security-question"],
        "windowsHelloForBusiness" => "Windows Hello",
        var other => other,
    };
}
