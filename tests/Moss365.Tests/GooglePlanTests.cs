using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Google;
using Moss365.Core.Inventory;
using Moss365.Core.Linking;
using Moss365.Core.Permissions;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>G2: the plans for Google users, groups, resources and licences.</summary>
public sealed class GooglePlanTests
{
    private static readonly GoogleUser An = new()
    {
        Id = "g-an", PrimaryEmail = "an.peeters@example.com", GivenName = "An", FamilyName = "Peeters", Department = "Sales",
        OrgUnitPath = "/Sales", Aliases = new[] { "an@example.com" },
    };

    private static InventoryData Data() => new()
    {
        GoogleUsers = new[] { An, new GoogleUser { Id = "g-admin", PrimaryEmail = "beheer@example.com", IsAdmin = true } },
        GoogleGroups = new[] { new GoogleGroup { Id = "gg-1", Email = "team@example.com", Aliases = new[] { "ploeg@example.com" } } },
        GoogleOrgUnits = new[] { new GoogleOrgUnit { Path = "/Sales" }, new GoogleOrgUnit { Path = "/Zorg" } },
    };

    private static readonly string[] Domains = { "example.com" };

    [Fact]
    public void OnlyWhatChangesIsPlannedWithWhatItWas()
    {
        var change = Assert.Single(GooglePlans.Edit(Data(), An, new GoogleUserDesired("An", "Peeters", "/zorg", "Zorg", " E-7 "), GoogleSource.Itself).Plan);
        Assert.Equal((ChangeAction.GoogleUpdateUser, TargetSystem.Google, "g-an"), (change.ChangeAction, change.TargetSystem, change.Target));
        Assert.Equal(new[] { "department", "employeeId", "orgUnitPath" }, change.Parameters.Keys.Order());
        Assert.Equal(("/Zorg", "E-7"), (change.P("orgUnitPath"), change.P("employeeId")));
        Assert.Equal(("Sales", "", "/Sales"), (change.Previous!["department"], change.Previous["employeeId"], change.Previous["orgUnitPath"]));

        // Nothing different: no plan; an unknown unit or an empty name: refused
        Assert.Empty(GooglePlans.Edit(Data(), An, new GoogleUserDesired("An"), GoogleSource.Itself).Plan);
        Assert.Single(GooglePlans.Edit(Data(), An, new GoogleUserDesired(OrgUnitPath: "/Elders"), GoogleSource.Itself).Errors);
        Assert.Single(GooglePlans.Edit(Data(), An, new GoogleUserDesired(GivenName: " "), GoogleSource.Itself).Errors);

        var suspend = Assert.Single(GooglePlans.Edit(Data(), An, new GoogleUserDesired(Suspended: true), GoogleSource.Itself).Plan);
        Assert.Equal(("True", "False"), (suspend.P("suspended"), suspend.Previous!["suspended"]));
        Assert.NotNull(suspend.Warning);
        Assert.Contains("schorsen", suspend.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatFollowsFromTheSourceIsNeverWrittenInGoogle()
    {
        var data = Data();
        var systems = TenantSystems.Both | TenantSystems.GoogleWorkspace;
        var fromEntra = GooglePlans.Source(systems, data, new SourceSettings { GoogleUsers = GoogleFeed.FromEntra });
        Assert.Equal(GoogleFeed.FromEntra, fromEntra.Feed);
        Assert.False(fromEntra.PasswordFollows);
        var refused = GooglePlans.Edit(data, An, new GoogleUserDesired(Department: "Zorg", Suspended: true), fromEntra);
        Assert.Empty(refused.Plan);
        Assert.Equal(2, refused.Errors.Count);

        // The provisioning of Entra leaves the unit alone; Directory Sync carries it, and with Password Sync the password
        Assert.Single(GooglePlans.Edit(data, An, new GoogleUserDesired(OrgUnitPath: "/Zorg"), fromEntra).Plan);
        var fromAd = GooglePlans.Source(systems, data, new SourceSettings { GoogleUsers = GoogleFeed.FromAd, GooglePasswordSync = true });
        Assert.Single(GooglePlans.Edit(data, An, new GoogleUserDesired(OrgUnitPath: "/Zorg"), fromAd).Errors);
        Assert.Single(GooglePlans.Password(An, "Lang-genoeg-1", true, fromAd).Errors);
        Assert.Single(GooglePlans.Password(An, "Lang-genoeg-1", true, fromEntra).Plan);

        // Google on its own, or a feed from a system the tenant does not have: nothing follows
        Assert.Null(GooglePlans.Source(TenantSystems.GoogleWorkspace, data, new SourceSettings { GoogleUsers = GoogleFeed.FromAd }).FollowsFrom);
        Assert.Empty(GooglePlans.Followed(GooglePlans.Source(systems, data, new SourceSettings { GoogleUsers = GoogleFeed.Itself })));

        // Signing out, revoking tokens and aliases are always in Google itself
        Assert.Equal(ChangeAction.GoogleSignOut, GooglePlans.SignOut(An).ChangeAction);
        Assert.NotNull(GooglePlans.RevokeTokens(An).Warning);
    }

    [Fact]
    public void ANewUserFollowsTheNamingRulesAndThePasswordStaysOutOfTheParameters()
    {
        var config = new OrganisationConfig();
        var request = new GoogleNewUserRequest { GivenName = "An", FamilyName = "Peeters", OrgUnitPath = "/Zorg", EmployeeId = "E-9" };
        var proposal = GooglePlans.NewUser(Data(), config, request, Domains, GoogleSource.Itself, "Eerste-wachtwoord-1");
        Assert.True(proposal.CanBeExecuted);
        Assert.Equal("an.peeters2@example.com", proposal.Email); // an.peeters is taken
        Assert.Single(proposal.Warnings);
        var create = Assert.Single(proposal.Plan);
        Assert.Equal((ChangeAction.GoogleCreateUser, "an.peeters2@example.com", "Eerste-wachtwoord-1"), (create.ChangeAction, create.Target, create.Secret));
        Assert.DoesNotContain(create.Parameters.Values, v => v.Contains("wachtwoord", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("/Zorg", "E-9", false), (create.P("orgUnitPath"), create.P("employeeId"), create.Parameters.ContainsKey("department")));

        Assert.False(GooglePlans.NewUser(Data(), config, new GoogleNewUserRequest { GivenName = "An" }, Domains, GoogleSource.Itself).CanBeExecuted);
        Assert.False(GooglePlans.NewUser(Data(), config, new GoogleNewUserRequest { GivenName = "An", FamilyName = "Vos", Domain = "fabrikam.com" }, Domains, GoogleSource.Itself).CanBeExecuted);
        var follows = GooglePlans.NewUser(Data(), config, new GoogleNewUserRequest { GivenName = "Bo", FamilyName = "Vos" }, Domains, new GoogleSource(Platform.EntraId, GoogleFeed.FromEntra, false));
        Assert.True(follows.CanBeExecuted);
        Assert.Contains(follows.Warnings, w => w.Contains("Entra ID", StringComparison.Ordinal));
        Assert.Equal(new[] { "example.com", "fabrikam.com" }, GooglePlans.Domains(new InventoryData { GoogleUsers = new[] { new GoogleUser { PrimaryEmail = "x@fabrikam.com" } } }, "Example.com"));
    }

    [Fact]
    public void AnAliasIsAnAddressOfTheDomainThatNobodyHas()
    {
        var change = Assert.Single(GooglePlans.AddAlias(Data(), An, " Peeters@Example.com ", Domains).Plan);
        Assert.Equal(("peeters@example.com", "g-an"), (change.P("alias"), change.Target));
        Assert.Single(GooglePlans.AddAlias(Data(), An, "geen adres", Domains).Errors);
        Assert.Single(GooglePlans.AddAlias(Data(), An, "an@fabrikam.com", Domains).Errors);
        Assert.Contains("team@example.com", Assert.Single(GooglePlans.AddAlias(Data(), An, "ploeg@example.com", Domains).Errors), StringComparison.Ordinal);
        Assert.Equal("an@example.com", GooglePlans.RemoveAlias(An, "an@example.com").P("alias"));
        Assert.Single(GooglePlans.Password(An, "kort", true, GoogleSource.Itself).Errors);
    }

    [Fact]
    public void TheHelpdeskMayHelpAGoogleUserButNotAnAdministrator()
    {
        var data = Data();
        var help = GooglePlans.Password(An, "Lang-genoeg-1", true, GoogleSource.Itself).Plan[0];
        Assert.Null(PermissionTable.Refusal(Role.Helpdesk, help, data));
        Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, GooglePlans.Edit(data, An, new GoogleUserDesired(Department: "Zorg"), GoogleSource.Itself).Plan[0], data));
        var admin = data.GoogleUsers.Single(u => u.IsAdmin);
        Assert.Contains("beheer@example.com", PermissionTable.Refusal(Role.Helpdesk, GooglePlans.SignOut(admin), data), StringComparison.Ordinal);
        Assert.Equal(Permission.GroupMembers, PermissionTable.ForAction(ChangeAction.GoogleAddGroupMember));
    }
}
