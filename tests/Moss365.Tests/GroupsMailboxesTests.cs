using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

/// <summary>Index across AD, Entra and Exchange, and the plans for groups and shared mailboxes.</summary>
public sealed class GroupsMailboxesTests
{
    private readonly InventoryData _d = TestData.AppEnvironment();

    private ObjectIndex Index => _d.Index;

    private Group G(string nameOrAddress) => Index.Group(Index.Find(nameOrAddress)!.Key)!;

    private ObjectRef Person(string sam) => Index.Ref(_d.SearchPerson("ad:" + sam)!);

    // ---------------------------------------------------------------- index

    [Fact]
    public void SynchronisedGroupIsOneGroupWithTwoSides()
    {
        var g = G("gg_rww_foxbeleid");
        Assert.NotNull(g.Ad);
        Assert.NotNull(g.Cloud);
        Assert.Equal("AD + Entra", g.Layers);
        Assert.Equal(WriteRoute.ActiveDirectory, g.Route);
        Assert.Same(g, G("g-ad"));
    }

    [Fact]
    public void ExchangeGroupIsLinkedByAddress()
    {
        var g = G("gemeentehuis@test.be");
        Assert.NotNull(g.Cloud);
        Assert.NotNull(g.Exo);
        Assert.Equal(WriteRoute.Exchange, g.Route);
        Assert.Equal("Room list", G("rl-gh").KindText);
    }

    [Fact]
    public void RecognisesAddressesDnsAndIds()
    {
        Assert.Equal(ObjectKind.Mailbox, Index.Find("info@test.be")!.Kind);
        Assert.Equal(ObjectKind.Room, Index.Find("rm-vz-a@test.be")!.Kind);
        Assert.Equal(ObjectKind.User, Index.Find(TestData.Dn("cas.vos", TestData.SocialWelfare))!.Kind);
        Assert.Equal(ObjectKind.User, Index.Find("TEST\\an.peeters")!.Kind);
        Assert.Equal(ObjectKind.Group, Index.Find("g-team")!.Kind);
        Assert.Null(Index.Find("bestaat.niet@test.be"));
        Assert.Equal(ObjectKind.Unknown, Index.Ref("bestaat.niet@test.be").Kind);
    }

    [Fact]
    public void MembersPerLevelAndNestedGroups()
    {
        var own = G("gg_eigen");
        var members = Index.MembersOf(own);
        Assert.Contains(members, l => l.Layer == "AD" && l.Object.Kind == ObjectKind.User && l.Object.Key == "ad:bo.janssens");
        Assert.Contains(members, l => l.Layer == "AD" && l.Object.Kind == ObjectKind.Group && l.Object.Name == "Gesynchroniseerd");

        var parents = Index.MemberOf(G("gg_rww_foxbeleid"));
        Assert.Contains(parents, o => o.Layer == "AD" && o.Object.Name == "gg_eigen");

        // An is in the SG-SM group in both Entra and Exchange: once, with both layers
        var an = Assert.Single(Index.UniqueMembersOf(G("g-sm")), l => l.Object.Key == "ad:an.peeters");
        Assert.Equal("Entra, Exchange", an.Layer);

        var room = Index.MembersOf(G("rl-gh"));
        Assert.Equal(ObjectKind.Room, Assert.Single(room).Object.Kind);
        Assert.Contains(Index.GroupsOfAddress("rm-vz-a@test.be"), r => r.Object.Name == "rl-gh");
    }

    [Fact]
    public void PermissionsDirectOrViaGroup()
    {
        var info = Index.Mailbox("info@test.be")!;
        var permissions = Index.PermissionsOn(info);
        Assert.Equal(3, permissions.Count);
        Assert.Contains(permissions, r => r.Permission == "FullAccess" && !r.Direct && r.Who.Name == "SG-SM-info-Full");
        Assert.Contains(permissions, r => r.Permission == "SendAs" && r.Direct && r.Who.Kind == ObjectKind.User);
        Assert.Contains(permissions, r => r.Who.Kind == ObjectKind.Unknown);
        Assert.Equal("SG-SM-info-Full", Index.AccessGroupOf(info)!.Name);

        // "FullAccess,SendAs" in one row becomes two permissions
        Assert.Equal(2, Index.PermissionsOn(Index.Mailbox("boekhouding@test.be")!).Count);
        Assert.Null(Index.AccessGroupOf(Index.Mailbox("boekhouding@test.be")!));

        // An has access to info via the group and directly to boekhouding
        var an = Index.MailboxPermissionsOf(_d.SearchPerson("ad:an.peeters")!);
        Assert.Contains(an, r => r.Mailbox.Smtp == "info@test.be" && r.ViaGroup?.Name == "SG-SM-info-Full");
        Assert.Contains(an, r => r.Mailbox.Smtp == "boekhouding@test.be" && r.ViaGroup is null);
    }

