using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moss365.Core.Changes;
using Moss365.Core.Storage;

namespace Moss365.Core.Scripts;

/// <summary>One script in the index of the library in the repository (R8).</summary>
public sealed record IndexEntry(string Path, string Name, string Version, string Sha256, string MinimumApp = "", bool Writes = false, string Connection = "Local");

/// <summary>
/// The index of the script library (<c>scripts/index.json</c> in the repository): every script with its version and
/// SHA-256. A test checks that it matches the scripts; the app downloads only what matches it. From 1.0 the index and
/// the scripts are also signed (U8).
/// </summary>
public sealed record ScriptIndex(int Format, IReadOnlyList<IndexEntry> Scripts)
{
    public const string FileName = "index.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static ScriptIndex Parse(string json)
        => JsonSerializer.Deserialize<ScriptIndex>(json, Json) is { Format: 1 } index ? index : throw new JsonException("Onbekend formaat van de index.");

    public string ToJson() => JsonSerializer.Serialize(this, Json).Replace("\r\n", "\n") + "\n";

    /// <summary>The index of the scripts in a folder (only scripts with a MOSS365 header), paths with /.</summary>
    public static ScriptIndex Build(string folder)
    {
        var entries = new List<IndexEntry>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.ps1", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(file);
            var header = ScriptText.Read(System.Text.Encoding.UTF8.GetString(bytes), file);
            if (!header.FromBlock)
            {
                continue;
            }

            entries.Add(new IndexEntry(System.IO.Path.GetRelativePath(folder, file).Replace('\\', '/'), header.Name, header.Version, Sha256(bytes),
                header.MinimumApp, header.Writes, ScriptText.ConnectionText(header.Connection)));
        }

        return new ScriptIndex(1, entries);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>The downloaded file is exactly the one in the index.</summary>
    public static bool Matches(IndexEntry entry, byte[] bytes) => string.Equals(Sha256(bytes), entry.Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>A safe path inside the library (no .., no absolute path, only .ps1).</summary>
    public static bool SafePath(string path)
        => path.Length is > 4 and < 200 && path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && !System.IO.Path.IsPathRooted(path)
           && !path.Contains('\\') && path.Split('/').All(p => p.Length > 0 && p != "." && p != ".." && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' '));
}

public enum LibraryChangeKind
{
    /// <summary>Not in this tenant yet.</summary>
    New,

    /// <summary>Another version (or the same version with other content) than in this tenant.</summary>
    Changed,

    /// <summary>Needs a newer Moss365.</summary>
    TooNew,

    /// <summary>Pinned in this tenant: stays on its version.</summary>
    Pinned,
}

/// <summary>What Update library would change for one script, before anything is downloaded.</summary>
public sealed record LibraryChange(IndexEntry Entry, LibraryChangeKind Kind, string? CurrentVersion, string? CurrentSha256)
{
    public bool CanTake => Kind is LibraryChangeKind.New or LibraryChangeKind.Changed;
}

/// <summary>A script of the library as this tenant has it: its version and fingerprint, and whether it is pinned.</summary>
public sealed record InstalledScript(string Path, string Version, string Sha256, bool Pinned = false);

