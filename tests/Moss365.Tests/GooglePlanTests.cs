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

/// <summary>G2: the plans for Google groups, calendar resources and licences.</summary>
public sealed class GoogleGroupPlanTests
{
    private static readonly GoogleGroup Team = new()
    {
        Id = "gg-1", Email = "team@example.com", Name = "Team", Description = "Het team", WhoCanJoin = "INVITED_CAN_JOIN",
        WhoCanPost = "ALL_MEMBERS_CAN_POST", WhoCanViewMembership = "ALL_MEMBERS_CAN_VIEW", AllowExternalMembers = false,
    };

    private static readonly GoogleMember[] Members = { new("an@example.com", "OWNER", "USER"), new("bo@example.com", "MEMBER", "USER") };

    private static readonly string[] Domains = { "example.com" };

    private static InventoryData Data() => new()
    {
        GoogleUsers = new[] { new GoogleUser { Id = "g-an", PrimaryEmail = "an@example.com" }, new GoogleUser { Id = "g-cas", PrimaryEmail = "cas@example.com" } },
        GoogleGroups = new[] { Team },
        GoogleLicences = new[] { new GoogleLicence("an@example.com", "Google-Apps", "1010020028", "Google Workspace Business Standard") },
    };

    private static LogEntry Log(Change c) => new(DateTimeOffset.Now, "beheerder", "test", c.TargetSystem.ToString(), c.ChangeAction.ToString(), c.Target, c.TargetName,
        c.Description, c.Parameters, "Succeeded", string.Empty, Previous: c.Previous);

    [Fact]
    public void TheNameAndTheMembersOfAGroupThatFollowsAreChangedAtTheSource()
    {
        var edit = Assert.Single(GoogleGroupPlans.Edit(Team, "Ploeg", "Het team", null).Plan);
        Assert.Equal(("gg-1", "Ploeg", "Team", false), (edit.Target, edit.P("name"), edit.Previous!["name"], edit.Parameters.ContainsKey("description")));
        Assert.Single(GoogleGroupPlans.Edit(Team, " ", null, null).Errors);
        Assert.Single(GoogleGroupPlans.Edit(Team, "Ploeg", null, Platform.EntraId).Errors);
        Assert.Single(GoogleGroupPlans.AddMember(Data(), Team, "cas@example.com", "MEMBER", Members, Domains, Platform.ActiveDirectory).Errors);
        Assert.Single(GoogleGroupPlans.RemoveMember(Team, Members[1], Members, Platform.ActiveDirectory).Errors);
        Assert.Equal(Platform.ActiveDirectory, GoogleGroupPlans.FollowsFrom(TenantSystems.Both | TenantSystems.GoogleWorkspace, Data(), new SourceSettings { GoogleGroups = GoogleFeed.FromAd }));
    }

