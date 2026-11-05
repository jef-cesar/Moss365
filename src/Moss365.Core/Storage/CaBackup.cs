using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

namespace Moss365.Core.Storage;

/// <summary>One backup of Conditional Access (a file in the data folder).</summary>
public sealed record CaBackupFile(string FilePath, DateTime Time, int Policies, int Locations)
{
    public string Name => Path.GetFileName(FilePath);
}

public sealed record CaBackupContent(DateTime Time, IReadOnlyList<CaPolicy> Policies, IReadOnlyList<NamedLocation> Locations);

/// <summary>
/// Backups of the Conditional Access policies and named locations, as Graph returned them (JSON), in
/// <c>%LOCALAPPDATA%\Moss365\ca-backup</c>. A new backup only when something changed. They contain ids of people and
/// groups: keep them local, never in the repository.
/// </summary>
public static class CaBackup
{
    public const string FolderName = "ca-backup";

    /// <summary>This many backups are kept (the oldest go).</summary>
    public const int Saving = 365;

    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    /// <summary>Saves a backup when the policies or locations differ from the last one. Returns the path, or
    /// null.</summary>
    public static string? Save(CaData ca, string folder, DateTime time)
    {
        if (ca.Message is not null || ca.Policies.Any(b => b.Json.Length == 0))
        {
            return null; // not (fully) read from Graph: no backup that would later lose something
        }

        var content = new JsonObject
        {
            ["beleiden"] = new JsonArray(ca.Policies.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => JsonNode.Parse(b.Json)).ToArray()),
            ["locaties"] = new JsonArray(ca.Locations.Where(l => l.Json.Length > 0).OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => JsonNode.Parse(l.Json)).ToArray()),
        };
        var compare = content.ToJsonString();
        if (Items(folder).FirstOrDefault() is { } last && Comparable(last.FilePath) == compare)
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        content["tijd"] = time.ToString("o", CultureInfo.InvariantCulture);
        var path = Path.Combine(folder, $"ca-{time:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, content.ToJsonString(Format));
        foreach (var old in Items(folder).Skip(Saving))
        {
            File.Delete(old.FilePath);
        }

        return path;
    }

    private static string Comparable(string path)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("tijd");
        return new JsonObject { ["beleiden"] = node["beleiden"]?.DeepClone(), ["locaties"] = node["locaties"]?.DeepClone() }.ToJsonString();
    }

    /// <summary>The backups, newest first.</summary>
    public static IReadOnlyList<CaBackupFile> Items(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return Array.Empty<CaBackupFile>();
        }

        var output = new List<CaBackupFile>();
        foreach (var path in Directory.GetFiles(folder, "ca-*.json"))
        {
            try
            {
                var i = Read(path);
                output.Add(new CaBackupFile(path, i.Time, i.Policies.Count, i.Locations.Count));
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                // A broken file is skipped
            }
        }

        return output.OrderByDescending(b => b.Time).ThenByDescending(b => b.FilePath, StringComparer.Ordinal).ToList();
    }

    public static CaBackupContent Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var time = r.TryGetProperty("tijd", out var t) ? DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : File.GetLastWriteTime(path);
        var policies = r.TryGetProperty("beleiden", out var b) ? b.EnumerateArray().Select(CaJson.Read).ToList() : new List<CaPolicy>();
        var locations = r.TryGetProperty("locaties", out var l) ? l.EnumerateArray().Select(CaJson.ReadLocation).ToList() : new List<NamedLocation>();
        return new CaBackupContent(time, policies, locations);
    }
}
