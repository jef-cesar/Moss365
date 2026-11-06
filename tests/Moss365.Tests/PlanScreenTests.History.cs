using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Permissions;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R4: the organisation over time in the app: the past is read only, KPIs come back from snapshots, restoring is a plan.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheInventoryAndALiveReadBecomeSnapshots()
    {
        // The inventory folder of the test data became the first snapshot, at the time in its name
        var first = Assert.Single(State.SnapshotList());
        Assert.Equal(new DateTime(2026, 10, 16, 13, 43, 0), first.Time.LocalDateTime);

        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        var list = State.SnapshotList();
        Assert.Equal(2, list.Count);
        Assert.StartsWith("Live: AD, Entra", list[^1].Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePastIsReadOnly()
    {
        var now = State.Data;
        Assert.Equal("Er is geen momentopname van voor die datum.", State.ShowAsOf(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(State.IsHistoric);

        Assert.Null(State.ShowAsOf(DateTimeOffset.Now));
        Assert.True(State.IsHistoric);
        Assert.NotSame(now, State.Data);
        Assert.Equal(now.AdUsers.Count, State.Data.AdUsers.Count);
        Assert.False(State.Can(Permission.Helpdesk));
        Assert.False(State.Can(Permission.Manage));
        Assert.False(State.StartLoading(LoadKind.AdAndEntra));
        var bo = State.Data.Persons.First(p => p.Ad?.SamAccountName == "bo.janssens");
        Assert.NotEmpty(State.RestorePlan(bo, State.Timeline(bo).First(t => t.Version is not null).Version!).Errors);

        // The page says what is shown and goes back
        var cut = RenderComponent<OrganisationHistory>();
        Assert.Contains("alleen lezen", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Terug naar nu").Click();
        Assert.False(State.IsHistoric);
        Assert.Same(now, State.Data);
        Assert.True(State.Can(Permission.Helpdesk));
    }

    [Fact]
    public void KpisComeBackFromTheSnapshots()
    {
        Assert.Equal(1, State.RecomputeKpis());
        var values = State.Database.ReadKpiSnapshot(new DateOnly(2026, 10, 1));
        Assert.Contains(values, v => v.Value.Any(p => p.Date == new DateOnly(2026, 10, 16)));
    }

    [Fact]
    public void RestoringAVersionIsAPlan()
    {
        var bo = State.Data.Persons.First(p => p.Ad?.SamAccountName == "bo.janssens");
        var timeline = State.Timeline(bo);
        var version = Assert.Single(timeline, t => t.What == "AD-account").Version!;
        Assert.Equal("eerste momentopname", Assert.Single(timeline, t => t.What == "AD-account").Details);

        // Exactly as it is now: nothing to do
        Assert.Equal("Er is niets terug te zetten: alles is zoals in die versie.", Assert.Single(State.RestorePlan(bo, version).Errors));

        // A version with another department and without one of its groups: the plan puts both back
        var content = version.Content.ToDictionary(k => k.Key, k => k.Value);
        content["Department"] = "bibliotheek";
        var groups = content["MemberOf"].Split('|', StringSplitOptions.RemoveEmptyEntries);
        var dropped = groups.First(g => !g.Contains("Domain Admins", StringComparison.OrdinalIgnoreCase));
        content["MemberOf"] = string.Join('|', groups.Where(g => g != dropped));
        var plan = State.RestorePlan(bo, version with { Content = content });
        Assert.Contains(plan.Plan, c => c.ChangeAction == ChangeAction.AdRemoveGroupMember);
        Assert.Contains(plan.Plan, c => c.Description.Contains("bibliotheek", StringComparison.OrdinalIgnoreCase)
            || c.Parameters.Values.Any(v => v.Contains("bibliotheek", StringComparison.OrdinalIgnoreCase)));
    }
}
