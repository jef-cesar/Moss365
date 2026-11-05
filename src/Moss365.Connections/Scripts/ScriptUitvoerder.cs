using System.Diagnostics;
using System.Text;
using Moss365.Core.Scripts;

namespace Moss365.Connections.Scripts;

/// <summary>
/// Draait een script uit de bibliotheek met de ingebedde opstart (Opstart.ps1) in een apart PowerShell-proces. De
/// tokens gaan via omgevingsvariabelen van dat proces, het AD-wachtwoord via stdin; nooit via de opdrachtregel of schijf.
/// </summary>
public sealed class ScriptUitvoerder : IScriptUitvoerder
{
    private readonly Func<CancellationToken, Task<string>>? _graphToken;
    private readonly Func<CancellationToken, Task<string?>>? _exchangeToken;
    private readonly Func<string?> _upn;
    private readonly string? _adServer;
    private readonly string? _adGebruiker;
    private readonly string? _adWachtwoord;
    private readonly Func<string, string> _powerShell;

    public ScriptUitvoerder(
        Func<CancellationToken, Task<string>>? graphToken = null,
        Func<CancellationToken, Task<string?>>? exchangeToken = null,
        Func<string?>? upn = null,
        string? adServer = null,
        string? adGebruiker = null,
        string? adWachtwoord = null,
        Func<string, string>? powerShell = null)
    {
        _graphToken = graphToken;
        _exchangeToken = exchangeToken;
        _upn = upn ?? (() => null);
        _adServer = string.IsNullOrWhiteSpace(adServer) ? null : adServer;
        _adGebruiker = string.IsNullOrWhiteSpace(adGebruiker) ? null : adGebruiker;
        _adWachtwoord = adWachtwoord;
        _powerShell = powerShell ?? ZoekPowerShell;
    }

