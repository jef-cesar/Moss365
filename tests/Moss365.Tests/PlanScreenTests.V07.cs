using Bunit;
using Moss365.Core.Scripts;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for 0.7: create scripts, start them (with preview), view the output and schedule.</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>Runs nothing for real: reports a few lines and a row, creates a file, and remembers the
    /// commands.</summary>
    private sealed class FakeScripts : IScriptExecutor
    {
        public List<ScriptCommand> Commands { get; } = new();
        public int Code { get; set; }

        public Task<int> ExecuteAsync(ScriptCommand command, IProgress<ScriptEvent>? events, CancellationToken cancel)
        {
            lock (Commands)
            {
                Commands.Add(command);
            }

            void Report(ScriptEvent g, string json)
            {
                File.AppendAllText(Path.Combine(command.RunFolder, "uitvoer.jsonl"), json + "\n");
                events?.Report(g);
            }

            var name = command.Values.TryGetValue("Postvak", out var p) ? p : "?";
            Report(new ScriptEvent(EventKind.Text, (command.Preview ? "What if: " : string.Empty) + "Bezig met " + name), "{\"s\":\"tekst\",\"t\":\"" + (command.Preview ? "What if: " : string.Empty) + "Bezig met " + name + "\"}");
            Report(new ScriptEvent(EventKind.Row, string.Empty, Table: "Resultaat", Row: new Dictionary<string, string> { ["Postvak"] = name, ["Items"] = "42" }),
                "{\"s\":\"rij\",\"tabel\":\"Resultaat\",\"r\":{\"Postvak\":\"" + name + "\",\"Items\":\"42\"}}");
            Directory.CreateDirectory(Path.Combine(command.RunFolder, "bestanden"));
            File.WriteAllText(Path.Combine(command.RunFolder, "bestanden", "export.csv"), "Onderwerp;Start\nOverleg;2026-09-01\n");
            return Task.FromResult(Code);
        }
    }

    private const string WritingScript = """
        <#MOSS365
        Naam: Postvak opkuisen
        Omschrijving: Verwijdert oude items
        Verbinding: Exchange
        Schrijft: ja
        Variabele: Postvak | Postvak | verplicht | Welk postvak? |  |
        #>
        [CmdletBinding(SupportsShouldProcess = $true)]
        param([string]$Postvak)
        'x'
        """;

    private ScriptFile BuiltInScript(string fileName = "Postvak-opkuisen.ps1", string text = WritingScript)
    {
        var folder = Path.Combine(_base, "scripts-ingebouwd", "Exchange");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, fileName), text);
        return State.SearchScript(Path.GetFileNameWithoutExtension(fileName))!;
    }

    private void WaitOnScript()
    {
        var until = DateTime.Now.AddSeconds(10);
        while (State.ScriptBusy && DateTime.Now < until)
        {
            Thread.Sleep(20);
        }

        Assert.False(State.ScriptBusy);
    }

    [Fact]
    public void ScriptsOverviewShowsBuiltInAndOwn()
    {
        BuiltInScript();
        ScriptLibrary.Store(State.ScriptFolders, ScriptLibrary.NewScript("Mijn export"));

        var cut = RenderComponent<ScriptsOverview>();
        Assert.Contains("Postvak opkuisen", cut.Markup);
        Assert.Contains("Mijn export", cut.Markup);
        Assert.Contains("wijzigt", cut.Markup);
        Assert.Contains("ingebouwd", cut.Markup);
        Assert.Contains("Kopie bewerken", cut.Markup);

        // Delete an own script (with confirmation)
        cut.FindAll("button").First(b => b.TextContent == "Verwijderen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, verwijderen").Click();
        Assert.Contains("is verwijderd", cut.Markup);
        Assert.Single(State.ReadScripts());
    }

    [Fact]
    public void CreateScriptWithVariablesAndSave()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("scripts/bewerken");
        var cut = RenderComponent<EditScript>();

        // Without Monaco (bUnit): the text box
        Assert.NotNull(cut.Find("textarea.kb-code"));
        Field(cut, "Naam").Change("Agenda export");
        cut.FindAll("button").First(b => b.TextContent == "+ Variabele").Click();
        cut.FindAll("input[aria-label=Naam]").Last().Change("Van");
        cut.FindAll("select[aria-label=Soort]").Last().Change(nameof(VariableKind.Date));
        cut.FindAll("input[aria-label=Standaard]").Last().Change("@werkjaar.van");

        cut.FindAll("button").First(b => b.TextContent == "Opslaan").Click();
        cut.WaitForAssertion(() => Assert.Contains("Opgeslagen als Agenda-export.ps1", cut.Markup));

        var script = State.SearchScript("Agenda-export")!;
        Assert.Equal(new[] { "Postvak", "Van" }, script.Header.Variables.Select(v => v.Name));
        Assert.Equal("@werkjaar.van", script.Header.Variables[1].Default);
        var text = ScriptLibrary.Text(State.ScriptFolders, script);
        Assert.Contains("[datetime]$Van", text);
        Assert.False(ScriptLibrary.IsChangedOutsideApp(State.ScriptFolders, script));
    }

    [Fact]
    public void ChangingScriptFirstPreviewThenRun()
    {
        var script = BuiltInScript();
        var cut = RenderComponent<StartScript>(p => p.Add(x => x.Key, script.Key));

        // No Run without a preview
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Uitvoeren");
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Voorbeeld", StringComparison.Ordinal)).Click();
        Assert.Contains("Welk postvak?: verplicht", cut.Markup);
        Assert.Empty(_factory.FakeScripts.Commands);

        cut.Find("input[list=kb-keuzes-mailbox]").Change("info@test.be");
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Voorbeeld", StringComparison.Ordinal)).Click();
        WaitOnScript();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Uitvoeren"));
        Assert.True(Assert.Single(_factory.FakeScripts.Commands).Preview);
        Assert.Contains("What if: Bezig met info@test.be", cut.Markup);

        // Another value: a new preview is needed
        cut.Find("input[list=kb-keuzes-mailbox]").Change("ander@test.be");
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Uitvoeren");
        cut.Find("input[list=kb-keuzes-mailbox]").Change("info@test.be");

        cut.FindAll("button").First(b => b.TextContent == "Uitvoeren").Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).Click();
        WaitOnScript();
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        Assert.False(_factory.FakeScripts.Commands[1].Preview);

        // Result: a table from the rows and the CSV, and the log
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Tabellen", StringComparison.Ordinal)).Click();
        Assert.Contains("42", cut.Markup);
        Assert.Contains("export.csv", cut.Markup);
        var log = State.ChangeLog.Recent(5);
        Assert.Contains(log, r => r.ChangeAction == "ScriptVoorbeeld");
        Assert.Contains(log, r => r.ChangeAction == "ScriptUitvoeren" && r.Succeeded && r.Parameters["Postvak"] == "info@test.be");

        // The last values are remembered (the same file as Get-ScriptWaarde)
        Assert.Equal("info@test.be", ScriptValues.Read(State.ScriptFolders, script.Key)["Postvak"]);
        Assert.Equal(2, ScriptRuns.Items(State.ScriptFolders, script.Key).Count);
    }

    [Fact]
    public void ReviewScriptRunAndStartAgain()
    {
        var script = BuiltInScript("Lees.ps1", WritingScript.Replace("Schrijft: ja", "Schrijft: nee").Replace("Postvak opkuisen", "Lees"));
        var (status, error) = State.StartScript(script, new Dictionary<string, string> { ["Postvak"] = "info@test.be" }, preview: false);
        Assert.Null(error);
        WaitOnScript();

        var cut = RenderComponent<ScriptRunDetail>(p => p.Add(x => x.Key, script.Key).Add(x => x.Id, Path.GetFileName(status!.Run.Folder)));
        Assert.Contains("Bezig met info@test.be", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Opnieuw starten met dezelfde waarden").Click();
        Assert.Equal("info@test.be", State.ScriptPrefill!["Postvak"]);
        Assert.EndsWith("scripts/starten/Lees", Services.GetRequiredService<NavigationManager>().Uri);

        // A second script at the same time is not possible; a broken script gives "failed"
        _factory.FakeScripts.Code = 1;
        State.StartScript(script, new Dictionary<string, string> { ["Postvak"] = "x@test.be" }, false);
        WaitOnScript();
        Assert.Equal(RunResult.Error, State.ScriptRun!.Run.Result);
    }

    [Fact]
    public void ScheduleScriptAndStartFromScheduled()
    {
        var script = BuiltInScript();
        var cut = RenderComponent<StartScript>(p => p.Add(x => x.Key, script.Key));
        cut.Find("input[list=kb-keuzes-mailbox]").Change("info@test.be");
        cut.Find("select").Change(nameof(Recurrence.Monthly));
        cut.FindAll("button").First(b => b.TextContent == "Inplannen").Click();
        Assert.Contains("Ingepland voor", cut.Markup);

        var action = Assert.Single(State.Scheduled.Read());
        Assert.Equal(ScheduledKind.Script, action.Kind);
        Assert.Equal(Recurrence.Monthly, action.Recurrence);
        Assert.Equal("info@test.be", action.ScriptValues["Postvak"]);

        var scheduled = RenderComponent<Scheduled>();
        Assert.Contains($"scripts/starten/{script.Key}?gepland={action.Id}", scheduled.Markup);
        Assert.Contains("(elke maand)", scheduled.Markup);
    }

    [Fact]
    public void SharedMailboxSeeAndBlockSignIn()
    {
        TestData.WriteInventory(Path.Combine(_base, "Inventaris", "Inventaris-20261030-1000"), Version07Tests.MailboxFiles());
        State.LoadInventory();
        var list = RenderComponent<Mailboxes>();
        Assert.Contains("Kan aanmelden", list.Markup);
        Assert.Contains("Laatste aanmelding", list.Markup);
        Assert.Contains("kunnen aanmelden", list.Markup);
        list.FindAll("button.kb-tegel").First(b => b.TextContent.Contains("kunnen aanmelden")).Click();
        Assert.Contains("Jeugd", list.Markup);
        Assert.DoesNotContain(">Cultuur<", list.Markup);

        var cut = RenderComponent<MailboxDetail>(p => p.Add(x => x.Smtp, "jeugd@test.be"));
        Assert.Contains("Aanmelden met het account", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Aanmelden blokkeren").Click();
        Assert.Contains("Aanmelden blokkeren in Entra", cut.Markup);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphSetAttributes && w.Target == "e-jeugd"));
    }
}
