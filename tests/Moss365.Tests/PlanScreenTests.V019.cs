using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Storage;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.19: editing groups and mailboxes from the user page, the systems of a tenant, the status bar.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void OnTheUserPageGroupsAndMailboxesAreAdded()
    {
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));

        // Add an AD group and access to Info (through its access group SG-SM-info-Full)
        cut.Find("input[aria-label='Groep toevoegen']").Change("gg_wifiIntern");
        cut.FindAll("#lidmaatschappen button").First(b => b.TextContent == "Erbij").Click();
        cut.Find("select[aria-label='Toegang geven tot postvak']").Change("info@test.be");
        cut.FindAll("#lidmaatschappen button").Last(b => b.TextContent == "Erbij").Click();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();

        var plan = cut.Find("#lidmaatschappen section.kb-plan").TextContent;
        Assert.Contains("gg_wifiIntern", plan, StringComparison.Ordinal);
        Assert.Contains("SG-SM-info-Full", plan, StringComparison.Ordinal);
        Assert.Empty(_factory.Executed);

        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdAddGroupMember && w.P("groep") == "gg_wifiIntern"));
        Assert.Contains(_factory.Executed, w => w.ChangeAction is ChangeAction.ExchangeAddGroupMember or ChangeAction.GraphAddGroupMember);
    }

    [Fact]
    public void OnTheUserPageGroupsAndMailboxesAreRemoved()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "an.peeters");
        var group = State.Data.Index.GroupsOf(an).First(g => !g.Dynamic && g.Ad is not null);
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, an.Key));
        cut.Find($"input[aria-label='{group.Name} weghalen']").Change(true);
        cut.Find("input[aria-label='Toegang tot Info weghalen']").Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdRemoveGroupMember));
        Assert.Contains(_factory.Executed, w => w.ChangeAction is ChangeAction.ExchangeRemoveGroupMember or ChangeAction.GraphRemoveGroupMember);

        // A dynamic group cannot be ticked
        Assert.All(cut.FindAll("#lidmaatschappen input[type=checkbox][disabled]"), x => Assert.Contains("weghalen", x.GetAttribute("aria-label"), StringComparison.Ordinal));
    }

    [Fact]
    public void MailboxAccessForOnePerson()
    {
        var d = State.Data;
        var bo = d.Persons.First(p => p.Ad?.SamAccountName == "bo.janssens");
        var info = d.Index.Mailbox("info@test.be")!;
        var boekhouding = d.Index.Mailbox("boekhouding@test.be")!;

        // Direct SendAs: removed as a permission
        var remove = MailboxPlans.RemoveAccess(d, info, bo);
        var change = Assert.Single(remove.Plan);
        Assert.Equal(ChangeAction.ExchangeRemoveMailboxPermission, change.ChangeAction);
        Assert.Equal(("SendAs", "bo.janssens@test.be"), (change.P("Recht"), change.P("Trustee")));

        // Already access / no access group / no access at all
        Assert.NotEmpty(MailboxPlans.GiveAccess(d, info, bo).Errors);
        Assert.Contains("toegangsgroep", Assert.Single(MailboxPlans.GiveAccess(d, boekhouding, bo).Errors), StringComparison.Ordinal);
        Assert.NotEmpty(MailboxPlans.RemoveAccess(d, boekhouding, bo).Errors);

        // Through the access group: out of the group
        var an = d.Persons.First(p => p.Ad?.SamAccountName == "an.peeters");
        var viaGroup = MailboxPlans.RemoveAccess(d, info, an);
        Assert.Contains(viaGroup.Plan, c => c.ChangeAction is ChangeAction.ExchangeRemoveGroupMember or ChangeAction.GraphRemoveGroupMember);
    }

    [Fact]
    public void ATenantWithOnlyAdOrOnlyMicrosoft365LeavesTheOtherOut()
    {
        var (hub, factories, _) = Hub("systemen");
        using var _ = hub;
        var t = hub.CurrentTenant;
        Assert.Equal(TenantSystems.Both, t.Systems);

        hub.Update(t with { Systems = TenantSystems.ActiveDirectory });
        var s = hub.Current;
        s.Settings.TenantId = "tenant";
        s.Settings.ClientId = "client";
        Assert.True(s.HasAd);
        Assert.False(s.HasMicrosoft365);
        Assert.False(s.EffectiveSettings.GraphFilled);
        Assert.True(s.EffectiveSettings.AdFilled);
        Assert.Contains(TargetSystem.Graph, s.MissingConnections(new[] { new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphSetAttributes, Target = "x", Description = "x" } }));

        // Only the AD read runs, no Exchange
        Assert.True(s.StartLoading(LoadKind.Everything));
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        Thread.Sleep(100);
        Assert.Equal("Live: AD", s.SnapshotList()[^1].Source);

        // The menu and the pages
        Assert.False(Access.FitsTenant("postvakken", s.HasAd, s.HasMicrosoft365));
        Assert.False(Access.FitsTenant("conditional-access", s.HasAd, s.HasMicrosoft365));
        Assert.False(Access.FitsTenant("vergelijk", s.HasAd, s.HasMicrosoft365));
        Assert.True(Access.FitsTenant("gebruikers", s.HasAd, s.HasMicrosoft365));
        Assert.True(Access.FitsTenant("postvakken", hasAd: false, hasMicrosoft365: true));
        Assert.False(Access.FitsTenant("vergelijk", hasAd: false, hasMicrosoft365: true));

        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Microsoft365 });
        Assert.False(s.HasAd);
        Assert.False(s.EffectiveSettings.AdFilled);
        Assert.True(s.EffectiveSettings.GraphFilled);
        Assert.Equal(TenantSystems.Microsoft365, hub.Tenants.Single(x => x.Id == t.Id).Systems);
    }

    [Fact]
    public void TheStatusBarShowsVersionDataAndGitHub()
    {
        var bar = RenderComponent<StatusBar>();
        Assert.Contains("Moss365 " + ManagementState.AppVersion, bar.Markup, StringComparison.Ordinal);
        Assert.Contains("Inventaris van ", bar.Markup, StringComparison.Ordinal);
        Assert.Matches("online|offline", bar.Find(".kb-online, .kb-offline").TextContent);
        Assert.Equal("https://github.com/jef-cesar/Moss365", bar.Find("a").GetAttribute("href"));
    }
}
