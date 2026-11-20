using Bunit;
using Moss365.Core.Scripts;
using Moss365.UI.Components;
using Xunit;

namespace Moss365.Tests;

/// <summary>R8: Update library downloads only what matches the index, and a pinned script stays on its version.</summary>
public sealed partial class PlanScreenTests
{
    private sealed class FakeLibrary : IScriptLibrarySource
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The index as published; a file can differ from it (tampered with).</summary>
        public List<IndexEntry> Index { get; } = new();

        public string Where => "test";

        public Task<string> IndexAsync(CancellationToken cancel) => Task.FromResult(new ScriptIndex(1, Index).ToJson());

        public Task<byte[]> FileAsync(string path, CancellationToken cancel) => Task.FromResult(Files[path]);

        public void Publish(string path, string text, string? tampered = null)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            var header = ScriptText.Read(text, path);
            Index.RemoveAll(e => e.Path == path);
            Index.Add(new IndexEntry(path, header.Name, header.Version, ScriptIndex.Sha256(bytes), header.MinimumApp, header.Writes));
            Files[path] = tampered is null ? bytes : System.Text.Encoding.ASCII.GetBytes(tampered);
        }
    }

    private sealed partial class FakeFactory
    {
        public FakeLibrary Library { get; } = new();

        public IScriptLibrarySource? ScriptLibrary(string? folder) => Library;
    }

    private static string LibraryScript(string name, string version, string body = "Write-Output 'ok'", string minimum = "")
        => $"<#MOSS365\r\nName: {name}\r\nVersion: {version}\r\n" + (minimum.Length > 0 ? $"MinimumApp: {minimum}\r\n" : string.Empty)
           + $"Connection: Local\r\nWrites: no\r\n#>\r\n[CmdletBinding()]\r\nparam()\r\n{body}\r\n";

    [Fact]
    public async Task UpdateLibraryTakesOnlyWhatMatchesTheIndex()
    {
        BuiltInScript("Rapport.ps1", LibraryScript("Rapport", "1.0", "Write-Output 'oud'"));
        var lib = _factory.Library;
        lib.Publish("Exchange/Rapport.ps1", LibraryScript("Rapport", "1.1", "Write-Output 'nieuw'"));
        lib.Publish("Graph/Nieuw.ps1", LibraryScript("Nieuw", "1.0"));
        lib.Publish("Graph/Vervalst.ps1", LibraryScript("Vervalst", "1.0"), tampered: LibraryScript("Vervalst", "1.0", "Remove-Item C:\\ -Recurse"));
        lib.Publish("Graph/Later.ps1", LibraryScript("Later", "1.0", minimum: "9.0.0"));
        lib.Publish("../buiten.ps1", LibraryScript("Buiten", "1.0"));

        var changes = await State.CheckLibraryAsync(null);
        Assert.Equal(LibraryChangeKind.Changed, changes.Single(c => c.Entry.Path == "Exchange/Rapport.ps1").Kind);
        Assert.Equal("1.0", changes.Single(c => c.Entry.Path == "Exchange/Rapport.ps1").CurrentVersion);
        Assert.Equal(LibraryChangeKind.New, changes.Single(c => c.Entry.Path == "Graph/Nieuw.ps1").Kind);
        Assert.Equal(LibraryChangeKind.TooNew, changes.Single(c => c.Entry.Path == "Graph/Later.ps1").Kind);
        Assert.DoesNotContain(changes, c => c.Entry.Path.Contains("..", StringComparison.Ordinal));

        // The difference, before anything is stored
        var diff = await State.LibraryDiffAsync(changes.Single(c => c.Entry.Path == "Exchange/Rapport.ps1"));
        Assert.Contains("- Write-Output 'oud'", diff);
        Assert.Contains("+ Write-Output 'nieuw'", diff);
        Assert.Equal("oud", Body(State.SearchScript("Rapport")!));

        var refused = await State.TakeLibraryAsync(changes);
        Assert.Contains(refused, e => e.Contains("Vervalst", StringComparison.Ordinal) && e.Contains("vingerafdruk", StringComparison.Ordinal));
        Assert.Null(State.SearchScript("Vervalst"));
        Assert.Null(State.SearchScript("Later"));
        Assert.NotNull(State.SearchScript("Nieuw"));
        Assert.Equal("nieuw", Body(State.SearchScript("Rapport")!));
        Assert.True(State.SearchScript("Rapport")!.BuiltIn);
        Assert.Contains(State.ChangeLog.Recent(5), r => r.ChangeAction == "UpdateLibrary" && r.Parameters.ContainsKey("Exchange/Rapport.ps1"));

        // Nothing left to take, except what cannot be taken
        Assert.All(await State.CheckLibraryAsync(null), c => Assert.False(c.CanTake && c.Entry.Path != "Graph/Vervalst.ps1"));
    }

    [Fact]
    public async Task APinnedScriptStaysOnItsVersion()
    {
        var script = BuiltInScript("Rapport.ps1", LibraryScript("Rapport", "1.0", "Write-Output 'v1'"));
        State.Pin(script, true);
        Assert.True(State.IsPinned(State.SearchScript("Rapport")!));

        // The app brings a newer version, and the library has one too: the pinned tenant stays on 1.0
        BuiltInScript("Rapport.ps1", LibraryScript("Rapport", "2.0", "Write-Output 'v2'"));
        Assert.Equal("v1", Body(State.SearchScript("Rapport")!));
        _factory.Library.Publish("Exchange/Rapport.ps1", LibraryScript("Rapport", "3.0", "Write-Output 'v3'"));
        var change = Assert.Single(await State.CheckLibraryAsync(null));
        Assert.Equal(LibraryChangeKind.Pinned, change.Kind);
        Assert.False(change.CanTake);

        // Released: it follows the library again
        State.Pin(State.SearchScript("Rapport")!, false);
        Assert.Equal("v2", Body(State.SearchScript("Rapport")!));
    }

    [Fact]
    public void ThePanelShowsTheChangesAndDownloadsAfterConfirmation()
    {
        BuiltInScript("Rapport.ps1", LibraryScript("Rapport", "1.0"));
        _factory.Library.Publish("Graph/Nieuw.ps1", LibraryScript("Nieuw", "1.0"));
        var cut = RenderComponent<LibraryPanel>();
        cut.FindAll("button").First(b => b.TextContent == "Nakijken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Graph/Nieuw.ps1", cut.Markup, StringComparison.Ordinal));
        Assert.Null(State.SearchScript("Nieuw"));
        cut.FindAll("button").First(b => b.TextContent == "Downloaden (1)").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, downloaden").Click();
        cut.WaitForAssertion(() => Assert.Contains("1 script(s) gedownload.", cut.Markup, StringComparison.Ordinal));
        Assert.NotNull(State.SearchScript("Nieuw"));
    }

    private string Body(ScriptFile s) => System.Text.RegularExpressions.Regex.Match(ScriptLibrary.Text(State.ScriptFolders, s), "Write-Output '(\\w+)'").Groups[1].Value;
}
