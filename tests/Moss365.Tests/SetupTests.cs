using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Connections.Check;
using Moss365.Connections.Graph;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Permissions;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R5: the wizard compares a tenant with what Moss365 needs and plans only the difference.</summary>
public sealed class SetupTests
{
    [Theory]
    [InlineData("contoso.com", "contoso.com")]
    [InlineData(" Jan.Peeters@Contoso.COM ", "contoso.com")]
    [InlineData("https://contoso.com/path", "contoso.com")]
    [InlineData("11111111-2222-3333-4444-55555555555A", "11111111-2222-3333-4444-55555555555a")]
    [InlineData("geen-domein", null)]
    [InlineData("con toso.com", null)]
    [InlineData("", null)]
    public void ADomainOrTenantIdIsCleaned(string input, string? expected) => Assert.Equal(expected, TenantLookup.Clean(input));

    [Fact]
    public void TheTenantIdComesFromTheMetadata()
    {
        Assert.Equal(SetupTestData.TenantId, TenantLookup.TenantIdFrom($$"""{"issuer":"https://login.microsoftonline.com/{{SetupTestData.TenantId}}/v2.0","token_endpoint":"x"}"""));
        Assert.Null(TenantLookup.TenantIdFrom("""{"error":"invalid_tenant"}"""));
        Assert.Null(TenantLookup.TenantIdFrom("geen json"));
        Assert.Equal("https://login.microsoftonline.com/contoso.com/v2.0/.well-known/openid-configuration", TenantLookup.MetadataUrl("contoso.com"));
    }

