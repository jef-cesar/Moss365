using System.Diagnostics;
using Moss365.Core.Scripts;
using Moss365.Connections.Scripts;
using Xunit;

namespace Moss365.Tests;

/// <summary>
/// De scriptuitvoerder met echte PowerShell (op de build-server staat pwsh). Enkel lokale scripts: geen Exchange of
/// Graph nodig. Zonder PowerShell op deze machine slaan de tests zichzelf over.
/// </summary>
public sealed class ScriptUitvoerderTests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    private static readonly Lazy<bool> HeeftPowerShell = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ScriptUitvoerder.ZoekPowerShell("7"), "-NoProfile -Command exit 0")
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

    private ScriptBestand Script(string naam, string tekst)
    {
        Directory.CreateDirectory(_map);
        var pad = Path.Combine(_map, naam + ".ps1");
        File.WriteAllText(pad, tekst, new System.Text.UTF8Encoding(true));
        return ScriptBibliotheek.LeesBestand(pad, false);
    }

    private async Task<(int Code, List<ScriptGebeurtenis> Gebeurtenissen, string RunMap)> Draai(ScriptBestand s, Dictionary<string, string>? waarden = null,
        bool voorbeeld = false, ScriptUitvoerder? uitvoerder = null, CancellationToken annuleren = default)
    {
        var runMap = Path.Combine(_map, "runs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runMap);
        var lijst = new List<ScriptGebeurtenis>();
        var voortgang = new Synchroon(g => { lock (lijst) { lijst.Add(g); } });
        var code = await (uitvoerder ?? new ScriptUitvoerder()).VoerUitAsync(new ScriptOpdracht(s, waarden ?? new(), voorbeeld, runMap), voortgang, annuleren);
        return (code, lijst, runMap);
    }

    private sealed class Synchroon(Action<ScriptGebeurtenis> actie) : IProgress<ScriptGebeurtenis>
    {
        public void Report(ScriptGebeurtenis value) => actie(value);
    }

    [Fact]
    public async Task UitvoerTabellenEnWaarden()
    {
        if (!HeeftPowerShell.Value)
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

        var (code, g, runMap) = await Draai(s, new Dictionary<string, string>
        {
            ["Naam"] = "Moss", ["Wie"] = "a\nb, c", ["Aantal"] = "3", ["Van"] = "@werkjaar.van", ["Alles"] = "true", ["Onbekend"] = "x",
        });

        Assert.Equal(2, code);
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Tekst && x.Tekst == "Hallo Moss" && x.Kleur == "Green");
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Waarschuwing && x.Tekst == "Let op");
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Voortgang && x.Procent == 50);
        var werkjaar = DateTime.Today.Month >= 9 ? DateTime.Today.Year : DateTime.Today.Year - 1;
        Assert.Contains(g, x => x.Tekst == $"types: String[] 3 Int32 {werkjaar}-09-01 True");
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Fout && x.Tekst.Contains("niet erg"));
        Assert.Contains(g, x => x.Tekst == "na de fout");
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Tekst && x.Tekst.Contains("bestanden"));

        var tabellen = ScriptUitvoer.Tabellen(ScriptUitvoer.LeesBestand(Path.Combine(runMap, "uitvoer.jsonl")), Path.Combine(runMap, "bestanden"));
        var resultaat = tabellen.First(t => t.Kolommen.Contains("Aantal"));
        Assert.Equal(new[] { "Naam", "Aantal", "Datum" }, resultaat.Kolommen);
        Assert.Equal("2026-09-01 10:00", resultaat.Rijen[0]["Datum"]);
        Assert.Equal(string.Empty, resultaat.Rijen[1]["Datum"]);
        Assert.Contains(tabellen, t => t.Kolommen.SequenceEqual(new[] { "Sleutel" }));
        var csv = Assert.Single(tabellen, t => t.Bestand is not null);
        Assert.Equal("C", csv.Rijen.Single()["Naam"]);
        Assert.False(File.Exists(Path.Combine(runMap, "invoer.json")));
    }

    [Fact]
    public async Task VoorbeeldMetWhatIfEnZonder()
    {
        if (!HeeftPowerShell.Value)
        {
            return;
        }

        var doel = Path.Combine(_map, "gemaakt.txt");
        var met = Script("Met", $$"""
            [CmdletBinding(SupportsShouldProcess = $true)]
            param([string]$Pad = '{{doel}}')
            if ($PSCmdlet.ShouldProcess($Pad, 'Aanmaken')) { New-Item -ItemType File -Path $Pad | Out-Null; 'gemaakt' } else { 'zou maken' }
            """);
        var (code, g, _) = await Draai(met, voorbeeld: true);
        Assert.Equal(0, code);
        Assert.Contains(g, x => x.Tekst == "zou maken");
        Assert.False(File.Exists(doel));

        var zonder = Script("Zonder", $$"""
            param([string]$Pad = '{{doel}}')
            New-Item -ItemType File -Path $Pad | Out-Null
            """);
        (code, g, _) = await Draai(zonder, voorbeeld: true);
        Assert.Equal(3, code);
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Fout && x.Tekst.Contains("-WhatIf"));
        Assert.False(File.Exists(doel));

        (code, _, _) = await Draai(zonder);
        Assert.Equal(0, code);
        Assert.True(File.Exists(doel));
    }

    [Fact]
    public async Task ReadHostConnectEnFoutMetRegel()
    {
        if (!HeeftPowerShell.Value)
        {
            return;
        }

        var (code, g, _) = await Draai(Script("Vraag", "<#MOSS365\nNaam: Vraag\nVerbinding: Lokaal\n#>\nConnect-ExchangeOnline\nConnect-MgGraph -Scopes x\n'verder'\n$x = Read-Host 'Naam'\n'niet hier'"));
        Assert.Equal(1, code);
        Assert.Contains(g, x => x.Tekst == "verder");
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Fout && x.Tekst.Contains("Read-Host") && x.Tekst.Contains("regel"));
        Assert.DoesNotContain(g, x => x.Tekst == "niet hier");

        // Zonder kop raadt de app Exchange uit de opdrachten; zonder aanmelding start het script dan niet
        (code, g, _) = await Draai(Script("Exo", "Get-Mailbox x"));
        Assert.Equal(4, code);
        Assert.Contains(g, x => x.Soort == GebeurtenisSoort.Fout && x.Tekst.Contains("Exchange Online"));
    }

    [Fact]
    public async Task TokensNietOpDeOpdrachtregelEnNietInDeOmgevingVanHetScript()
    {
        if (!HeeftPowerShell.Value)
        {
            return;
        }

        const string geheim = "eyJ-geheim-token-123";
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
        var uitvoerder = new ScriptUitvoerder(graphToken: _ => Task.FromResult(geheim), upn: () => "beheerder@test.be");
        var (code, g, runMap) = await Draai(s, uitvoerder: uitvoerder);

        Assert.Equal(0, code);
        Assert.Contains(g, x => x.Tekst == $"env:[] lengte:{geheim.Length} account:beheerder@test.be");
        var args = g.Single(x => x.Tekst.StartsWith("args:", StringComparison.Ordinal)).Tekst;
        Assert.DoesNotContain(geheim, args);
        Assert.DoesNotContain(geheim, File.ReadAllText(Path.Combine(runMap, "uitvoer.jsonl")));
    }

    [Fact]
    public async Task AnnulerenStoptHetProces()
    {
        if (!HeeftPowerShell.Value)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var klok = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Draai(Script("Lang", "'start'\nStart-Sleep -Seconds 120\n'klaar'"), annuleren: cts.Token));
        Assert.True(klok.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task SyntaxControleZonderUitvoeren()
    {
        if (!HeeftPowerShell.Value)
        {
            return;
        }

        var marker = Path.Combine(_map, "uitgevoerd.txt");
        Directory.CreateDirectory(_map);
        var u = new ScriptUitvoerder();
        Assert.Empty(await u.ControleerAsync($"New-Item -ItemType File -Path '{marker}'", CancellationToken.None));
        Assert.False(File.Exists(marker));

        var fouten = await u.ControleerAsync("'ok'\nif ($x {\n  'x'\n", CancellationToken.None);
        Assert.NotEmpty(fouten);
        Assert.Equal(2, fouten[0].Regel);
    }

    /// <summary>Een echt script uit de bibliotheek: eerst het voorbeeld (niets weg), dan echt (weg, met een log in de map van de run).</summary>
    [Fact]
    public async Task BibliotheekscriptLockbestandenOpruimen()
    {
        if (!HeeftPowerShell.Value)
        {
            return;
        }

        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !Directory.Exists(Path.Combine(repo.FullName, "scripts")))
        {
            repo = repo.Parent;
        }

        var bron = Path.Combine(repo!.FullName, "scripts", "FileServer", "Remove-OfficeLockFiles.ps1");
        Directory.CreateDirectory(_map);
        var pad = Path.Combine(_map, "Remove-OfficeLockFiles.ps1");
        File.Copy(bron, pad);
        var script = ScriptBibliotheek.LeesBestand(pad, true);
        Assert.True(script.Kop.Schrijft);

        var share = Path.Combine(_map, "share", "team");
        Directory.CreateDirectory(share);
        var lock1 = Path.Combine(share, "~$rapport.docx");
        File.WriteAllText(lock1, "x");
        File.WriteAllText(Path.Combine(share, "rapport.docx"), "x");

        var (code, g, _) = await Draai(script, new Dictionary<string, string> { ["Map"] = Path.Combine(_map, "share") }, voorbeeld: true);
        Assert.Equal(0, code);
        Assert.Contains(g, x => x.Tekst.Contains("WhatIf"));
        Assert.True(File.Exists(lock1));

        (code, g, var runMap) = await Draai(script, new Dictionary<string, string> { ["Map"] = Path.Combine(_map, "share") });
        Assert.Equal(0, code);
        Assert.False(File.Exists(lock1));
        Assert.True(File.Exists(Path.Combine(_map, "share", "team", "rapport.docx")));
        Assert.Contains("VERWIJDERD", File.ReadAllText(Path.Combine(runMap, "bestanden", "LockFilesLog.txt")));
    }

    [Fact]
    public void ResultaatUitCode()
    {
        Assert.Equal(RunResultaat.Gelukt, ScriptUitvoer.Resultaat(0, 0, 0, false).Resultaat);
        Assert.Equal(RunResultaat.Waarschuwingen, ScriptUitvoer.Resultaat(0, 0, 2, false).Resultaat);
        Assert.Equal(RunResultaat.Fout, ScriptUitvoer.Resultaat(2, 1, 0, false).Resultaat);
        Assert.Contains("-WhatIf", ScriptUitvoer.Resultaat(3, 0, 0, false).Melding);
        Assert.Equal(RunResultaat.Geannuleerd, ScriptUitvoer.Resultaat(-1, 0, 0, true).Resultaat);
        Assert.Equal(GebeurtenisSoort.Tekst, ScriptUitvoer.Lees("gewone tekst").Soort);
        Assert.Equal(GebeurtenisSoort.Tekst, ScriptUitvoer.Lees("{kapot").Soort);
    }
}
