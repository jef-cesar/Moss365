using Bunit;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Security;
using Moss365.UI.Components;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16: a compromised account is blocked in one plan; nothing is deleted.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void ACompromisedAccountIsBlockedInOnePlan()
    {
        EntraFilled();
        var person = State.Data.Persons.First(p => p.Ad is { Enabled: true } && p.Entra is not null);
        var outcome = CompromisedAccount.Plan(State.Data, person, "Tijdelijk-Wachtwoord-123!");

        Assert.Empty(outcome.Errors);
        Assert.Contains(outcome.Plan, c => c.ChangeAction == ChangeAction.AdDisableAccount && c.Selected);
        var password = Assert.Single(outcome.Plan, c => c.ChangeAction == ChangeAction.AdSetPassword);
        Assert.Equal("Tijdelijk-Wachtwoord-123!", password.Secret);
        Assert.Equal("True", password.Parameters["wijzigenBijAanmelding"]);
        Assert.Contains(outcome.Plan, c => c.ChangeAction == ChangeAction.GraphRevokeSessions && c.Target == person.Entra!.Id);
        Assert.Equal(State.Data.AdGroupsOf(person).Count(MirrorUser.IsPrivileged), outcome.Plan.Count(c => c.ChangeAction == ChangeAction.AdRemoveGroupMember));

        // Nothing is deleted, and a synchronised account is not blocked in Entra itself (it follows AD)
        Assert.DoesNotContain(outcome.Plan, c => c.ChangeAction is ChangeAction.GraphRemoveGuest);
        if (person.Entra!.OnPremisesSyncEnabled)
        {
            Assert.DoesNotContain(outcome.Plan, c => c.ChangeAction == ChangeAction.GraphSetAttributes);
        }

        // A cloud account is blocked in Entra, with the state before for undo
        var cloud = State.Data.Persons.FirstOrDefault(p => p.Ad is null && p.Entra is { OnPremisesSyncEnabled: false, AccountEnabled: true } && !p.IsGuest);
        if (cloud is not null)
        {
            var block = Assert.Single(CompromisedAccount.Plan(State.Data, cloud, "Tijdelijk-Wachtwoord-123!").Plan, c => c.ChangeAction == ChangeAction.GraphSetAttributes);
            Assert.Equal("false", block.Parameters["accountEnabled"]);
            Assert.Equal("true", block.Previous!["accountEnabled"]);
        }
    }

    [Fact]
    public void TheCardShowsThePlanAndWhatToCheckAfterwards()
    {
        EntraFilled();
        var person = State.Data.Persons.First(p => p.Ad is { Enabled: true } && p.Entra is not null);
        var cut = RenderComponent<CompromisedCard>(p => p.Add(c => c.Person, person));

        cut.FindAll("button").First(b => b.TextContent == "Blokkeerplan maken").Click();
        Assert.Contains("Kijk daarna na", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Alle aanmeldsessies intrekken", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_factory.Executed); // only after confirming in the plan
    }
}