    [Fact]
    public void AnEmptyTenantGetsTheWholeSetUp()
    {
        var a = TenantSetup.Assess(SetupTestData.Empty());
        Assert.False(a.Ready);
        Assert.Null(a.ClientId);

        var create = Assert.Single(a.Plan, c => c.ChangeAction == ChangeAction.GraphCreateApplication);
        var app = JsonNode.Parse(create.P("json"))!;
        Assert.Equal("AzureADMyOrg", (string?)app["signInAudience"]);
        Assert.True((bool)app["isFallbackPublicClient"]!);
        Assert.Equal(AppManifest.RedirectUri, (string?)app["publicClient"]!["redirectUris"]![0]);
        var access = app["requiredResourceAccess"]!.AsArray();
        Assert.Equal(AppManifest.GraphScopes.Count, access.Single(r => (string?)r!["resourceAppId"] == AppManifest.GraphAppId)!["resourceAccess"]!.AsArray().Count);
        Assert.Single(access.Single(r => (string?)r!["resourceAppId"] == AppManifest.ExchangeAppId)!["resourceAccess"]!.AsArray());
        Assert.Equal(PermissionTable.AppRoles.Select(r => r.Id), app["appRoles"]!.AsArray().Select(r => (string?)r!["id"]));

        // The enterprise app waits for the app registration; consent for Graph and Exchange; a group per profile; the admin
        var sp = Assert.Single(a.Plan, c => c.ChangeAction == ChangeAction.GraphCreateServicePrincipal);
        Assert.Equal(create.Id, sp.DependentOf);
        Assert.Equal(2, a.Plan.Count(c => c.ChangeAction == ChangeAction.GraphGrantConsent));
        Assert.All(a.Plan.Where(c => c.ChangeAction == ChangeAction.GraphGrantConsent), c => Assert.Equal(sp.Id, c.DependentOf));
        Assert.Equal(PermissionTable.AppRoles.Select(r => r.Group), a.Plan.Where(c => c.ChangeAction == ChangeAction.GraphCreateGroup).Select(c => c.P("displayName")));
        Assert.Equal(5, a.Plan.Count(c => c.ChangeAction == ChangeAction.GraphAssignAppRole && c.P("kind") == "group"));
        Assert.Equal(new[] { "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8003", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8005" },
            a.Plan.Where(c => c.ChangeAction == ChangeAction.GraphAssignAppRole && c.P("kind") == "user").Select(c => c.P("appRoleId")));
        Assert.Equal(16, a.Plan.Count);
        Assert.All(a.Plan, c => Assert.Equal(TargetSystem.Graph, c.TargetSystem));

        // Only the profile AppAdmin may run it
        Assert.All(a.Plan.Where(c => c.ChangeAction != ChangeAction.GraphCreateGroup), c => Assert.Equal(Permission.Settings, PermissionTable.ForAction(c.ChangeAction)));
        Assert.NotNull(PermissionTable.Refusal(Role.Administrator, create));
        Assert.Null(PermissionTable.Refusal(Role.AppAdmin, create));
        Assert.All(a.Plan, c => Assert.Null(PermissionTable.Refusal(Role.AppAdmin, c)));
    }

    [Fact]
    public void ATenantThatIsSetUpNeedsNothing()
    {
        var a = TenantSetup.Assess(SetupTestData.Complete());
        Assert.Empty(a.Plan);
        Assert.True(a.Ready);
        Assert.Equal(SetupTestData.AppId, a.ClientId);
        Assert.Contains(a.Items, i => i.Name == "Hybride" && i.Status == SetupStatus.InOrder);
    }

    [Fact]
    public void OnlyTheDifferenceIsPlanned()
    {
        var full = SetupTestData.Complete();
        var other = new AppRoleInfo("99999999-0000-0000-0000-000000000000", "Other.Role", "Other", "keep me", true, new[] { "User" });
        var r = full with
        {
            App = full.App! with
            {
                RedirectUris = new[] { "https://example.org" },
                RequiredAccess = full.App.RequiredAccess.Where(x => x.Id != "id-Policy.Read.All").Append(new ResourceAccess(AppManifest.GraphAppId, "id-Mail.Read", "Scope")).ToList(),
                AppRoles = full.App.AppRoles.Where(x => x.Value != "Moss365.Auditor").Append(other).ToList(),
            },
            AppSp = full.AppSp! with { AssignmentRequired = false },
            Grants = new[] { full.Grants[0] with { Scope = "User.ReadWrite.All" }, full.Grants[1] },
            Assignments = full.Assignments.Where(x => x.PrincipalId != "grp-" + Role.Helpdesk).ToList(),
        };

        var a = TenantSetup.Assess(r);
        Assert.Equal(new[] { ChangeAction.GraphUpdateApplication, ChangeAction.GraphUpdateServicePrincipal, ChangeAction.GraphGrantConsent, ChangeAction.GraphAssignAppRole },
            a.Plan.Select(c => c.ChangeAction));

        // The app registration keeps what it had (an extra permission, another role) and gets what is missing
        var update = a.Plan[0];
        Assert.Equal("app-object", update.Target);
        var patch = JsonNode.Parse(update.P("json"))!;
        var redirects = patch["publicClient"]!["redirectUris"]!.AsArray().Select(x => (string?)x).ToList();
        Assert.Equal(new[] { "https://example.org", AppManifest.RedirectUri }, redirects);
        var graph = patch["requiredResourceAccess"]!.AsArray().Single(x => (string?)x!["resourceAppId"] == AppManifest.GraphAppId)!["resourceAccess"]!.AsArray().Select(x => (string?)x!["id"]).ToList();
        Assert.Contains("id-Mail.Read", graph);
        Assert.Contains("id-Policy.Read.All", graph);
        var roles = patch["appRoles"]!.AsArray().Select(x => (string?)x!["value"]).ToList();
        Assert.Contains("Other.Role", roles);
        Assert.Contains("Moss365.Auditor", roles);
        Assert.Null(patch["isFallbackPublicClient"]);
        Assert.Contains("https://example.org", update.Previous!["json"]);

        Assert.Equal("{\"appRoleAssignmentRequired\":true}", a.Plan[1].P("json"));
        var consent = a.Plan[2];
        Assert.Equal("grant-graph", consent.P("grantId"));
        Assert.StartsWith("User.ReadWrite.All ", consent.P("scope"), StringComparison.Ordinal);
        Assert.Equal("User.ReadWrite.All", consent.Previous!["scope"]);
        Assert.Equal("grp-Helpdesk", a.Plan[3].Target);
        Assert.Null(a.Plan[3].DependentOf);
    }

    [Fact]
    public void WithoutGroupsThereIsNoGroupInThePlan()
    {
        var a = TenantSetup.Assess(SetupTestData.Empty(), new SetupChoices(CreateGroups: false, AssignSignedIn: false));
        Assert.DoesNotContain(a.Plan, c => c.ChangeAction is ChangeAction.GraphCreateGroup or ChangeAction.GraphAssignAppRole);
        Assert.Equal(5, a.Items.Count(i => i.Step == TenantSetup.ProfilesStep && i.Status == SetupStatus.Attention));
    }

    [Fact]
    public void WhatGraphReturnsIsRead()
    {
        using var app = JsonDocument.Parse("""
            {"id":"o1","appId":"a1","displayName":"Moss365","signInAudience":"AzureADMyOrg","isFallbackPublicClient":true,
             "publicClient":{"redirectUris":["http://localhost"]},
             "requiredResourceAccess":[{"resourceAppId":"00000003-0000-0000-c000-000000000000","resourceAccess":[{"id":"s1","type":"Scope"},{"id":"r1","type":"Role"}]}],
             "appRoles":[{"id":"x","value":"Moss365.Reader","displayName":"Moss365 Reader","description":"d","isEnabled":false,"allowedMemberTypes":["User"]}]}
            """);
        var a = TenantSetupConnection.ToApplication(app.RootElement);
        Assert.Equal(("o1", "a1", true), (a.ObjectId, a.AppId, a.FallbackPublicClient));
        Assert.Equal(new[] { "Scope", "Role" }, a.RequiredAccess.Select(x => x.Type));
        Assert.False(Assert.Single(a.AppRoles).Enabled);

        using var sp = JsonDocument.Parse("""{"id":"sp1","appId":"a1","displayName":"Microsoft Graph","appRoleAssignmentRequired":true,"oauth2PermissionScopes":[{"value":"User.Read","id":"u1"}]}""");
        var s = TenantSetupConnection.ToServicePrincipal(sp.RootElement);
        Assert.True(s.AssignmentRequired);
        Assert.Equal("u1", s.Scopes["user.read"]);

        using var org = JsonDocument.Parse("""{"id":"t1","displayName":"Contoso","verifiedDomains":[{"name":"contoso.com"}],"onPremisesSyncEnabled":null}""");
        using var me = JsonDocument.Parse("""{"id":"u1","displayName":"Admin","userPrincipalName":"admin@contoso.com"}""");
        var reading = TenantSetupConnection.ToReading(org.RootElement, me.RootElement, "t0", null, null, null, null,
            Array.Empty<GrantInfo>(), Array.Empty<AssignmentInfo>(), new Dictionary<string, string>());
        Assert.Equal(("t1", false, "admin@contoso.com"), (reading.TenantId, reading.SyncEnabled, reading.SignedIn.Upn));
        Assert.Equal(TenantSetupConnection.Filter("displayName", "O'Brien"), Uri.EscapeDataString("displayName eq 'O''Brien'"));
    }

    [Fact]
    public void TheDomainOfThisPc()
    {
        Assert.Equal(new LocalDomain("test.internal", "dc01.test.internal"), LocalDomainFinder.From("TEST.internal", null, @"\\DC01", "PC1"));
        Assert.Equal(new LocalDomain("test.internal", null), LocalDomainFinder.From("", "TEST.INTERNAL", @"\\PC1", "PC1"));
        Assert.Null(LocalDomainFinder.From("", null, @"\\PC1", "PC1"));
        Assert.Null(LocalDomainFinder.From("WORKGROUP", null, null, "PC1"));
    }

    [Fact]
    public void TheClockOfThisPc()
    {
        var now = new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(CheckStatus.Good, SystemCheck.Clock(now, now.AddSeconds(20)).Status);
        Assert.Equal(CheckStatus.Warning, SystemCheck.Clock(now, now.AddMinutes(-3)).Status);
        Assert.Equal(CheckStatus.Error, SystemCheck.Clock(now, now.AddMinutes(7)).Status);
        Assert.Equal(CheckStatus.Warning, SystemCheck.Clock(now, null).Status);
    }

    [Fact]
    public void SuggestionsComeFromTheData()
    {
        var files = TestData.Files();
        files[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "an.peeters")["Office"] = "Gemeentehuis";
        files[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "cas.vos")["Office"] = "gemeentehuis ";
        files[InventoryLoader.EntraUsers].Add(TestData.EntraUser("e-nood", "breakglass@test.onmicrosoft.com", displayName: "Break glass"));
        var data = InventoryLoader.Build(files);
        data = data with
        {
            Security = new SecurityData(Array.Empty<MfaRegistration>(), new[]
            {
                new RoleMember("Global Administrator", "e-nood", "Break glass", "breakglass@test.onmicrosoft.com", "user"),
                new RoleMember("Global Administrator", "e-info", "Info", "info@test.onmicrosoft.com", "user"),
                new RoleMember("Global Administrator", "e-an", "An", "an.peeters@test.be", "user"),
            }),
        };
        var config = TestData.Config();
        config.Offices.Clear();
        config.Departments.RemoveAll(d => d.Department == "gezinszorg");
        config.AdDomain = null;

        var s = OrganisationSuggestions.For(data, config);
        Assert.Contains(s, x => x.Kind == SuggestionKind.AdDomain && x.Value == "test.internal");
        Assert.Contains(s, x => x.Kind == SuggestionKind.Department && x.Value == "gezinszorg");
        Assert.DoesNotContain(s, x => x.Kind == SuggestionKind.Department && x.Value == "cultuur");
        Assert.Equal(2, Assert.Single(s, x => x.Kind == SuggestionKind.Office).Count);

        // Emergency accounts: cloud-only Global Administrators with a name that says so (CA not loaded); not a synced account
        var emergency = s.Where(x => x.Kind == SuggestionKind.EmergencyAccount).Select(x => x.Value).ToList();
        Assert.Equal(new[] { "breakglass@test.onmicrosoft.com" }, emergency);

        OrganisationSuggestions.Apply(config, s);
        Assert.Equal("test.internal", config.AdDomain);
        Assert.NotNull(config.SearchDepartment("gezinszorg"));
        Assert.Contains("breakglass@test.onmicrosoft.com", config.EmergencyAccounts);
        Assert.Empty(OrganisationSuggestions.For(data, config).Where(x => x.Kind is SuggestionKind.Department or SuggestionKind.Office or SuggestionKind.EmergencyAccount));
    }

    [Theory]
    [InlineData("gg_wifiIntern", "gg_")]
    [InlineData("SG-SM-info", "SG-")]
    [InlineData("Domain Admins", null)]
    [InlineData("_x", null)]
    public void APrefixOfAGroupName(string name, string? prefix) => Assert.Equal(prefix, OrganisationSuggestions.Prefix(name));
}
