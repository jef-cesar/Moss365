using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Storage;

/// <summary>For All tenants (R19, U20): the last count per key of one tenant, and the day of the newest one.</summary>
public sealed record TenantCounts(DateOnly? Date, IReadOnlyDictionary<string, int> Values);

public sealed partial class Database
{
    /// <summary>
    /// The last count per key from the counts kept per day of a tenant database (the KPIs and the overview), for All
    /// tenants (R19, U20). The database is opened only to read: no migration, no copy, nothing written, and only counts
    /// leave it. Null when the tenant has no database yet.
    /// </summary>
    public static TenantCounts? ReadCounts(string path, byte[]? key, IReadOnlyCollection<string> keys)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return null;
        }

        var connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        using var c = new SqliteConnection(connection);
        c.Open();
        if (key is not null)
        {
            Run(c, TenantKeys.Pragma(key));
        }

        Run(c, "PRAGMA query_only = ON;");
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        DateOnly? newest = null;
        if (keys.Count == 0)
        {
            return new TenantCounts(null, values);
        }

        using var cmd = c.CreateCommand();
        var names = keys.Select((k, i) => (Key: k, Name: $"$k{i}")).ToList();
        cmd.CommandText = $"""
            SELECT k.Sleutel, k.Waarde, k.Datum FROM KpiStand k
            JOIN (SELECT Sleutel, MAX(Datum) AS Laatste FROM KpiStand WHERE Sleutel IN ({string.Join(", ", names.Select(n => n.Name))}) GROUP BY Sleutel) m
              ON m.Sleutel = k.Sleutel AND m.Laatste = k.Datum;
            """;
        foreach (var (k, name) in names)
        {
            cmd.Parameters.AddWithValue(name, k);
        }

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            values[r.GetString(0)] = r.GetInt32(1);
            if (DateOnly.TryParseExact(r.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && (newest is null || d > newest))
            {
                newest = d;
            }
        }

        return new TenantCounts(newest, values);
    }
}
