using Moss365.Core.Storage;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Scripts;

/// <summary>A script in the library: built-in (shipped with the app, read-only) or own (saved locally).</summary>
public sealed record ScriptFile(string FilePath, ScriptHeader Header, bool BuiltIn, string Fingerprint, string Folder = "")
{
    /// <summary>Key for the last values and the runs: the file name without extension (as Get-ScriptWaarde
    /// does).</summary>
    public string Key => Path.GetFileNameWithoutExtension(FilePath);

    public string Origin => BuiltIn ? "ingebouwd" : "eigen";
}

/// <summary>Where the scripts and their data are. Everything local; in the web version this goes to the
/// server.</summary>
public sealed record ScriptFolders(string BuiltIn, string Data)
{
    public string Own => Path.Combine(Data, "eigen");
    public string Values => Path.Combine(Data, "waarden");
    public string Runs => Path.Combine(Data, "runs");
    public string Approved => Path.Combine(Own, "goedgekeurd.json");
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
        var output = new List<ScriptFile>();
        if (Directory.Exists(folders.BuiltIn))
        {
            // Only scripts with a MOSS365 header: the others are in the download for use outside the app
            foreach (var path in Directory.EnumerateFiles(folders.BuiltIn, "*.ps1", SearchOption.AllDirectories))
            {
                var script = ReadFile(path, true, Path.GetRelativePath(folders.BuiltIn, Path.GetDirectoryName(path)!).Replace('\\', '/') is var m && m != "." ? m : string.Empty);
                if (script.Header.FromBlock)
                {
                    output.Add(script);
                }
            }
        }

        if (Directory.Exists(folders.Own))
        {
            output.AddRange(Directory.EnumerateFiles(folders.Own, "*.ps1").Select(p => ReadFile(p, false)));
        }

        return output.OrderBy(s => s.Header.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static ScriptFile ReadFile(string path, bool builtIn, string folder = "")
    {
        var text = File.ReadAllText(path);
        return new ScriptFile(path, ScriptText.Read(text, path), builtIn, Fingerprint(text), folder);
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
        Directory.CreateDirectory(folders.Own);
        var header = ScriptText.Read(text, existingPath ?? "script.ps1");
        var path = existingPath;
        if (path is null || !IsOwn(folders, path))
        {
            path = Path.Combine(folders.Own, FileName(header.Name));
            var builtInNames = Load(folders with { Data = Path.Combine(folders.Data, "-geen-") }).Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseline = Path.GetFileNameWithoutExtension(path);
            var n = 1;
            while (File.Exists(path) || builtInNames.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                path = Path.Combine(folders.Own, $"{baseline}-{++n}.ps1");
            }
        }

        // Always with a BOM: Windows PowerShell 5.1 reads a file without a BOM as ANSI
        File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        var file = ReadFile(path, false);
        Approve(folders, file);
        return file;
    }

    public static bool IsOwn(ScriptFolders folders, string path)
        => string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(folders.Own), StringComparison.OrdinalIgnoreCase);

    public static void Remove(ScriptFolders folders, ScriptFile script)
    {
        if (script.BuiltIn || !IsOwn(folders, script.FilePath))
        {
            throw new InvalidOperationException("Een ingebouwd script kan je niet verwijderen.");
        }

        File.Delete(script.FilePath);
        var list = Approvals(folders);
        list.Remove(Path.GetFileName(script.FilePath));
        SaveApprovals(folders, list);
    }

    /// <summary>Imports a .ps1 as an own script (a copy; the original stays where it was).</summary>
    public static ScriptFile Import(ScriptFolders folders, string sourcePath)
    {
        var text = File.ReadAllText(sourcePath);
        var header = ScriptText.Read(text, sourcePath);
        if (!header.FromBlock)
        {
            text = ScriptText.Write(header, text);
        }

        return Store(folders, text);
    }

    /// <summary>Was this own script changed outside the app since it was saved or approved?</summary>
    public static bool IsChangedOutsideApp(ScriptFolders folders, ScriptFile script)
        => !script.BuiltIn && (!Approvals(folders).TryGetValue(Path.GetFileName(script.FilePath), out var v) || v != script.Fingerprint);

    public static void Approve(ScriptFolders folders, ScriptFile script)
    {
        var list = Approvals(folders);
        list[Path.GetFileName(script.FilePath)] = script.Fingerprint;
        SaveApprovals(folders, list);
    }

    private static Dictionary<string, string> Approvals(ScriptFolders folders)
    {
        try
        {
            return File.Exists(folders.Approved)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(folders.Approved)) is { } d
                    ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveApprovals(ScriptFolders folders, Dictionary<string, string> list)
    {
        Directory.CreateDirectory(folders.Own);
        File.WriteAllText(folders.Approved, JsonSerializer.Serialize(list, Json));
    }

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
/// The last used values per script, in the same file as the Get-ScriptWaarde block in the scripts:
/// <c>waarden\&lt;script&gt;.json</c>, with a text or a list per name.
/// </summary>
public static class ScriptValues
{
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }.WithLegacyNames();

    public static Dictionary<string, string> Read(ScriptFolders folders, string key)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(folders.Values, key + ".json");
        if (!File.Exists(path))
        {
            return output;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
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
        Directory.CreateDirectory(folders.Values);
        var path = Path.Combine(folders.Values, key + ".json");
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

        File.WriteAllText(path, JsonSerializer.Serialize(existing, Json), new UTF8Encoding(true));
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

/// <summary>One run of a script, saved as run.json in the folder of the run.</summary>
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

    [JsonIgnore]
    public string Folder { get; init; } = string.Empty;

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

/// <summary>The runs on disk: runs\&lt;script&gt;\&lt;id&gt;\ with run.json, uitvoer.jsonl, tables and files.</summary>
public static class ScriptRuns
{
    public const int SaveDays = 90;
    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }.WithLegacyNames();

    public static string NewFolder(ScriptFolders folders, string key, string id)
    {
        var folder = Path.Combine(folders.Runs, key, id);
        var n = 1;
        while (Directory.Exists(folder))
        {
            folder = Path.Combine(folders.Runs, key, $"{id}-{++n}");
        }

        Directory.CreateDirectory(Path.Combine(folder, "bestanden"));
        return folder;
    }

    public static void Save(ScriptRun run) => File.WriteAllText(Path.Combine(run.Folder, "run.json"), JsonSerializer.Serialize(run, Json));

    public static IReadOnlyList<ScriptRun> Items(ScriptFolders folders, string? key = null)
    {
        var baseline = key is null ? folders.Runs : Path.Combine(folders.Runs, key);
        if (!Directory.Exists(baseline))
        {
            return Array.Empty<ScriptRun>();
        }

        var output = new List<ScriptRun>();
        foreach (var file in Directory.EnumerateFiles(baseline, "run.json", SearchOption.AllDirectories))
        {
            try
            {
                if (JsonSerializer.Deserialize<ScriptRun>(File.ReadAllText(file), Json) is { } r)
                {
                    output.Add(r with { Folder = Path.GetDirectoryName(file)! });
                }
            }
            catch (JsonException)
            {
            }
        }

        return output.OrderByDescending(r => r.Start).ToList();
    }

    /// <summary>Deletes the runs older than 90 days; returns the count.</summary>
    public static int Cleanup(ScriptFolders folders, DateTimeOffset now, int days = SaveDays)
    {
        var route = 0;
        foreach (var r in Items(folders).Where(r => r.Start < now.AddDays(-days)))
        {
            try
            {
                Directory.Delete(r.Folder, true);
                route++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return route;
    }
}
