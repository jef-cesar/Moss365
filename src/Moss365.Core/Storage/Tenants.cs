using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Storage;

/// <summary>
/// One organisation (tenant) in Moss365. <see cref="Id"/> is the name of its folder (<c>tenants\&lt;id&gt;</c>), not the
/// Entra tenant ID, which may be unknown at first. The colour and name are in the bar, so you always see where you work.
/// </summary>
public sealed record TenantInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>A colour for the bar, as #rrggbb.</summary>
    public string Colour { get; init; } = TenantColours.All[0];

    /// <summary>The Entra tenant ID, when known (filled in by the connections or the wizard).</summary>
    public string? TenantId { get; init; }

    /// <summary>A domain of the tenant, e.g. contoso.com, when known.</summary>
    public string? Domain { get; init; }

    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
}

public static class TenantColours
{
    /// <summary>Colours that are easy to tell apart, with white text readable on each.</summary>
    public static readonly IReadOnlyList<string> All = new[] { "#4f6d3a", "#6b3f8f", "#1f5f8b", "#a0522d", "#2f7d6d", "#8b1e3f", "#5b5b5b", "#7a6a1f" };
}

/// <summary>
/// The app database <c>moss365.db</c> in the data folder: the list of tenants and app settings. It never holds data of a
/// tenant: that is in <c>tenants\&lt;id&gt;\tenant.db</c>.
/// </summary>
public sealed class AppDatabase
{
    public const string FileName = "moss365.db";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _connection;
    private readonly object _lock = new();

    /// <summary>The migrations of the app database, in order; the index + 1 is the schema version.</summary>
    public static readonly IReadOnlyList<string> Migrations = new[]
    {
        // 1 (0.12): tenants and settings
        """
        CREATE TABLE Tenant (Id TEXT PRIMARY KEY, Json TEXT NOT NULL, Position INTEGER NOT NULL);
        CREATE TABLE Setting (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        """,
    };

