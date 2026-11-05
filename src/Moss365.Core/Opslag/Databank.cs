using System.Text.Json;
using System.Text.Encodings.Web;
using Moss365.Core.Wijzigingen;
using Microsoft.Data.Sqlite;

namespace Moss365.Core.Opslag;

/// <summary>Een tekst (JSON) die ergens bewaard wordt: een bestand of een rij in de databank.</summary>
public interface ITekstOpslag
{
    string? Lees();

    void Bewaar(string inhoud);
}

/// <summary>Zoals vóór 0.8: een bestand, veilig weggeschreven via een tijdelijk bestand.</summary>
public sealed class BestandTekst(string pad) : ITekstOpslag
{
    public string? Lees() => File.Exists(pad) ? File.ReadAllText(pad) : null;

    public void Bewaar(string inhoud)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pad))!);
        var tijdelijk = pad + ".tmp";
        File.WriteAllText(tijdelijk, inhoud);
        File.Move(tijdelijk, pad, overwrite: true);
    }
}

/// <summary>Een document in de databank (tabel Document), bv. "gepland" of "weergave".</summary>
public sealed class DatabankTekst(Databank db, string sleutel) : ITekstOpslag
{
    public string? Lees() => db.LeesDocument(sleutel);

    public void Bewaar(string inhoud) => db.BewaarDocument(sleutel, inhoud);
}

/// <summary>Wie bekeek of exporteerde wat (toegangslog, 0.8). Nooit de inhoud zelf, enkel wat en hoeveel.</summary>
public sealed record ToegangRegel(DateTimeOffset Tijd, string Gebruiker, string Wat, string Details);

/// <summary>
/// De databank van Moss365 (SQLite, WAL-modus) in de gegevensmap: logboek (enkel toevoegen), documenten (gepland,
/// opvolging, weergave, verbinding) en het toegangslog. Het schema heeft versies; <see cref="Migraties"/> werkt een
/// oudere databank bij. Zelfde code later op de server (W1 in de roadmap).
/// </summary>
public sealed class Databank
{
    public const string Bestandsnaam = "moss365.db";

    private static readonly JsonSerializerOptions Opties = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _verbinding;
    private readonly object _slot = new();

    /// <summary>De migraties, in volgorde; de index + 1 is de schemaversie.</summary>
    public static readonly IReadOnlyList<string> Migraties = new[]
    {
        // 1 (0.8): logboek, documenten, toegangslog
        """
        CREATE TABLE Logboek (Id INTEGER PRIMARY KEY AUTOINCREMENT, Tijd TEXT NOT NULL, Gebruiker TEXT NOT NULL, Actie TEXT NOT NULL,
                              Status TEXT NOT NULL, PlanId TEXT, Json TEXT NOT NULL);
        CREATE INDEX Logboek_Tijd ON Logboek (Tijd);
        CREATE TABLE Document (Sleutel TEXT PRIMARY KEY, Json TEXT NOT NULL, Gewijzigd TEXT NOT NULL);
        CREATE TABLE Toegang (Id INTEGER PRIMARY KEY AUTOINCREMENT, Tijd TEXT NOT NULL, Gebruiker TEXT NOT NULL, Wat TEXT NOT NULL, Details TEXT NOT NULL);
        CREATE INDEX Toegang_Tijd ON Toegang (Tijd);
        """,

        // 2 (0.8.1): de stand van de KPI's per dag (enkel aantallen, geen namen), voor het verloop op Statistieken
        """
        CREATE TABLE KpiStand (Datum TEXT NOT NULL, Sleutel TEXT NOT NULL, Waarde INTEGER NOT NULL, PRIMARY KEY (Datum, Sleutel));
        """,
    };

