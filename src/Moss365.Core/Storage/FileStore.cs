using System.Text;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Storage;

/// <summary>A file kept in the tenant database: its path (with forward slashes), size and last change.</summary>
public sealed record StoredFile(string Path, long Size, DateTimeOffset Modified)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

/// <summary>
/// Files by path, kept in the tenant database (since 0.12): the organisation, the inventory, Conditional Access backups,
/// own scripts, last values and script runs. Paths use forward slashes and are case-insensitive, e.g.
/// <c>scripts/own/Report.ps1</c>. Nothing of a tenant is kept as loose files next to its database.
/// </summary>
public interface IFileStore
{
    byte[]? ReadBytes(string path);

    void Write(string path, byte[] content);

    void Delete(string path);

    /// <summary>All files whose path starts with this prefix (e.g. <c>runs/Report/</c>), in order of path.</summary>
    IReadOnlyList<StoredFile> List(string prefix);

    bool Exists(string path) => ReadBytes(path) is not null;

    string? ReadText(string path) => ReadBytes(path) is { } b ? Encoding.UTF8.GetString(b).TrimStart('﻿') : null;

    void WriteText(string path, string text, bool bom = false)
        => Write(path, (bom ? Encoding.UTF8.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(text)).ToArray());

    /// <summary>Deletes every file under this prefix; returns how many.</summary>
    int DeleteAll(string prefix)
    {
        var files = List(prefix);
        foreach (var f in files)
        {
            Delete(f.Path);
        }

        return files.Count;
    }

    /// <summary>Copies every file of a folder on disk into the store under the prefix (e.g. after a script run).</summary>
    int Import(string folder, string prefix)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            Write(FileStorePaths.Join(prefix, System.IO.Path.GetRelativePath(folder, file)), File.ReadAllBytes(file));
            count++;
        }

        return count;
    }

    /// <summary>Writes every file under the prefix to a folder on disk (e.g. to open or run it); returns the folder.</summary>
    string Export(string prefix, string folder)
    {
        Directory.CreateDirectory(folder);
        var root = FileStorePaths.Normalise(prefix).TrimEnd('/') + "/";
        foreach (var f in List(root))
        {
            var target = System.IO.Path.Combine(folder, f.Path[root.Length..].Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, ReadBytes(f.Path)!);
        }

        return folder;
    }
}

public static class FileStorePaths
{
    public static string Normalise(string path) => path.Replace('\\', '/').Trim('/');

    public static string Join(params string[] parts)
        => string.Join('/', parts.Select(Normalise).Where(p => p.Length > 0));
}

/// <summary>The file store in the tenant database (table File).</summary>
public sealed class DatabaseFileStore(Database db) : IFileStore
{
    public byte[]? ReadBytes(string path) => db.ReadFile(FileStorePaths.Normalise(path));

    public void Write(string path, byte[] content) => db.WriteFile(FileStorePaths.Normalise(path), content);

    public void Delete(string path) => db.DeleteFile(FileStorePaths.Normalise(path));

    public IReadOnlyList<StoredFile> List(string prefix) => db.ListFiles(FileStorePaths.Normalise(prefix) + (prefix.EndsWith('/') ? "/" : string.Empty));
}

public sealed partial class Database
{
    // ------------------------------------------------------------------ files

    internal byte[]? ReadFile(string path)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Content FROM File WHERE Path = $p COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$p", path);
            return cmd.ExecuteScalar() as byte[];
        }
    }

    internal void WriteFile(string path, byte[] content)
    {
        lock (_lock)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            using (var del = c.CreateCommand())
            {
                // One row per path whatever the case (Windows file names are case-insensitive)
                del.Transaction = t;
                del.CommandText = "DELETE FROM File WHERE Path = $p COLLATE NOCASE;";
                del.Parameters.AddWithValue("$p", path);
                del.ExecuteNonQuery();
            }

            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "INSERT INTO File (Path, Content, Modified) VALUES ($p, $c, $m);";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.Add("$c", SqliteType.Blob).Value = content;
            cmd.Parameters.AddWithValue("$m", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
            t.Commit();
        }
    }

    internal void DeleteFile(string path)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM File WHERE Path = $p COLLATE NOCASE;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    internal IReadOnlyList<StoredFile> ListFiles(string prefix)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Path, length(Content), Modified FROM File WHERE substr(lower(Path), 1, length($p)) = lower($p) ORDER BY lower(Path);";
            cmd.Parameters.AddWithValue("$p", prefix);
            using var r = cmd.ExecuteReader();
            var output = new List<StoredFile>();
            while (r.Read())
            {
                output.Add(new StoredFile(r.GetString(0), r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2), System.Globalization.CultureInfo.InvariantCulture)));
            }

            return output;
        }
    }

    // ------------------------------------------------------------------ backup

    /// <summary>
    /// A consistent copy of the whole database in one file (SQLite VACUUM INTO), also while the app uses it: the
    /// backup of a tenant.
    /// </summary>
    public void BackupTo(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "VACUUM INTO $p;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>PRAGMA integrity_check: "ok", or what is wrong.</summary>
    public string IntegrityCheck()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            return cmd.ExecuteScalar() as string ?? "?";
        }
    }

    // ------------------------------------------------------------------ technical log

    public void WriteTechnicalLog(DateTimeOffset time, string level, string message)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO TechnicalLog (Time, Level, Message) VALUES ($t, $l, $m);";
            cmd.Parameters.AddWithValue("$t", time.ToUniversalTime().ToString("o"));
            cmd.Parameters.AddWithValue("$l", level);
            cmd.Parameters.AddWithValue("$m", message);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The most recent lines of the technical log, newest first.</summary>
    public IReadOnlyList<(DateTimeOffset Time, string Level, string Message)> ReadTechnicalLog(int count)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Time, Level, Message FROM TechnicalLog ORDER BY Id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", count);
            using var r = cmd.ExecuteReader();
            var output = new List<(DateTimeOffset, string, string)>();
            while (r.Read())
            {
                output.Add((DateTimeOffset.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2)));
            }

            return output;
        }
    }

    /// <summary>Removes technical log lines older than the date (retention, 90 days by default); returns how many.</summary>
    public int CleanupTechnicalLog(DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM TechnicalLog WHERE Time < $t;";
            cmd.Parameters.AddWithValue("$t", olderThan.ToUniversalTime().ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }
}
