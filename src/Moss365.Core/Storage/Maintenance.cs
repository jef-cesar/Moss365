using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Storage;

/// <summary>How long each part of a tenant database is kept (decision U9; adjustable per tenant).</summary>
public sealed class RetentionSettings
{
    /// <summary>Snapshots: every snapshot for this many days, after that one per month (the last of the month).</summary>
    public int SnapshotsDailyDays { get; set; } = 365;

    /// <summary>The access log (who viewed or exported what).</summary>
    public int AccessLogYears { get; set; } = 5;

    public int TechnicalLogDays { get; set; } = 90;

    public int ScriptRunDays { get; set; } = 365;

    /// <summary>The automatic copies made before a migration (tenant.db.bak-...); the newest always stays.</summary>
    public int MigrationCopyDays { get; set; } = 30;
}

/// <summary>One part of the database: how many rows and roughly how many bytes of content.</summary>
public sealed record PartSize(string Part, long Rows, long Bytes);

public enum CleanupPart
{
    Snapshots,
    ChangeLog,
    AccessLog,
    TechnicalLog,
    ScriptRuns,
    MigrationCopies,
}

/// <summary>What a clean-up would remove from one part, before anything is removed.</summary>
public sealed record CleanupItem(CleanupPart Part, int Count, DateTimeOffset Before, string Explanation);

/// <summary>Maintenance of a tenant database (R6): sizes, compacting and removing old data.</summary>
public sealed partial class Database
{
    /// <summary>The size on disk: the database and its write-ahead log.</summary>
    public long FileSize => new[] { FilePath, FilePath + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);

    /// <summary>Rows and bytes of content per part.</summary>
    public IReadOnlyList<PartSize> Sizes()
    {
        lock (_lock)
        {
            using var c = Open();
            var output = new List<PartSize>();
            void Add(string part, string sql)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    output.Add(new PartSize(part, r.IsDBNull(0) ? 0 : r.GetInt64(0), r.IsDBNull(1) ? 0 : r.GetInt64(1)));
                }
            }

            Add("Momentopnamen", "SELECT (SELECT COUNT(*) FROM Snapshot), (SELECT COALESCE(SUM(length(Content)), 0) FROM Version);");
            Add("Logboek", "SELECT COUNT(*), COALESCE(SUM(length(Json)), 0) FROM Logboek;");
            Add("Toegangslog", "SELECT COUNT(*), COALESCE(SUM(length(Wat) + length(Details) + length(Gebruiker)), 0) FROM Toegang;");
            Add("Technisch log", "SELECT COUNT(*), COALESCE(SUM(length(Message)), 0) FROM TechnicalLog;");
            foreach (var (part, prefix) in new[] { ("Scriptruns", "runs/"), ("Inventarissen", "inventory/"), ("Back-ups Conditional Access", "ca-backup/"), ("Scripts", "scripts/") })
            {
                Add(part, $"SELECT COUNT(*), COALESCE(SUM(length(Content)), 0) FROM File WHERE Path LIKE '{prefix}%';");
            }

