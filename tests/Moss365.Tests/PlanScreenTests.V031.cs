using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.31: new users in the cloud, and guests in groups.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void ACloudUserGetsLicencesAndCloudGroupsAtOnceAndMailGroupsLater()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "an.peeters");
        var profile = JobProfilePlans.FromPerson(State.Data, State.Config, an, "Cultuur");
        profile.Licences.Add("DESKLESSPACK");
        State.Config.JobProfiles.Add(profile);
        var request = new NewUserRequest { GivenName = "Nora", Surname = "Wolk", JobProfile = profile.Id, InCloud = true, Domain = "test.be" };

        var p = CloudUserPlans.NewUser(State.Data, State.Config, request, "Geheim-123!");
        Assert.True(p.CanBeExecuted, string.Join(" ", p.Errors));
        Assert.Equal("nora.wolk@test.be", p.Upn);
        var create = p.Now[0];
        Assert.Equal(ChangeAction.GraphCreateUser, create.ChangeAction);
        Assert.Equal("Geheim-123!", create.Secret);
        Assert.DoesNotContain(create.Parameters.Values, v => v.Contains("Geheim", StringComparison.Ordinal));
        Assert.Equal("nora.wolk", create.P("mailNickname"));
        Assert.Equal("cultuur", create.P("department"));
        Assert.Contains(p.Now, c => c.ChangeAction == ChangeAction.GraphAssignLicence && c.P("sku") == "DESKLESSPACK" && c.DependentOf == create.Id);
        Assert.Contains(p.Now, c => c.ChangeAction == ChangeAction.GraphAddGroupMember && c.P("groep") == "DNST_CULTUUR");
        Assert.Contains(p.AfterSynchronisation, c => c.ChangeAction == ChangeAction.ExchangeAddGroupMember && c.Description.Contains("SG-SM-info-Full", StringComparison.Ordinal));
        Assert.Contains(p.Warnings, w => w.Contains("gg_wifiIntern", StringComparison.Ordinal) && w.Contains("AD-groep", StringComparison.Ordinal));
        Assert.DoesNotContain(p.Now.Concat(p.AfterSynchronisation), c => c.TargetSystem == TargetSystem.ActiveDirectory);

        // An existing name gets a number
        var again = CloudUserPlans.NewUser(State.Data, State.Config, new NewUserRequest { GivenName = "An", Surname = "Peeters", InCloud = true, Domain = "test.be" });
        Assert.Equal("an.peeters2@test.be", again.Upn);
    }

    [Fact]
    public void ATenantWithoutAdCreatesTheUserInTheCloud()
    {
        var (hub, factories, _) = Hub("cloud-nieuw");
        using var __ = hub;
        var s = hub.Current;
        s.Settings.TenantId = "tenant";
        s.Settings.ClientId = "client";
        Assert.True(s.StartLoading(LoadKind.Everything));
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Microsoft365 });

        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(hub);
        ctx.Services.AddSingleton(s);
        ctx.Services.AddSingleton(s.AppEnvironment);
        var cut = ctx.RenderComponent<NewUser>();
        Assert.Contains("rechtstreeks in Entra", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Logonscript", cut.Markup, StringComparison.Ordinal);
        Field(cut, "Voornaam").Change("Nora");
        Field(cut, "Achternaam").Change("Wolk");
        cut.FindAll("button").First(b => b.TextContent == "Voorbeeld").Click();
        Assert.Contains("Entra (cloudaccount)", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("sAMAccountName", cut.Markup, StringComparison.Ordinal);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(factories[hub.CurrentTenant.Id].Executed, w => w.ChangeAction == ChangeAction.GraphCreateUser));
        cut.WaitForAssertion(() => Assert.Contains("Startwachtwoord", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void GuestsGoIntoCloudGroups()
    {
        var gast = State.Data.Persons.First(p => p.IsGuest);
        var team = State.Data.Index.Groups.First(g => g.Name == "DNST_CULTUUR");
        var plan = Assert.Single(GroupPlans.AddMember(State.Data, team, State.Data.Index.Ref(gast)).Plan);
        Assert.Equal(ChangeAction.GraphAddGroupMember, plan.ChangeAction);
        Assert.Equal("e-gast", plan.Target);

        // An AD group cannot hold a guest
        var ad = State.Data.Index.Groups.First(g => g.Ad?.SamAccountName == "gg_eigen");
        Assert.NotEmpty(GroupPlans.AddMember(State.Data, ad, State.Data.Index.Ref(gast)).Errors);

        // On the page of the group the picker finds guests
        var page = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, team.Key));
        page.Find("input[aria-label='Lid toevoegen']").Input("gast");
        Assert.Contains(page.FindAll("button.kb-link"), b => b.TextContent == "Gast");

        // On the page of the guest: the groups can be edited, only cloud groups to choose from
        var detail = RenderComponent<UserDetail>(p => p.Add(x => x.Key, gast.Key));
        Assert.Contains("Groepen en postvakken bewerken", detail.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(detail.FindAll("datalist option"), o => o.GetAttribute("value") == "gg_eigen");
    }

    [Fact]
    public void AnInvitedGuestGoesIntoItsGroupsInTheSamePlan()
    {
        var team = State.Data.Index.Groups.First(g => g.Name == "DNST_CULTUUR");
        var mail = State.Data.Index.Groups.First(g => g.Name == "Gemeentehuis");
        var outcome = GuestPlans.Invite(State.Data, "nieuw@example.com", "Nieuwe Gast", false, new[] { team, mail });
        Assert.Equal(2, outcome.Plan.Count);
        var member = outcome.Plan[1];
        Assert.Equal(ChangeAction.GraphAddGroupMember, member.ChangeAction);
        Assert.Equal("nieuw@example.com", member.Target);
        Assert.Equal(outcome.Plan[0].Id, member.DependentOf);
        Assert.Contains(outcome.Errors, e => e.Contains("mailgroep", StringComparison.Ordinal));

        // In bulk from the guest list
        var cut = RenderComponent<Guests>();
        cut.FindAll("tbody input[type=checkbox]").First().Click();
        cut.Find("input[aria-label^='Gekozen gasten (1) in een groep']").Input("DNST");
        cut.FindAll("button.kb-link").First(b => b.TextContent == "DNST_CULTUUR").Click();
        Assert.Contains("Lid maken van cloudgroep DNST_CULTUUR", cut.Markup, StringComparison.Ordinal);
    }
}
