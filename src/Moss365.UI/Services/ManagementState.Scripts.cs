using Moss365.Core.Scripts;
using Moss365.Core.Changes;

namespace Moss365.UI.Services;

/// <summary>A script that runs (or just finished): the events, the progress and the result.</summary>
public sealed class ScriptRunStatus
{
    private readonly object _lock = new();
    private readonly List<ScriptEvent> _events = new();

    public required ScriptFile Script { get; init; }
    public required ScriptRun Run { get; set; }
    public CancellationTokenSource Cancel { get; } = new();
    public bool Done { get; set; }
    public string? Progress { get; set; }
    public int? Percent { get; set; }

    public IReadOnlyList<ScriptEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }
    }

    public void Add(ScriptEvent g)
    {
        lock (_lock)
        {
            _events.Add(g);
        }
    }
}

public sealed partial class ManagementState
{
    private DateTime _lastMessage = DateTime.MinValue;

    /// <summary>Values to fill in the next start form with (Start again with the same values).</summary>
    public IReadOnlyDictionary<string, string>? ScriptPrefill { get; set; }

    public ScriptFolders ScriptFolders => new(_environment.BuiltInScripts, Files, Path.Combine(_environment.TempFolder, "runs", Tenant.Id));

    /// <summary>The script that runs now or ran last (one at a time).</summary>
    public ScriptRunStatus? ScriptRun { get; private set; }

    public bool ScriptBusy => ScriptRun is { Done: false };

    /// <summary>Scripts can run (Windows app with PowerShell); a fake executor in the test environment.</summary>
    public bool ScriptsAvailable => _factory?.Scripts(Settings, AdPassword) is not null;

    public IReadOnlyList<ScriptFile> ReadScripts() => ScriptLibrary.Load(ScriptFolders);

    public ScriptFile? SearchScript(string key)
        => ReadScripts().FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    private void CleanupScriptRuns()
    {
        try
        {
            ScriptRuns.Cleanup(ScriptFolders, DateTimeOffset.Now, Math.Max(1, Config.Retention.ScriptRunDays));
            Database.CleanupTechnicalLog(DateTimeOffset.Now.AddDays(-Math.Max(1, Config.Retention.TechnicalLogDays)));
        }
        catch (IOException)
        {
            // Again the next time
        }
    }

    public async Task<IReadOnlyList<ScriptEvent>> CheckScriptAsync(string text)
        => _factory?.Scripts(Settings, AdPassword) is { } u ? await u.CheckAsync(text, CancellationToken.None) : Array.Empty<ScriptEvent>();

    /// <summary>
    /// Starts a script in the background. For a one-time script (not saved) the text is given: it goes to the folder of
    /// the run and in full to the change log. Returns the status, or null with an error.
    /// </summary>
    public (ScriptRunStatus? Status, string? Error) StartScript(ScriptFile script, IReadOnlyDictionary<string, string> values, bool preview, string? oneTimeText = null, Guid? scheduled = null)
    {
        if (ScriptBusy)
        {
            return (null, "Er draait al een script. Wacht tot het klaar is, of annuleer het.");
        }

        if (_factory?.Scripts(Settings, AdPassword) is not { } executor)
        {
            return (null, "Scripts kunnen hier niet draaien (enkel in de Windows-app).");
        }

        if (oneTimeText is null && ScriptLibrary.IsChangedOutsideApp(ScriptFolders, script))
        {
            return (null, "Dit script werd buiten de app gewijzigd. Bekijk het en keur het goed (Bewerken > Opslaan) voor het mag draaien.");
        }

        var errors = ScriptValues.Check(script.Header, values);
        if (errors.Count > 0)
        {
            return (null, string.Join(" ", errors));
        }

        var clean = script.Header.Variables
            .Where(v => values.ContainsKey(v.Name))
            .ToDictionary(v => v.Name, v => values[v.Name], StringComparer.OrdinalIgnoreCase);
        var key = oneTimeText is null ? script.Key : "eenmalig";
        var run = new ScriptRun
        {
            Key = key,
            Name = script.Header.Name,
            Fingerprint = oneTimeText is null ? script.Fingerprint : ScriptLibrary.Fingerprint(oneTimeText),
            Who = User,
            Preview = preview,
            Once = oneTimeText is not null,
            Values = new Dictionary<string, string>(clean),
        };
        run = ScriptRuns.New(ScriptFolders, run);

        // A one-time script runs from the folder of the run (so the text stays with the output); an own script is
        // written into the work folder for PowerShell
        string scriptPath;
        if (oneTimeText is not null)
        {
            scriptPath = Path.Combine(run.WorkFolder, "script.ps1");
            File.WriteAllText(scriptPath, oneTimeText, new System.Text.UTF8Encoding(true));
            script = ScriptLibrary.ReadFile(scriptPath, false);
        }
        else
        {
            scriptPath = ScriptLibrary.ToDisk(ScriptFolders, script, Path.Combine(run.WorkFolder, "script"));
        }

        ScriptRuns.Save(ScriptFolders, run);
        if (oneTimeText is null)
        {
            ScriptValues.Save(ScriptFolders, script.Key, script.Header, clean);
        }

        var status = new ScriptRunStatus { Script = script, Run = run };
        ScriptRun = status;
        Changed?.Invoke();
        _ = RunAsync(executor, status, clean, preview, oneTimeText, scheduled, scriptPath);
        return (status, null);
    }

