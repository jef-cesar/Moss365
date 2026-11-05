using System.Diagnostics;
using Moss365.Core.Scripts;
using Moss365.Connections.Scripts;
using Xunit;

namespace Moss365.Tests;

/// <summary>
/// The script executor with real PowerShell (the build server has pwsh). Local scripts only: no Exchange or Graph
/// needed. Without PowerShell on this machine the tests skip themselves.
/// </summary>
public sealed class ScriptExecutorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly Lazy<bool> HasPowerShell = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ScriptExecutor.SearchPowerShell("7"), "-NoProfile -Command exit 0")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            p!.WaitForExit(60000);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    });

    private ScriptFile Script(string name, string text)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name + ".ps1");
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
        return ScriptLibrary.ReadFile(path, false);
    }

    private async Task<(int Code, List<ScriptEvent> Events, string RunFolder)> Run(ScriptFile s, Dictionary<string, string>? values = null,
        bool preview = false, ScriptExecutor? executor = null, CancellationToken cancel = default)
    {
        var runFolder = Path.Combine(_folder, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runFolder);
        var list = new List<ScriptEvent>();
        var progress = new Synchronous(g => { lock (list) { list.Add(g); } });
        var code = await (executor ?? new ScriptExecutor()).ExecuteAsync(new ScriptCommand(s, values ?? new(), preview, runFolder), progress, cancel);
        return (code, list, runFolder);
    }

    private sealed class Synchronous(Action<ScriptEvent> action) : IProgress<ScriptEvent>
    {
        public void Report(ScriptEvent value) => action(value);
    }

    [Fact]
    public async Task OutputTablesAndValues()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        var s = Script("Test", """
            <#MOSS365
            Naam: Test
            Verbinding: Lokaal
            Schrijft: nee
            Variabele: Naam | Tekst | verplicht | Naam |  |
            Variabele: Wie | Lijst | optioneel | Wie |  |
            Variabele: Aantal | Getal | optioneel | Aantal |  |
            Variabele: Van | Datum | optioneel | Van |  |
            Variabele: Alles | JaNee | optioneel | Alles |  |
            #>
            [CmdletBinding()]
            param([string]$Naam, [string[]]$Wie, [int]$Aantal, [datetime]$Van, [switch]$Alles)
            Write-Host "Hallo $Naam" -ForegroundColor Green
            Write-Warning 'Let op'
            Write-Progress -Activity 'Bezig' -PercentComplete 50
            "types: $($Wie.GetType().Name) $($Wie.Count) $($Aantal.GetType().Name) $($Van.ToString('yyyy-MM-dd')) $($Alles.IsPresent)"
            [pscustomobject]@{ Naam = 'A'; Aantal = 1; Datum = [datetime]'2026-09-01T10:00:00' }
            [pscustomobject]@{ Naam = 'B'; Aantal = 2; Datum = $null }
            @{ Sleutel = 'x' }
            [pscustomobject]@{ Naam = 'C' } | Export-Csv -Path (Join-Path $MossUitvoer 'lijst.csv') -NoTypeInformation -Delimiter ';'
            Get-Item -LiteralPath $MossUitvoer | Format-Table Name
            Write-Error 'niet erg'
            'na de fout'
            """);

        var (code, g, runFolder) = await Run(s, new Dictionary<string, string>
        {
            ["Naam"] = "Moss", ["Wie"] = "a\nb, c", ["Aantal"] = "3", ["Van"] = "@werkjaar.van", ["Alles"] = "true", ["Onbekend"] = "x",
        });

        Assert.Equal(2, code);
        Assert.Contains(g, x => x.Kind == EventKind.Text && x.Text == "Hallo Moss" && x.Colour == "Green");
        Assert.Contains(g, x => x.Kind == EventKind.Warning && x.Text == "Let op");
        Assert.Contains(g, x => x.Kind == EventKind.Progress && x.Percent == 50);
        var workYear = DateTime.Today.Month >= 9 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        Assert.Contains(g, x => x.Text == $"types: String[] 3 Int32 {workYear}-09-01 True");
        Assert.Contains(g, x => x.Kind == EventKind.Error && x.Text.Contains("niet erg"));
        Assert.Contains(g, x => x.Text == "na de fout");
        Assert.Contains(g, x => x.Kind == EventKind.Text && x.Text.Contains("bestanden"));

        var tables = ScriptOutput.Tables(ScriptOutput.ReadFile(Path.Combine(runFolder, "uitvoer.jsonl")), Path.Combine(runFolder, "bestanden"));
        var result = tables.First(t => t.Columns.Contains("Aantal"));
        Assert.Equal(new[] { "Naam", "Aantal", "Datum" }, result.Columns);
        Assert.Equal("2026-09-01 10:00", result.Rows[0]["Datum"]);
        Assert.Equal(string.Empty, result.Rows[1]["Datum"]);
        Assert.Contains(tables, t => t.Columns.SequenceEqual(new[] { "Sleutel" }));
        var csv = Assert.Single(tables, t => t.File is not null);
        Assert.Equal("C", csv.Rows.Single()["Naam"]);
        Assert.False(File.Exists(Path.Combine(runFolder, "invoer.json")));
    }

    [Fact]
    public async Task PreviewWithAndWithoutWhatIf()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        var target = Path.Combine(_folder, "gemaakt.txt");
        var withValue = Script("Met", $$"""
            [CmdletBinding(SupportsShouldProcess = $true)]
            param([string]$Pad = '{{target}}')
            if ($PSCmdlet.ShouldProcess($Pad, 'Aanmaken')) { New-Item -ItemType File -Path $Pad | Out-Null; 'gemaakt' } else { 'zou maken' }
            """);
        var (code, g, _) = await Run(withValue, preview: true);
        Assert.Equal(0, code);
        Assert.Contains(g, x => x.Text == "zou maken");
        Assert.False(File.Exists(target));

        var without = Script("Zonder", $$"""
            param([string]$Pad = '{{target}}')
            New-Item -ItemType File -Path $Pad | Out-Null
            """);
        (code, g, _) = await Run(without, preview: true);
        Assert.Equal(3, code);
        Assert.Contains(g, x => x.Kind == EventKind.Error && x.Text.Contains("-WhatIf"));
        Assert.False(File.Exists(target));

        (code, _, _) = await Run(without);
        Assert.Equal(0, code);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ReadHostConnectAndErrorWithLine()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        var (code, g, _) = await Run(Script("Vraag", "<#MOSS365\nNaam: Vraag\nVerbinding: Lokaal\n#>\nConnect-ExchangeOnline\nConnect-MgGraph -Scopes x\n'verder'\n$x = Read-Host 'Naam'\n'niet hier'"));
        Assert.Equal(1, code);
        Assert.Contains(g, x => x.Text == "verder");
        Assert.Contains(g, x => x.Kind == EventKind.Error && x.Text.Contains("Read-Host") && x.Text.Contains("line"));
        Assert.DoesNotContain(g, x => x.Text == "niet hier");

        // Without a header the app guesses Exchange from the commands; without a sign-in the script then does not start
        (code, g, _) = await Run(Script("Exo", "Get-Mailbox x"));
        Assert.Equal(4, code);
        Assert.Contains(g, x => x.Kind == EventKind.Error && x.Text.Contains("Exchange Online"));
    }

    [Fact]
    public async Task TokensNotOnTheCommandLineAndNotInTheScriptEnvironment()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        const string secret = "eyJ-geheim-token-123";
        var s = Script("Graph", """
            <#MOSS365
            Naam: Graph
            Verbinding: Graph
            Schrijft: nee
            #>
            param()
            "env:[$env:MOSS365_GRAPH_TOKEN] lengte:$($global:MossGraphToken.Length) account:$((Get-MgContext).Account)"
            "args:$([Environment]::GetCommandLineArgs() -join ' ')"
            """);
        var executor = new ScriptExecutor(graphToken: _ => Task.FromResult(secret), upn: () => "beheerder@test.be");
        var (code, g, runFolder) = await Run(s, executor: executor);

        Assert.Equal(0, code);
        Assert.Contains(g, x => x.Text == $"env:[] lengte:{secret.Length} account:beheerder@test.be");
        var args = g.Single(x => x.Text.StartsWith("args:", StringComparison.Ordinal)).Text;
        Assert.DoesNotContain(secret, args);
        Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(runFolder, "uitvoer.jsonl")));
    }

    [Fact]
    public async Task CancelStopsTheProcess()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Script("Lang", "'start'\nStart-Sleep -Seconds 120\n'klaar'"), cancel: cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task SyntaxCheckWithoutRunning()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        var marker = Path.Combine(_folder, "uitgevoerd.txt");
        Directory.CreateDirectory(_folder);
        var u = new ScriptExecutor();
        Assert.Empty(await u.CheckAsync($"New-Item -ItemType File -Path '{marker}'", CancellationToken.None));
        Assert.False(File.Exists(marker));

        var errors = await u.CheckAsync("'ok'\nif ($x {\n  'x'\n", CancellationToken.None);
        Assert.NotEmpty(errors);
        Assert.Equal(2, errors[0].Rule);
    }

    /// <summary>A real script from the library: first the preview (nothing removed), then for real (removed, with a log
    /// in the folder of the run).</summary>
    [Fact]
    public async Task CleanupLibraryScriptLockFiles()
    {
        if (!HasPowerShell.Value)
        {
            return;
        }

        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "scripts")))
        {
            repo = repo.Parent;
        }

        var source = Path.Combine(repo!.FullName, "scripts", "FileServer", "Remove-OfficeLockFiles.ps1");
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "Remove-OfficeLockFiles.ps1");
        File.Copy(source, path);
        var script = ScriptLibrary.ReadFile(path, true);
        Assert.True(script.Header.Writes);

        var share = Path.Combine(_folder, "share", "team");
        Directory.CreateDirectory(share);
        var lock1 = Path.Combine(share, "~$rapport.docx");
        File.WriteAllText(lock1, "x");
        File.WriteAllText(Path.Combine(share, "rapport.docx"), "x");

        var (code, g, _) = await Run(script, new Dictionary<string, string> { ["Map"] = Path.Combine(_folder, "share") }, preview: true);
        Assert.Equal(0, code);
        Assert.Contains(g, x => x.Text.Contains("WhatIf"));
        Assert.True(File.Exists(lock1));

        (code, g, var runFolder) = await Run(script, new Dictionary<string, string> { ["Map"] = Path.Combine(_folder, "share") });
        Assert.Equal(0, code);
        Assert.False(File.Exists(lock1));
        Assert.True(File.Exists(Path.Combine(_folder, "share", "team", "rapport.docx")));
        Assert.Contains("DELETED", File.ReadAllText(Path.Combine(runFolder, "bestanden", "LockFilesLog.txt")));
    }

    [Fact]
    public void ResultFromCode()
    {
        Assert.Equal(RunResult.Succeeded, ScriptOutput.Result(0, 0, 0, false).Result);
        Assert.Equal(RunResult.Warnings, ScriptOutput.Result(0, 0, 2, false).Result);
        Assert.Equal(RunResult.Error, ScriptOutput.Result(2, 1, 0, false).Result);
        Assert.Contains("-WhatIf", ScriptOutput.Result(3, 0, 0, false).Message);
        Assert.Equal(RunResult.Cancelled, ScriptOutput.Result(-1, 0, 0, true).Result);
        Assert.Equal(EventKind.Text, ScriptOutput.Read("gewone tekst").Kind);
        Assert.Equal(EventKind.Text, ScriptOutput.Read("{kapot").Kind);
    }
}
