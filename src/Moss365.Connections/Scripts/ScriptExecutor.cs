using System.Diagnostics;
using System.Text;
using Moss365.Core.Scripts;

namespace Moss365.Connections.Scripts;

/// <summary>
/// Runs a script from the library with the embedded startup (Startup.ps1) in a separate PowerShell process. The tokens
/// go via environment variables of that process, the AD password via stdin; never via the command line or disk.
/// </summary>
public sealed class ScriptExecutor : IScriptExecutor
{
    private readonly Func<CancellationToken, Task<string>>? _graphToken;
    private readonly Func<CancellationToken, Task<string?>>? _exchangeToken;
    private readonly Func<string?> _upn;
    private readonly string? _adServer;
    private readonly string? _adUser;
    private readonly string? _adPassword;
    private readonly Func<string, string> _powerShell;

    public ScriptExecutor(
        Func<CancellationToken, Task<string>>? graphToken = null,
        Func<CancellationToken, Task<string?>>? exchangeToken = null,
        Func<string?>? upn = null,
        string? adServer = null,
        string? adUser = null,
        string? adPassword = null,
        Func<string, string>? powerShell = null)
    {
        _graphToken = graphToken;
        _exchangeToken = exchangeToken;
        _upn = upn ?? (() => null);
        _adServer = string.IsNullOrWhiteSpace(adServer) ? null : adServer;
        _adUser = string.IsNullOrWhiteSpace(adUser) ? null : adUser;
        _adPassword = adPassword;
        _powerShell = powerShell ?? SearchPowerShell;
    }

    public async Task<int> ExecuteAsync(ScriptCommand command, IProgress<ScriptEvent>? events, CancellationToken cancel)
    {
        var header = command.Script.Header;
        var startup = await UnpackStartupAsync(cancel);
        var files = Path.Combine(command.RunFolder, "bestanden");
        Directory.CreateDirectory(files);
        var input = Path.Combine(command.RunFolder, "invoer.json");
        await File.WriteAllTextAsync(input, ScriptOutput.ValuesJson(header, command.Values, DateTime.Now), new UTF8Encoding(false), cancel);

        var departments = new List<string>();
        foreach (var (flag, name) in new[] { (ScriptConnection.Exchange, "exchange"), (ScriptConnection.Graph, "graph"), (ScriptConnection.Ad, "ad"), (ScriptConnection.Local, "lokaal") })
        {
            if (header.Connection.HasFlag(flag))
            {
                departments.Add(name);
            }
        }

        var info = new ProcessStartInfo(_powerShell(header.PowerShell))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = files,
        };
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", startup,
            "-Script", command.Script.FilePath, "-Invoer", input, "-Uitvoer", files, "-Verbinding", string.Join(',', departments),
        };
        if (command.Preview)
        {
            arguments.Add("-Voorbeeld");
        }

        if (_upn() is { Length: > 0 } upn)
        {
            arguments.AddRange(new[] { "-Upn", upn });
        }

        var adWithPassword = header.Connection.HasFlag(ScriptConnection.Ad) && _adUser is not null && !string.IsNullOrEmpty(_adPassword);
        if (header.Connection.HasFlag(ScriptConnection.Ad))
        {
            if (_adServer is not null)
            {
                arguments.AddRange(new[] { "-AdServer", _adServer });
            }

            if (adWithPassword)
            {
                arguments.AddRange(new[] { "-AdGebruiker", _adUser!, "-AdWachtwoord" });
            }
        }

        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        // Tokens only for the services the script asks for
        if (header.Connection.HasFlag(ScriptConnection.Exchange) && _exchangeToken is not null && await _exchangeToken(cancel) is { Length: > 0 } exo)
        {
            info.Environment["MOSS365_EXO_TOKEN"] = exo;
        }

        if (header.Connection.HasFlag(ScriptConnection.Graph) && _graphToken is not null)
        {
            info.Environment["MOSS365_GRAPH_TOKEN"] = await _graphToken(cancel);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell kon niet gestart worden.");
        try
        {
            if (adWithPassword)
            {
                await process.StandardInput.WriteLineAsync(_adPassword);
            }

            process.StandardInput.Close();

            var changeLog = Path.Combine(command.RunFolder, "uitvoer.jsonl");
            var writeLock = new object();
            void Report(string rule, ScriptEvent g)
            {
                lock (writeLock)
                {
                    File.AppendAllText(changeLog, rule + "\n");
                }

                events?.Report(g);
            }

            var output = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } rule)
                {
                    if (rule.Length > 0)
                    {
                        Report(rule, ScriptOutput.Read(rule));
                    }
                }
            }, CancellationToken.None);
            var errors = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } rule)
                {
                    if (rule.Trim().Length > 0)
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(new { s = "fout", t = rule });
                        Report(json, new ScriptEvent(EventKind.Error, rule));
                    }
                }
            }, CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, header.DurationMinutes)));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Stop(process);
                await Task.WhenAll(output, errors);
                if (cancel.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException($"Het script liep langer dan {header.DurationMinutes} minuten en werd gestopt.");
            }

            await Task.WhenAll(output, errors);
            return process.ExitCode;
        }
        finally
        {
            Stop(process);
            if (File.Exists(input))
            {
                File.Delete(input);
            }
        }
    }

    public async Task<IReadOnlyList<ScriptEvent>> CheckAsync(string text, CancellationToken cancel)
    {
        var startup = await UnpackStartupAsync(cancel);
        var path = Path.Combine(Path.GetTempPath(), "Moss365", "controle-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(true), cancel);
        try
        {
            var info = new ProcessStartInfo(_powerShell(string.Empty))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", startup, "-Script", path, "-Controleer" })
            {
                info.ArgumentList.Add(a);
            }

            using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell kon niet gestart worden.");
            var output = await process.StandardOutput.ReadToEndAsync(cancel);
            await process.WaitForExitAsync(cancel);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ScriptOutput.Read)
                .Where(g => g.Kind == EventKind.Error).ToList();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<string> UnpackStartupAsync(CancellationToken cancel)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Moss365");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Startup.ps1");
        await using var source = typeof(ScriptExecutor).Assembly.GetManifestResourceStream("Startup.ps1")
                               ?? throw new InvalidOperationException("Startup.ps1 ontbreekt in de app.");
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await source.CopyToAsync(target, cancel);
        return path;
    }

    /// <summary>PowerShell 7 if present (or asked for), otherwise Windows PowerShell 5.1; outside Windows
    /// "pwsh".</summary>
    public static string SearchPowerShell(string preference)
    {
        if (Environment.GetEnvironmentVariable("MOSS365_PWSH") is { Length: > 0 } own)
        {
            return own;
        }

        if (!OperatingSystem.IsWindows())
        {
            return "pwsh";
        }

        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return preference == "5.1" ? ps51 : File.Exists(pwsh) ? pwsh : ps51;
    }
}
