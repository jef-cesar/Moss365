using System.Globalization;
using System.Text.Json;

namespace Moss365.Core.Scripts;

public enum EventKind
{
    Text,
    Info,
    Warning,
    Error,
    Progress,
    Row,
}

/// <summary>Something a script reports during the run: a line of text, an error, progress, or a row for a
/// table.</summary>
public sealed record ScriptEvent(
    EventKind Kind,
    string Text,
    string? Colour = null,
    int? Percent = null,
    string? Table = null,
    IReadOnlyDictionary<string, string>? Row = null,
    bool Continue = false,
    bool Done = false,
    int? Rule = null,
    int? Column = null);

/// <summary>A table from the output: the rows the script returned (per kind of object), or a CSV it created.</summary>
public sealed record ScriptTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, string? File = null);

/// <summary>What has to run: which script, with which values, and whether it is a preview (-WhatIf).</summary>
public sealed record ScriptCommand(ScriptFile Script, IReadOnlyDictionary<string, string> Values, bool Preview, string RunFolder, string? ScriptPath = null);

/// <summary>Runs a script in a separate PowerShell process with the sign-in of the app.</summary>
public interface IScriptExecutor
{
    /// <summary>The exit code: 0 succeeded, 1 stopped with an error, 2 done with errors, 3 preview not possible, 4
    /// connection failed.</summary>
    Task<int> ExecuteAsync(ScriptCommand command, IProgress<ScriptEvent>? events, CancellationToken cancel);

    /// <summary>Syntax errors in the text (PowerShell checks it without running anything); empty = in order.</summary>
    Task<IReadOnlyList<ScriptEvent>> CheckAsync(string text, CancellationToken cancel)
        => Task.FromResult<IReadOnlyList<ScriptEvent>>(Array.Empty<ScriptEvent>());
}