    public Databank(string pad)
    {
        Pad = pad;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pad))!);
        _verbinding = new SqliteConnectionStringBuilder { DataSource = pad, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = Open();
        Voer(c, "PRAGMA journal_mode=WAL;");
        Bijwerken(c);
    }

    public string Pad { get; }

    /// <summary>De huidige schemaversie (aantal uitgevoerde migraties).</summary>
    public int Versie
    {
        get
        {
            using var c = Open();
            return LeesVersie(c);
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_verbinding);
        c.Open();
        return c;
    }

    private static void Voer(SqliteConnection c, string sql, SqliteTransaction? t = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int LeesVersie(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Bijwerken(SqliteConnection c)
    {
        var versie = LeesVersie(c);
        for (var i = versie; i < Migraties.Count; i++)
        {
            using var t = c.BeginTransaction();
            Voer(c, Migraties[i], t);
            Voer(c, $"PRAGMA user_version = {i + 1};", t);
            t.Commit();
        }
    }

    // ------------------------------------------------------------------ documenten

    public string? LeesDocument(string sleutel)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Json FROM Document WHERE Sleutel = $s;";
            cmd.Parameters.AddWithValue("$s", sleutel);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void BewaarDocument(string sleutel, string json)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Document (Sleutel, Json, Gewijzigd) VALUES ($s, $j, $t) ON CONFLICT(Sleutel) DO UPDATE SET Json = $j, Gewijzigd = $t;";
            cmd.Parameters.AddWithValue("$s", sleutel);
            cmd.Parameters.AddWithValue("$j", json);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ logboek

    public void SchrijfLog(IEnumerable<LogRegel> regels)
    {
        lock (_slot)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            foreach (var r in regels)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO Logboek (Tijd, Gebruiker, Actie, Status, PlanId, Json) VALUES ($t, $g, $a, $s, $p, $j);";
                cmd.Parameters.AddWithValue("$t", r.Tijd.ToUniversalTime().ToString("o"));
                cmd.Parameters.AddWithValue("$g", r.Gebruiker);
                cmd.Parameters.AddWithValue("$a", r.Actie);
                cmd.Parameters.AddWithValue("$s", r.Status);
                cmd.Parameters.AddWithValue("$p", (object?)r.PlanId?.ToString() ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(r, Opties));
                cmd.ExecuteNonQuery();
            }

            t.Commit();
        }
    }

    /// <summary>De recentste regels, nieuwste eerst.</summary>
    public IReadOnlyList<LogRegel> LeesLog(int aantal, DateTimeOffset? ouderDan = null)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = ouderDan is null
                ? "SELECT Json FROM Logboek ORDER BY Tijd DESC, Id DESC LIMIT $n;"
                : "SELECT Json FROM Logboek WHERE Tijd < $o ORDER BY Tijd DESC, Id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", aantal);
            if (ouderDan is { } o)
            {
                cmd.Parameters.AddWithValue("$o", o.ToUniversalTime().ToString("o"));
            }

            var uit = new List<LogRegel>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (JsonSerializer.Deserialize<LogRegel>(r.GetString(0), Opties) is { } regel)
                {
                    uit.Add(regel);
                }
            }

            return uit;
        }
    }

    /// <summary>Aantal regels in het logboek, of enkel die ouder dan een datum.</summary>
    public int AantalLog(DateTimeOffset? ouderDan = null)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = ouderDan is null ? "SELECT COUNT(*) FROM Logboek;" : "SELECT COUNT(*) FROM Logboek WHERE Tijd < $o;";
            if (ouderDan is { } o)
            {
                cmd.Parameters.AddWithValue("$o", o.ToUniversalTime().ToString("o"));
            }

            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Haalt de regels ouder dan een datum weg (enkel na archiveren en bevestiging, zie <see cref="Bewaartermijn"/>).</summary>
    public int VerwijderLog(DateTimeOffset ouderDan)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM Logboek WHERE Tijd < $o;";
            cmd.Parameters.AddWithValue("$o", ouderDan.ToUniversalTime().ToString("o"));
            return cmd.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------------ KPI-stand per dag

    /// <summary>Bewaart de stand van vandaag; een latere lezing op dezelfde dag vervangt die van eerder die dag.</summary>
    public void BewaarKpiStand(DateOnly datum, IReadOnlyDictionary<string, int> waarden)
    {
        lock (_slot)
        {
            using var c = Open();
            using var t = c.BeginTransaction();
            foreach (var (sleutel, waarde) in waarden)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = t;
                cmd.CommandText = "INSERT INTO KpiStand (Datum, Sleutel, Waarde) VALUES ($d, $s, $w) ON CONFLICT (Datum, Sleutel) DO UPDATE SET Waarde = excluded.Waarde;";
                cmd.Parameters.AddWithValue("$d", datum.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$s", sleutel);
                cmd.Parameters.AddWithValue("$w", waarde);
                cmd.ExecuteNonQuery();
            }

            t.Commit();
        }
    }

    /// <summary>De bewaarde standen vanaf een datum, per KPI-sleutel in volgorde van datum.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(DateOnly Datum, int Waarde)>> LeesKpiStand(DateOnly vanaf)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Datum, Sleutel, Waarde FROM KpiStand WHERE Datum >= $v ORDER BY Datum;";
            cmd.Parameters.AddWithValue("$v", vanaf.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            var uit = new Dictionary<string, List<(DateOnly, int)>>(StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var sleutel = r.GetString(1);
                if (!uit.TryGetValue(sleutel, out var lijst))
                {
                    uit[sleutel] = lijst = new List<(DateOnly, int)>();
                }

                lijst.Add((DateOnly.ParseExact(r.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), r.GetInt32(2)));
            }

            return uit.ToDictionary(x => x.Key, x => (IReadOnlyList<(DateOnly Datum, int Waarde)>)x.Value, StringComparer.Ordinal);
        }
    }

    // ------------------------------------------------------------------ toegangslog

    public void SchrijfToegang(ToegangRegel r)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Toegang (Tijd, Gebruiker, Wat, Details) VALUES ($t, $g, $w, $d);";
            cmd.Parameters.AddWithValue("$t", r.Tijd.ToUniversalTime().ToString("o"));
            cmd.Parameters.AddWithValue("$g", r.Gebruiker);
            cmd.Parameters.AddWithValue("$w", r.Wat);
            cmd.Parameters.AddWithValue("$d", r.Details);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ToegangRegel> LeesToegang(int aantal)
    {
        lock (_slot)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Tijd, Gebruiker, Wat, Details FROM Toegang ORDER BY Tijd DESC, Id DESC LIMIT $n;";
            cmd.Parameters.AddWithValue("$n", aantal);
            var uit = new List<ToegangRegel>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                uit.Add(new ToegangRegel(DateTimeOffset.Parse(r.GetString(0), System.Globalization.CultureInfo.InvariantCulture), r.GetString(1), r.GetString(2), r.GetString(3)));
            }

            return uit;
        }
    }
}

/// <summary>Het logboek in de databank (enkel toevoegen).</summary>
public sealed class DatabankLogboek(Databank db) : ILogboek
{
    public void Schrijf(LogRegel regel) => db.SchrijfLog(new[] { regel });

    public IReadOnlyList<LogRegel> Recent(int aantal) => db.LeesLog(aantal);
}
