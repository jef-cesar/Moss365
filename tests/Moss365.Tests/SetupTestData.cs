using Moss365.Core.Permissions;
using Moss365.Core.Setup;

namespace Moss365.Tests;

/// <summary>Made-up tenants for the wizard (R5): empty, and set up the way Moss365 wants it.</summary>
public static class SetupTestData
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string AppId = "aaaaaaaa-0000-0000-0000-000000000001";

    public static ServicePrincipalInfo GraphSp() => new("sp-graph", AppManifest.GraphAppId, "Microsoft Graph", false,
        AppManifest.GraphScopes.Concat(new[] { "Mail.Read" }).ToDictionary(s => s, s => "id-" + s, StringComparer.OrdinalIgnoreCase));

    public static ServicePrincipalInfo ExchangeSp() => new("sp-exo", AppManifest.ExchangeAppId, "Office 365 Exchange Online", false,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Exchange.Manage"] = "id-Exchange.Manage" });

    public static readonly DirectoryPrincipal Admin = new("u-admin", "Admin", "admin@contoso.com");

    public static TenantSetupReading Empty() => new(TenantId, "Contoso", new[] { "contoso.com" }, true, null, Admin, GraphSp(), ExchangeSp(),
        null, null, Array.Empty<GrantInfo>(), Array.Empty<AssignmentInfo>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public static ApplicationInfo App() => new("app-object", AppId, AppManifest.Name, "AzureADMyOrg", true, new[] { AppManifest.RedirectUri },
        AppManifest.GraphScopes.Select(s => new ResourceAccess(AppManifest.GraphAppId, "id-" + s, "Scope"))
            .Append(new ResourceAccess(AppManifest.ExchangeAppId, "id-Exchange.Manage", "Scope")).ToList(),
        TenantSetup.ProfileRoles());

    public static TenantSetupReading Complete()
    {
        var groups = PermissionTable.AppRoles.ToDictionary(a => a.Group, a => "grp-" + a.Role, StringComparer.OrdinalIgnoreCase);
        var assignments = PermissionTable.AppRoles.Select(a => new AssignmentInfo("grp-" + a.Role, a.Id))
            .Concat(AppManifest.FirstAdministratorRoles.Select(r => new AssignmentInfo(Admin.Id, PermissionTable.AppRoles.First(a => a.Role == r).Id)))
            .ToList();
        return Empty() with
        {
            App = App(),
            AppSp = new ServicePrincipalInfo("sp-app", AppId, AppManifest.Name, true, new Dictionary<string, string>()),
            Grants = new[]
            {
                new GrantInfo("grant-graph", "sp-graph", "AllPrincipals", string.Join(' ', AppManifest.GraphScopes)),
                new GrantInfo("grant-exo", "sp-exo", "AllPrincipals", "Exchange.Manage"),
            },
            Assignments = assignments,
            Groups = groups,
        };
    }
}
