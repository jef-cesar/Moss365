using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R6: the Databank screen cleans up only after a preview, and archives the logs first.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void CleaningUpShowsFirstAndArchivesTheLogs()
    {
        State.ChangeLog.Write(new LogEntry(DateTimeOffset.Now.AddYears(-6), "oud@test.be", "c", "AD", "AdUnlockAccount", "d", "heel oud", "o", new Dictionary<string, string>(), "Succeeded", ""));
        State.Database.WriteAccess(new AccessEntry(DateTimeOffset.Now.AddYears(-7), "oud@test.be", "Export", "3 rijen"));
        var logBefore = State.Database.CountLog();

        var cut = RenderComponent<DatabaseMaintenance>();
        Assert.Contains("Momentopnamen", cut.Find("table").TextContent, StringComparison.Ordinal);
        Assert.Contains("Toegangslog", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(logBefore, State.Database.CountLog());

        // Choose the change log and the access log: nothing happens before the confirmation
        cut.Find("input[aria-label='Logboek']").Change(true);
        cut.Find("input[aria-label='Toegangslog']").Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Opruimen (2)").Click();
        Assert.Equal(logBefore, State.Database.CountLog());
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Ja, opruimen in", StringComparison.Ordinal)).Click();

        // Archived, then removed; the clean-up itself is in the change log
        Assert.Single(Directory.GetFiles(State.ArchiveFolder, "Logboek-archief-*.csv"));
        var access = Assert.Single(Directory.GetFiles(State.ArchiveFolder, "Toegangslog-archief-*.csv"));
        Assert.Contains("3 rijen", File.ReadAllText(access), StringComparison.Ordinal);
        Assert.Equal(0, State.Database.CountLog(DateTimeOffset.Now.AddYears(-5)));
        Assert.Equal(0, State.Database.CountAccess(DateTimeOffset.Now.AddYears(-5)));
        Assert.Contains(State.ChangeLog.Recent(5), r => r.ChangeAction == "Cleanup" && r.Parameters["ChangeLog"] == "1" && r.Parameters["AccessLog"] == "1");
        Assert.Contains("Logboek: 1 weggehaald", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionIsSetPerTenant()
    {
        var cut = RenderComponent<DatabaseMaintenance>();
        cut.FindAll("label").First(l => l.TextContent.StartsWith("Logboek (jaar)", StringComparison.Ordinal)).QuerySelector("input")!.Change("7");
        cut.FindAll("label").First(l => l.TextContent.StartsWith("Scriptruns", StringComparison.Ordinal)).QuerySelector("input")!.Change("30");
        cut.FindAll("button").First(b => b.TextContent == "Bewaren").Click();
        State.LoadConfig();
        Assert.Equal((7, 30), (State.Config.ChangeLogSaveYears, State.Config.Retention.ScriptRunDays));
        Assert.Contains("Ouder dan 7 jaar", cut.Markup, StringComparison.Ordinal);
    }
}
