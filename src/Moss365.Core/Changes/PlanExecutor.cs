using Moss365.Core.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Moss365.Core.Changes;

/// <summary>
/// One entry in the change log: who, when, what, with which result. Never contains secrets. PlanId, ChangeId and
/// Previous (the values before the change) exist since 0.5; older entries do not have them.
/// </summary>
public sealed record LogEntry(
    DateTimeOffset Time,
    string User,
    string Context,
    string TargetSystem,
    string ChangeAction,
    string Target,
    string TargetName,
    string Description,
    IReadOnlyDictionary<string, string> Parameters,
    string Status,
    string Message,
    Guid? PlanId = null,
    Guid? ChangeId = null,
    IReadOnlyDictionary<string, string>? Previous = null)
{
    /// <summary>The run went well (also for entries written before 0.11, which say "Gelukt").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Succeeded => LegacyNames.TryParseEnum<ResultStatus>(Status, out var s) && s == ResultStatus.Succeeded;

    /// <summary>The run failed or was refused (also for entries written before 0.11, which say "Mislukt").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Failed => LegacyNames.TryParseEnum<ResultStatus>(Status, out var s) && s == ResultStatus.Failed;
}

public interface IChangeLog
{
    void Write(LogEntry rule);

    IReadOnlyList<LogEntry> Recent(int count);
}

/// <summary>Change log as JSON lines, one file per month (logboek-yyyy-MM.jsonl).</summary>
public sealed class JsonlChangeLog : IChangeLog
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    }.WithLegacyNames();

    private readonly string _folder;
    private readonly object _lock = new();

    public JsonlChangeLog(string folder) => _folder = folder;

    public string Folder => _folder;

    public void Write(LogEntry rule)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, $"logboek-{rule.Time:yyyy-MM}.jsonl");
            File.AppendAllText(path, JsonSerializer.Serialize(rule, Options) + Environment.NewLine);
        }
    }

    public IReadOnlyList<LogEntry> Recent(int count)
    {
        lock (_lock)
        {
            if (!Directory.Exists(_folder))
            {
                return Array.Empty<LogEntry>();
            }

            var rules = new List<LogEntry>();
            foreach (var file in Directory.GetFiles(_folder, "logboek-*.jsonl").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                foreach (var line in File.ReadLines(file).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var rule = JsonSerializer.Deserialize<LogEntry>(line, Options);
                        if (rule is not null)
                        {
                            rules.Add(rule);
                        }
                    }
                    catch (JsonException)
                    {
                        // A damaged line is skipped; the rest of the change log stays readable
                    }

                    if (rules.Count >= count)
                    {
                        return rules;
                    }
                }
            }

            return rules;
        }
    }
}

/// <summary>
/// Runs a plan in the order of the plan. Consecutive changes for the same system go to the executor together (Exchange
/// then starts a PowerShell process only once). Every change goes to the change log.
/// </summary>
public sealed class PlanExecutor
{
    private readonly IReadOnlyDictionary<TargetSystem, IExecutor> _executors;
    private readonly IChangeLog _changeLog;
    private readonly Func<string> _user;
    private readonly Func<Change, string?>? _refuse;

    /// <param name="refuse">Why a change is not allowed (the profile is not enough), or null. A refused change is not
    /// run and goes to the change log as "Failed" with the reason.</param>
    public PlanExecutor(IEnumerable<IExecutor> executors, IChangeLog changeLog, Func<string> user, Func<Change, string?>? refuse = null)
    {
        _executors = executors.ToDictionary(u => u.TargetSystem);
        _changeLog = changeLog;
        _user = user;
        _refuse = refuse;
    }

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(
        IReadOnlyList<Change> plan,
        string context,
        IProgress<string>? progress = null,
        CancellationToken cancel = default)
    {
        var results = new Dictionary<Guid, ChangeResult>();
        var notDone = plan.Where(w => !w.Selected).Select(w => w.Id).ToHashSet();

        var index = 0;
        while (index < plan.Count)
        {
            var system = plan[index].TargetSystem;
            var series = new List<Change>();
            while (index < plan.Count && plan[index].TargetSystem == system)
            {
                var w = plan[index];

                // If this step depends on a step in the current batch, run that batch first
                if (w.DependentOf is { } within && series.Any(x => x.Id == within))
                {
                    break;
                }

                index++;
                if (!w.Selected)
                {
                    continue;
                }

                if (w.DependentOf is { } start && (notDone.Contains(start)
                                                   || (results.TryGetValue(start, out var r) && r.Status != ResultStatus.Succeeded)))
                {
                    results[w.Id] = ChangeResult.Skip(w, "Overgeslagen: een eerdere stap waar deze van afhangt, is niet gelukt.");
                    notDone.Add(w.Id);
                    continue;
                }

                // The engine checks the profile itself: whoever bypasses the screens still runs nothing that is not
                // allowed
                if (_refuse?.Invoke(w) is { } reason)
                {
                    results[w.Id] = ChangeResult.Error(w, "Geweigerd: " + reason);
                    notDone.Add(w.Id);
                    continue;
                }

                series.Add(w);
            }

            if (series.Count == 0)
            {
                continue;
            }

            progress?.Report($"{series.Count} wijziging(en) in {series[0].SystemText}...");
            IReadOnlyList<ChangeResult> outcome;
            if (!_executors.TryGetValue(system, out var executor))
            {
                outcome = series.Select(w => ChangeResult.Error(w, "Geen verbinding met dit systeem.")).ToList();
            }
            else
            {
                try
                {
                    outcome = await executor.ExecuteAsync(series, cancel);
                }
                catch (Exception ex)
                {
                    outcome = series.Select(w => ChangeResult.Error(w, ex.Message)).ToList();
                }
            }

            foreach (var w in series)
            {
                results[w.Id] = outcome.FirstOrDefault(u => u.ChangeId == w.Id)
                                   ?? ChangeResult.Error(w, "Geen resultaat ontvangen.");
                if (results[w.Id].Status != ResultStatus.Succeeded)
                {
                    notDone.Add(w.Id);
                }
            }
        }

        var user = _user();
        var planId = Guid.NewGuid();
        foreach (var w in plan.Where(w => results.ContainsKey(w.Id)))
        {
            var r = results[w.Id];
            _changeLog.Write(new LogEntry(DateTimeOffset.Now, user, context, w.SystemText, w.ChangeAction.ToString(), w.Target, w.TargetName,
                w.Description, new Dictionary<string, string>(w.Parameters), r.Status.ToString(), r.Message,
                planId, w.Id, w.Previous is null ? null : new Dictionary<string, string>(w.Previous)));
        }

        return plan.Where(w => results.ContainsKey(w.Id)).Select(w => results[w.Id]).ToList();
    }
}
