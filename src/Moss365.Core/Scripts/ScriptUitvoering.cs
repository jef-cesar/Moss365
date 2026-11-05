using System.Globalization;
using System.Text.Json;

namespace Moss365.Core.Scripts;

public enum GebeurtenisSoort
{
    Tekst,
    Info,
    Waarschuwing,
    Fout,
    Voortgang,
    Rij,
}

/// <summary>Iets wat een script tijdens de run meldt: een regel tekst, een fout, voortgang, of een rij voor een tabel.</summary>
public sealed record ScriptGebeurtenis(
    GebeurtenisSoort Soort,
    string Tekst,
    string? Kleur = null,
    int? Procent = null,
    string? Tabel = null,
    IReadOnlyDictionary<string, string>? Rij = null,
    bool Doorgaan = false,
    bool Klaar = false,
    int? Regel = null,
    int? Kolom = null);

/// <summary>Een tabel uit de uitvoer: de rijen die het script teruggaf (per soort object), of een CSV die het maakte.</summary>
public sealed record ScriptTabel(string Naam, IReadOnlyList<string> Kolommen, IReadOnlyList<IReadOnlyDictionary<string, string>> Rijen, string? Bestand = null);

/// <summary>Wat er moet draaien: welk script, met welke waarden, en of het een voorbeeld (-WhatIf) is.</summary>
public sealed record ScriptOpdracht(ScriptBestand Script, IReadOnlyDictionary<string, string> Waarden, bool Voorbeeld, string RunMap);

/// <summary>Draait een script in een apart PowerShell-proces met de aanmelding van de app.</summary>
public interface IScriptUitvoerder
{
    /// <summary>De uitgangscode: 0 gelukt, 1 gestopt met een fout, 2 klaar met fouten, 3 voorbeeld niet mogelijk, 4 verbinding mislukt.</summary>
    Task<int> VoerUitAsync(ScriptOpdracht opdracht, IProgress<ScriptGebeurtenis>? gebeurtenissen, CancellationToken annuleren);

    /// <summary>Syntaxfouten in de tekst (PowerShell kijkt het na zonder iets uit te voeren); leeg = in orde.</summary>
    Task<IReadOnlyList<ScriptGebeurtenis>> ControleerAsync(string tekst, CancellationToken annuleren)
        => Task.FromResult<IReadOnlyList<ScriptGebeurtenis>>(Array.Empty<ScriptGebeurtenis>());
}

