using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moss365.Core.Inventory;

namespace Moss365.Core.Storage;

/// <summary>One snapshot: when, from where (live AD, Entra, Exchange, or an imported inventory) and which tables it read
/// in full.</summary>
public sealed record SnapshotInfo(long Id, DateTimeOffset Time, string Source, IReadOnlyList<string> Tables);

/// <summary>An object (a row of an inventory table) that differs between two snapshots.</summary>
public sealed record SnapshotChange(string Table, string Key, IReadOnlyDictionary<string, string>? Before, IReadOnlyDictionary<string, string>? After)
{
    public string Kind => Before is null ? "nieuw" : After is null ? "verdwenen" : "gewijzigd";

    /// <summary>The columns that changed (all columns for a new or vanished object).</summary>
    public IReadOnlyList<string> Columns => (Before?.Keys ?? Enumerable.Empty<string>()).Union(After?.Keys ?? Enumerable.Empty<string>())
        .Where(k => Before?.GetValueOrDefault(k) != After?.GetValueOrDefault(k)).ToList();
}

/// <summary>One version of an object: its content, and from which snapshot until which one it was valid.</summary>
public sealed record ObjectVersion(long From, long? Until, DateTimeOffset FromTime, IReadOnlyDictionary<string, string> Content);

/// <summary>
/// Snapshots of the organisation (R4): every read is a snapshot, but the database only stores what changed. Every row of
/// an inventory table (a user, a group, a membership, a mailbox permission, ...) is an object with versions that are
/// valid from one snapshot until another. That makes it possible to rebuild exactly what the organisation looked like at
/// any snapshot, to list what changed between two dates, and to show the timeline of one object.
/// </summary>
public static class Snapshots
{
    /// <summary>The columns that identify a row per table; a row without them is identified by its whole content.</summary>
    private static readonly Dictionary<string, string[]> KeyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        [InventoryLoader.AdUsers] = new[] { "SamAccountName" },
        [InventoryLoader.AdGroups] = new[] { "SamAccountName" },
        [InventoryLoader.AdGroupMembers] = new[] { "GroepDN", "LidDN" },
        [InventoryLoader.EntraUsers] = new[] { "Id" },
        [InventoryLoader.EntraGroups] = new[] { "Id" },
        [InventoryLoader.EntraGroupMembers] = new[] { "GroepId", "LidId" },
        [InventoryLoader.EntraLicences] = new[] { "SkuId" },
        [InventoryLoader.EntraMfa] = new[] { "Id" },
        [InventoryLoader.EntraRoles] = new[] { "Rol", "LidId" },
        [InventoryLoader.ExoRooms] = new[] { "Smtp" },
        [InventoryLoader.ExoGroups] = new[] { "PrimarySmtpAddress" },
        [InventoryLoader.ExoGroupMembers] = new[] { "Groep", "Lid" },
        [InventoryLoader.ExoMailboxes] = new[] { "PrimarySmtpAddress" },
        [InventoryLoader.ExoMailboxPermissions] = new[] { "Postvak", "Recht", "Gebruiker" },
    };

    /// <summary>The key of a row in its table.</summary>
    public static string Key(string table, IReadOnlyDictionary<string, string> row)
    {
        if (KeyColumns.TryGetValue(table, out var columns)
            && columns.Select(c => row.TryGetValue(c, out var v) ? v.Trim() : string.Empty).ToList() is var parts
            && parts.All(p => p.Length > 0))
        {
            return string.Join('|', parts).ToLowerInvariant();
        }

        return "#" + Fingerprint(Content(row))[..16];
    }

    internal static string Content(IReadOnlyDictionary<string, string> row)
        => JsonSerializer.Serialize(new SortedDictionary<string, string>(row.ToDictionary(k => k.Key, k => k.Value), StringComparer.Ordinal));

    internal static string Fingerprint(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>
    /// The tables of an inventory in the form the snapshot keeps: the CSV files read as the loader reads them, and the
    /// Conditional Access JSON as one row. So a rebuilt date loads exactly like the inventory it came from.
    /// </summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Tables(IReadOnlyDictionary<string, string> files)
    {
        var tables = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in files)
        {
            tables[name] = name.Equals(InventoryLoader.EntraCa, StringComparison.OrdinalIgnoreCase)
                ? new List<Dictionary<string, string>> { new() { ["Json"] = text } }
                : CsvReader.Read(text.TrimStart('﻿'));
        }

        return tables;
    }

    /// <summary>The data of a snapshot, as the loader would have loaded it.</summary>
    public static InventoryData Data(IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables, string origin)
    {
        var data = InventoryLoader.Build(tables, tables.Select(t => new SourceFile(t.Key, origin, t.Value.Count)).ToList());
        if (tables.TryGetValue(InventoryLoader.EntraCa, out var ca) && ca.FirstOrDefault()?.GetValueOrDefault("Json") is { } json)
        {
            data = data with { ConditionalAccess = CaStorage.FromJson(json) };
        }

        return data with { Origin = origin };
    }
}