public static class ScriptOutput
{
    /// <summary>The values for the startup, with the right JSON type per kind (list, yes/no, number, date).</summary>
    public static string ValuesJson(ScriptHeader header, IReadOnlyDictionary<string, string> values, DateTime now)
    {
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, raw) in values)
        {
            var w = raw?.Trim() ?? string.Empty;
            var kind = header.Variables.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Kind ?? VariableKind.Text;
            output[name] = kind switch
            {
                VariableKind.Items => ScriptValues.Items(w),
                VariableKind.YesNo => w is "true" or "True" or "ja" or "1",
                VariableKind.Number when int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) => g,
                VariableKind.Date => Date(ScriptValues.Loose(w, now)),
                _ => w,
            };
        }

        return JsonSerializer.Serialize(output);
    }

    private static string Date(string w)
        => DateTime.TryParseExact(w, new[] { "yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : w;

    /// <summary>One line of the startup (JSON) to an event; other text becomes plain text.</summary>
    public static ScriptEvent Read(string rule)
    {
        if (!rule.StartsWith('{'))
        {
            return new ScriptEvent(EventKind.Text, rule);
        }

        try
        {
            using var doc = JsonDocument.Parse(rule);
            var e = doc.RootElement;
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : string.Empty;
            var kind = S("s") switch
            {
                "info" => EventKind.Info,
                "waarschuwing" => EventKind.Warning,
                "fout" => EventKind.Error,
                "voortgang" => EventKind.Progress,
                "rij" => EventKind.Row,
                _ => EventKind.Text,
            };
            Dictionary<string, string>? row = null;
            if (kind == EventKind.Row && e.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                row = r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? string.Empty : p.Value.ToString());
            }

            int? percent = e.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() >= 0 ? Math.Min(100, p.GetInt32()) : null;
            int? Number(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            return new ScriptEvent(kind, S("t"), S("kleur") is { Length: > 0 } k ? k : null, percent, S("tabel") is { Length: > 0 } t ? t : null, row,
                e.TryGetProperty("doorgaan", out var d) && d.ValueKind == JsonValueKind.True,
                e.TryGetProperty("klaar", out var kl) && kl.ValueKind == JsonValueKind.True, Number("regel"), Number("kolom"));
        }
        catch (JsonException)
        {
            return new ScriptEvent(EventKind.Text, rule);
        }
    }

    public static IReadOnlyList<ScriptEvent> ReadFile(string path)
        => File.Exists(path) ? File.ReadLines(path).Where(r => r.Length > 0).Select(Read).ToList() : Array.Empty<ScriptEvent>();

    /// <summary>The events of a finished run, from the store (uitvoer.jsonl).</summary>
    public static IReadOnlyList<ScriptEvent> ReadStored(Storage.IFileStore store, string path)
        => store.ReadText(path) is { } text ? text.Split('\n').Select(r => r.TrimEnd('\r')).Where(r => r.Length > 0).Select(Read).ToList() : Array.Empty<ScriptEvent>();

    /// <summary>Like <see cref="Tables(IEnumerable{ScriptEvent}, string?)"/>, with the files of the run in the store.</summary>
    public static IReadOnlyList<ScriptTable> Tables(IEnumerable<ScriptEvent> events, Storage.IFileStore store, string filesPrefix)
    {
        var output = Tables(events).ToList();
        foreach (var csv in store.List(filesPrefix.TrimEnd('/') + "/").Where(f => f.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            if (store.ReadText(csv.Path) is { } text && ReadCsvLines(text.Split('\n').Select(r => r.TrimEnd('\r')).Take(20001).ToList(), csv.Name, csv.Path) is { } table)
            {
                output.Add(table);
            }
        }

        return output;
    }

    /// <summary>
    /// The tables of a run: the returned objects per kind (same name and columns together), and every CSV in the folder
    /// with files.
    /// </summary>
    public static IReadOnlyList<ScriptTable> Tables(IEnumerable<ScriptEvent> events, string? filesFolder = null)
    {
        var output = new List<ScriptTable>();
        foreach (var groupValue in events.Where(g => g.Kind == EventKind.Row && g.Row is not null)
                     .GroupBy(g => (g.Table ?? "Resultaat") + "|" + string.Join(",", g.Row!.Keys)))
        {
            var first = groupValue.First();
            output.Add(new ScriptTable(first.Table ?? "Resultaat", first.Row!.Keys.ToList(), groupValue.Select(g => g.Row!).ToList()));
        }

        // The same name more than once (other columns): number them
        foreach (var doubleValue in output.GroupBy(t => t.Name).Where(g => g.Count() > 1).ToList())
        {
            var i = 1;
            foreach (var t in doubleValue)
            {
                output[output.IndexOf(t)] = t with { Name = $"{t.Name} ({i++})" };
            }
        }

        if (filesFolder is not null && Directory.Exists(filesFolder))
        {
            foreach (var csv in Directory.EnumerateFiles(filesFolder, "*.csv", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (ReadCsv(csv) is { } t)
                {
                    output.Add(t);
                }
            }
        }

        return output;
    }

    /// <summary>A CSV (comma or semicolon, with or without quotes) as a table; null when it cannot be read.</summary>
    public static ScriptTable? ReadCsv(string path, int maxRows = 20000)
    {
        try
        {
            return ReadCsvLines(File.ReadLines(path).Take(maxRows + 1).ToList(), Path.GetFileName(path), path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ScriptTable? ReadCsvLines(List<string> rules, string name, string path)
    {
        {
            if (rules.Count == 0 || rules[0].Length == 0)
            {
                return null;
            }

            var separation = rules[0].Count(c => c == ';') > rules[0].Count(c => c == ',') ? ';' : ',';
            var header = Splits(rules[0], separation);
            var rows = rules.Skip(1).Where(r => r.Length > 0).Select(r =>
            {
                var v = Splits(r, separation);
                return (IReadOnlyDictionary<string, string>)header.Select((k, i) => (k, i)).GroupBy(x => x.k).ToDictionary(g => g.Key, g => g.First().i < v.Count ? v[g.First().i] : string.Empty);
            }).ToList();
            return new ScriptTable(name, header.Distinct().ToList(), rows, path);
        }
    }

    private static List<string> Splits(string rule, char separation)
    {
        var output = new List<string>();
        var current = new System.Text.StringBuilder();
        var within = false;
        for (var i = 0; i < rule.Length; i++)
        {
            var c = rule[i];
            if (within)
            {
                if (c == '"' && i + 1 < rule.Length && rule[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    within = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                within = true;
            }
            else if (c == separation)
            {
                output.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        output.Add(current.ToString());
        return output.Select(x => x.Trim('﻿')).ToList();
    }

    /// <summary>The result of a run from the exit code and the events.</summary>
    public static (RunResult Result, string Message) Result(int code, int errors, int warnings, bool cancelled) => cancelled
        ? (RunResult.Cancelled, "Geannuleerd.")
        : code switch
        {
            0 when warnings > 0 => (RunResult.Warnings, $"Klaar, met {warnings} waarschuwing(en)."),
            0 => (RunResult.Succeeded, "Klaar."),
            2 => (RunResult.Error, $"Klaar, maar met {errors} fout(en)."),
            3 => (RunResult.Error, "Geen voorbeeld mogelijk: het script ondersteunt -WhatIf niet. Er werd niets uitgevoerd."),
            4 => (RunResult.Error, "Verbinden mislukt; het script is niet gestart."),
            _ => (RunResult.Error, "Gestopt met een fout."),
        };
}
