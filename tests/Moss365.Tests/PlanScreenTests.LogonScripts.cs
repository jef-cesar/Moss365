using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.LogonScripts;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.32: logon scripts in NETLOGON.</summary>
public sealed partial class PlanScreenTests
{
    private sealed class FakeShare : ILogonScriptShare
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["logon.bat"] = "@echo off\r\nrem start\r\nnet use * /delete /y\r\nnet use H: \\\\fs01\\home\r\nifmember \"GG_Finance\"\r\nnet use F: \"\\\\fs01\\finance\"\r\ncall printers.bat\r\n",
            ["printers.bat"] = "rundll32 printui.dll,PrintUIEntry /in /n \"\\\\ps01\\P-Floor2\"\r\n",
            ["old.vbs"] = "Set n = CreateObject(\"WScript.Network\")\r\nn.MapNetworkDrive \"S:\", \"\\\\fs01\\shared\"\r\n",
        };

        public string Location => "\\\\dc01\\NETLOGON";

        public IReadOnlyList<LogonScriptFile> List() => Files.Keys.Select(k => new LogonScriptFile(k, Files[k].Length, DateTimeOffset.Now)).ToList();

        public string Read(string name) => Files[name];

        public IReadOnlyList<GpoScript> GpoScripts() => new[] { new GpoScript("{31B2F340-016D-11D2-945F-00C04FB984F9}", "Logon", "gpo-logon.bat", "") };
    }

    private InventoryData WithScripts(params (string Sam, string Script)[] scripts)
    {
        var d = State.Data;
        var ad = d.AdUsers.Select(a => scripts.FirstOrDefault(s => s.Sam == a.SamAccountName) is { Sam: not null } s ? a with { ScriptPath = s.Script } : a).ToList();
        return d with { AdUsers = ad, Persons = InventoryLoader.LinkPersons(ad, d.EntraUsers) };
    }

    [Fact]
    public void TheAppExplainsWhatALogonScriptDoes()
    {
        var share = new FakeShare();
        var e = LogonScriptAnalysis.Explain(share.Files["logon.bat"]);
        Assert.Contains("Verwijdert alle bestaande schijfkoppelingen", e.Lines);
        Assert.Contains("Koppelt H: aan \\\\fs01\\home", e.Lines);
        Assert.Contains("Koppelt F: aan \\\\fs01\\finance (voor leden van GG_Finance)", e.Lines);
        Assert.Contains("printers.bat", e.Calls);
        Assert.Equal("\\\\ps01\\P-Floor2", Assert.Single(LogonScriptAnalysis.Explain(share.Files["printers.bat"]).Printers));
        Assert.Equal("S:", Assert.Single(LogonScriptAnalysis.Explain(share.Files["old.vbs"]).Drives).Letter);
        Assert.Contains("herkent", Assert.Single(LogonScriptAnalysis.Explain("echo hello").Lines), StringComparison.Ordinal);
        Assert.Contains(LogonScriptAnalysis.Explain("New-PSDrive -Name X -PSProvider FileSystem -Root \\\\fs01\\x -Persist").Drives, x => x.Letter == "X:");
    }

    [Fact]
    public void WhoHasWhichScriptAndWhatIsMissingOrUnused()
    {
        var d = WithScripts(("an.peeters", "logon.bat"), ("bo.janssens", "\\\\dc01\\NETLOGON\\LOGON.BAT"), ("cas.vos", "gone.bat"));
        var uses = LogonScriptAnalysis.Uses(new FakeShare().List(), d);
        Assert.Equal(2, uses.Single(u => u.Name == "logon.bat").Users.Count);
        var missing = Assert.Single(uses, u => u.File is null);
        Assert.Equal("gone.bat", missing.Name);
        Assert.Contains(uses, u => u.Name == "old.vbs" && u.Users.Count == 0);
    }

    [Fact]
    public void SavingAScriptIsAPlanWithThePreviousVersionForUndo()
    {
        var before = new FakeShare().Files["printers.bat"];
        var after = before + "rundll32 printui.dll,PrintUIEntry /in /n \"\\\\ps01\\P-Floor3\"\r\n";
        var change = Assert.Single(LogonScriptPlans.Save("printers.bat", before, after).Plan);
        Assert.Equal(ChangeAction.AdWriteLogonScript, change.ChangeAction);
        Assert.Equal(after, change.P(LogonScriptPlans.Content));
        Assert.Equal(before, change.Previous![LogonScriptPlans.Content]);
        Assert.Equal("+1 / -0 regels", change.NewValue);

        Assert.NotEmpty(LogonScriptPlans.Save("..\\x.bat", null, "x").Errors);
        Assert.NotEmpty(LogonScriptPlans.Save("x.exe", null, "x").Errors);
        Assert.Equal("Er is niets gewijzigd.", Assert.Single(LogonScriptPlans.Save("printers.bat", before, before).Errors));

        // Undo puts the version from before back
        var entry = new LogEntry(DateTimeOffset.Now, "test", "ctx", "ActiveDirectory", nameof(ChangeAction.AdWriteLogonScript), "printers.bat", "printers.bat",
            change.Description, change.Parameters, nameof(ResultStatus.Succeeded), "ok", Previous: change.Previous);
        var undo = Assert.Single(Undo.Plan(new[] { entry }).Plan);
        Assert.Equal(before, undo.P(LogonScriptPlans.Content));
        Assert.NotNull(Undo.WhyNot(entry with { Previous = null }));
    }

    [Fact]
    public void TheDiffShowsAddedAndRemovedLines()
    {
        var diff = LogonScriptAnalysis.Diff("a\nb\nc", "a\nc\nd");
        Assert.Equal(new[] { "  a", "- b", "  c", "+ d" }, diff);
    }

    [Fact]
    public void AssigningAScriptChangesScriptPathInAd()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "an.peeters");
        var info = State.Data.Persons.First(p => p.Ad is null && !p.IsGuest);
        var outcome = LogonScriptPlans.Assign(State.Data, State.Config, new[] { an, info }, "logon.bat");
        var change = Assert.Single(outcome.Plan);
        Assert.Equal(ChangeAction.AdSetAttributes, change.ChangeAction);
        Assert.Equal("logon.bat", change.P("scriptPath"));
        Assert.Contains(outcome.Errors, e => e.Contains("geen AD-account", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLogonScriptsScreenEditsAsAPlan()
    {
        _factory.Share = new FakeShare();
        var cut = RenderComponent<LogonScripts>();
        Assert.Contains("\\\\dc01\\NETLOGON", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("gpo-logon.bat", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Koppelt H: aan", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("tr").First(r => r.TextContent.Contains("printers.bat", StringComparison.Ordinal)).QuerySelector("button")!.Click();
        Assert.Contains("Voegt de printer", cut.Markup, StringComparison.Ordinal);
        cut.Find("textarea").Input(new FakeShare().Files["printers.bat"] + "net use P: \\\\fs01\\p\r\n");
        Assert.Contains("1 erbij", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Plan maken (bewaren)", StringComparison.Ordinal)).Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdWriteLogonScript && w.Target == "printers.bat"));
    }
}
