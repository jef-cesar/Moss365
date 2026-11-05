using System.Diagnostics;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.7 (#12): read AD, Entra and Exchange at the same time; rooms and mailboxes in one Exchange
/// session.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void LoadAllReadsAdEntraAndExchangeTogether()
    {
        var delay = TimeSpan.FromSeconds(1);
        _factory.Delay = delay;
        var clock = Stopwatch.StartNew();
        Assert.True(State.StartLoading(LoadKind.Everything));

        // After a short time the three lanes run at the same time; the menu shows "3 busy"
        var until = DateTime.Now.AddSeconds(5);
        while (State.Job!.Steps.Count(s => s.Status == StepStatus.Busy) < 3 && DateTime.Now < until)
        {
            Thread.Sleep(10);
        }

        Assert.StartsWith("3 bezig:", State.Job.Summary);
        WaitOnTask();
        clock.Stop();

        // One after the other: AD + Entra + rooms + mailboxes = 4 times the delay; at the same time: about 2 (Exchange
        // does two)
        Assert.True(clock.Elapsed < delay * 3.5, $"duurde {clock.Elapsed.TotalMilliseconds} ms");
        Assert.All(State.Job.Steps, s => Assert.Equal(StepStatus.Done, s.Status));

        // Everything merged, nothing overwritten
        Assert.Equal(new[] { "AD", "Entra", "Postvakken", "Rooms" }, State.LiveOn.Keys.Order());
        Assert.NotEmpty(State.Data.AdUsers);
        Assert.NotEmpty(State.Data.EntraUsers);
        Assert.NotEmpty(State.Data.Rooms);
        Assert.Single(State.Data.Mailboxes); // the fake Exchange returns one

        // Save as an inventory only when all lanes are done
        var save = State.Job.Steps.Single(s => s.Name == "Bewaren als inventaris");
        Assert.All(State.Job.Steps.Where(s => s != save), s => Assert.True(s.End <= save.Start));
    }

    [Fact]
    public void AnErrorInAdLetsEntraAndExchangeFinish()
    {
        _factory.AdReadFailed = true;
        Assert.True(State.StartLoading(LoadKind.Everything));
        WaitOnTask();

        var task = State.Job!;
        Assert.Equal(StepStatus.Error, task.Steps.Single(s => s.Name == "AD lezen").Status);
        Assert.Contains("domeincontroller", task.Steps.Single(s => s.Name == "AD lezen").Message);
        Assert.All(task.Steps.Where(s => s.Name != "AD lezen"), s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.DoesNotContain("AD", State.LiveOn.Keys);
        Assert.Contains("Entra", State.LiveOn.Keys);
        Assert.Contains("Postvakken", State.LiveOn.Keys);
        Assert.Contains(task.Log, r => r.Contains("AD lezen: MISLUKT"));
    }

    [Fact]
    public void CancelStopsAllLanes()
    {
        _factory.Delay = TimeSpan.FromSeconds(10);
        Assert.True(State.StartLoading(LoadKind.Everything));
        Thread.Sleep(100);
        State.CancelTask();
        WaitOnTask();

        Assert.True(State.Job!.Cancelled);
        Assert.All(State.Job.Steps, s => Assert.Equal(StepStatus.Cancelled, s.Status));
        Assert.Empty(State.LiveOn);
    }
}
