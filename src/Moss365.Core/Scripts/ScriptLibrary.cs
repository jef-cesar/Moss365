using Moss365.Core.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Scripts;

/// <summary>
/// A script in the library: built-in (shipped with the app, read-only, a file on disk) or own (in the tenant database,
/// <see cref="FilePath"/> is then a path in the file store such as <c>scripts/own/Report.ps1</c>).
/// </summary>
public sealed record ScriptFile(string FilePath, ScriptHeader Header, bool BuiltIn, string Fingerprint, string Folder = "")
{
    /// <summary>Key for the last values and the runs: the file name without extension (as Get-ScriptWaarde
    /// does).</summary>
    public string Key => Path.GetFileNameWithoutExtension(FileName);

    public string FileName => FilePath[(FilePath.Replace('\\', '/').LastIndexOf('/') + 1)..];

    public string Origin => BuiltIn ? "ingebouwd" : "eigen";
}

/// <summary>
/// Where the scripts and their data are: the built-in scripts in a folder on disk, the own scripts, last values and runs
/// in the file store of the tenant database, and a work folder on disk where a script runs (PowerShell needs real
/// files; when the run is done, its files move into the store and the work folder is removed).
/// </summary>
public sealed record ScriptFolders(string BuiltIn, IFileStore Store, string Work)
{
    public const string Own = "scripts/own";
    public const string Values = "scripts/values";
    public const string Runs = "runs";
    public const string Approved = "scripts/approvals.json";
}