            Add("Andere bestanden", "SELECT COUNT(*), COALESCE(SUM(length(Content)), 0) FROM File WHERE Path NOT LIKE 'runs/%' AND Path NOT LIKE 'inventory/%' AND Path NOT LIKE 'ca-backup/%' AND Path NOT LIKE 'scripts/%';");
            Add("Instellingen en lijsten", "SELECT COUNT(*), COALESCE(SUM(length(Json)), 0) FROM Document;");
            Add("KPI per dag", "SELECT COUNT(*), COUNT(*) * 24 FROM KpiStand;");
            return output;
        }
    }

    /// <summary>Gives the free space back to the disk (VACUUM).</summary>
    public void Compact()
    {
        lock (_lock)
        {
            using var c = Open();
            Run(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            Run(c, "VACUUM;");
        }
    }

    /// <summary>Access log entries older than the date.</summary>
    public int CountAccess(DateTimeOffset olderThan) => Scalar("SELECT COUNT(*) FROM Toegang WHERE Tijd < $o;", olderThan);

    public int CountTechnicalLog(DateTimeOffset olderThan) => Scalar("SELECT COUNT(*) FROM TechnicalLog WHERE Time < $o;", olderThan);

    /// <summary>The access log older than the date, oldest first (to archive it before removing).</summary>
    public IReadOnlyList<AccessEntry> ReadAccessOlder(DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Tijd, Gebruiker, Wat, Details FROM Toegang WHERE Tijd < $o ORDER BY Tijd, Id;";
            cmd.Parameters.AddWithValue("$o", olderThan.ToUniversalTime().ToString("o"));
            var output = new List<AccessEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                output.Add(new AccessEntry(DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2), r.GetString(3)));
            }

            return output;
        }
    }

    public int RemoveAccess(DateTimeOffset olderThan) => Execute("DELETE FROM Toegang WHERE Tijd < $o;", olderThan);

    private int Scalar(string sql, DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$o", olderThan.ToUniversalTime().ToString("o"));
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private int Execute(string sql, DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$o", olderThan.ToUniversalTime().ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ thinning snapshots

    /// <summary>
    /// The snapshots that thinning removes: older than the daily period and not the last of their month. The newest
    /// snapshot always stays.
    /// </summary>
    public IReadOnlyList<SnapshotInfo> SnapshotsToThin(DateTimeOffset now, int dailyDays)
    {
        var all = SnapshotList();
        if (all.Count == 0)
        {
            return Array.Empty<SnapshotInfo>();
        }

        var limit = now.AddDays(-Math.Max(1, dailyDays));
        var keep = all.Where(s => s.Time >= limit).Select(s => s.Id)
            .Concat(all.Where(s => s.Time < limit).GroupBy(s => (s.Time.LocalDateTime.Year, s.Time.LocalDateTime.Month)).Select(g => g.Max(s => s.Id)))
            .Append(all[^1].Id)
            .ToHashSet();
        return all.Where(s => !keep.Contains(s.Id)).ToList();
    }

    /// <summary>
    /// Removes snapshots and keeps every other date exact: a version that was valid at a snapshot that stays is still
    /// valid there; versions only valid between removed snapshots go. Returns the number of snapshots removed.
    /// </summary>
    public int RemoveSnapshots(IReadOnlyCollection<long> remove)
    {
        if (remove.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            var all = new List<(long Id, string Tables)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = t;
                cmd.CommandText = "SELECT Id, Tables FROM Snapshot ORDER BY Id;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    all.Add((r.GetInt64(0), r.GetString(1)));
                }
            }

            var gone = remove.ToHashSet();
            var kept = all.Where(s => !gone.Contains(s.Id)).Select(s => s.Id).ToArray();
            if (kept.Length == 0)
            {
                throw new InvalidOperationException("Minstens één momentopname blijft.");
            }

            // The first kept snapshot from an id on (null: none after it)
            long? NextKept(long id)
            {
                var i = Array.BinarySearch(kept, id);
                i = i >= 0 ? i : ~i;
                return i < kept.Length ? kept[i] : null;
            }

            // The tables a removed snapshot read count from the next one that stays (an empty table stays "read")
            var tables = all.ToDictionary(s => s.Id, s => s.Tables.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var s in all.Where(s => gone.Contains(s.Id)))
            {
                if (NextKept(s.Id) is { } next)
                {
                    tables[next].UnionWith(tables[s.Id]);
                }
            }

            foreach (var id in kept)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "UPDATE Snapshot SET Tables = $b WHERE Id = $i;";
                cmd.Parameters.AddWithValue("$b", string.Join('|', tables[id].OrderBy(x => x, StringComparer.Ordinal)));
                cmd.Parameters.AddWithValue("$i", id);
                cmd.ExecuteNonQuery();
            }

            // Versions: valid on [From, Until). Move both ends to the next kept snapshot; drop what no kept one sees.
            var versions = new List<(long Id, long From, long? Until)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = t;
                cmd.CommandText = "SELECT Id, FromSnapshot, UntilSnapshot FROM Version;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    versions.Add((r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
                }
            }

            using (var update = c.CreateCommand())
            using (var delete = c.CreateCommand())
            {
                update.Transaction = t;
                update.CommandText = "UPDATE Version SET FromSnapshot = $f, UntilSnapshot = $u WHERE Id = $i;";
                var pf = update.Parameters.Add("$f", SqliteType.Integer);
                var pu = update.Parameters.Add("$u", SqliteType.Integer);
                var pi = update.Parameters.Add("$i", SqliteType.Integer);
                delete.Transaction = t;
                delete.CommandText = "DELETE FROM Version WHERE Id = $i;";
                var di = delete.Parameters.Add("$i", SqliteType.Integer);
                foreach (var (id, from, until) in versions)
                {
                    var newFrom = NextKept(from);
                    if (newFrom is null || (until is { } u && newFrom >= u))
                    {
                        di.Value = id;
                        delete.ExecuteNonQuery();
                        continue;
                    }

                    long? newUntil = until is { } x ? NextKept(x) : null;
                    if (newFrom == from && newUntil == until)
                    {
                        continue;
                    }

                    pf.Value = newFrom;
                    pu.Value = newUntil is { } nu ? nu : DBNull.Value;
                    pi.Value = id;
                    update.ExecuteNonQuery();
                }
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = t;
                cmd.CommandText = $"DELETE FROM Snapshot WHERE Id IN ({string.Join(',', gone)}); DELETE FROM Object WHERE Id NOT IN (SELECT ObjectId FROM Version);";
                cmd.ExecuteNonQuery();
            }

            t.Commit();
            return all.Count - kept.Length;
        }
    }

    // ------------------------------------------------------------------ copies before a migration

    /// <summary>The automatic copies of this database (tenant.db.bak-...), newest first.</summary>
    public IReadOnlyList<FileInfo> MigrationCopies()
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        return Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles(Path.GetFileName(FilePath) + ".bak-*").OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : new List<FileInfo>();
    }

    /// <summary>The copies older than the date, except the newest copy.</summary>
    public IReadOnlyList<FileInfo> OldMigrationCopies(DateTimeOffset olderThan) => MigrationCopies().Skip(1).Where(f => f.LastWriteTimeUtc < olderThan.UtcDateTime).ToList();
}
