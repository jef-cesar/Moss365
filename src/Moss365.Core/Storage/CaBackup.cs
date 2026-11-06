using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

namespace Moss365.Core.Storage;

/// <summary>One backup of Conditional Access (a file in the tenant database, under <c>ca-backup/</c>).</summary>
public sealed record CaBackupFile(string FilePath, DateTime Time, int Policies, int Locations)
{
    public string Name => FilePath[(FilePath.LastIndexOf('/') + 1)..];
}

public sealed record CaBackupContent(DateTime Time, IReadOnlyList<CaPolicy> Policies, IReadOnlyList<NamedLocation> Locations);

/// <summary>
/// Backups of the Conditional Access policies and named locations, as Graph returned them (JSON), in the tenant
/// database (files under <c>ca-backup/</c>). A new backup only when something changed. They contain ids of people and
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
    public static string? Save(CaData ca, IFileStore store, DateTime time)
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
        if (Items(store).FirstOrDefault() is { } last && Comparable(store, last.FilePath) == compare)
        {
            return null;
        }

        content["tijd"] = time.ToString("o", CultureInfo.InvariantCulture);
        var path = $"{FolderName}/ca-{time:yyyyMMdd-HHmmss}.json";
        store.WriteText(path, content.ToJsonString(Format));
        foreach (var old in Items(store).Skip(Saving))
        {
            store.Delete(old.FilePath);
        }

        return path;
    }

    private static string Comparable(IFileStore store, string path)
    {
        var node = JsonNode.Parse(store.ReadText(path)!)!.AsObject();
        node.Remove("tijd");
        return new JsonObject { ["beleiden"] = node["beleiden"]?.DeepClone(), ["locaties"] = node["locaties"]?.DeepClone() }.ToJsonString();
    }

    /// <summary>The backups, newest first.</summary>
    public static IReadOnlyList<CaBackupFile> Items(IFileStore store)
    {
        var output = new List<CaBackupFile>();
        foreach (var path in store.List(FolderName + "/").Select(f => f.Path).Where(p => p[(p.LastIndexOf('/') + 1)..].StartsWith("ca-", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var i = Read(store, path);
                output.Add(new CaBackupFile(path, i.Time, i.Policies.Count, i.Locations.Count));
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                // A broken file is skipped
            }
        }

        return output.OrderByDescending(b => b.Time).ThenByDescending(b => b.FilePath, StringComparer.Ordinal).ToList();
    }

    public static CaBackupContent Read(IFileStore store, string path)
    {
        using var doc = JsonDocument.Parse(store.ReadText(path) ?? throw new FileNotFoundException("Back-up niet gevonden.", path));
        var r = doc.RootElement;
        var time = r.TryGetProperty("tijd", out var t) ? DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTime.MinValue;
        var policies = r.TryGetProperty("beleiden", out var b) ? b.EnumerateArray().Select(CaJson.Read).ToList() : new List<CaPolicy>();
        var locations = r.TryGetProperty("locaties", out var l) ? l.EnumerateArray().Select(CaJson.ReadLocation).ToList() : new List<NamedLocation>();
        return new CaBackupContent(time, policies, locations);
    }
}