    public void CancelScript()
    {
        if (ScriptRun is { Done: false } s)
        {
            s.Cancel.Cancel();
        }
    }

    private async Task RunAsync(IScriptExecutor executor, ScriptRunStatus status, Dictionary<string, string> values, bool preview, string? oneTimeText, Guid? scheduled, string scriptPath)
    {
        // Synchronous and in order (Progress<T> would report via the thread pool, even after the end of the script)
        var reporter = new ScriptReporter(g =>
        {
            status.Add(g);
            if (g.Kind == EventKind.Progress)
            {
                status.Progress = g.Done ? null : g.Text;
                status.Percent = g.Done ? null : g.Percent;
            }

            // Do not redraw on every line
            if ((DateTime.Now - _lastMessage).TotalMilliseconds > 150)
            {
                _lastMessage = DateTime.Now;
                Changed?.Invoke();
            }
        });

        var code = -1;
        string? error = null;
        var cancelled = false;
        try
        {
            var command = new ScriptCommand(status.Script, values, preview, status.Run.WorkFolder, scriptPath);
            code = await Task.Run(() => executor.ExecuteAsync(command, reporter, status.Cancel.Token));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var list = status.Events;
        var countErrors = list.Count(g => g.Kind == EventKind.Error);
        var (result, message) = error is not null ? (RunResult.Error, error) : ScriptOutput.Result(code, countErrors, list.Count(g => g.Kind == EventKind.Warning), cancelled);
        status.Run = status.Run with
        {
            End = DateTimeOffset.Now,
            Result = result,
            Message = message,
            Errors = countErrors,
            Warnings = list.Count(g => g.Kind == EventKind.Warning),
        };
        // An own script was copied into the work folder for PowerShell: not part of the result
        var copy = Path.Combine(status.Run.WorkFolder, "script");
        if (Directory.Exists(copy))
        {
            Directory.Delete(copy, true);
        }

        status.Run = ScriptRuns.Finish(ScriptFolders, status.Run);
        status.Progress = null;

        var parameters = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
        {
            ["Vingerafdruk"] = status.Run.Fingerprint,
            ["Run"] = status.Run.Folder,
        };
        if (oneTimeText is not null)
        {
            parameters["Script"] = oneTimeText;
        }

        ChangeLog.Write(new LogEntry(DateTimeOffset.Now, User, oneTimeText is null ? "Script" : "Eenmalig script", "Script",
            preview ? "ScriptVoorbeeld" : "ScriptUitvoeren", status.Run.Key, status.Run.Name,
            (preview ? "Voorbeeld (-WhatIf) van " : "Script ") + status.Run.Name + " (kan niet automatisch ongedaan gemaakt worden)",
            parameters, result is RunResult.Succeeded or RunResult.Warnings ? nameof(ResultStatus.Succeeded) : result == RunResult.Cancelled ? nameof(ResultStatus.Skipped) : nameof(ResultStatus.Failed), message));

        // A scheduled script: after a real run, schedule the next time (or remove it)
        if (scheduled is { } id && !preview && Scheduled.Read().FirstOrDefault(a => a.Id == id) is { } action)
        {
            if (action.NextTime(DateTimeOffset.Now) is { } next)
            {
                action.When = next;
                Scheduled.Save(Scheduled.Read().Where(a => a.Id != id).Append(action));
            }
            else
            {
                Scheduled.Remove(id);
            }
        }

        // Done only when everything is saved and logged
        status.Done = true;
        Changed?.Invoke();
    }

    private sealed class ScriptReporter(Action<ScriptEvent> doValue) : IProgress<ScriptEvent>
    {
        public void Report(ScriptEvent value) => doValue(value);
    }
}
