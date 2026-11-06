using System.Text.Json;
using System.Text.Encodings.Web;
using Moss365.Core.Changes;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Storage;

/// <summary>A text (JSON) that is saved somewhere: a file or a row in the database.</summary>
public interface ITextStorage
{
    string? Read();

    void Save(string content);
}

/// <summary>As before 0.8: a file, written safely via a temporary file.</summary>
public sealed class FileText(string path) : ITextStorage
{
    public string? Read() => File.Exists(path) ? File.ReadAllText(path) : null;

    public void Save(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>A document in the database (table Document), e.g. "gepland" or "weergave".</summary>
public sealed class DatabaseText(Database db, string key) : ITextStorage
{
    public string? Read() => db.ReadDocument(key);

    public void Save(string content) => db.SaveDocument(key, content);
}

/// <summary>Who viewed or exported what (access log, 0.8). Never the content itself, only what and how many.</summary>
public sealed record AccessEntry(DateTimeOffset Time, string User, string What, string Details);

/// <summary>
/// The Moss365 database (SQLite, WAL mode) in the data folder: change log (append only), documents (scheduled,
/// follow-up, view, connection) and the access log. The schema has versions; <see cref="Migrations"/> upgrades an older
/// database. The same code later on the server (W1 in the roadmap).
/// </summary>
public sealed partial class Database
{
    public const string FileName = "moss365.db";

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }.WithLegacyNames();

    private readonly string _connection;
    private readonly object _lock = new();

    /// <summary>The migrations, in order; the index + 1 is the schema version.</summary>
    public static readonly IReadOnlyList<string> Migrations = new[]
    {
        // 1 (0.8): change log, documents, access log
        """
        CREATE TABLE Logboek (Id INTEGER PRIMARY KEY AUTOINCREMENT, Tijd TEXT NOT NULL, Gebruiker TEXT NOT NULL, Actie TEXT NOT NULL,
                              Status TEXT NOT NULL, PlanId TEXT, Json TEXT NOT NULL);
        CREATE INDEX Logboek_Tijd ON Logboek (Tijd);
        CREATE TABLE Document (Sleutel TEXT PRIMARY KEY, Json TEXT NOT NULL, Gewijzigd TEXT NOT NULL);
        CREATE TABLE Toegang (Id INTEGER PRIMARY KEY AUTOINCREMENT, Tijd TEXT NOT NULL, Gebruiker TEXT NOT NULL, Wat TEXT NOT NULL, Details TEXT NOT NULL);
        CREATE INDEX Toegang_Tijd ON Toegang (Tijd);
        """,

        // 2 (0.8.1): the KPI snapshot per day (counts only, no names), for the trend on Statistics
        """
        CREATE TABLE KpiStand (Datum TEXT NOT NULL, Sleutel TEXT NOT NULL, Waarde INTEGER NOT NULL, PRIMARY KEY (Datum, Sleutel));
        """,

        // 3 (0.12): one database per tenant holds everything: files (organisation, inventory, Conditional Access
        // backups, own scripts, script runs) and the technical log
        """
        CREATE TABLE File (Path TEXT PRIMARY KEY, Content BLOB NOT NULL, Modified TEXT NOT NULL);
        CREATE TABLE TechnicalLog (Id INTEGER PRIMARY KEY AUTOINCREMENT, Time TEXT NOT NULL, Level TEXT NOT NULL, Message TEXT NOT NULL);
        CREATE INDEX TechnicalLog_Time ON TechnicalLog (Time);
        """,

        // 4 (0.13): snapshots of the organisation; every row of an inventory table is an object with versions
        """
        CREATE TABLE Snapshot (Id INTEGER PRIMARY KEY AUTOINCREMENT, Time TEXT NOT NULL, Source TEXT NOT NULL, Tables TEXT NOT NULL);
        CREATE TABLE Object (Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL, Key TEXT NOT NULL, UNIQUE (Kind, Key));
        CREATE TABLE Version (Id INTEGER PRIMARY KEY AUTOINCREMENT, ObjectId INTEGER NOT NULL REFERENCES Object (Id),
                              FromSnapshot INTEGER NOT NULL, UntilSnapshot INTEGER, Fingerprint TEXT NOT NULL, Content TEXT NOT NULL);
        CREATE INDEX Version_Object ON Version (ObjectId, UntilSnapshot);
        CREATE INDEX Version_From ON Version (FromSnapshot);
        """,
    };

    /// <param name="key">The key of an encrypted database (R6), or null.</param>
    public Database(string path, byte[]? key = null)
    {
        FilePath = path;
        _keyPragma = key is null ? null : TenantKeys.Pragma(key);
        Encrypted = key is not null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        var existed = File.Exists(path) && new FileInfo(path).Length > 0;
        using var c = Open();
        Run(c, "PRAGMA journal_mode=WAL;");
        var version = ReadVersion(c);
        if (existed && version > 0 && version < Migrations.Count)
        {
            // An automatic copy before every migration (tenant.db.bak-<date>)
            Run(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            var copy = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, copy, overwrite: true);
            BackupBeforeMigration = copy;
        }

        Update(c);
    }

    /// <summary>The copy made before this start migrated the schema, or null.</summary>
    public string? BackupBeforeMigration { get; }

    public string FilePath { get; }

    /// <summary>The current schema version (number of migrations run).</summary>
    public int Version
    {
        get
        {
            using var c = Open();
            return ReadVersion(c);
        }
    }

    private readonly string? _keyPragma;

    /// <summary>Opened with a key (an encrypted database).</summary>
    public bool Encrypted { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        if (_keyPragma is not null)
        {
            Run(c, _keyPragma);
        }

        return c;
    }

    private static void Run(SqliteConnection c, string sql, SqliteTransaction? t = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int ReadVersion(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Update(SqliteConnection c)
    {
        var version = ReadVersion(c);
        for (var i = version; i < Migrations.Count; i++)
        {
            using var t = c.BeginTransaction();
            Run(c, Migrations[i], t);
            Run(c, $"PRAGMA user_version = {i + 1};", t);
            t.Commit();
        }
    }

    // ------------------------------------------------------------------ documents

    public string? ReadDocument(string key)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Json FROM Document WHERE Sleutel = $s;";
            cmd.Parameters.AddWithValue("$s", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SaveDocument(string key, string json)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Document (Sleutel, Json, Gewijzigd) VALUES ($s, $j, $t) ON CONFLICT(Sleutel) DO UPDATE SET Json = $j, Gewijzigd = $t;";
            cmd.Parameters.AddWithValue("$s", key);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteDocument(string key)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM Document WHERE Sleutel = $s;";
            cmd.Parameters.AddWithValue("$s", key);
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ change log

    public void WriteLog(IEnumerable<LogEntry> rules)
    {
        lock (_lock)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            foreach (var r in rules)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO Logboek (Tijd, Gebruiker, Actie, Status, PlanId, Json) VALUES ($t, $g, $a, $s, $p, $j);";
                cmd.Parameters.AddWithValue("$t", r.Time.ToUniversalTime().ToString("o"));
                cmd.Parameters.AddWithValue("$g", r.User);
                cmd.Parameters.AddWithValue("$a", r.ChangeAction);
                cmd.Parameters.AddWithValue("$s", r.Status);
                cmd.Parameters.AddWithValue("$p", (object?)r.PlanId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(r, Options));
                cmd.ExecuteNonQuery();
            }

            t.Commit();
        }
    }

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<LogEntry> ReadLog(int count, DateTimeOffset? olderThan = null)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = olderThan is null
                ? "SELECT Json FROM Logboek ORDER BY Tijd DESC, Id DESC LIMIT $n;"
                : "SELECT Json FROM Logboek WHERE Tijd < $o ORDER BY Tijd DESC, Id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", count);
            if (olderThan is { } o)
            {
                cmd.Parameters.AddWithValue("$o", o.ToUniversalTime().ToString("o"));
            }

            var output = new List<LogEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (JsonSerializer.Deserialize<LogEntry>(r.GetString(0), Options) is { } rule)
                {
                    output.Add(rule);
                }
            }