    [Fact]
    public void IndexBelongsToTheData()
    {
        var copy = _d with { Mailboxes = Array.Empty<Mailbox>() };
        Assert.NotNull(_d.Index.Mailbox("info@test.be"));
        Assert.Null(copy.Index.Mailbox("info@test.be"));
    }

    // ---------------------------------------------------------------- groups

    [Fact]
    public void AddMemberChoosesTheRightWay()
    {
        var cas = Person("cas.vos");

        var ad = Assert.Single(GroupPlans.AddMember(_d, G("gg_rww_foxbeleid"), cas).Plan);
        Assert.Equal(ChangeAction.AdAddGroupMember, ad.ChangeAction);
        Assert.Equal(TestData.Dn("cas.vos", TestData.SocialWelfare), ad.Target);

        var graph = Assert.Single(GroupPlans.AddMember(_d, G("g-team"), cas).Plan);
        Assert.Equal(ChangeAction.GraphAddGroupMember, graph.ChangeAction);
        Assert.Equal("e-cas", graph.Target);

        var exo = Assert.Single(GroupPlans.AddMember(_d, G("gemeentehuis@test.be"), Index.Ref("info@test.be")).Plan);
        Assert.Equal(ChangeAction.ExchangeAddGroupMember, exo.ChangeAction);
        Assert.Equal("info@test.be", exo.Target);
        Assert.Equal("gemeentehuis@test.be", exo.P("groep"));
    }

    [Fact]
    public void NotDynamicOrPrivilegedGroups()
    {
        Assert.Contains("dynamische", Assert.Single(GroupPlans.AddMember(_d, G("g-alle"), Person("cas.vos")).Errors));
        Assert.Contains("bevoorrecht", Assert.Single(GroupPlans.AddMember(_d, G("Domain Admins"), Person("cas.vos")).Errors));

        // A mailbox has no AD object
        Assert.Contains("geen AD-object", Assert.Single(GroupPlans.AddMember(_d, G("gg_eigen"), Index.Ref("info@test.be")).Errors));
    }

    [Fact]
    public void AttributesPerRoute()
    {
        var ad = Assert.Single(GroupPlans.Attributes(G("gg_eigen"), new GroupDesired { Description = "Eigen groep" }).Plan);
        Assert.Equal(ChangeAction.AdSetAttributes, ad.ChangeAction);
        Assert.Equal("Eigen groep", ad.P("description"));

        var cloud = Assert.Single(GroupPlans.Attributes(G("g-team"), new GroupDesired { DisplayName = "DNST_Cultuur" }).Plan);
        Assert.Equal(ChangeAction.GraphSetGroupAttributes, cloud.ChangeAction);
        Assert.Equal("g-team", cloud.Target);

        var exo = GroupPlans.Attributes(G("gemeentehuis@test.be"), new GroupDesired { DisplayName = "Gemeentehuis (alle)", Description = "x" });
        Assert.Equal(ChangeAction.ExchangeUpdateGroup, Assert.Single(exo.Plan).ChangeAction);
        Assert.Contains("omschrijving", Assert.Single(exo.Errors));

        // Nothing changed = no plan
        Assert.Empty(GroupPlans.Attributes(G("g-team"), new GroupDesired { DisplayName = "DNST_CULTUUR" }).Plan);
    }

    [Fact]
    public void AdministratorsPerRoute()
    {
        var bo = Person("bo.janssens");
        Assert.Equal("managedBy", Assert.Single(Assert.Single(GroupPlans.Administrator(_d, G("gg_eigen"), bo, true).Plan).Parameters).Key);
        Assert.Equal(ChangeAction.GraphAddOwner, Assert.Single(GroupPlans.Administrator(_d, G("g-team"), bo, true).Plan).ChangeAction);
        var exo = Assert.Single(GroupPlans.Administrator(_d, G("gemeentehuis@test.be"), bo, false).Plan);
        Assert.Equal(ChangeAction.ExchangeGroupManager, exo.ChangeAction);
        Assert.Equal("bo.janssens@test.be", exo.P("Verwijderen"));
    }

    // ---------------------------------------------------------------- postvakken