/// <summary>Reading, saving, importing and deleting scripts, with the fingerprint of the own scripts.</summary>
public static class ScriptLibrary
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }.WithLegacyNames();

    public static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>All scripts: built-in (also in subfolders, only with a MOSS365 header) and own, sorted by
    /// name.</summary>
    public static IReadOnlyList<ScriptFile> Load(ScriptFolders folders)
    {
        var output = new List<ScriptFile>(BuiltIns(folders));
        output.AddRange(folders.Store.List(ScriptFolders.Own + "/")
            .Where(f => f.Path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && f.Path.Count(c => c == '/') == 2)
            .Select(f => ReadStored(folders.Store, f.Path)));
        return output.OrderBy(s => s.Header.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<ScriptFile> BuiltIns(ScriptFolders folders)
    {
        if (!Directory.Exists(folders.BuiltIn))
        {
            yield break;
        }

        // Only scripts with a MOSS365 header: the others are in the download for use outside the app
        foreach (var path in Directory.EnumerateFiles(folders.BuiltIn, "*.ps1", SearchOption.AllDirectories))
        {
            var script = ReadFile(path, true, Path.GetRelativePath(folders.BuiltIn, Path.GetDirectoryName(path)!).Replace('\\', '/') is var m && m != "." ? m : string.Empty);
            if (script.Header.FromBlock)
            {
                yield return script;
            }
        }
    }

    /// <summary>A script file on disk (built-in, or a one-time script in the work folder).</summary>
    public static ScriptFile ReadFile(string path, bool builtIn, string folder = "")
    {
        var text = File.ReadAllText(path);
        return new ScriptFile(path, ScriptText.Read(text, path), builtIn, Fingerprint(text), folder);
    }

    private static ScriptFile ReadStored(IFileStore store, string path)
    {
        var text = store.ReadText(path) ?? string.Empty;
        return new ScriptFile(path, ScriptText.Read(text, path), false, Fingerprint(text));
    }

    /// <summary>The text of a script, wherever it is.</summary>
    public static string Text(ScriptFolders folders, ScriptFile script)
        => script.BuiltIn || !IsOwn(script.FilePath) ? File.ReadAllText(script.FilePath) : folders.Store.ReadText(script.FilePath) ?? string.Empty;

    /// <summary>
    /// A file on disk with the text of the script, for PowerShell: the built-in file itself, or the own script written
    /// into the folder (with a BOM, so that Windows PowerShell 5.1 reads it as UTF-8).
    /// </summary>
    public static string ToDisk(ScriptFolders folders, ScriptFile script, string folder)
    {
        if (script.BuiltIn || !IsOwn(script.FilePath))
        {
            return script.FilePath;
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, script.FileName);
        File.WriteAllText(path, Text(folders, script).Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        return path;
    }

    /// <summary>A valid file name from the name of the script.</summary>
    public static string FileName(string name)
    {
        var clean = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : c == ' ' ? '-' : '_').ToArray()).Trim('-', '_', '.');
        return (clean.Length == 0 ? "script" : clean) + ".ps1";
    }

    /// <summary>Saves an own script (new or updated) and approves it with its fingerprint.</summary>
    public static ScriptFile Store(ScriptFolders folders, string text, string? existingPath = null)
    {
        var header = ScriptText.Read(text, existingPath ?? "script.ps1");
        var path = existingPath;
        if (path is null || !IsOwn(path))
        {
            path = $"{ScriptFolders.Own}/{FileName(header.Name)}";
            var builtInNames = BuiltIns(folders).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseline = Path.GetFileNameWithoutExtension(path);
            var n = 1;
            while (folders.Store.Exists(path) || builtInNames.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                path = $"{ScriptFolders.Own}/{baseline}-{++n}.ps1";
            }
        }

        // Always with a BOM and Windows line endings: Windows PowerShell 5.1 reads a file without a BOM as ANSI
        folders.Store.WriteText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), bom: true);
        var file = ReadStored(folders.Store, path);
        Approve(folders, file);
        return file;
    }

    /// <summary>Is this path an own script in the store (and not a file on disk)?</summary>
    public static bool IsOwn(string path)
        => FileStorePaths.Normalise(path).StartsWith(ScriptFolders.Own + "/", StringComparison.OrdinalIgnoreCase) && !Path.IsPathRooted(path);

    public static void Remove(ScriptFolders folders, ScriptFile script)
    {
        if (script.BuiltIn || !IsOwn(script.FilePath))
        {
            throw new InvalidOperationException("Een ingebouwd script kan je niet verwijderen.");
        }

        folders.Store.Delete(script.FilePath);
        var list = Approvals(folders);
        list.Remove(script.FileName);
        SaveApprovals(folders, list);
    }

    /// <summary>Imports a .ps1 as an own script (a copy; the original stays where it was).</summary>
    public static ScriptFile Import(ScriptFolders folders, string sourcePath) => ImportText(folders, File.ReadAllText(sourcePath), sourcePath);

    public static ScriptFile ImportText(ScriptFolders folders, string text, string name)
    {
        var header = ScriptText.Read(text, name);
        if (!header.FromBlock)
        {
            text = ScriptText.Write(header, text);
        }

        return Store(folders, text);
    }

    /// <summary>Was this own script changed outside the app since it was saved or approved?</summary>
    public static bool IsChangedOutsideApp(ScriptFolders folders, ScriptFile script)
        => !script.BuiltIn && IsOwn(script.FilePath) && (!Approvals(folders).TryGetValue(script.FileName, out var v) || v != script.Fingerprint);

    public static void Approve(ScriptFolders folders, ScriptFile script)
    {
        var list = Approvals(folders);
        list[script.FileName] = script.Fingerprint;
        SaveApprovals(folders, list);
    }

    private static Dictionary<string, string> Approvals(ScriptFolders folders)
    {
        try
        {
            return folders.Store.ReadText(ScriptFolders.Approved) is { } json
                && JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } d
                ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveApprovals(ScriptFolders folders, Dictionary<string, string> list)
        => folders.Store.WriteText(ScriptFolders.Approved, JsonSerializer.Serialize(list, Json));

    /// <summary>A new, empty script with an example.</summary>
    public static string NewScript(string name) => ScriptText.Write(new ScriptHeader
    {
        Name = name,
        Description = "Wat dit script doet.",
        Connection = ScriptConnection.Exchange,
        Variables = new[] { new ScriptVariable("Postvak", VariableKind.Mailbox, true, "Welk postvak?") },
    }, """

$ErrorActionPreference = 'Stop'

# De app is al verbonden (zie Verbinding hierboven). Wat je teruggeeft, verschijnt als tabel;
# Write-Host en Write-Warning verschijnen als tekst. Bestanden zet je in $MossUitvoer.
Get-Mailbox -Identity $Postvak | Select-Object DisplayName, PrimarySmtpAddress, RecipientTypeDetails

""");
}