    public AppDatabase(string path)
    {
        FilePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            for (var i = version; i < Migrations.Count; i++)
            {
                using var t = c.BeginTransaction();
                using var m = c.CreateCommand();
                m.Transaction = t;
                m.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
                m.ExecuteNonQuery();
                t.Commit();
            }
        }
    }

    public string FilePath { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        return c;
    }

    /// <summary>Is this file the database of an older version (0.8 to 0.11), with the data of one organisation?</summary>
    public static bool IsSingleTenantDatabase(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'Logboek';";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    public IReadOnlyList<TenantInfo> Tenants()
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Json FROM Tenant ORDER BY Position, Id;";
            using var r = cmd.ExecuteReader();
            var output = new List<TenantInfo>();
            while (r.Read())
            {
                if (JsonSerializer.Deserialize<TenantInfo>(r.GetString(0), Json) is { } t)
                {
                    output.Add(t);
                }
            }

            return output;
        }
    }

    public void SaveTenant(TenantInfo tenant)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Tenant (Id, Json, Position) VALUES ($i, $j, (SELECT coalesce(max(Position), 0) + 1 FROM Tenant))
                ON CONFLICT(Id) DO UPDATE SET Json = $j;
                """;
            cmd.Parameters.AddWithValue("$i", tenant.Id);
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(tenant, Json));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Takes a tenant off the list. Its folder and database stay (see <see cref="TenantFolders.Remove"/>).</summary>
    public void RemoveTenant(string id)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM Tenant WHERE Id = $i;";
            cmd.Parameters.AddWithValue("$i", id);
            cmd.ExecuteNonQuery();
        }
    }

    public string? Setting(string key)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Setting WHERE Key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string? value)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = value is null
                ? "DELETE FROM Setting WHERE Key = $k;"
                : "INSERT INTO Setting (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value = $v;";
            cmd.Parameters.AddWithValue("$k", key);
            if (value is not null)
            {
                cmd.Parameters.AddWithValue("$v", value);
            }

            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>
/// The folders of the tenants: <c>&lt;root&gt;\tenants\&lt;id&gt;\tenant.db</c>, with automatic copies
/// <c>tenant.db.bak-&lt;date&gt;</c> before a migration.
/// </summary>
public static class TenantFolders
{
    public const string DatabaseName = "tenant.db";

    public static string Folder(string root, string id) => Path.Combine(root, "tenants", id);

    public static string DatabasePath(string root, string id) => Path.Combine(Folder(root, id), DatabaseName);

    /// <summary>A new id: short, unique and safe as a folder name.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Takes a tenant's folder out of use without deleting anything: it is moved to <c>tenants\removed\</c>, where the
    /// administrator can delete it (or bring it back).
    /// </summary>
    public static string? Remove(string root, string id)
    {
        var folder = Folder(root, id);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var target = Path.Combine(root, "tenants", "removed", $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(folder, target);
        return target;
    }
}

/// <summary>
/// Moves an installation of 0.11 or earlier (one organisation: <c>moss365.db</c> with the change log, and loose files
/// next to it) to the layout of 0.12: the first tenant, with everything in its <c>tenant.db</c>. Nothing is deleted:
/// the old files move to <c>&lt;root&gt;\old-0.11\</c>, where the administrator can remove them.
/// </summary>
public static class TenantMigration
{
    /// <summary>Files and folders of the old layout that move into the tenant database.</summary>
    private static readonly string[] OldItems =
    {
        "moss365.db-wal", "moss365.db-shm", "organisatie.json", "organisatie-meegeleverd.json", "ca-backup", "scripts", "Inventaris", "logs",
        "imported-from.txt",
    };

    /// <summary>
    /// When the root holds a single-tenant database: creates the first tenant from it and returns it (the caller adds it
    /// to the app database). Returns null when there is nothing to migrate.
    /// </summary>
    public static (TenantInfo Tenant, IReadOnlyList<string> Messages)? FromSingleTenant(string root, string name)
    {
        var old = Path.Combine(root, AppDatabase.FileName);
        if (!AppDatabase.IsSingleTenantDatabase(old))
        {
            return null;
        }

        var messages = new List<string>();
        var tenant = new TenantInfo { Id = TenantFolders.NewId(), Name = name };
        var folder = TenantFolders.Folder(root, tenant.Id);
        Directory.CreateDirectory(folder);

        // The database itself becomes tenant.db (the WAL first into it, so nothing is lost)
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = old, Pooling = false }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }

        var databasePath = Path.Combine(folder, TenantFolders.DatabaseName);
        File.Copy(old, databasePath);
        var db = new Database(databasePath);
        IFileStore store = new DatabaseFileStore(db);

        // The own organisation
        var own = Path.Combine(root, "organisatie.json");
        if (File.Exists(own))
        {
            db.SaveDocument(Documents.Organisation, File.ReadAllText(own));
            messages.Add("organisatie");
        }

        // Conditional Access backups, own scripts, last values, script runs
        Count(messages, "back-ups van Conditional Access", store.Import(Path.Combine(root, "ca-backup"), CaBackup.FolderName));
        Count(messages, "eigen scripts", store.Import(Path.Combine(root, "scripts", "eigen"), "scripts/own"));
        if (store.ReadText("scripts/own/goedgekeurd.json") is { } approvals)
        {
            store.WriteText("scripts/approvals.json", approvals);
            store.Delete("scripts/own/goedgekeurd.json");
        }

        Count(messages, "laatste waarden", store.Import(Path.Combine(root, "scripts", "waarden"), "scripts/values"));
        Count(messages, "bestanden van script-runs", store.Import(Path.Combine(root, "scripts", "runs"), "runs"));

        // The inventory: the newest folders
        Count(messages, "inventarismappen", Inventory.InventoryLoader.ImportFolders(store, Path.Combine(root, "Inventaris")));

        // Out of the way, but not deleted
        var archive = Path.Combine(root, "old-0.11");
        Directory.CreateDirectory(archive);
        File.Move(old, Path.Combine(archive, AppDatabase.FileName), overwrite: true);
        foreach (var item in OldItems)
        {
            var path = Path.Combine(root, item);
            var target = Path.Combine(archive, item);
            if (File.Exists(path))
            {
                File.Move(path, target, overwrite: true);
            }
            else if (Directory.Exists(path) && !Directory.Exists(target))
            {
                Directory.Move(path, target);
            }
        }

        return (tenant, messages);
    }

    private static void Count(List<string> messages, string what, int count)
    {
        if (count > 0)
        {
            messages.Add($"{count} {what}");
        }
    }
}

/// <summary>Keys of the documents in the tenant database.</summary>
public static class Documents
{
    public const string Organisation = "organisatie";
    public const string FollowUp = "opvolging";
    public const string Scheduled = "gepland";
    public const string View = "weergave";
    public const string Connection = "verbinding";
}
