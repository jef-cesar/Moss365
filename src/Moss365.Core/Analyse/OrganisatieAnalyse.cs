using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

/// <summary>Een waarde van Department zoals ze in de data voorkomt, met het aantal gebruikers.</summary>
public sealed record DienstGebruik(string Waarde, int Aantal, bool InConfig, bool IsSchrijfvariant);

/// <summary>Vergelijkt de keuzelijsten van de configuratie met wat er werkelijk in AD (of anders Entra) staat.</summary>
public static class OrganisatieAnalyse
{
    /// <summary>Alle Department-waarden van ingeschakelde AD-gebruikers (of Entra-leden als er geen AD is), met hun aantal.</summary>
    public static IReadOnlyList<DienstGebruik> DienstenInData(InventarisData data, OrganisatieConfig config)
    {
        var waarden = data.HeeftAd
            ? data.AdGebruikers.Where(g => g.Enabled).Select(g => g.Department)
            : data.EntraGebruikers.Where(g => g.AccountEnabled && !g.IsGast).Select(g => g.Department);

        return waarden
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .GroupBy(w => w, StringComparer.Ordinal)
            .Select(g =>
            {
                var dienst = config.ZoekDienst(g.Key);
                var variant = dienst is not null && !string.Equals(dienst.Department, g.Key, StringComparison.Ordinal);
                return new DienstGebruik(g.Key, g.Count(), dienst is not null, variant);
            })
            .OrderByDescending(d => d.Aantal)
            .ThenBy(d => d.Waarde, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Hoeveel ingeschakelde gebruikers per dienst uit de configuratie (spaties en hoofdletters tellen niet mee).</summary>
    public static IReadOnlyDictionary<string, int> AantalPerDienst(InventarisData data, OrganisatieConfig config)
    {
        var uit = config.Diensten.GroupBy(d => d.Department, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var gebruik in DienstenInData(data, config))
        {
            var dienst = config.ZoekDienst(gebruik.Waarde);
            if (dienst is not null)
            {
                uit[dienst.Department] += gebruik.Aantal;
            }
        }

        return uit;
    }

    /// <summary>Alle Office-waarden van ingeschakelde AD-gebruikers (of Entra-leden), met hun aantal en of ze in de lijst staan.</summary>
    public static IReadOnlyList<DienstGebruik> KantorenInData(InventarisData data, OrganisatieConfig config)
    {
        var waarden = data.HeeftAd
            ? data.AdGebruikers.Where(g => g.Enabled).Select(g => g.Office)
            : data.EntraGebruikers.Where(g => g.AccountEnabled && !g.IsGast).Select(g => g.OfficeLocation);
        var bekend = config.Kantoren.GroupBy(OrganisatieConfig.Normaliseer).ToDictionary(g => g.Key, g => g.First());

        return waarden
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .GroupBy(w => w, StringComparer.Ordinal)
            .Select(g =>
            {
                var inLijst = bekend.TryGetValue(OrganisatieConfig.Normaliseer(g.Key), out var lijstWaarde);
                return new DienstGebruik(g.Key, g.Count(), inLijst, inLijst && !string.Equals(lijstWaarde, g.Key, StringComparison.Ordinal));
            })
            .OrderByDescending(d => d.Aantal)
            .ThenBy(d => d.Waarde, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