/// <summary>
/// The last used values per script, in the tenant database (<c>scripts/values/&lt;script&gt;.json</c>, the same format
/// as the Get-ScriptWaarde block in the scripts uses outside the app), with a text or a list per name.
/// </summary>
public static class ScriptValues
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }.WithLegacyNames();

    public static Dictionary<string, string> Read(ScriptFolders folders, string key)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (folders.Store.ReadText($"{ScriptFolders.Values}/{key}.json") is not { } json)
        {
            return output;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                output[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Array => string.Join('\n', p.Value.EnumerateArray().Select(x => x.ToString())),
                    JsonValueKind.Null => string.Empty,
                    _ => p.Value.ToString(),
                };
            }
        }
        catch (JsonException)
        {
            // Broken file: ask again
        }

        return output;
    }

    /// <summary>Saves the values of a run; lists (one value per line) as a JSON list, as Get-ScriptWaarde
    /// does.</summary>
    public static void Save(ScriptFolders folders, string key, ScriptHeader header, IReadOnlyDictionary<string, string> values)
    {
        var existing = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Read(folders, key))
        {
            existing[k] = v;
        }

        foreach (var (name, value) in values)
        {
            var kind = header.Variables.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Kind;
            existing[name] = kind == VariableKind.Items ? Items(value) : value;
        }

        folders.Store.WriteText($"{ScriptFolders.Values}/{key}.json", JsonSerializer.Serialize(existing, Json));
    }

    public static string[] Items(string value)
        => value.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Relative values for scheduled scripts, at the moment of the run: @vandaag, @gisteren, @werkjaar.van,
    /// @werkjaar.tot, @vorigemaand.van, @vorigemaand.tot, @dezemaand.van, @dezemaand.tot.
    /// </summary>
    public static string Loose(string value, DateTime now)
    {
        var day = now.Date;
        var workYear = day.Month >= 9 ? day.Year : day.Year - 1;
        var thisMonth = new DateTime(day.Year, day.Month, 1);
        DateTime? d = value.Trim().ToLowerInvariant() switch
        {
            "@vandaag" => day,
            "@gisteren" => day.AddDays(-1),
            "@werkjaar.van" => new DateTime(workYear, 9, 1),
            "@werkjaar.tot" => new DateTime(workYear + 1, 8, 31),
            "@dezemaand.van" => thisMonth,
            "@dezemaand.tot" => thisMonth.AddMonths(1).AddDays(-1),
            "@vorigemaand.van" => thisMonth.AddMonths(-1),
            "@vorigemaand.tot" => thisMonth.AddDays(-1),
            _ => null,
        };
        return d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? value;
    }

    /// <summary>Check of the values for a run: required fields, numbers, dates, choices.</summary>
    public static IReadOnlyList<string> Check(ScriptHeader header, IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        foreach (var v in header.Variables)
        {
            var w = values.TryGetValue(v.Name, out var x) ? x.Trim() : string.Empty;
            if (w.Length == 0)
            {
                if (v.Required)
                {
                    errors.Add($"{v.Label}: verplicht.");
                }

                continue;
            }

            switch (v.Kind)
            {
                case VariableKind.Number when !int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                    errors.Add($"{v.Label}: geen geheel getal.");
                    break;
                case VariableKind.Date when !DateTime.TryParseExact(Loose(w, DateTime.Now), new[] { "yyyy-MM-dd", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors.Add($"{v.Label}: geen datum (dd/mm/jjjj).");
                    break;
                case VariableKind.Choice when v.Options is { Count: > 0 } o && !o.Contains(w, StringComparer.OrdinalIgnoreCase):
                    errors.Add($"{v.Label}: kies een van {string.Join(", ", o)}.");
                    break;
            }
        }

        return errors;
    }
}

public enum RunResult
{
    Busy,
    Succeeded,
    Warnings,
    Error,
    Cancelled,
}

/// <summary>
/// One run of a script, saved as run.json. While it runs, its files are in <see cref="WorkFolder"/> on disk; when it is
/// done they move into the store under <see cref="Folder"/> (<c>runs/&lt;script&gt;/&lt;id&gt;</c>).
/// </summary>
public sealed record ScriptRun
{
    public string Id { get; init; } = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string Who { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? End { get; init; }

    [JsonConverter(typeof(LegacyEnumConverter))]
    public RunResult Result { get; init; } = RunResult.Busy;

    public bool Preview { get; init; }
    public bool Once { get; init; }
    public Dictionary<string, string> Values { get; init; } = new();
    public string Message { get; init; } = string.Empty;
    public int Errors { get; init; }
    public int Warnings { get; init; }

    /// <summary>The run in the file store: <c>runs/&lt;script&gt;/&lt;id&gt;</c>.</summary>
    [JsonIgnore]
    public string Folder { get; init; } = string.Empty;

    /// <summary>The folder on disk while the script runs (empty when it is done).</summary>
    [JsonIgnore]
    public string WorkFolder { get; init; } = string.Empty;

    /// <summary>The last part of <see cref="Folder"/>: the id in the address of the run.</summary>
    public string FolderName => Folder[(Folder.LastIndexOf('/') + 1)..];

    public TimeSpan? Duration => End - Start;

    public string ResultText => (Preview ? "voorbeeld: " : string.Empty) + Result switch
    {
        RunResult.Busy => "bezig",
        RunResult.Succeeded => "gelukt",
        RunResult.Warnings => "gelukt, met waarschuwingen",
        RunResult.Error => "mislukt",
        _ => "geannuleerd",
    };
}

/// <summary>
/// The runs in the store: <c>runs/&lt;script&gt;/&lt;id&gt;/</c> with run.json, uitvoer.jsonl, script.ps1 (one-time
/// scripts) and the files the script made (bestanden/).
/// </summary>
public static class ScriptRuns
{
    public const int SaveDays = 365;
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }.WithLegacyNames();

    /// <summary>A new run: its place in the store and an empty work folder on disk (with bestanden/).</summary>
    public static ScriptRun New(ScriptFolders folders, ScriptRun run)
    {
        var id = run.Id;
        var n = 1;
        while (folders.Store.List($"{ScriptFolders.Runs}/{run.Key}/{id}/").Count > 0 || Directory.Exists(Path.Combine(folders.Work, run.Key, id)))
        {
            id = $"{run.Id}-{++n}";
        }

        var work = Path.Combine(folders.Work, run.Key, id);
        Directory.CreateDirectory(Path.Combine(work, "bestanden"));
        return run with { Folder = $"{ScriptFolders.Runs}/{run.Key}/{id}", WorkFolder = work };
    }

    /// <summary>Saves run.json: in the work folder while it runs, in the store when it is done.</summary>
    public static void Save(ScriptFolders folders, ScriptRun run)
    {
        var json = JsonSerializer.Serialize(run, Json);
        if (run.WorkFolder.Length > 0 && Directory.Exists(run.WorkFolder))
        {
            File.WriteAllText(Path.Combine(run.WorkFolder, "run.json"), json);
        }
        else
        {
            folders.Store.WriteText($"{run.Folder}/run.json", json);
        }
    }

    /// <summary>The run is done: its files go into the store and the work folder is removed.</summary>
    public static ScriptRun Finish(ScriptFolders folders, ScriptRun run)
    {
        Save(folders, run);
        folders.Store.Import(run.WorkFolder, run.Folder);
        try
        {
            Directory.Delete(run.WorkFolder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The files are in the store; the work folder is cleaned up the next time
        }

        return run with { WorkFolder = string.Empty };
    }

    public static IReadOnlyList<ScriptRun> Items(ScriptFolders folders, string? key = null)
    {
        var output = new List<ScriptRun>();
        foreach (var file in folders.Store.List(key is null ? ScriptFolders.Runs + "/" : $"{ScriptFolders.Runs}/{key}/")
                     .Where(f => f.Name.Equals("run.json", StringComparison.OrdinalIgnoreCase) && f.Path.Count(c => c == '/') == 3))
        {
            try
            {
                if (JsonSerializer.Deserialize<ScriptRun>(folders.Store.ReadText(file.Path)!, Json) is { } r)
                {
                    output.Add(r with { Folder = file.Path[..file.Path.LastIndexOf('/')] });
                }
            }
            catch (JsonException)
            {
            }
        }

        return output.OrderByDescending(r => r.Start).ToList();
    }

    /// <summary>The files of a run that the script made (under bestanden/).</summary>
    public static IReadOnlyList<StoredFile> Files(ScriptFolders folders, ScriptRun run) => folders.Store.List($"{run.Folder}/bestanden/");

    /// <summary>Deletes the runs older than a year (decision U9), and work folders left behind; returns the count.</summary>
    public static int Cleanup(ScriptFolders folders, DateTimeOffset now, int days = SaveDays)
    {
        var count = 0;
        foreach (var r in Items(folders).Where(r => r.Start < now.AddDays(-days)))
        {
            folders.Store.DeleteAll(r.Folder + "/");
            count++;
        }

        if (Directory.Exists(folders.Work))
        {
            foreach (var dir in Directory.EnumerateDirectories(folders.Work).SelectMany(Directory.EnumerateDirectories))
            {
                try
                {
                    if (Directory.GetLastWriteTime(dir) < now.AddDays(-1).LocalDateTime)
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return count;
    }
}
