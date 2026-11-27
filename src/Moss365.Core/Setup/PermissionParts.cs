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

    public static IReadOnlyList<PermissionPart> All { get; } = new PermissionPart[]
    {
        new(EntraDeviceCleanup, new[] { "Directory.AccessAsUser.All" }),
        new(CompliancePolicies, new[] { "DeviceManagementConfiguration.ReadWrite.All" }),
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
        _ => null,
    };

    /// <summary>The permissions of the parts that are off: the wizard takes them away when they are still given.</summary>
    public static IReadOnlyList<string> NotWanted(IEnumerable<string>? on)
        => All.Where(p => !IsOn(on, p.Key)).SelectMany(p => p.GraphScopes).Except(AppManifest.GraphScopes, StringComparer.OrdinalIgnoreCase).ToList();
}