public static class ScriptUitvoer
{
    /// <summary>De waarden voor de opstart, met het juiste JSON-type per soort (lijst, ja/nee, getal, datum).</summary>
    public static string WaardenJson(ScriptKop kop, IReadOnlyDictionary<string, string> waarden, DateTime nu)
    {
        var uit = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (naam, ruw) in waarden)
        {
            var w = ruw?.Trim() ?? string.Empty;
            var soort = kop.Variabelen.FirstOrDefault(v => v.Naam.Equals(naam, StringComparison.OrdinalIgnoreCase))?.Soort ?? VariabeleSoort.Tekst;
            uit[naam] = soort switch
            {
                VariabeleSoort.Lijst => ScriptWaarden.Lijst(w),
                VariabeleSoort.JaNee => w is "true" or "True" or "ja" or "1",
                VariabeleSoort.Getal when int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) => g,
                VariabeleSoort.Datum => Datum(ScriptWaarden.Los(w, nu)),
                _ => w,
            };
        }

        return JsonSerializer.Serialize(uit);
    }

    private static string Datum(string w)
        => DateTime.TryParseExact(w, new[] { "yyyy-MM-dd", "d/M/yyyy", "d-M-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : w;

    /// <summary>Eén regel van de opstart (JSON) naar een gebeurtenis; andere tekst wordt gewone tekst.</summary>
    public static ScriptGebeurtenis Lees(string regel)
    {
        if (!regel.StartsWith('{'))
        {
            return new ScriptGebeurtenis(GebeurtenisSoort.Tekst, regel);
        }

        try
        {
            using var doc = JsonDocument.Parse(regel);
            var e = doc.RootElement;
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : string.Empty;
            var soort = S("s") switch
            {
                "info" => GebeurtenisSoort.Info,
                "waarschuwing" => GebeurtenisSoort.Waarschuwing,
                "fout" => GebeurtenisSoort.Fout,
                "voortgang" => GebeurtenisSoort.Voortgang,
                "rij" => GebeurtenisSoort.Rij,
                _ => GebeurtenisSoort.Tekst,
            };
            Dictionary<string, string>? rij = null;
            if (soort == GebeurtenisSoort.Rij && e.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                rij = r.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? string.Empty : p.Value.ToString());
            }

            int? procent = e.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() >= 0 ? Math.Min(100, p.GetInt32()) : null;
            int? Getal(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            return new ScriptGebeurtenis(soort, S("t"), S("kleur") is { Length: > 0 } k ? k : null, procent, S("tabel") is { Length: > 0 } t ? t : null, rij,
                e.TryGetProperty("doorgaan", out var d) && d.ValueKind == JsonValueKind.True,
                e.TryGetProperty("klaar", out var kl) && kl.ValueKind == JsonValueKind.True, Getal("regel"), Getal("kolom"));
        }
        catch (JsonException)
        {
            return new ScriptGebeurtenis(GebeurtenisSoort.Tekst, regel);
        }
    }

    public static IReadOnlyList<ScriptGebeurtenis> LeesBestand(string pad)
        => File.Exists(pad) ? File.ReadLines(pad).Where(r => r.Length > 0).Select(Lees).ToList() : Array.Empty<ScriptGebeurtenis>();

    /// <summary>
    /// De tabellen van een run: de teruggegeven objecten per soort (zelfde naam en kolommen samen), en elke CSV in de
    /// map met bestanden.
    /// </summary>
    public static IReadOnlyList<ScriptTabel> Tabellen(IEnumerable<ScriptGebeurtenis> gebeurtenissen, string? bestandenMap = null)
    {
        var uit = new List<ScriptTabel>();
        foreach (var groep in gebeurtenissen.Where(g => g.Soort == GebeurtenisSoort.Rij && g.Rij is not null)
                     .GroupBy(g => (g.Tabel ?? "Resultaat") + "|" + string.Join(",", g.Rij!.Keys)))
        {
            var eerste = groep.First();
            uit.Add(new ScriptTabel(eerste.Tabel ?? "Resultaat", eerste.Rij!.Keys.ToList(), groep.Select(g => g.Rij!).ToList()));
        }

        // Dezelfde naam meer dan eens (andere kolommen): nummeren
        foreach (var dubbel in uit.GroupBy(t => t.Naam).Where(g => g.Count() > 1).ToList())
        {
            var i = 1;
            foreach (var t in dubbel)
            {
                uit[uit.IndexOf(t)] = t with { Naam = $"{t.Naam} ({i++})" };
            }
        }

        if (bestandenMap is not null && Directory.Exists(bestandenMap))
        {
            foreach (var csv in Directory.EnumerateFiles(bestandenMap, "*.csv", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (LeesCsv(csv) is { } t)
                {
                    uit.Add(t);
                }
            }
        }

        return uit;
    }

    /// <summary>Een CSV (komma of puntkomma, met of zonder aanhalingstekens) als tabel; null als hij niet leesbaar is.</summary>
    public static ScriptTabel? LeesCsv(string pad, int maxRijen = 20000)
    {
        try
        {
            var regels = File.ReadLines(pad).Take(maxRijen + 1).ToList();
            if (regels.Count == 0)
            {
                return null;
            }

            var scheiding = regels[0].Count(c => c == ';') > regels[0].Count(c => c == ',') ? ';' : ',';
            var kop = Splits(regels[0], scheiding);
            var rijen = regels.Skip(1).Where(r => r.Length > 0).Select(r =>
            {
                var v = Splits(r, scheiding);
                return (IReadOnlyDictionary<string, string>)kop.Select((k, i) => (k, i)).GroupBy(x => x.k).ToDictionary(g => g.Key, g => g.First().i < v.Count ? v[g.First().i] : string.Empty);
            }).ToList();
            return new ScriptTabel(Path.GetFileName(pad), kop.Distinct().ToList(), rijen, pad);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static List<string> Splits(string regel, char scheiding)
    {
        var uit = new List<string>();
        var huidig = new System.Text.StringBuilder();
        var binnen = false;
        for (var i = 0; i < regel.Length; i++)
        {
            var c = regel[i];
            if (binnen)
            {
                if (c == '"' && i + 1 < regel.Length && regel[i + 1] == '"')
                {
                    huidig.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    binnen = false;
                }
                else
                {
                    huidig.Append(c);
                }
            }
            else if (c == '"')
            {
                binnen = true;
            }
            else if (c == scheiding)
            {
                uit.Add(huidig.ToString());
                huidig.Clear();
            }
            else
            {
                huidig.Append(c);
            }
        }

        uit.Add(huidig.ToString());
        return uit.Select(x => x.Trim('﻿')).ToList();
    }

    /// <summary>Het resultaat van een run uit de uitgangscode en de gebeurtenissen.</summary>
    public static (RunResultaat Resultaat, string Melding) Resultaat(int code, int fouten, int waarschuwingen, bool geannuleerd) => geannuleerd
        ? (RunResultaat.Geannuleerd, "Geannuleerd.")
        : code switch
        {
            0 when waarschuwingen > 0 => (RunResultaat.Waarschuwingen, $"Klaar, met {waarschuwingen} waarschuwing(en)."),
            0 => (RunResultaat.Gelukt, "Klaar."),
            2 => (RunResultaat.Fout, $"Klaar, maar met {fouten} fout(en)."),
            3 => (RunResultaat.Fout, "Geen voorbeeld mogelijk: het script ondersteunt -WhatIf niet. Er werd niets uitgevoerd."),
            4 => (RunResultaat.Fout, "Verbinden mislukt; het script is niet gestart."),
            _ => (RunResultaat.Fout, "Gestopt met een fout."),
        };
}
