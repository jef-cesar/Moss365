using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.22: job profiles (R9): made from a person, compared, applied as a plan, versions, new user.</summary>
public sealed partial class PlanScreenTests
{
    private JobProfile ProfileOfAn()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "an.peeters");
        return JobProfilePlans.FromPerson(State.Data, State.Config, an, "Cultuur");
    }

    [Fact]
    public void AProfileFromAPersonLeavesOutWhatIsAutomaticOrPrivileged()
    {
        var p = ProfileOfAn();
        Assert.Equal("Cultuur", p.Name);
        Assert.Contains("gg_wifiIntern", p.Groups);
        Assert.Contains("DNST_CULTUUR", p.Groups);
        Assert.Contains("Gemeentehuis", p.Groups);
        Assert.DoesNotContain("Domain Admins", p.Groups);       // privileged
        Assert.DoesNotContain("Alle gebruikers", p.Groups);     // dynamic
        Assert.DoesNotContain("SG-SM-info-Full", p.Groups);     // the access group counts as the mailbox
        Assert.Contains("info@test.be", p.Mailboxes);
        Assert.Contains("boekhouding@test.be", p.Mailboxes);
        Assert.Equal("cultuur", p.Department);
    }

    [Fact]
    public void ApplyingAProfileGivesWhatIsMissingAlongTheRightRoute()
    {
        var profile = ProfileOfAn();
        profile.Groups.Add("Domain Admins");
        profile.Groups.Add("bestaat_niet");
        profile.Licences.Add("SPB");
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");

        var items = JobProfilePlans.Compare(State.Data, State.Config, profile, cas);
        Assert.Contains(items, i => i.Name == "gg_wifiIntern" && i.Status == ProfileStatus.Missing);
        Assert.Contains(items, i => i.Name == "Domain Admins" && i.Status == ProfileStatus.NotPossible);
        Assert.Contains(items, i => i.Name == "bestaat_niet" && i.Status == ProfileStatus.NotFound);
        Assert.Contains(items, i => i.Name == "Info" && i.Status == ProfileStatus.Missing);
        Assert.Contains(items, i => i.Name == "Boekhouding" && i.Status == ProfileStatus.NotPossible); // no access group yet
        Assert.Contains(items, i => i.Kind == ProfileItemKind.Attribute && i.Name == "Dienst: cultuur" && i.Status == ProfileStatus.Missing);

        var plan = JobProfilePlans.Apply(State.Data, State.Config, profile, cas, removeExtras: false).Plan;
        Assert.Contains(plan, c => c.ChangeAction == ChangeAction.AdAddGroupMember && c.P("groep") == "gg_wifiIntern");
        Assert.Contains(plan, c => c.ChangeAction is ChangeAction.ExchangeAddGroupMember or ChangeAction.GraphAddGroupMember && c.Description.Contains("SG-SM-info-Full", StringComparison.Ordinal));
        Assert.Contains(plan, c => c.ChangeAction == ChangeAction.AdSetAttributes && c.P("department") == "cultuur");
        var licence = Assert.Single(plan, c => c.ChangeAction == ChangeAction.GraphAssignLicence);
        Assert.Equal("SPB", licence.P("sku"));
        Assert.NotNull(licence.Warning); // no free SPB licences
        Assert.DoesNotContain(plan, c => c.Description.Contains("Domain Admins", StringComparison.Ordinal));
        Assert.All(plan, c => Assert.True(c.Selected));
    }

    [Fact]
    public void WhatIsMoreThanTheProfileIsOnlyProposed()
    {
        var profile = ProfileOfAn();
        var bo = State.Data.Persons.First(p => p.Ad?.SamAccountName == "bo.janssens");
        var items = JobProfilePlans.Compare(State.Data, State.Config, profile, bo);
        Assert.Contains(items, i => i.Name == "gg_eigen" && i.Status == ProfileStatus.Extra);

        Assert.DoesNotContain(JobProfilePlans.Apply(State.Data, State.Config, profile, bo, removeExtras: false).Plan, c => c.ChangeAction == ChangeAction.AdRemoveGroupMember);
        var remove = Assert.Single(JobProfilePlans.Apply(State.Data, State.Config, profile, bo, removeExtras: true).Plan,
            c => c.ChangeAction == ChangeAction.AdRemoveGroupMember && c.P("groep") == "gg_eigen");
        Assert.False(remove.Selected);
    }

    [Fact]
    public void ASavedProfileThatChangesGetsANewVersion()
    {
        var profile = ProfileOfAn();
        State.Config.JobProfiles.Add(profile);
        State.SaveConfig();
        Assert.Equal(1, profile.Version);

        State.SaveConfig();
        Assert.Equal(1, profile.Version);

        profile.Groups.Remove("Gemeentehuis");
        State.SaveConfig();
        Assert.Equal(2, profile.Version);

        State.LoadConfig();
        Assert.Equal(2, Assert.Single(State.Config.JobProfiles).Version);
    }

    [Fact]
    public void ANewUserWithAProfileGetsItsGroupsMailboxesAndLicences()
    {
        var profile = ProfileOfAn();
        profile.Licences.Add("DESKLESSPACK");
        profile.JobTitle = "Medewerker cultuur";
        State.Config.JobProfiles.Add(profile);

        var request = new NewUserRequest { Population = "Gemeente", GivenName = "Nora", Surname = "Nieuw", Office = "gemeentehuis", JobProfile = profile.Id, DefaultAdGroups = false };
        var proposal = PlanBuilder.NewUser(State.Data, State.Config, request);
        Assert.True(proposal.CanBeExecuted, string.Join(" ", proposal.Errors));
        var create = proposal.Now.First(c => c.ChangeAction == ChangeAction.AdCreateUser);
        Assert.Equal("cultuur", create.P("department"));
        Assert.Equal("Medewerker cultuur", create.P("title"));
        Assert.Contains(proposal.Now, c => c.ChangeAction == ChangeAction.AdAddGroupMember && c.P("groep") == "gg_wifiIntern");
        Assert.Contains(proposal.AfterSynchronisation, c => c.Description.Contains("DNST_CULTUUR", StringComparison.Ordinal));
        Assert.Contains(proposal.AfterSynchronisation, c => c.Description.Contains("SG-SM-info-Full", StringComparison.Ordinal));
        Assert.Contains(proposal.AfterSynchronisation, c => c.ChangeAction == ChangeAction.GraphAssignLicence && c.P("sku") == "DESKLESSPACK");
        Assert.Contains(proposal.Warnings, w => w.Contains("Boekhouding", StringComparison.Ordinal));
        Assert.Equal(string.Empty, request.Department); // the request itself is not changed
    }

    [Fact]
    public void ThePageAppliesAProfileAndRemembersWhoGotIt()
    {
        var profile = ProfileOfAn();
        State.Config.JobProfiles.Add(profile);
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"functieprofielen?profiel={profile.Id}&gebruiker={Uri.EscapeDataString(cas.Key)}");
        var cut = RenderComponent<JobProfilesPage>();

        Assert.Contains("cas vos tegenover Cultuur", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Ontbreekt", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Profiel toepassen", cut.Find("section.kb-plan").TextContent, StringComparison.Ordinal);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdAddGroupMember && w.P("groep") == "gg_wifiIntern"));
        cut.WaitForAssertion(() => Assert.Equal(profile.Id, State.ProfileAssignments()[cas.Key].ProfileId));
        Assert.Equal(1, State.ProfileAssignments()[cas.Key].Version);
    }

    [Fact]
    public void TheEditorMakesAProfileFromAUser()
    {
        var cut = RenderComponent<Organisation>(p => p.Add(x => x.Part, "functieprofielen"));
        Assert.Contains("Nog geen functieprofielen", cut.Markup, StringComparison.Ordinal);
        cut.Find("input[aria-label='Naam van het nieuwe profiel']").Input("Onthaal");
        Choose(cut, "Of neem over van een gebruiker (groepen, postvakken, licenties en gegevens)", "an", "an peeters");
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Profiel maken van", StringComparison.Ordinal)).Click();

        var profile = Assert.Single(State.Config.JobProfiles);
        Assert.Equal("Onthaal", profile.Name);
        Assert.Contains("gg_wifiIntern", profile.Groups);
        Assert.Contains("versie 1", cut.Find("#profiel-bewerken").TextContent, StringComparison.Ordinal);
        Assert.True(State.ConfigModifications > 0);

        // Taking a group out
        cut.Find("button[aria-label='gg_wifiIntern uit het profiel']").Click();
        Assert.DoesNotContain("gg_wifiIntern", profile.Groups);
    }
}
