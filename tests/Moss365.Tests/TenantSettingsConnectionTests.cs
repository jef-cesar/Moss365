using System.Net;
using Moss365.Connections.Graph;
using Moss365.Core.Changes;
using Moss365.Core.Security;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16: the settings of the tenant read through Graph, and the policies changed as plans.</summary>
public class TenantSettingsConnectionTests
{
    private static GraphConnection Make(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

    private static object Items(params object[] items) => new { value = items };

    [Fact]
    public async Task TheSettingsOfTheTenantAreReadWithTheLicences()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/policies/identitySecurityDefaultsEnforcementPolicy", new { isEnabled = false })
            .Op(HttpMethod.Get, "/policies/authorizationPolicy", new
            {
                guestUserRoleId = TenantAdvice.LimitedGuests, allowInvitesFrom = "everyone",
                defaultUserRolePermissions = new { allowedToCreateApps = true, allowedToCreateTenants = false, permissionGrantPoliciesAssigned = new[] { "ManagePermissionGrantsForSelf.microsoft-user-default-low" } },
            })
            .Op(HttpMethod.Get, "/policies/adminConsentRequestPolicy", new { isEnabled = true, reviewers = Array.Empty<object>() })
            .Op(HttpMethod.Get, "/policies/authenticationMethodsPolicy", new
            {
                authenticationMethodConfigurations = new[] { new { id = "Sms", state = "enabled" }, new { id = "Fido2", state = "disabled" }, new { id = "MicrosoftAuthenticator", state = "enabled" } },
            })
            .Op(HttpMethod.Get, "/admin/sharepoint/settings", new { sharingCapability = "externalUserAndGuestSharing" })
            .Op(HttpMethod.Get, "/subscribedSkus", Items(new { skuPartNumber = "SPB", servicePlans = new[] { new { servicePlanName = "AAD_PREMIUM", provisioningStatus = "Success" } } }));

        var r = await Make(fake).ReadTenantSettingsAsync(CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal((false, true, false), (r!.SecurityDefaults, r.UsersCreateApps, r.UsersCreateTenants));
        Assert.Equal(("everyone", TenantAdvice.LimitedGuests), (r.AllowInvitesFrom, r.GuestRoleId));
        Assert.Equal("ManagePermissionGrantsForSelf.microsoft-user-default-low", Assert.Single(r.ConsentPolicies));
        Assert.True(r.ConsentWorkflow);
        Assert.Equal(("enabled", "disabled", "enabled"), (r.Methods["sms"], r.Methods["fido2"], r.Methods["microsoftauthenticator"]));
        Assert.Equal("externalUserAndGuestSharing", r.Sharing);
        Assert.True(r.HasP1);
        Assert.False(r.HasP2);
        Assert.Empty(r.Notes);

        // What cannot be read is named, not hidden
        var partial = await Make(new FakeGraph().Op(HttpMethod.Get, "/policies/identitySecurityDefaultsEnforcementPolicy", new { isEnabled = true })).ReadTenantSettingsAsync(CancellationToken.None);
        Assert.True(partial!.SecurityDefaults);
        Assert.Contains(partial.Notes, n => n.StartsWith("SharePoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnlyThePoliciesOfTheAdviceAreChanged()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Patch, "/policies/authorizationPolicy", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Put, "/policies/adminConsentRequestPolicy", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Post, "/roleManagement/directory/roleEligibilityScheduleRequests", new { id = "e1" }, HttpStatusCode.Created)
            .Op(HttpMethod.Post, "/roleManagement/directory/roleAssignmentScheduleRequests", new { id = "a1" }, HttpStatusCode.Created);
        Change Policy(string target) => new()
        {
            TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphUpdatePolicy, Target = target, Description = "x",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = "{\"isEnabled\":true}" },
        };
        var eligible = new Change
        {
            TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphRoleEligibility, Target = "u-1", Description = "x",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["rolId"] = "62e90394-69f5-4237-9190-012177145e10", ["rol"] = "Global Administrator", ["naar"] = "eligible" },
        };

        var r = await Make(fake).ExecuteAsync(new[] { Policy("policies/authorizationPolicy"), Policy("policies/adminConsentRequestPolicy"), Policy("policies/crossTenantAccessPolicy"), eligible }, CancellationToken.None);

        Assert.Equal(new[] { ResultStatus.Succeeded, ResultStatus.Succeeded, ResultStatus.Failed, ResultStatus.Succeeded }, r.Select(x => x.Status));
        Assert.DoesNotContain(fake.Requests, v => v.Url.Contains("crossTenantAccessPolicy", StringComparison.Ordinal));
        // First the eligible schedule, then the permanent assignment removed: the role is never gone in between
        var posts = fake.Requests.Where(v => v.Method == HttpMethod.Post).ToList();
        Assert.EndsWith("/roleEligibilityScheduleRequests", posts[0].Url, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"adminAssign\"", posts[0].Content, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"noExpiration\"", posts[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("/roleAssignmentScheduleRequests", posts[1].Url, StringComparison.Ordinal);
        Assert.Contains("\"action\":\"adminRemove\"", posts[1].Content, StringComparison.Ordinal);
        Assert.Contains("\"principalId\":\"u-1\"", posts[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RoleAssignmentsComeWithTheirIds()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/roleManagement/directory/roleAssignmentScheduleInstances", Items(new Dictionary<string, object>
            {
                ["roleDefinitionId"] = "62e90394-69f5-4237-9190-012177145e10", ["principalId"] = "u-1", ["assignmentType"] = "Assigned",
                ["principal"] = new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.user", ["userPrincipalName"] = "an@test.be" },
                ["roleDefinition"] = new { displayName = "Global Administrator" },
            }))
            .Op(HttpMethod.Get, "/roleManagement/directory/roleEligibilityScheduleInstances", Items());

        var roles = await Make(fake).ReadRoleAssignmentsAsync(CancellationToken.None);

        var role = Assert.Single(roles!);
        Assert.Equal(("permanent", "62e90394-69f5-4237-9190-012177145e10", "u-1", "user", true), (role.Kind, role.RoleId, role.PrincipalId, role.PrincipalType, role.ViaPim));
        Assert.Contains(PermissionParts.All, p => p.Key == PermissionParts.Pim && p.GraphScopes.Contains("RoleEligibilitySchedule.ReadWrite.Directory"));
    }
}
