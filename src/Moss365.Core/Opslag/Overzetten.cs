using Moss365.Core.Wijzigingen;

namespace Moss365.Core.Opslag;

/// <summary>
/// Zet de losse bestanden van vóór 0.8 eenmalig over naar de databank: het logboek (logboek-*.jsonl), de geplande
/// acties, de opvolging, de weergavekeuzes en de verbindingsinstellingen. De oude bestanden blijven als .bak staan.
/// Wat al in de databank staat, wordt niet overschreven; opnieuw draaien doet dus niets.
/// </summary>
public static class Overzetten
{
    /// <summary>Bestand in de gegevensmap -> sleutel van het document in de databank.</summary>
    public static IReadOnlyList<(string Bestand, string Document)> Documenten { get; } = new[]
    {
        ("gepland.json", "gepland"),
        ("opvolging.json", "opvolging"),
        ("weergave.json", "weergave"),
        ("verbinding.json", "verbinding"),
    };

    public static IReadOnlyList<string> VanBestanden(Databank db, string gegevensMap)
    {
        var meldingen = new List<string>();
        foreach (var (bestand, document) in Documenten)
        {
            var pad = Path.Combine(gegevensMap, bestand);
            if (!File.Exists(pad))
            {
                continue;
            }

            if (db.LeesDocument(document) is null)
            {
                db.BewaarDocument(document, File.ReadAllText(pad));
                meldingen.Add($"{bestand} overgezet.");
            }

            NaarBak(pad);
        }

        var logMap = Path.Combine(gegevensMap, "logboek");
        var logbestanden = Directory.Exists(logMap) ? Directory.GetFiles(logMap, "logboek-*.jsonl") : Array.Empty<string>();
        if (logbestanden.Length > 0)
        {
            // Oud naar nieuw, zodat de volgorde bewaard blijft
            var regels = new JsonlLogboek(logMap).Recent(int.MaxValue).Reverse().ToList();
            if (db.AantalLog() == 0)
            {
                db.SchrijfLog(regels);
                meldingen.Add($"Logboek overgezet ({regels.Count} regels).");
            }

            foreach (var b in logbestanden)
            {
                NaarBak(b);
            }
        }

        return meldingen;
    }

    private static void NaarBak(string pad)
    {
        var bak = pad + ".bak";
        if (File.Exists(bak))
        {
            bak = $"{pad}.{DateTime.Now:yyyyMMddHHmmss}.bak";
        }

        File.Move(pad, bak);
    }
}
