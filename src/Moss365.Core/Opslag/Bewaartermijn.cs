using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Core.Opslag;

/// <summary>
/// De bewaartermijn van het logboek (standaard 2 jaar): oudere regels eerst archiveren naar een CSV in de exportmap, en
/// pas daarna, na een bevestiging, uit de databank halen. Nooit stil verwijderen.
/// </summary>
public static class Bewaartermijn
{
    public static DateTimeOffset Grens(DateTimeOffset nu, int jaren) => nu.AddYears(-Math.Max(1, jaren));

    /// <summary>Schrijft de regels ouder dan de grens naar een CSV; geeft het pad en het aantal terug.</summary>
    public static (string Pad, int Aantal) Archiveer(Databank db, DateTimeOffset grens, string exportMap, DateTime nu)
    {
        var regels = db.LeesLog(int.MaxValue, grens).Reverse().ToList();
        var pad = Path.Combine(exportMap, $"Logboek-archief-tot-{grens:yyyy-MM-dd}-{nu:yyyyMMdd-HHmmss}.csv");
        CsvSchrijver.Schrijf(pad,
            new[] { "Tijd", "Gebruiker", "Context", "Systeem", "Actie", "Doel", "Naam", "Omschrijving", "Status", "Melding", "Parameters", "Vorige" },
            regels.Select(r => new[]
            {
                r.Tijd.ToString("yyyy-MM-dd HH:mm:ss"), r.Gebruiker, r.Context, r.Systeem, r.Actie, r.Doel, r.DoelNaam, r.Omschrijving, r.Status, r.Melding,
                string.Join("; ", r.Parameters.Select(p => $"{p.Key}={p.Value}")),
                r.Vorige is null ? string.Empty : string.Join("; ", r.Vorige.Select(p => $"{p.Key}={p.Value}")),
            }),
            voorExcel: true);
        return (pad, regels.Count);
    }
}