public sealed partial class Database
{
    // ------------------------------------------------------------------ snapshots (R4, migration 4)

    /// <summary>
    /// Saves a snapshot: for every table in it, rows that are new or changed get a new version, and rows that are no
    /// longer there are closed. Tables that are not in this snapshot stay as they were (that part was not read).
    /// Returns the id of the snapshot.
    /// </summary>
    public long SaveSnapshot(DateTimeOffset time, string source, IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables)
    {
        lock (_lock)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            long id;
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO Snapshot (Time, Source, Tables) VALUES ($t, $s, $b); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("$t", time.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$s", source);
                cmd.Parameters.AddWithValue("$b", string.Join('|', tables.Keys.OrderBy(k => k, StringComparer.Ordinal)));
                id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            foreach (var (table, rows) in tables)
            {
                // What is valid now for this table: object id -> (key, version id, fingerprint)
                var open = new Dictionary<string, (long ObjectId, long VersionId, string Fingerprint)>(StringComparer.Ordinal);
                using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = t;
                    cmd.CommandText = """
                        SELECT o.Key, o.Id, v.Id, v.Fingerprint FROM Object o JOIN Version v ON v.ObjectId = o.Id
                        WHERE o.Kind = $k AND v.UntilSnapshot IS NULL;
                        """;
                    cmd.Parameters.AddWithValue("$k", table);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        open[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2), r.GetString(3));
                    }
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var key = Snapshots.Key(table, row);
                    while (!seen.Add(key))
                    {
                        key += "+"; // the same key twice in one table (unusual data): keep both rows
                    }

                    var content = Snapshots.Content(row);
                    var fingerprint = Snapshots.Fingerprint(content);
                    if (open.TryGetValue(key, out var current) && current.Fingerprint == fingerprint)
                    {
                        continue; // unchanged: nothing to store
                    }

                    var objectId = open.TryGetValue(key, out var existing) ? existing.ObjectId : ObjectId(c, t, table, key);
                    if (open.ContainsKey(key))
                    {
                        Close(c, t, existing.VersionId, id);
                    }

                    using var insert = c.CreateCommand();
                    insert.Transaction = t;
                    insert.CommandText = "INSERT INTO Version (ObjectId, FromSnapshot, UntilSnapshot, Fingerprint, Content) VALUES ($o, $f, NULL, $p, $c);";
                    insert.Parameters.AddWithValue("$o", objectId);
                    insert.Parameters.AddWithValue("$f", id);
                    insert.Parameters.AddWithValue("$p", fingerprint);
                    insert.Parameters.AddWithValue("$c", content);
                    insert.ExecuteNonQuery();
                }

