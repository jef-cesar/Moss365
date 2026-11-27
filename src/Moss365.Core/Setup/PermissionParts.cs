using Moss365.Core.Localization;

namespace Moss365.Core.Setup;

/// <summary>A part of Moss365 that needs a permission no other part needs, with what it is for.</summary>
public sealed record PermissionPart(string Key, IReadOnlyList<string> GraphScopes)
{
    public string Name => Texts.T[$"setup.part.{Key}"];

    public string Explanation => Texts.T[$"setup.part.{Key}.help"];
}

/// <summary>
/// Write permissions per part (R16, U25, U26): a broad or rarely needed permission is only asked for a tenant that
/// turns its part on (<see cref="Configuration.OrganisationConfig.PermissionParts"/>); turning the part off again
/// takes the permission away at the next run of the wizard. The engine refuses the changes of a part that is off.
/// </summary>
public static class PermissionParts
{
    /// <summary>Disabling and deleting old devices in Entra (U25): Microsoft only allows it with Directory.AccessAsUser.All,
    /// with which the app may do everything in the directory the signed-in administrator may.</summary>
    public const string EntraDeviceCleanup = "entra-device-cleanup";

    /// <summary>Making baseline compliance policies from templates (U26).</summary>
    public const string CompliancePolicies = "compliance-policies";

    /// <summary>The policies of the tenant (R16): apps and tenants made by users, consent and the admin consent
    /// workflow, the access of guests and who invites them.</summary>
    public const string TenantPolicies = "tenant-policies";

    /// <summary>The authentication methods of the tenant (R16): passkeys and Authenticator on, SMS and voice off.</summary>
    public const string AuthenticationMethods = "authentication-methods";

    /// <summary>Making permanent admin roles eligible (R16, PIM; needs Entra ID P2).</summary>
    public const string Pim = "pim";

    /// <summary>The sharing settings of SharePoint and OneDrive (R16: no links for everyone).</summary>
    public const string SharePointSettings = "sharepoint-settings";

    public static IReadOnlyList<PermissionPart> All { get; } = new PermissionPart[]
    {
        new(EntraDeviceCleanup, new[] { "Directory.AccessAsUser.All" }),
        new(CompliancePolicies, new[] { "DeviceManagementConfiguration.ReadWrite.All" }),
        new(TenantPolicies, new[] { "Policy.ReadWrite.Authorization", "Policy.ReadWrite.ConsentRequest" }),
        new(AuthenticationMethods, new[] { "Policy.ReadWrite.AuthenticationMethod" }),
        new(Pim, new[] { "RoleEligibilitySchedule.ReadWrite.Directory", "RoleAssignmentSchedule.ReadWrite.Directory" }),
        new(SharePointSettings, new[] { "SharePointTenantSettings.ReadWrite.All" }),
    };

    public static bool IsOn(IEnumerable<string>? on, string part) => on?.Contains(part, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>The delegated Graph permissions for a tenant: the base list and those of the parts that are on.</summary>
    public static IReadOnlyList<string> GraphScopes(IEnumerable<string>? on)
        => AppManifest.GraphScopes.Concat(All.Where(p => IsOn(on, p.Key)).SelectMany(p => p.GraphScopes)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The engine refuses a change of a part that is off (U25, U26), also when a plan was made by hand.</summary>
    public static string? Refusal(Changes.Change w, IEnumerable<string>? on) => w.ChangeAction switch
    {
        Changes.ChangeAction.IntuneCreateCompliancePolicy or Changes.ChangeAction.IntuneSetComplianceSettings when !IsOn(on, CompliancePolicies) => Texts.T["compliance.template.part-off"],
        Changes.ChangeAction.GraphSetDeviceEnabled or Changes.ChangeAction.GraphDeleteDevice when !IsOn(on, EntraDeviceCleanup) => Texts.T["device.cleanup.part-off"],
        Changes.ChangeAction.GraphRoleEligibility when !IsOn(on, Pim) => Texts.T.Format("advice.part-off", Texts.T[$"setup.part.{Pim}"]),
        Changes.ChangeAction.GraphUpdatePolicy when Security.TenantAdvice.PolicyTargets.GetValueOrDefault(w.Target) is { Length: > 0 } part && !IsOn(on, part)
            => Texts.T.Format("advice.part-off", Texts.T[$"setup.part.{part}"]),
        _ => null,
    };

    /// <summary>The permissions of the parts that are off: the wizard takes them away when they are still given.</summary>
    public static IReadOnlyList<string> NotWanted(IEnumerable<string>? on)
        => All.Where(p => !IsOn(on, p.Key)).SelectMany(p => p.GraphScopes).Except(AppManifest.GraphScopes, StringComparer.OrdinalIgnoreCase).ToList();
}
