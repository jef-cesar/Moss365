using System.Globalization;
using System.Text.Json;
using Moss365.Core.Security;

namespace Moss365.Core.Storage;

/// <summary>The sign-in and audit logs kept locally (R18, table LocalLog): one row per line of Microsoft, never twice.</summary>
public sealed partial class Database
{
    private static string Utc(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Keeps the sign-ins that are new; returns how many were new.</summary>
    public int KeepSignIns(IEnumerable<KeptSignIn> kept, string kind = LocalLogs.SignInKind)
        => KeepLocal(kind, kept.Select(k => (k.SignIn.Id, k.SignIn.Time, JsonSerializer.Serialize(k, Options))));

    /// <summary>Keeps the audit lines that are new; returns how many were new.</summary>
    public int KeepAudits(IEnumerable<AuditEvent> audits, string kind = LocalLogs.AuditKind)
        => KeepLocal(kind, audits.Where(a => a.Id.Length > 0).Select(a => (a.Id, a.Time, JsonSerializer.Serialize(a, Options))));

    private int KeepLocal(string kind, IEnumerable<(string Id, DateTimeOffset Time, string Json)> rows)
    {
        lock (_lock)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "INSERT OR IGNORE INTO LocalLog (Kind, Id, Time, Json) VALUES ($k, $i, $t, $j);";
            var pk = cmd.Parameters.AddWithValue("$k", kind);
            var pi = cmd.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Text);
            var pt = cmd.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Text);
            var pj = cmd.Parameters.Add("$j", Microsoft.Data.Sqlite.SqliteType.Text);
            var added = 0;
            foreach (var (id, time, json) in rows)
            {
                pi.Value = id;
                pt.Value = Utc(time);
                pj.Value = json;
                added += cmd.ExecuteNonQuery();
            }

            t.Commit();
            return added;
        }
    }

    /// <summary>The sign-ins kept between two times, newest first.</summary>
    public IReadOnlyList<KeptSignIn> ReadSignIns(DateTimeOffset from, DateTimeOffset until, int max = 5000, string kind = LocalLogs.SignInKind)
        => ReadLocal(kind, from, until, max).Select(j => JsonSerializer.Deserialize<KeptSignIn>(j, Options)).OfType<KeptSignIn>().ToList();

    /// <summary>The audit lines kept between two times, newest first.</summary>
    public IReadOnlyList<AuditEvent> ReadAudits(DateTimeOffset from, DateTimeOffset until, int max = 5000, string kind = LocalLogs.AuditKind)
        => ReadLocal(kind, from, until, max).Select(j => JsonSerializer.Deserialize<AuditEvent>(j, Options)).OfType<AuditEvent>().ToList();

    private List<string> ReadLocal(string kind, DateTimeOffset from, DateTimeOffset until, int max)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Json FROM LocalLog WHERE Kind = $k AND Time >= $f AND Time < $u ORDER BY Time DESC LIMIT $m;";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$f", Utc(from));
            cmd.Parameters.AddWithValue("$u", Utc(until));
            cmd.Parameters.AddWithValue("$m", Math.Max(1, max));
            var output = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                output.Add(r.GetString(0));
            }

            return output;
        }
    }

    /// <summary>The time of the newest line kept of a kind, or null.</summary>
    public DateTimeOffset? LastLocal(string kind)
    {
        lock (_lock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MAX(Time) FROM LocalLog WHERE Kind = $k;";
            cmd.Parameters.AddWithValue("$k", kind);
            return cmd.ExecuteScalar() is string s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>Lines of the local logs older than the date (both kinds).</summary>
    public int CountLocalLogs(DateTimeOffset olderThan) => Scalar("SELECT COUNT(*) FROM LocalLog WHERE Time < $o;", olderThan);

    public int RemoveLocalLogs(DateTimeOffset olderThan) => Execute("DELETE FROM LocalLog WHERE Time < $o;", olderThan);
}