                // Gone since the previous snapshot of this table
                foreach (var (key, v) in open.Where(o => !seen.Contains(o.Key)))
                {
                    Close(c, t, v.VersionId, id);
                }
            }

            t.Commit();
            return id;
        }
    }

    private static long ObjectId(SqliteConnection c, SqliteTransaction t, string kind, string key)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "INSERT INTO Object (Kind, Key) VALUES ($k, $y) ON CONFLICT(Kind, Key) DO NOTHING; SELECT Id FROM Object WHERE Kind = $k AND Key = $y;";
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$y", key);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Close(SqliteConnection c, SqliteTransaction t, long versionId, long snapshot)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = "UPDATE Version SET UntilSnapshot = $s WHERE Id = $v;";
        cmd.Parameters.AddWithValue("$s", snapshot);
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>All snapshots, oldest first.</summary>
    public IReadOnlyList<SnapshotInfo> SnapshotList()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, Time, Source, Tables FROM Snapshot ORDER BY Id;";
            using var r = cmd.ExecuteReader();
            var output = new List<SnapshotInfo>();
            while (r.Read())
            {
                output.Add(new SnapshotInfo(r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1), CultureInfo.InvariantCulture),
                    r.GetString(2), r.GetString(3).Split('|', StringSplitOptions.RemoveEmptyEntries)));
            }

            return output;
        }
    }

    /// <summary>The last snapshot at or before this moment, or null.</summary>
    public SnapshotInfo? SnapshotAt(DateTimeOffset moment)
        => SnapshotList().Where(s => s.Time <= moment).OrderBy(s => s.Time).ThenBy(s => s.Id).LastOrDefault();

    /// <summary>
    /// All tables as they were at a snapshot: every object whose version was valid then, rows in the order of their key.
    /// A table that no snapshot up to then has read is missing.
    /// </summary>
    public Dictionary<string, List<Dictionary<string, string>>> TablesAt(long snapshot)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT o.Kind, o.Key, v.Content FROM Object o JOIN Version v ON v.ObjectId = o.Id
                WHERE v.FromSnapshot <= $s AND (v.UntilSnapshot IS NULL OR v.UntilSnapshot > $s)
                ORDER BY o.Kind, o.Key;
                """;
            cmd.Parameters.AddWithValue("$s", snapshot);
            using var r = cmd.ExecuteReader();
            var output = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            while (r.Read())
            {
                var table = r.GetString(0);
                if (!output.TryGetValue(table, out var rows))
                {
                    output[table] = rows = new();
                }

                rows.Add(JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2))!);
            }

            // Tables that were read but are empty at this snapshot
            foreach (var read in SnapshotList().Where(s => s.Id <= snapshot).SelectMany(s => s.Tables))
            {
                output.TryAdd(read, new());
            }

            return output;
        }
    }

    /// <summary>What changed between two snapshots: new, vanished and changed objects, per table.</summary>
    public IReadOnlyList<SnapshotChange> Differences(long from, long to)
    {
        var before = TablesAt(from);
        var after = TablesAt(to);
        var output = new List<SnapshotChange>();
        foreach (var table in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.Ordinal))
        {
            var b = Keyed(table, before.GetValueOrDefault(table));
            var a = Keyed(table, after.GetValueOrDefault(table));
            foreach (var key in b.Keys.Union(a.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                var x = b.GetValueOrDefault(key);
                var y = a.GetValueOrDefault(key);
                if (x is null || y is null || Snapshots.Content(x) != Snapshots.Content(y))
                {
                    output.Add(new SnapshotChange(table, key, x, y));
                }
            }
        }

        return output;
    }

    private static Dictionary<string, Dictionary<string, string>> Keyed(string table, List<Dictionary<string, string>>? rows)
    {
        var output = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var row in rows ?? new())
        {
            var key = Snapshots.Key(table, row);
            while (output.ContainsKey(key))
            {
                key += "+";
            }

            output[key] = row;
        }

        return output;
    }

    /// <summary>All versions of one object (a row of a table, by its key), oldest first.</summary>
    public IReadOnlyList<ObjectVersion> Timeline(string table, string key)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT v.FromSnapshot, v.UntilSnapshot, s.Time, v.Content FROM Object o JOIN Version v ON v.ObjectId = o.Id
                JOIN Snapshot s ON s.Id = v.FromSnapshot
                WHERE o.Kind = $k AND o.Key = $y ORDER BY v.FromSnapshot;
                """;
            cmd.Parameters.AddWithValue("$k", table);
            cmd.Parameters.AddWithValue("$y", key.ToLowerInvariant());
            using var r = cmd.ExecuteReader();
            var output = new List<ObjectVersion>();
            while (r.Read())
            {
                output.Add(new ObjectVersion(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1),
                    DateTimeOffset.Parse(r.GetString(2), CultureInfo.InvariantCulture), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3))!));
            }

            return output;
        }
    }
}
