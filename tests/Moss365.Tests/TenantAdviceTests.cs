using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Security;
using Moss365.Core.Setup;
using CheckStatus = Moss365.Core.Security.CheckStatus;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16: the advice for Microsoft 365 as plans, each write permission in its own part (U25).</summary>
public class TenantAdviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(1));
    private const string Me = "an.peeters@test.be";

    public static TenantSettingsReading Open() => new()
    {
        SecurityDefaults = false,
        UsersCreateApps = true,
        UsersCreateTenants = true,
        ConsentPolicies = new[] { "ManagePermissionGrantsForSelf.microsoft-user-default-legacy", "ManagePermissionGrantsForOwnedResource.microsoft-dynamically-managed-permissions-for-team" },
        GuestRoleId = TenantAdvice.LimitedGuests,
        AllowInvitesFrom = "everyone",
        ConsentWorkflow = false,
        ConsentWorkflowJson = "{\"id\":\"policy\",\"isEnabled\":false,\"notifyReviewers\":false,\"remindersEnabled\":false,\"requestDurationInDays\":0,\"reviewers\":[],\"version\":0}",
        Methods = new Dictionary<string, string> { ["sms"] = "enabled", ["voice"] = "enabled", ["fido2"] = "disabled", ["microsoftauthenticator"] = "disabled" },
        Sharing = "externalUserAndGuestSharing",
        HasP1 = true,
    };

    private static OrganisationConfig Config(params string[] parts)
    {
        var c = TestData.Config();
        c.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        c.PermissionParts.AddRange(parts);
        return c;
    }

    private static InventoryData Data(params MfaRegistration[] mfa) => TestData.AppEnvironment() with
    {
        ConditionalAccess = new CaData(Array.Empty<CaPolicy>(), Array.Empty<NamedLocation>(), Array.Empty<AuthenticationStrength>(), new Dictionary<string, string>(), false),
        Security = new SecurityData(mfa, Array.Empty<RoleMember>()),
    };

    private static CheckRow Row(SecurityCheck c, string fix) => c.Rows.Single(r => r.Fix == fix);

    private static LogEntry Log(Change c) => new(Now, Me, "test", c.TargetSystem.ToString(), c.ChangeAction.ToString(), c.Target, c.TargetName,
        c.Description, c.Parameters, "Succeeded", string.Empty, Previous: c.Previous);

    [Fact]
    public void TheSettingsOfTheTenantAreCheckedWithAFixPerPoint()
    {
        var check = TenantAdvice.Check(Open(), Data());
        Assert.Equal(CheckStatus.Problem, check.Status);
        Assert.Equal(new[] { TenantAdvice.CaBaseline, TenantAdvice.Apps, TenantAdvice.Consent, TenantAdvice.Guests, TenantAdvice.WeakMethods, TenantAdvice.StrongMethods, TenantAdvice.Sharing },
            check.Rows.Select(r => r.Fix));
        Assert.Contains("register apps", Row(check, TenantAdvice.Apps).Detail.Replace("apps registreren", "register apps", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Problem, Row(check, TenantAdvice.Consent).Status); // any app: legacy consent

        // Without Entra ID P1: Security Defaults instead of Conditional Access
        Assert.Equal(TenantAdvice.SecurityDefaultsOn, TenantAdvice.Check(Open() with { HasP1 = false }, Data()).Rows[0].Fix);
        // Security Defaults on, restricted guests, no consent, workflow on, only strong methods: all in order
        var fine = Open() with
        {
            SecurityDefaults = true, UsersCreateApps = false, UsersCreateTenants = false, ConsentPolicies = Array.Empty<string>(), ConsentWorkflow = true,
            GuestRoleId = TenantAdvice.RestrictedGuests, AllowInvitesFrom = "adminsAndGuestInviters", Sharing = "externalUserSharingOnly",
            Methods = new Dictionary<string, string> { ["sms"] = "disabled", ["voice"] = "disabled", ["fido2"] = "enabled", ["microsoftauthenticator"] = "enabled" },
        };
        Assert.Equal(CheckStatus.Ok, TenantAdvice.Check(fine, Data()).Status);
        Assert.Equal(CheckStatus.Unknown, TenantAdvice.Check(null, Data()).Status);
    }

    [Fact]
    public void EachPlanNeedsItsPartAndCanBeUndone()
    {
        var check = TenantAdvice.Check(Open(), Data());
        var off = new AdviceContext(Config(), Me);
        foreach (var fix in new[] { TenantAdvice.Apps, TenantAdvice.Consent, TenantAdvice.Guests, TenantAdvice.StrongMethods, TenantAdvice.Sharing })
        {
            Assert.Contains("Tenant inrichten", Assert.Single(SecurityAdvice.SolveRow(Data(), Row(check, fix), off).Errors), StringComparison.Ordinal);
        }

        var on = new AdviceContext(Config(PermissionParts.TenantPolicies, PermissionParts.AuthenticationMethods, PermissionParts.SharePointSettings), Me);
        var apps = Assert.Single(SecurityAdvice.SolveRow(Data(), Row(check, TenantAdvice.Apps), on).Plan);
        Assert.Equal((ChangeAction.GraphUpdatePolicy, "policies/authorizationPolicy"), (apps.ChangeAction, apps.Target));
        Assert.Equal("{\"defaultUserRolePermissions\":{\"allowedToCreateApps\":false,\"allowedToCreateTenants\":false}}", apps.P("json"));
        Assert.Equal("{\"defaultUserRolePermissions\":{\"allowedToCreateApps\":true,\"allowedToCreateTenants\":true}}", apps.Previous!["json"]);
        Assert.Equal(apps.Previous["json"], Assert.Single(Undo.Plan(new[] { Log(apps) }).Plan).P("json"));

        // Consent: only the consent users give themselves goes (team owners keep theirs), and the workflow comes with you as reviewer
        var consent = SecurityAdvice.SolveRow(Data(), Row(check, TenantAdvice.Consent), on).Plan;
        Assert.Equal(new[] { "policies/authorizationPolicy", "policies/adminConsentRequestPolicy" }, consent.Select(c => c.Target));
        var kept = JsonNode.Parse(consent[0].P("json"))!["defaultUserRolePermissions"]!["permissionGrantPoliciesAssigned"]!.AsArray().Select(x => (string?)x).ToList();
        Assert.Equal(new[] { "ManagePermissionGrantsForOwnedResource.microsoft-dynamically-managed-permissions-for-team" }, kept);
        var workflow = JsonNode.Parse(consent[1].P("json"))!;
        Assert.True((bool)workflow["isEnabled"]!);
        Assert.Equal("/users/e-an", (string?)workflow["reviewers"]![0]!["query"]);
        Assert.DoesNotContain("\"id\"", consent[1].Previous!["json"], StringComparison.Ordinal); // the previous one as it can be sent back

        var guests = JsonNode.Parse(Assert.Single(SecurityAdvice.SolveRow(Data(), Row(check, TenantAdvice.Guests), on).Plan).P("json"))!;
        Assert.Equal((TenantAdvice.RestrictedGuests, "adminsAndGuestInviters"), ((string?)guests["guestUserRoleId"], (string?)guests["allowInvitesFrom"]));

        var strong = SecurityAdvice.SolveRow(Data(), Row(check, TenantAdvice.StrongMethods), on).Plan;
        Assert.Equal(2, strong.Count);
        Assert.All(strong, c => Assert.Contains("\"state\":\"enabled\"", c.P("json"), StringComparison.Ordinal));
        Assert.Contains("microsoftAuthenticatorAuthenticationMethodConfiguration", strong[0].P("json"), StringComparison.Ordinal);

        var sharing = Assert.Single(SecurityAdvice.SolveRow(Data(), Row(check, TenantAdvice.Sharing), on).Plan);
        Assert.Equal(("admin/sharepoint/settings", "{\"sharingCapability\":\"externalUserSharingOnly\"}"), (sharing.Target, sharing.P("json")));

        // The engine: the part of the policy, and only the policies of the advice
        Assert.NotNull(PermissionParts.Refusal(apps, null));
        Assert.Null(PermissionParts.Refusal(apps, on.Config.PermissionParts));
        Assert.NotNull(PermissionParts.Refusal(sharing, new[] { PermissionParts.TenantPolicies }));
        var forged = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphUpdatePolicy, Target = "policies/crossTenantAccessPolicy", Description = "x" };
        Assert.NotNull(TenantAdvice.Refusal(forged));
        Assert.Null(TenantAdvice.Refusal(apps));
    }

    [Fact]
    public void SmsGoesOffOnlyOnceNobodyDependsOnIt()
    {
        var check = TenantAdvice.Check(Open(), Data());
        var weak = Row(check, TenantAdvice.WeakMethods);
        var ctx = new AdviceContext(Config(PermissionParts.AuthenticationMethods), Me);
        Assert.True(SecurityAdvice.SolvableAlone(check, weak)); // guided
        Assert.DoesNotContain(SecurityAdvice.Solvable(check), r => r.Fix == TenantAdvice.WeakMethods);

        var onlySms = new MfaRegistration("u-1", "bo.janssens@test.be", true, true, new[] { "mobilePhone" }, "mobilePhone", false);
        var strong = new MfaRegistration("u-2", "cas.vos@test.be", true, true, new[] { "mobilePhone", "microsoftAuthenticatorPush" }, "microsoftAuthenticatorPush", false);
        var blocked = SecurityAdvice.SolveRow(Data(onlySms, strong), weak, ctx);
        Assert.Empty(blocked.Plan);
        Assert.Contains("bo.janssens@test.be", Assert.Single(blocked.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain("cas.vos", blocked.Errors[0], StringComparison.Ordinal);

        var plan = SecurityAdvice.SolveRow(Data(strong), weak, ctx).Plan;
        Assert.Equal(new[] { "policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Sms", "policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Voice" },
            plan.Select(c => c.Target));
        Assert.All(plan, c => Assert.Contains("\"state\":\"disabled\"", c.P("json"), StringComparison.Ordinal));
        Assert.All(plan, c => Assert.Contains("\"state\":\"enabled\"", c.Previous!["json"], StringComparison.Ordinal));
    }

    [Fact]
    public void TheBaselineOfConditionalAccessOrSecurityDefaults()
    {
        var check = TenantAdvice.Check(Open(), Data());
        var baseline = SecurityAdvice.SolveRow(Data(), Row(check, TenantAdvice.CaBaseline), new AdviceContext(Config(), Me));
        Assert.Equal(TenantAdvice.BaselineTemplates.Count, baseline.Plan.Count);
        Assert.All(baseline.Plan, c => Assert.Equal(ChangeAction.GraphCreateCaPolicy, c.ChangeAction));
        Assert.All(baseline.Plan, c => Assert.Equal("enabledForReportingButNotEnforced", (string?)JsonNode.Parse(c.P("json"))!["state"]));

        var defaults = TenantAdvice.Check(Open() with { HasP1 = false }, Data());
        var on = Assert.Single(SecurityAdvice.SolveRow(Data(), defaults.Rows[0], new AdviceContext(Config(), Me)).Plan);
        Assert.Equal(("policies/identitySecurityDefaultsEnforcementPolicy", "{\"isEnabled\":true}"), (on.Target, on.P("json")));
        Assert.Null(PermissionParts.Refusal(on, null)); // the same permission as Conditional Access
    }

    [Fact]
    public void AdminRolesOnTheWrongAccountsAndEligibleThroughPim()
    {
        var d = TestData.AppEnvironment();
        var an = d.EntraUsers.First(u => u.UserPrincipalName == Me);
        var synced = d.EntraUsers.First(u => u.OnPremisesSyncEnabled);
        var cloud = d.EntraUsers.First(u => !u.OnPremisesSyncEnabled && u.UserPrincipalName != Me && u.UserPrincipalName != "info@test.onmicrosoft.com");
        RoleAssignmentInfo Role(EntraUser u, bool pim) => new("Global Administrator", u.UserPrincipalName, "permanent", null)
        {
            RoleId = "62e90394-69f5-4237-9190-012177145e10", PrincipalId = u.Id, PrincipalType = "user", ViaPim = pim,
        };

        var roles = new[] { Role(an, true), Role(synced, true), Role(cloud with { }, true) };
        var c = Config();
        var check = SecurityChecks.Roles(roles, d, c, Me);
        Assert.Contains(check.Rows, r => r.Object == synced.UserPrincipalName && r.Detail.Contains("AD", StringComparison.Ordinal));
        var pimRows = check.Rows.Where(r => r.Fix == TenantAdvice.Pim).ToList();
        Assert.DoesNotContain(pimRows, r => r.Object.StartsWith(Me, StringComparison.Ordinal)); // never the own account
        Assert.Contains(pimRows, r => r.Object.StartsWith(cloud.UserPrincipalName, StringComparison.Ordinal));

        var row = pimRows.First(r => r.Object.StartsWith(cloud.UserPrincipalName, StringComparison.Ordinal));
        Assert.Contains("Tenant inrichten", Assert.Single(SecurityAdvice.SolveRow(d, row, new AdviceContext(c, Me)).Errors), StringComparison.Ordinal);
        c.PermissionParts.Add(PermissionParts.Pim);
        var change = Assert.Single(SecurityAdvice.SolveRow(d, row, new AdviceContext(c, Me)).Plan);
        Assert.Equal((ChangeAction.GraphRoleEligibility, cloud.Id, "eligible", "active"), (change.ChangeAction, change.Target, change.P("naar"), change.Previous!["naar"]));
        Assert.Equal("active", Assert.Single(Undo.Plan(new[] { Log(change) }).Plan).P("naar"));
        Assert.NotNull(PermissionParts.Refusal(change, null));

        // Without P2 the line says so, instead of a plan
        var noP2 = SecurityChecks.Roles(new[] { Role(cloud, false) }, d, c, Me);
        Assert.Contains(noP2.Rows, r => r.Fix is null && r.Detail.Contains("Entra ID P2", StringComparison.Ordinal));
    }

    [Fact]
    public void SharedMailboxesWithSignInAndTheSignInsOfEmergencyAccounts()
    {
        var d = TestData.AppEnvironment();
        var cloud = d.EntraUsers.First(u => !u.OnPremisesSyncEnabled);
        var data = d with
        {
            Mailboxes = new[] { new Mailbox { Type = "SharedMailbox", DisplayName = "Info", Smtp = cloud.Mail.Length > 0 ? cloud.Mail : cloud.UserPrincipalName } },
            EntraUsers = d.EntraUsers.Select(u => u.Id == cloud.Id ? u with { AccountEnabled = true, Mail = u.Mail.Length > 0 ? u.Mail : u.UserPrincipalName } : u).ToList(),
        };
        var shared = TenantAdvice.SharedMailboxes(data);
        var row = Assert.Single(shared.Rows);
        var block = Assert.Single(SecurityAdvice.SolveRow(data, row).Plan);
        Assert.Equal((ChangeAction.GraphSetAttributes, cloud.Id, "false", "true"), (block.ChangeAction, block.Target, block.P("accountEnabled"), block.Previous!["accountEnabled"]));
        Assert.Equal(CheckStatus.Unknown, TenantAdvice.SharedMailboxes(new InventoryData()).Status);

        // Emergency accounts: named, the logs kept, every sign-in a line
        Assert.Equal(CheckStatus.Attention, TenantAdvice.Emergency(TestData.Config(), Array.Empty<KeptSignIn>(), Now).Status);
        var c = Config();
        var signIn = new KeptSignIn(new SignInEvent(Now.AddDays(-2), "e-info", "info@test.onmicrosoft.com", "Info", "Azure Portal", "198.51.100.7", "BE", 0, string.Empty, "Browser"), new[] { "emergency", "admin" });
        var old = signIn with { SignIn = signIn.SignIn with { Time = Now.AddDays(-40) } };
        var emergency = TenantAdvice.Emergency(c, new[] { signIn, old }, Now);
        Assert.Equal(CheckStatus.Problem, emergency.Status);
        Assert.Contains("Azure Portal", Assert.Single(emergency.Rows).Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, TenantAdvice.Emergency(c, new[] { old }, Now).Status);
        c.Retention.KeepLocalLogs = false;
        Assert.Equal(CheckStatus.Unknown, TenantAdvice.Emergency(c, Array.Empty<KeptSignIn>(), Now).Status);
    }
}