    public async Task<int> VoerUitAsync(ScriptOpdracht opdracht, IProgress<ScriptGebeurtenis>? gebeurtenissen, CancellationToken annuleren)
    {
        var kop = opdracht.Script.Kop;
        var opstart = await PakOpstartUitAsync(annuleren);
        var bestanden = Path.Combine(opdracht.RunMap, "bestanden");
        Directory.CreateDirectory(bestanden);
        var invoer = Path.Combine(opdracht.RunMap, "invoer.json");
        await File.WriteAllTextAsync(invoer, ScriptUitvoer.WaardenJson(kop, opdracht.Waarden, DateTime.Now), new UTF8Encoding(false), annuleren);

        var diensten = new List<string>();
        foreach (var (vlag, naam) in new[] { (ScriptVerbinding.Exchange, "exchange"), (ScriptVerbinding.Graph, "graph"), (ScriptVerbinding.Ad, "ad"), (ScriptVerbinding.Lokaal, "lokaal") })
        {
            if (kop.Verbinding.HasFlag(vlag))
            {
                diensten.Add(naam);
            }
        }

        var info = new ProcessStartInfo(_powerShell(kop.PowerShell))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = bestanden,
        };
        var argumenten = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", opstart,
            "-Script", opdracht.Script.Pad, "-Invoer", invoer, "-Uitvoer", bestanden, "-Verbinding", string.Join(',', diensten),
        };
        if (opdracht.Voorbeeld)
        {
            argumenten.Add("-Voorbeeld");
        }

        if (_upn() is { Length: > 0 } upn)
        {
            argumenten.AddRange(new[] { "-Upn", upn });
        }

        var adMetWachtwoord = kop.Verbinding.HasFlag(ScriptVerbinding.Ad) && _adGebruiker is not null && !string.IsNullOrEmpty(_adWachtwoord);
        if (kop.Verbinding.HasFlag(ScriptVerbinding.Ad))
        {
            if (_adServer is not null)
            {
                argumenten.AddRange(new[] { "-AdServer", _adServer });
            }

            if (adMetWachtwoord)
            {
                argumenten.AddRange(new[] { "-AdGebruiker", _adGebruiker!, "-AdWachtwoord" });
            }
        }

        foreach (var a in argumenten)
        {
            info.ArgumentList.Add(a);
        }

        // Tokens enkel voor de diensten die het script vraagt
        if (kop.Verbinding.HasFlag(ScriptVerbinding.Exchange) && _exchangeToken is not null && await _exchangeToken(annuleren) is { Length: > 0 } exo)
        {
            info.Environment["MOSS365_EXO_TOKEN"] = exo;
        }

        if (kop.Verbinding.HasFlag(ScriptVerbinding.Graph) && _graphToken is not null)
        {
            info.Environment["MOSS365_GRAPH_TOKEN"] = await _graphToken(annuleren);
        }

        using var proces = Process.Start(info) ?? throw new InvalidOperationException("PowerShell kon niet gestart worden.");
        try
        {
            if (adMetWachtwoord)
            {
                await proces.StandardInput.WriteLineAsync(_adWachtwoord);
            }

            proces.StandardInput.Close();

            var logboek = Path.Combine(opdracht.RunMap, "uitvoer.jsonl");
            var schrijfslot = new object();
            void Meld(string regel, ScriptGebeurtenis g)
            {
                lock (schrijfslot)
                {
                    File.AppendAllText(logboek, regel + "\n");
                }

                gebeurtenissen?.Report(g);
            }

            var uit = Task.Run(async () =>
            {
                while (await proces.StandardOutput.ReadLineAsync() is { } regel)
                {
                    if (regel.Length > 0)
                    {
                        Meld(regel, ScriptUitvoer.Lees(regel));
                    }
                }
            }, CancellationToken.None);
            var fouten = Task.Run(async () =>
            {
                while (await proces.StandardError.ReadLineAsync() is { } regel)
                {
                    if (regel.Trim().Length > 0)
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(new { s = "fout", t = regel });
                        Meld(json, new ScriptGebeurtenis(GebeurtenisSoort.Fout, regel));
                    }
                }
            }, CancellationToken.None);

            using var tijdslimiet = CancellationTokenSource.CreateLinkedTokenSource(annuleren);
            tijdslimiet.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, kop.DuurMinuten)));
            try
            {
                await proces.WaitForExitAsync(tijdslimiet.Token);
            }
            catch (OperationCanceledException)
            {
                Stop(proces);
                await Task.WhenAll(uit, fouten);
                if (annuleren.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException($"Het script liep langer dan {kop.DuurMinuten} minuten en werd gestopt.");
            }

            await Task.WhenAll(uit, fouten);
            return proces.ExitCode;
        }
        finally
        {
            Stop(proces);
            if (File.Exists(invoer))
            {
                File.Delete(invoer);
            }
        }
    }

    public async Task<IReadOnlyList<ScriptGebeurtenis>> ControleerAsync(string tekst, CancellationToken annuleren)
    {
        var opstart = await PakOpstartUitAsync(annuleren);
        var pad = Path.Combine(Path.GetTempPath(), "Moss365", "controle-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(pad, tekst, new UTF8Encoding(true), annuleren);
        try
        {
            var info = new ProcessStartInfo(_powerShell(string.Empty))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", opstart, "-Script", pad, "-Controleer" })
            {
                info.ArgumentList.Add(a);
            }

            using var proces = Process.Start(info) ?? throw new InvalidOperationException("PowerShell kon niet gestart worden.");
            var uit = await proces.StandardOutput.ReadToEndAsync(annuleren);
            await proces.WaitForExitAsync(annuleren);
            return uit.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ScriptUitvoer.Lees)
                .Where(g => g.Soort == GebeurtenisSoort.Fout).ToList();
        }
        finally
        {
            File.Delete(pad);
        }
    }

    private static void Stop(Process proces)
    {
        try
        {
            if (!proces.HasExited)
            {
                proces.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> PakOpstartUitAsync(CancellationToken annuleren)
    {
        var map = Path.Combine(Path.GetTempPath(), "Moss365");
        Directory.CreateDirectory(map);
        var pad = Path.Combine(map, "Opstart.ps1");
        await using var bron = typeof(ScriptUitvoerder).Assembly.GetManifestResourceStream("Opstart.ps1")
                               ?? throw new InvalidOperationException("Opstart.ps1 ontbreekt in de app.");
        await using var doel = new FileStream(pad, FileMode.Create, FileAccess.Write, FileShare.Read);
        await bron.CopyToAsync(doel, annuleren);
        return pad;
    }

    /// <summary>PowerShell 7 als die er is (of gevraagd), anders Windows PowerShell 5.1; buiten Windows "pwsh".</summary>
    public static string ZoekPowerShell(string voorkeur)
    {
        if (Environment.GetEnvironmentVariable("MOSS365_PWSH") is { Length: > 0 } eigen)
        {
            return eigen;
        }

        if (!OperatingSystem.IsWindows())
        {
            return "pwsh";
        }

        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return voorkeur == "5.1" ? ps51 : File.Exists(pwsh) ? pwsh : ps51;
    }
}