/// <summary>
/// Update library (R8): compares the index with what this tenant has (the shipped scripts, and what was downloaded
/// before), and stores downloaded scripts in the tenant database (<c>scripts/library/...</c>). Nothing runs by itself.
/// </summary>
public static class LibraryUpdate
{
    public const string Folder = "scripts/library";
    public const string StateDocument = "scripts/library.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<LibraryChange> Compare(ScriptIndex index, IReadOnlyDictionary<string, InstalledScript> installed, string appVersion)
    {
        var output = new List<LibraryChange>();
        foreach (var e in index.Scripts.Where(e => ScriptIndex.SafePath(e.Path)))
        {
            installed.TryGetValue(e.Path, out var have);
            if (have is not null && string.Equals(have.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = have?.Pinned == true ? LibraryChangeKind.Pinned
                : e.MinimumApp.Length > 0 && Versions.IsNewer(e.MinimumApp, appVersion) ? LibraryChangeKind.TooNew
                : have is null ? LibraryChangeKind.New
                : LibraryChangeKind.Changed;
            output.Add(new LibraryChange(e, kind, have?.Version, have?.Sha256));
        }

        return output.OrderBy(c => c.Kind).ThenBy(c => c.Entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What this tenant has: the shipped scripts, overridden by what was downloaded, with the pins.</summary>
    public static Dictionary<string, InstalledScript> Installed(ScriptFolders folders)
    {
        var output = new Dictionary<string, InstalledScript>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folders.BuiltIn))
        {
            foreach (var e in ScriptIndex.Build(folders.BuiltIn).Scripts)
            {
                output[e.Path] = new InstalledScript(e.Path, e.Version, e.Sha256);
            }
        }

        foreach (var s in ReadState(folders.Store).Values)
        {
            output[s.Path] = s;
        }

        foreach (var pinned in Pins(folders.Store))
        {
            if (output.TryGetValue(pinned, out var s))
            {
                output[pinned] = s with { Pinned = true };
            }
        }

        return output;
    }

    /// <summary>Stores a downloaded script, only when it is exactly the one in the index. Returns why not, or null.</summary>
    public static string? Take(ScriptFolders folders, IndexEntry entry, byte[] bytes)
    {
        if (!ScriptIndex.SafePath(entry.Path))
        {
            return $"{entry.Path}: geen geldig pad.";
        }

        if (!ScriptIndex.Matches(entry, bytes))
        {
            return $"{entry.Path}: de vingerafdruk klopt niet met de index; niet bewaard.";
        }

        folders.Store.Write($"{Folder}/{entry.Path}", bytes);
        var state = ReadState(folders.Store);
        state[entry.Path] = new InstalledScript(entry.Path, entry.Version, entry.Sha256);
        folders.Store.WriteText(StateDocument, JsonSerializer.Serialize(state.Values.OrderBy(s => s.Path, StringComparer.Ordinal), Json));
        return null;
    }

    /// <summary>Pins a script of the library in this tenant (it stays on its version), or releases it.</summary>
    public static void Pin(ScriptFolders folders, string path, bool pinned)
    {
        var pins = Pins(folders.Store).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pinned)
        {
            pins.Add(path);
        }
        else
        {
            pins.Remove(path);
        }

        folders.Store.WriteText(PinsDocument, JsonSerializer.Serialize(pins.OrderBy(p => p, StringComparer.Ordinal), Json));
    }

    public const string PinsDocument = "scripts/pins.json";

    public static IReadOnlyList<string> Pins(IFileStore store)
    {
        try
        {
            return store.ReadText(PinsDocument) is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>() : new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    private static Dictionary<string, InstalledScript> ReadState(IFileStore store)
    {
        try
        {
            return store.ReadText(StateDocument) is { } json && JsonSerializer.Deserialize<List<InstalledScript>>(json) is { } list
                ? list.ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, InstalledScript>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, InstalledScript>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>A simple line diff (for the preview of a changed script): lines with "- ", "+ " or "  ".</summary>
    public static IReadOnlyList<string> Diff(string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var output = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                output.Add("  " + a[x]);
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                output.Add("- " + a[x++]);
            }
            else
            {
                output.Add("+ " + b[y++]);
            }
        }

        output.AddRange(a.Skip(x).Select(l => "- " + l));
        output.AddRange(b.Skip(y).Select(l => "+ " + l));
        return output;
    }
}

/// <summary>Where Update library gets the index and the scripts: this repository on GitHub, or a shared folder.</summary>
public interface IScriptLibrarySource
{
    /// <summary>For the screen: where the scripts come from.</summary>
    string Where { get; }

    Task<string> IndexAsync(CancellationToken cancel);

    Task<byte[]> FileAsync(string path, CancellationToken cancel);
}
