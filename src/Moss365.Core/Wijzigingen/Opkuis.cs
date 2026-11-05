using System.Globalization;
using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

public enum OpkuisSoort
{
    /// <summary>Actieve medewerkers die lang niet aangemeld zijn: uitschakelen (nooit verwijderen).</summary>
    Medewerkers,

    /// <summary>Gasten die lang niet aangemeld zijn: verwijderen (30 dagen terug te zetten in Entra).</summary>
    Gasten,
}

/// <summary>Een account dat in aanmerking komt voor de opkuisronde.</summary>
public sealed record OpkuisKandidaat(Persoon Persoon, DateTime? LaatsteAanmelding, DateTime? Aangemaakt, bool Bevoorrecht)
{
    public string Reden => LaatsteAanmelding is { } l ? $"laatst aangemeld {l:dd/MM/yyyy}" : "nooit aangemeld (of onbekend)";
}

/// <summary>
/// Periodieke opkuis: wie lang niet aangemeld is, vink je aan en de app maakt één plan. Medewerkers worden uitgeschakeld
/// met de datum in de omschrijving (zoals bij uitstroom: nooit verwijderen), gasten worden verwijderd. Een rapport met
/// alle nagekeken accounts en de beslissing dient als bewijs van de controle.
/// </summary>
public static class OpkuisPlannen
{
    public static IReadOnlyList<OpkuisKandidaat> Kandidaten(InventarisData d, OrganisatieConfig config, OpkuisSoort soort, int dagen, DateTime nu)
    {
        var grens = nu.AddDays(-dagen);
        var uitstroomOu = config.Uitstroom?.Ou;
        return d.Personen
            .Where(p => soort == OpkuisSoort.Gasten ? p.IsGast : !p.IsGast && !d.Index.IsPostvakAccount(p))
            .Where(p => p.Ingeschakeld || soort == OpkuisSoort.Gasten)
            .Where(p => string.IsNullOrWhiteSpace(uitstroomOu) || p.Ad is null || !DnHulp.ValtOnder(p.Ad.OuPad, new[] { uitstroomOu }))
            .Select(p => new OpkuisKandidaat(p, Statistiek.LaatsteAanmelding(p), Aangemaakt(p),
                d.AdGroepenVan(p).Any(Spiegelgebruiker.IsBevoorrecht)))
            .Where(k => k.LaatsteAanmelding is { } l ? l < grens : k.Aangemaakt is { } a && a < grens)
            .OrderBy(k => k.LaatsteAanmelding ?? DateTime.MinValue)
            .ThenBy(k => k.Persoon.Naam, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static PlanUitkomst Plan(InventarisData d, OpkuisSoort soort, IEnumerable<Persoon> gekozen, DateOnly datum)
    {
        var lijst = gekozen.ToList();
        if (soort == OpkuisSoort.Gasten)
        {
            return GastPlannen.Verwijderen(d, lijst);
        }

        var plan = new List<Wijziging>();
        var fouten = new List<string>();
        foreach (var p in lijst)
        {
            if (p.Ad is { } ad)
            {
                var omschrijving = $"Uitgeschakeld bij opkuisronde {datum:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(ad.Description) ? string.Empty : " - " + ad.Description.Trim());
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.ActiveDirectory,
                    Actie = Actie.AdAccountUitschakelen,
                    Doel = ad.DistinguishedName,
                    DoelNaam = p.Naam,
                    Omschrijving = "AD-account uitschakelen (opkuisronde)",
                    HuidigeWaarde = ad.Enabled ? "ingeschakeld" : "al uitgeschakeld",
                    NieuweWaarde = "uitgeschakeld",
                });
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.ActiveDirectory,
                    Actie = Actie.AdKenmerkenZetten,
                    Doel = ad.DistinguishedName,
                    DoelNaam = p.Naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["description"] = omschrijving },
                    Vorige = new(StringComparer.OrdinalIgnoreCase) { ["description"] = ad.Description },
                    Omschrijving = "Reden in de omschrijving zetten",
                    HuidigeWaarde = ad.Description,
                    NieuweWaarde = omschrijving,
                });
            }
            else if (p.Entra is { OnPremisesSyncEnabled: false } e)
            {
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphKenmerkenZetten,
                    Doel = e.Id,
                    DoelNaam = p.Naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
                    Vorige = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = e.AccountEnabled ? "true" : "false" },
                    Omschrijving = "Cloudaccount uitschakelen (aanmelden blokkeren, opkuisronde)",
                    HuidigeWaarde = e.AccountEnabled ? "ingeschakeld" : "al uitgeschakeld",
                    NieuweWaarde = "uitgeschakeld",
                });
            }
            else
            {
                fouten.Add($"{p.Naam}: gesynchroniseerd account zonder AD-gegevens; laad AD en probeer opnieuw.");
            }
        }

        return PlanUitkomst.Van(plan, fouten);
    }

    /// <summary>
    /// Rapport van de ronde: elk nagekeken account met de beslissing en, na het uitvoeren, het resultaat. Bevat
    /// persoonsgegevens en blijft lokaal (exportmap).
    /// </summary>
    public static string BewaarRapport(string map, OpkuisSoort soort, int dagen, IReadOnlyList<OpkuisKandidaat> kandidaten,
        IReadOnlySet<string> gekozen, IReadOnlyList<Wijziging> plan, IReadOnlyList<WijzigingResultaat> resultaten, string door, DateTime tijd)
    {
        var status = resultaten.ToDictionary(r => r.WijzigingId);
        string Resultaat(Persoon p)
        {
            var eigen = plan.Where(w => w.Doel == p.Ad?.DistinguishedName || w.Doel == p.Entra?.Id)
                .Select(w => status.TryGetValue(w.Id, out var r) ? r : null)
                .ToList();
            if (eigen.Count == 0)
            {
                return gekozen.Contains(p.Sleutel) ? "niet in het plan" : string.Empty;
            }

            return eigen.All(r => r?.Status == ResultaatStatus.Gelukt) ? "gelukt"
                : eigen.All(r => r is null) ? "niet uitgevoerd"
                : "mislukt: " + string.Join("; ", eigen.Where(r => r is not null && r.Status != ResultaatStatus.Gelukt).Select(r => r!.Melding));
        }

        var beslissing = soort == OpkuisSoort.Gasten ? "verwijderen" : "uitschakelen";
        var pad = Path.Combine(map, $"Opkuisronde-{soort}-{tijd:yyyyMMdd-HHmm}.csv");
        CsvSchrijver.Schrijf(pad,
            new[] { "Naam", "UPN", "Bron", "Laatste aanmelding", "Aangemaakt", "Bevoorrecht", "Beslissing", "Resultaat", "Drempel (dagen)", "Nagekeken door", "Datum" },
            kandidaten.Select(k => new[]
            {
                k.Persoon.Naam, k.Persoon.Upn, k.Persoon.Bron,
                k.LaatsteAanmelding?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "nooit",
                k.Aangemaakt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                k.Bevoorrecht ? "ja" : string.Empty,
                gekozen.Contains(k.Persoon.Sleutel) ? beslissing : "behouden",
                Resultaat(k.Persoon),
                dagen.ToString(CultureInfo.InvariantCulture), door, tijd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }),
            voorExcel: true);
        return pad;
    }

    private static DateTime? Aangemaakt(Persoon p)
    {
        foreach (var t in new[] { p.Ad?.WhenCreated, p.Entra?.CreatedDateTime })
        {
            if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return d;
            }
        }

        return null;
    }
}