            return output;
        }
    }

    /// <summary>Number of entries in the change log, or only those older than a date.</summary>
    public int CountLog(DateTimeOffset? olderThan = null)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = olderThan is null ? "SELECT COUNT(*) FROM Logboek;" : "SELECT COUNT(*) FROM Logboek WHERE Tijd < $o;";
            if (olderThan is { } o)
            {
                cmd.Parameters.AddWithValue("$o", o.ToUniversalTime().ToString("o"));
            }

            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Removes the entries older than a date (only after archiving and confirmation, see
    /// <see cref="Retention"/>).</summary>
    public int RemoveLog(DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM Logboek WHERE Tijd < $o;";
            cmd.Parameters.AddWithValue("$o", olderThan.ToUniversalTime().ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ KPI snapshot per day

    /// <summary>Saves today's snapshot; a later read on the same day replaces the earlier one of that day.</summary>
    public void SaveKpiSnapshot(DateOnly date, IReadOnlyDictionary<string, int> values)
    {
        lock (_lock)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            foreach (var (key, value) in values)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO KpiStand (Datum, Sleutel, Waarde) VALUES ($d, $s, $w) ON CONFLICT (Datum, Sleutel) DO UPDATE SET Waarde = excluded.Waarde;";
                cmd.Parameters.AddWithValue("$d", date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$s", key);
                cmd.Parameters.AddWithValue("$w", value);
                cmd.ExecuteNonQuery();
            }

            t.Commit();
        }
    }

    /// <summary>The saved snapshots from a date, per KPI key in order of date.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(DateOnly Date, int Value)>> ReadKpiSnapshot(DateOnly since)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Datum, Sleutel, Waarde FROM KpiStand WHERE Datum >= $v ORDER BY Datum;";
            cmd.Parameters.AddWithValue("$v", since.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            var output = new Dictionary<string, List<(DateOnly, int)>>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(1);
                if (!output.TryGetValue(key, out var list))
                {
                    output[key] = list = new List<(DateOnly, int)>();
                }

                list.Add((DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), r.GetInt32(2)));
            }

            return output.ToDictionary(x => x.Key, x => (IReadOnlyList<(DateOnly Date, int Value)>)x.Value, StringComparer.Ordinal);
        }
    }

    // ------------------------------------------------------------------ toegangslog

    public void WriteAccess(AccessEntry r)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Toegang (Tijd, Gebruiker, Wat, Details) VALUES ($t, $g, $w, $d);";
            cmd.Parameters.AddWithValue("$t", r.Time.ToUniversalTime().ToString("o"));
            cmd.Parameters.AddWithValue("$g", r.User);
            cmd.Parameters.AddWithValue("$w", r.What);
            cmd.Parameters.AddWithValue("$d", r.Details);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AccessEntry> ReadAccess(int count)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Tijd, Gebruiker, Wat, Details FROM Toegang ORDER BY Tijd DESC, Id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", count);
            var output = new List<AccessEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                output.Add(new AccessEntry(DateTimeOffset.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2), r.GetString(3)));
            }

            return output;
        }
    }
}

/// <summary>The change log in the database (append only).</summary>
public sealed class DatabaseChangeLog(Database db) : IChangeLog
{
    public void Write(LogEntry rule) => db.WriteLog(new[] { rule });

    public IReadOnlyList<LogEntry> Recent(int count) => db.ReadLog(count);
}
