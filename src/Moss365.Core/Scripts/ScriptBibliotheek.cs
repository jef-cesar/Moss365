using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Scripts;

/// <summary>Een script in de bibliotheek: ingebouwd (met de app meegeleverd, alleen lezen) of eigen (lokaal opgeslagen).</summary>
public sealed record ScriptBestand(string Pad, ScriptKop Kop, bool Ingebouwd, string Vingerafdruk, string Map = "")
{
    /// <summary>Sleutel voor de laatste waarden en de runs: de bestandsnaam zonder extensie (zoals Get-ScriptWaarde).</summary>
    public string Sleutel => Path.GetFileNameWithoutExtension(Pad);

    public string Herkomst => Ingebouwd ? "ingebouwd" : "eigen";
}

/// <summary>Waar de scripts en hun gegevens staan. Alles lokaal; in de webversie komt dit op de server.</summary>
public sealed record ScriptMappen(string Ingebouwd, string Gegevens)
{
    public string Eigen => Path.Combine(Gegevens, "eigen");
    public string Waarden => Path.Combine(Gegevens, "waarden");
    public string Runs => Path.Combine(Gegevens, "runs");
    public string Goedgekeurd => Path.Combine(Eigen, "goedgekeurd.json");
}

/// <summary>Lezen, opslaan, importeren en verwijderen van scripts, met de vingerafdruk van de eigen scripts.</summary>
public static class ScriptBibliotheek
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Vingerafdruk(string tekst)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tekst.Replace("\r\n", "\n")))).ToLowerInvariant();

    /// <summary>Alle scripts: ingebouwd (ook in submappen, enkel met een MOSS365-kop) en eigen, op naam gesorteerd.</summary>
    public static IReadOnlyList<ScriptBestand> Laad(ScriptMappen mappen)
    {
        var uit = new List<ScriptBestand>();
        if (Directory.Exists(mappen.Ingebouwd))
        {
            // Enkel scripts met een MOSS365-kop: de andere zitten in de download voor gebruik buiten de app
            foreach (var pad in Directory.EnumerateFiles(mappen.Ingebouwd, "*.ps1", SearchOption.AllDirectories))
            {
                var script = LeesBestand(pad, true, Path.GetRelativePath(mappen.Ingebouwd, Path.GetDirectoryName(pad)!).Replace('\\', '/') is var m && m != "." ? m : string.Empty);
                if (script.Kop.UitBlok)
                {
                    uit.Add(script);
                }
            }
        }

        if (Directory.Exists(mappen.Eigen))
        {
            uit.AddRange(Directory.EnumerateFiles(mappen.Eigen, "*.ps1").Select(p => LeesBestand(p, false)));
        }

        return uit.OrderBy(s => s.Kop.Naam, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static ScriptBestand LeesBestand(string pad, bool ingebouwd, string map = "")
    {
        var tekst = File.ReadAllText(pad);
        return new ScriptBestand(pad, ScriptTekst.Lees(tekst, pad), ingebouwd, Vingerafdruk(tekst), map);
    }

    /// <summary>Een geldige bestandsnaam uit de naam van het script.</summary>
    public static string Bestandsnaam(string naam)
    {
        var schoon = new string(naam.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : c == ' ' ? '-' : '_').ToArray()).Trim('-', '_', '.');
        return (schoon.Length == 0 ? "script" : schoon) + ".ps1";
    }

    /// <summary>Slaat een eigen script op (nieuw of bijgewerkt) en keurt het goed met zijn vingerafdruk.</summary>
    public static ScriptBestand Opslaan(ScriptMappen mappen, string tekst, string? bestaandPad = null)
    {
        Directory.CreateDirectory(mappen.Eigen);
        var kop = ScriptTekst.Lees(tekst, bestaandPad ?? "script.ps1");
        var pad = bestaandPad;
        if (pad is null || !IsEigen(mappen, pad))
        {
            pad = Path.Combine(mappen.Eigen, Bestandsnaam(kop.Naam));
            var ingebouwdeNamen = Laad(mappen with { Gegevens = Path.Combine(mappen.Gegevens, "-geen-") }).Select(s => s.Sleutel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var basis = Path.GetFileNameWithoutExtension(pad);
            var n = 1;
            while (File.Exists(pad) || ingebouwdeNamen.Contains(Path.GetFileNameWithoutExtension(pad)))
            {
                pad = Path.Combine(mappen.Eigen, $"{basis}-{++n}.ps1");
            }
        }

        // Altijd met BOM: Windows PowerShell 5.1 leest een bestand zonder BOM als ANSI
        File.WriteAllText(pad, tekst.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        var bestand = LeesBestand(pad, false);
        Keur(mappen, bestand);
        return bestand;
    }

    public static bool IsEigen(ScriptMappen mappen, string pad)
        => string.Equals(Path.GetFullPath(Path.GetDirectoryName(pad)!), Path.GetFullPath(mappen.Eigen), StringComparison.OrdinalIgnoreCase);

    public static void Verwijder(ScriptMappen mappen, ScriptBestand script)
    {
        if (script.Ingebouwd || !IsEigen(mappen, script.Pad))
        {
            throw new InvalidOperationException("Een ingebouwd script kan je niet verwijderen.");
        }

        File.Delete(script.Pad);
        var lijst = Goedkeuringen(mappen);
        lijst.Remove(Path.GetFileName(script.Pad));
        BewaarGoedkeuringen(mappen, lijst);
    }

    /// <summary>Importeert een .ps1 als eigen script (een kopie; het origineel blijft waar het stond).</summary>
    public static ScriptBestand Importeer(ScriptMappen mappen, string bronPad)
    {
        var tekst = File.ReadAllText(bronPad);
        var kop = ScriptTekst.Lees(tekst, bronPad);
        if (!kop.UitBlok)
        {
            tekst = ScriptTekst.Schrijf(kop, tekst);
        }

        return Opslaan(mappen, tekst);
    }

    /// <summary>Is dit eigen script buiten de app gewijzigd sinds het opgeslagen of goedgekeurd werd?</summary>
    public static bool IsGewijzigdBuitenApp(ScriptMappen mappen, ScriptBestand script)
        => !script.Ingebouwd && (!Goedkeuringen(mappen).TryGetValue(Path.GetFileName(script.Pad), out var v) || v != script.Vingerafdruk);

    public static void Keur(ScriptMappen mappen, ScriptBestand script)
    {
        var lijst = Goedkeuringen(mappen);
        lijst[Path.GetFileName(script.Pad)] = script.Vingerafdruk;
        BewaarGoedkeuringen(mappen, lijst);
    }

    private static Dictionary<string, string> Goedkeuringen(ScriptMappen mappen)
    {
        try
        {
            return File.Exists(mappen.Goedgekeurd)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mappen.Goedgekeurd)) is { } d
                    ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase)
                : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void BewaarGoedkeuringen(ScriptMappen mappen, Dictionary<string, string> lijst)
    {
        Directory.CreateDirectory(mappen.Eigen);
        File.WriteAllText(mappen.Goedgekeurd, JsonSerializer.Serialize(lijst, Json));
    }

    /// <summary>Een nieuw, leeg script met een voorbeeld.</summary>
    public static string NieuwScript(string naam) => ScriptTekst.Schrijf(new ScriptKop
    {
        Naam = naam,
        Omschrijving = "Wat dit script doet.",
        Verbinding = ScriptVerbinding.Exchange,
        Variabelen = new[] { new ScriptVariabele("Postvak", VariabeleSoort.Postvak, true, "Welk postvak?") },
    }, """

$ErrorActionPreference = 'Stop'

# De app is al verbonden (zie Verbinding hierboven). Wat je teruggeeft, verschijnt als tabel;
# Write-Host en Write-Warning verschijnen als tekst. Bestanden zet je in $MossUitvoer.
Get-Mailbox -Identity $Postvak | Select-Object DisplayName, PrimarySmtpAddress, RecipientTypeDetails

""");
}

/// <summary>
/// De laatst gebruikte waarden per script, in hetzelfde bestand als het blok Get-ScriptWaarde in de scripts:
/// <c>waarden\&lt;script&gt;.json</c>, met per naam een tekst of een lijst.
/// </summary>
public static class ScriptWaarden
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Dictionary<string, string> Lees(ScriptMappen mappen, string sleutel)
    {
        var uit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pad = Path.Combine(mappen.Waarden, sleutel + ".json");
        if (!File.Exists(pad))
        {
            return uit;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pad));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                uit[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.Array => string.Join('\n', p.Value.EnumerateArray().Select(x => x.ToString())),
                    JsonValueKind.Null => string.Empty,
                    _ => p.Value.ToString(),
                };
            }
        }
        catch (JsonException)
        {
            // Kapot bestand: opnieuw vragen
        }

        return uit;
    }

    /// <summary>Bewaart de waarden van een run; lijsten (een waarde per regel) als JSON-lijst, zoals Get-ScriptWaarde.</summary>
    public static void Bewaar(ScriptMappen mappen, string sleutel, ScriptKop kop, IReadOnlyDictionary<string, string> waarden)
    {
        Directory.CreateDirectory(mappen.Waarden);
        var pad = Path.Combine(mappen.Waarden, sleutel + ".json");
        var bestaand = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Lees(mappen, sleutel))
        {
            bestaand[k] = v;
        }

        foreach (var (naam, waarde) in waarden)
        {
            var soort = kop.Variabelen.FirstOrDefault(v => v.Naam.Equals(naam, StringComparison.OrdinalIgnoreCase))?.Soort;
            bestaand[naam] = soort == VariabeleSoort.Lijst ? Lijst(waarde) : waarde;
        }

        File.WriteAllText(pad, JsonSerializer.Serialize(bestaand, Json), new UTF8Encoding(true));
    }

    public static string[] Lijst(string waarde)
        => waarde.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Relatieve waarden voor ingeplande scripts, op het moment van de run: @vandaag, @gisteren, @werkjaar.van,
    /// @werkjaar.tot, @vorigemaand.van, @vorigemaand.tot, @dezemaand.van, @dezemaand.tot.
    /// </summary>
    public static string Los(string waarde, DateTime nu)
    {
        var dag = nu.Date;
        var werkjaar = dag.Month >= 9 ? dag.Year : dag.Year - 1;
        var dezeMaand = new DateTime(dag.Year, dag.Month, 1);
        DateTime? d = waarde.Trim().ToLowerInvariant() switch
        {
            "@vandaag" => dag,
            "@gisteren" => dag.AddDays(-1),
            "@werkjaar.van" => new DateTime(werkjaar, 9, 1),
            "@werkjaar.tot" => new DateTime(werkjaar + 1, 8, 31),
            "@dezemaand.van" => dezeMaand,
            "@dezemaand.tot" => dezeMaand.AddMonths(1).AddDays(-1),
            "@vorigemaand.van" => dezeMaand.AddMonths(-1),
            "@vorigemaand.tot" => dezeMaand.AddDays(-1),
            _ => null,
        };
        return d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? waarde;
    }

    /// <summary>Controle van de waarden voor een run: verplichte velden, getallen, datums, keuzes.</summary>
    public static IReadOnlyList<string> Controleer(ScriptKop kop, IReadOnlyDictionary<string, string> waarden)
    {
        var fouten = new List<string>();
        foreach (var v in kop.Variabelen)
        {
            var w = waarden.TryGetValue(v.Naam, out var x) ? x.Trim() : string.Empty;
            if (w.Length == 0)
            {
                if (v.Verplicht)
                {
                    fouten.Add($"{v.Label}: verplicht.");
                }

                continue;
            }

            switch (v.Soort)
            {
                case VariabeleSoort.Getal when !int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                    fouten.Add($"{v.Label}: geen geheel getal.");
                    break;
                case VariabeleSoort.Datum when !DateTime.TryParseExact(Los(w, DateTime.Now), new[] { "yyyy-MM-dd", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    fouten.Add($"{v.Label}: geen datum (dd/mm/jjjj).");
                    break;
                case VariabeleSoort.Keuze when v.Opties is { Count: > 0 } o && !o.Contains(w, StringComparer.OrdinalIgnoreCase):
                    fouten.Add($"{v.Label}: kies een van {string.Join(", ", o)}.");
                    break;
            }
        }

        return fouten;
    }
}

public enum RunResultaat
{
    Bezig,
    Gelukt,
    Waarschuwingen,
    Fout,
    Geannuleerd,
}

/// <summary>Eén run van een script, bewaard als run.json in de map van de run.</summary>
public sealed record ScriptRun
{
    public string Id { get; init; } = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    public string Sleutel { get; init; } = string.Empty;
    public string Naam { get; init; } = string.Empty;
    public string Vingerafdruk { get; init; } = string.Empty;
    public string Wie { get; init; } = string.Empty;
    public DateTimeOffset Start { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Einde { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RunResultaat Resultaat { get; init; } = RunResultaat.Bezig;

    public bool Voorbeeld { get; init; }
    public bool Eenmalig { get; init; }
    public Dictionary<string, string> Waarden { get; init; } = new();
    public string Melding { get; init; } = string.Empty;
    public int Fouten { get; init; }
    public int Waarschuwingen { get; init; }

    [JsonIgnore]
    public string Map { get; init; } = string.Empty;

    public TimeSpan? Duur => Einde - Start;

    public string ResultaatTekst => (Voorbeeld ? "voorbeeld: " : string.Empty) + Resultaat switch
    {
        RunResultaat.Bezig => "bezig",
        RunResultaat.Gelukt => "gelukt",
        RunResultaat.Waarschuwingen => "gelukt, met waarschuwingen",
        RunResultaat.Fout => "mislukt",
        _ => "geannuleerd",
    };
}

/// <summary>De runs op schijf: runs\&lt;script&gt;\&lt;id&gt;\ met run.json, uitvoer.jsonl, tabellen en bestanden.</summary>
public static class ScriptRuns
{
    public const int BewaarDagen = 90;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string NieuweMap(ScriptMappen mappen, string sleutel, string id)
    {
        var map = Path.Combine(mappen.Runs, sleutel, id);
        var n = 1;
        while (Directory.Exists(map))
        {
            map = Path.Combine(mappen.Runs, sleutel, $"{id}-{++n}");
        }

        Directory.CreateDirectory(Path.Combine(map, "bestanden"));
        return map;
    }

    public static void Bewaar(ScriptRun run) => File.WriteAllText(Path.Combine(run.Map, "run.json"), JsonSerializer.Serialize(run, Json));

    public static IReadOnlyList<ScriptRun> Lijst(ScriptMappen mappen, string? sleutel = null)
    {
        var basis = sleutel is null ? mappen.Runs : Path.Combine(mappen.Runs, sleutel);
        if (!Directory.Exists(basis))
        {
            return Array.Empty<ScriptRun>();
        }

        var uit = new List<ScriptRun>();
        foreach (var bestand in Directory.EnumerateFiles(basis, "run.json", SearchOption.AllDirectories))
        {
            try
            {
                if (JsonSerializer.Deserialize<ScriptRun>(File.ReadAllText(bestand), Json) is { } r)
                {
                    uit.Add(r with { Map = Path.GetDirectoryName(bestand)! });
                }
            }
            catch (JsonException)
            {
            }
        }

        return uit.OrderByDescending(r => r.Start).ToList();
    }

    /// <summary>Verwijdert de runs die ouder zijn dan 90 dagen; geeft het aantal terug.</summary>
    public static int Opruimen(ScriptMappen mappen, DateTimeOffset nu, int dagen = BewaarDagen)
    {
        var weg = 0;
        foreach (var r in Lijst(mappen).Where(r => r.Start < nu.AddDays(-dagen)))
        {
            try
            {
                Directory.Delete(r.Map, true);
                weg++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return weg;
    }
}