    [Fact]
    public void DirectPermissionsGoToTheGroup()
    {
        var u = MailboxPlans.CleanupDirectPermissions(_d, Index.Mailbox("info@test.be")!);
        Assert.Empty(u.Errors);

        // The group already has FullAccess; SendAs is still missing
        var groupPermission = Assert.Single(u.Plan, w => w.ChangeAction == ChangeAction.ExchangeAddMailboxPermission);
        Assert.Equal("SendAs", groupPermission.P("Recht"));
        Assert.Equal("sg-sm-info-full@test.be", groupPermission.P("Trustee"));

        // Bo becomes a member, then loses the direct SendAs
        var member = Assert.Single(u.Plan, w => w.ChangeAction == ChangeAction.ExchangeAddGroupMember);
        Assert.Equal("bo.janssens@test.be", member.Target);
        var route = u.Plan.Where(w => w.ChangeAction == ChangeAction.ExchangeRemoveMailboxPermission).ToList();
        var boRoute = Assert.Single(route, w => w.P("Trustee") == "bo.janssens@test.be");
        Assert.Equal(member.Id, boRoute.DependentOf);
        Assert.True(boRoute.Selected);

        // An unknown trustee is listed, but not ticked
        Assert.False(Assert.Single(route, w => w.P("Trustee").StartsWith("S-1-5")).Selected);
    }

    [Fact]
    public void WithoutAccessGroupCreateFirst()
    {
        var accounting = Index.Mailbox("boekhouding@test.be")!;
        Assert.Contains("SG-SM-boekhouding-Full", Assert.Single(MailboxPlans.CleanupDirectPermissions(_d, accounting).Errors));

        var plan = MailboxPlans.CreateAccessGroup(_d, accounting).Plan;
        var groupValue = plan[0];
        Assert.Equal(ChangeAction.ExchangeCreateGroup, groupValue.ChangeAction);
        Assert.Equal("sg-sm-boekhouding-full@test.be", groupValue.Target);
        Assert.Equal(new[] { "FullAccess", "SendAs" }, plan.Skip(1).Select(w => w.P("Recht")));
        Assert.All(plan.Skip(1), w => Assert.Equal(groupValue.Id, w.DependentOf));

        Assert.Contains("bestaat al", Assert.Single(MailboxPlans.CreateAccessGroup(_d, Index.Mailbox("info@test.be")!).Errors));
    }

    [Fact]
    public void NewMailboxIsAChain()
    {
        var a = new NewMailboxRequest { DisplayName = "Jeugddienst", LocalPart = "Jeugddienst", Domain = "test.be", Members = { Person("cas.vos") } };
        var u = MailboxPlans.NewMailbox(_d, a);
        Assert.Empty(u.Errors);
        Assert.Equal(
            new[] { ChangeAction.ExchangeCreateSharedMailbox, ChangeAction.ExchangeCreateGroup, ChangeAction.ExchangeAddMailboxPermission, ChangeAction.ExchangeAddMailboxPermission, ChangeAction.ExchangeAddGroupMember },
            u.Plan.Select(w => w.ChangeAction));
        Assert.Equal("jeugddienst@test.be", u.Plan[0].Target);
        Assert.Equal("SG-SM-jeugddienst-Full", u.Plan[1].TargetName);
        Assert.Equal(u.Plan[0].Id, u.Plan[1].DependentOf);
        Assert.All(u.Plan.Skip(2), w => Assert.Equal(u.Plan[1].Id, w.DependentOf));

        Assert.Contains("al in gebruik", Assert.Single(MailboxPlans.NewMailbox(_d, new NewMailboxRequest { DisplayName = "x", LocalPart = "info", Domain = "test.be" }).Errors));
        Assert.NotEmpty(MailboxPlans.NewMailbox(_d, new NewMailboxRequest { DisplayName = "x", LocalPart = "met spatie", Domain = "test.be" }).Errors);
    }

    [Fact]
    public void SettingsOnlyWhatDiffers()
    {
        var info = Index.Mailbox("info@test.be")!;
        Assert.Empty(MailboxPlans.Settings(info, new MailboxDesired { DisplayName = "Info", Hidden = false }).Plan);

        var w = Assert.Single(MailboxPlans.Settings(info, new MailboxDesired { Hidden = true, ForwardingTo = "iemand@fabrikam.com" }).Plan);
        Assert.Equal("True", w.P("HiddenFromAddressListsEnabled"));
        Assert.Equal("iemand@fabrikam.com", w.P("ForwardingSmtpAddress"));
        Assert.NotNull(w.Warning);

        var auto = MailboxPlans.Settings(info, new MailboxDesired { AutoReply = "Enabled", AutoReplyInternal = "Afwezig" }).Plan;
        Assert.Equal("Afwezig", Assert.Single(auto).P("InternalMessage"));
        Assert.NotEmpty(MailboxPlans.Settings(info, new MailboxDesired { AutoReply = "Enabled" }).Errors);
        Assert.NotEmpty(MailboxPlans.Settings(info, new MailboxDesired { ForwardingTo = "geen-adres" }).Errors);
    }
}
