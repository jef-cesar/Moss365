using Moss365.Core.Changes;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16, U25, U26: a broad or rarely needed permission only for a tenant that turns its part on.</summary>
public class PermissionPartsTests
{
    [Fact]
    public void APermissionOfAPartIsOnlyAskedWhenItIsOnAndTakenAwayWhenItIsOff()
    {
        Assert.True(TenantSetup.Assess(SetupTestData.Complete()).Ready);
        Assert.Equal(AppManifest.GraphScopes, PermissionParts.GraphScopes(null));
        Assert.Contains("DeviceManagementConfiguration.Read.All", AppManifest.GraphScopes); // reading compliance: for everyone (U26)
        Assert.DoesNotContain("Directory.AccessAsUser.All", AppManifest.GraphScopes);

        // Turned on: the app registration and the admin consent get its permission, and nothing else
        var on = TenantSetup.Assess(SetupTestData.Complete(), new SetupChoices(Parts: new[] { PermissionParts.CompliancePolicies }));
        var consent = Assert.Single(on.Plan, c => c.ChangeAction == ChangeAction.GraphGrantConsent);
        Assert.Contains("DeviceManagementConfiguration.ReadWrite.All", consent.P("scope"), StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.AccessAsUser.All", consent.P("scope"), StringComparison.Ordinal);
        Assert.Contains("id-DeviceManagementConfiguration.ReadWrite.All", Assert.Single(on.Plan, c => c.ChangeAction == ChangeAction.GraphUpdateApplication).P("json"), StringComparison.Ordinal);

        // Still given for a part that is off: taken away from the app registration and from the consent
        var broad = SetupTestData.Complete() with
        {
            App = SetupTestData.App() with
            {
                RequiredAccess = SetupTestData.App().RequiredAccess.Append(new ResourceAccess(AppManifest.GraphAppId, "id-Directory.AccessAsUser.All", "Scope")).ToList(),
            },
            Grants = new[]
            {
                new GrantInfo("grant-graph", "sp-graph", "AllPrincipals", string.Join(' ', AppManifest.GraphScopes.Append("Directory.AccessAsUser.All"))),
                new GrantInfo("grant-exo", "sp-exo", "AllPrincipals", "Exchange.Manage"),
            },
        };
        var off = TenantSetup.Assess(broad);
        var away = Assert.Single(off.Plan, c => c.ChangeAction == ChangeAction.GraphGrantConsent);
        Assert.DoesNotContain("Directory.AccessAsUser.All", away.P("scope"), StringComparison.Ordinal);
        Assert.Contains("Directory.AccessAsUser.All", away.Previous!["scope"], StringComparison.Ordinal);
        Assert.Contains("Weghalen uit de beheerderstoestemming", away.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("id-Directory.AccessAsUser.All", Assert.Single(off.Plan, c => c.ChangeAction == ChangeAction.GraphUpdateApplication).P("json"), StringComparison.Ordinal);

        // With the part on, the same tenant is in order
        Assert.True(TenantSetup.Assess(broad, new SetupChoices(Parts: new[] { PermissionParts.EntraDeviceCleanup })).Ready);
    }
}