    [Fact]
    public void MembersComeInWithARoleAndGoOutWithItSoThatUndoPutsThemBack()
    {
        var add = Assert.Single(GoogleGroupPlans.AddMember(Data(), Team, " Cas@Example.com ", "MANAGER", Members, Domains, null).Plan);
        Assert.Equal((ChangeAction.GoogleAddGroupMember, "cas@example.com", "team@example.com", "MANAGER"), (add.ChangeAction, add.Target, add.P("groep"), add.P("rol")));
        Assert.Null(add.Warning);

        // Already in the group: the same role is refused, another role is a change of role with undo
        Assert.Single(GoogleGroupPlans.AddMember(Data(), Team, "bo@example.com", "MEMBER", Members, Domains, null).Errors);
        var role = Assert.Single(GoogleGroupPlans.AddMember(Data(), Team, "bo@example.com", "OWNER", Members, Domains, null).Plan);
        Assert.Equal((ChangeAction.GoogleSetGroupMemberRole, "OWNER", "MEMBER"), (role.ChangeAction, role.P("rol"), role.Previous!["rol"]));
        var back = Assert.Single(Undo.Plan(new[] { Log(role) }).Plan);
        Assert.Equal(("MEMBER", "team@example.com"), (back.P("rol"), back.P("groep")));

        // From outside: a warning, sharper when the group allows nobody from outside
        Assert.Contains("weigeren", GoogleGroupPlans.AddMember(Data(), Team, "x@fabrikam.com", "MEMBER", Members, Domains, null).Plan[0].Warning, StringComparison.Ordinal);
        Assert.Single(GoogleGroupPlans.AddMember(Data(), Team, "team@example.com", "MEMBER", Members, Domains, null).Errors);
        Assert.Single(GoogleGroupPlans.AddMember(Data(), Team, "geen adres", "MEMBER", Members, Domains, null).Errors);

        var remove = Assert.Single(GoogleGroupPlans.RemoveMember(Team, Members[0], Members, null).Plan);
        Assert.NotNull(remove.Warning); // the last owner
        var again = Assert.Single(Undo.Plan(new[] { Log(remove) }).Plan);
        Assert.Equal((ChangeAction.GoogleAddGroupMember, "an@example.com", "OWNER"), (again.ChangeAction, again.Target, again.P("rol")));

        // A member is helpdesk work; an owner or a manager is a change of the group itself
        Assert.Null(PermissionTable.Refusal(Role.Helpdesk, GoogleGroupPlans.AddMember(Data(), Team, "cas@example.com", "MEMBER", Members, Domains, null).Plan[0]));
        Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, add));
    }

    [Fact]
    public void TheSettingsOfAGroupNeedTheirPartAndOpeningItUpComesWithAWarning()
    {
        var on = new[] { Moss365.Core.Setup.GoogleParts.GroupSettings };
        Assert.Single(GoogleGroupPlans.Settings(Team, new GoogleGroupSettingsDesired("ANYONE_CAN_JOIN"), Array.Empty<string>()).Errors);
        Assert.Single(GoogleGroupPlans.Settings(Team with { WhoCanJoin = "" }, new GoogleGroupSettingsDesired("ANYONE_CAN_JOIN"), on).Errors);
        Assert.Single(GoogleGroupPlans.Settings(Team, new GoogleGroupSettingsDesired("IEDEREEN"), on).Errors);
        var open = Assert.Single(GoogleGroupPlans.Settings(Team, new GoogleGroupSettingsDesired("ANYONE_CAN_JOIN", "ALL_MEMBERS_CAN_POST", null, true), on).Plan);
        Assert.Equal(("team@example.com", "ANYONE_CAN_JOIN", "True", false), (open.Target, open.P("whoCanJoin"), open.P("allowExternalMembers"), open.Parameters.ContainsKey("whoCanPostMessage")));
        Assert.Equal(("INVITED_CAN_JOIN", "False"), (open.Previous!["whoCanJoin"], open.Previous["allowExternalMembers"]));
        Assert.NotNull(open.Warning);
        Assert.Null(Assert.Single(GoogleGroupPlans.Settings(Team, new GoogleGroupSettingsDesired(WhoCanPost: "ALL_MANAGERS_CAN_POST"), on).Plan).Warning);
        Assert.Equal("Alleen wie uitgenodigd is", GoogleGroupPlans.Name("INVITED_CAN_JOIN"));
    }

    [Fact]
    public void AResourceAndALicenceAreChangedWithWhatWasBefore()
    {
        var room = new GoogleResource { Id = "r-1", Name = "Zaal 1", Capacity = 8, Floor = "1" };
        var edit = Assert.Single(GoogleGroupPlans.EditResource(room, new GoogleResourceDesired("Zaal 1", 12, "2", "")).Plan);
        Assert.Equal(("12", "8", "2", false), (edit.P("capacity"), edit.Previous!["capacity"], edit.P("floorName"), edit.Parameters.ContainsKey("resourceName")));
        Assert.Equal("", Assert.Single(GoogleGroupPlans.EditResource(room, new GoogleResourceDesired(ClearCapacity: true)).Plan).P("capacity"));
        Assert.Single(GoogleGroupPlans.EditResource(room, new GoogleResourceDesired(Capacity: -1)).Errors);
        Assert.Empty(GoogleGroupPlans.EditResource(room, new GoogleResourceDesired("Zaal 1", 8)).Plan);

        var on = new[] { Moss365.Core.Setup.GoogleParts.Licences };
        var cas = Data().GoogleUsers[1];
        Assert.Single(GoogleGroupPlans.AssignLicence(Data(), cas, "Google-Apps", "1010020028", Array.Empty<string>()).Errors);
        Assert.Single(GoogleGroupPlans.AssignLicence(Data(), cas, "Google-Apps", "1010020099", on).Errors);
        Assert.Single(GoogleGroupPlans.AssignLicence(Data(), Data().GoogleUsers[0], "Google-Apps", "1010020028", on).Errors);
        var assign = Assert.Single(GoogleGroupPlans.AssignLicence(Data(), cas, "Google-Apps", "1010020028", on).Plan);
        Assert.Equal(("cas@example.com", "Google Workspace Business Standard"), (assign.Target, assign.P("sku")));
        var remove = GoogleGroupPlans.RemoveLicence(cas, Data().GoogleLicences[0]);
        Assert.NotNull(remove.Warning);
        Assert.Equal(ChangeAction.GoogleAssignLicence, Assert.Single(Undo.Plan(new[] { Log(remove) }).Plan).ChangeAction);
    }
}
