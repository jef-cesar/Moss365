using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

/// <summary>Issue 40: what is computed more than once is kept, without becoming stale.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public async Task TileCountsAreKeptUntilAPlanRuns()
    {
        var before = State.KpiCount(Kpis.Succeeded7);
        Assert.Equal(before, State.KpiCount(Kpis.Succeeded7));

        var user = State.Data.AdUsers.First(u => u.Enabled);
        await State.ExecuteAsync(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdDisableAccount,
                Target = user.DistinguishedName, TargetName = user.SamAccountName, Description = "test",
            },
        }, "test", null);

        Assert.Equal(before + 1, State.KpiCount(Kpis.Succeeded7));
    }

    [Fact]
    public void ThePastOfASnapshotIsBuiltOnce()
    {
        var moment = DateTimeOffset.Now;
        var first = State.DataAt(moment);
        Assert.NotNull(first);
        Assert.Same(first, State.DataAt(moment.AddMinutes(-1))); // the same snapshot: kept
        Assert.Equal(State.Data.AdUsers.Count, first!.AdUsers.Count);
        Assert.Null(State.DataAt(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
