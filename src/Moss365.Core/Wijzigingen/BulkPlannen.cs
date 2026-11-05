using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Taken voor een selectie uit een lijst (0.8, #10): telkens één plan met voorbeeld en bevestiging. Wat voor één object
/// niet kan (al lid, geen toegangsgroep, dynamische of bevoorrechte groep), komt bij de fouten; de rest gaat door.
/// </summary>
public static class BulkPlannen
{
    /// <summary>Vanaf zoveel wijzigingen vraagt het plan een extra bevestiging met het aantal.</summary>
    public const int GrootPlan = 50;

    /// <summary>De gekozen gebruikers lid maken van een groep (of eruit halen).</summary>
    public static PlanUitkomst GebruikersInGroep(InventarisData data, IEnumerable<Persoon> personen, Groep groep, bool toevoegen)
        => Samen(personen.Select(p =>
        {
            var r = data.Index.Ref(p);
            var lid = data.Index.UniekeLedenVan(groep).Any(l => l.Object.Soort == r.Soort && string.Equals(l.Object.Sleutel, r.Sleutel, StringComparison.OrdinalIgnoreCase));
            if (toevoegen == lid)
            {
                return PlanUitkomst.Fout(lid ? $"{p.Naam} is al lid van {groep.Naam}." : $"{p.Naam} is geen lid van {groep.Naam}.");
            }

            return toevoegen ? GroepPlannen.LidToevoegen(data, groep, r) : GroepPlannen.LidVerwijderen(data, groep, r);
        }));

    /// <summary>Eén kenmerk (bv. kantoor of dienst) gelijk zetten voor de gekozen gebruikers.</summary>
    public static PlanUitkomst Kenmerk(InventarisData data, OrganisatieConfig config, IEnumerable<Persoon> personen, string veld, string waarde)
    {
        var eigenschap = typeof(GebruikerGewenst).GetProperty(veld) ?? throw new ArgumentException($"Onbekend veld {veld}.", nameof(veld));
        return Samen(personen.Select(p =>
        {
            var g = new GebruikerGewenst();
            eigenschap.SetValue(g, waarde);
            var u = KenmerkPlannen.Gebruiker(data, config, p, g);
            return u.Plan.Count == 0 ? PlanUitkomst.Fout($"{p.Naam}: {string.Join(" ", u.Fouten)}") : u;
        }));
    }

    /// <summary>Eén gebruiker lid maken van alle gekozen groepen.</summary>
    public static PlanUitkomst GebruikerInGroepen(InventarisData data, IEnumerable<Groep> groepen, Persoon persoon)
        => Samen(groepen.Select(g => GebruikersInGroep(data, new[] { persoon }, g, toevoegen: true)));

    /// <summary>Iemand toegang geven tot alle gekozen gedeelde postvakken, via hun toegangsgroep SG-SM-...-Full.</summary>
    public static PlanUitkomst ToegangTotPostvakken(InventarisData data, IEnumerable<Postvak> postvakken, Persoon persoon)
        => Samen(postvakken.Select(p => data.Index.ToegangsgroepVan(p) is { } g
            ? GebruikersInGroep(data, new[] { persoon }, g, toevoegen: true)
            : PlanUitkomst.Fout($"{p.DisplayName} heeft nog geen toegangsgroep; maak die eerst aan op de pagina van het postvak.")));

    /// <summary>De gekozen postvakken verbergen in de adreslijst (of weer tonen).</summary>
    public static PlanUitkomst Verbergen(IEnumerable<Postvak> postvakken, bool verbergen)
        => Samen(postvakken.Select(p => p.Verborgen == verbergen
            ? PlanUitkomst.Fout($"{p.DisplayName} is al {(verbergen ? "verborgen" : "zichtbaar")}.")
            : PostvakPlannen.Instellingen(p, new PostvakGewenst { Verborgen = verbergen })));

    /// <summary>Aanmelden met het account van de gekozen postvakken blokkeren (een postvak heeft geen aanmelding nodig).</summary>
    public static PlanUitkomst AanmeldenBlokkeren(InventarisData data, IEnumerable<Postvak> postvakken)
        => Samen(postvakken.Select(p => data.Index.AccountVan(p.Smtp, p.Alias) is { Ingeschakeld: true }
            ? PostvakPlannen.Aanmelden(data, p, toestaan: false)
            : PlanUitkomst.Fout($"{p.DisplayName}: aanmelden is al geblokkeerd of het account is niet gekend.")));

    /// <summary>Eén persoon beheerder (AD, Exchange) of eigenaar (cloud) maken van alle gekozen groepen.</summary>
    public static PlanUitkomst BeheerderVoorGroepen(InventarisData data, IEnumerable<Groep> groepen, Persoon persoon)
        => Samen(groepen.Select(g => GroepPlannen.Beheerder(data, g, data.Index.Ref(persoon), toevoegen: true)));

    /// <summary>
    /// De uitstroom inplannen voor de gekozen gebruikers: één geplande actie per persoon, met de standaardopties en de
    /// gekozen datum. Er gebeurt niets vanzelf: op het moment zelf staat elk plan klaar bij Gepland, met bevestiging.
    /// </summary>
    public static IReadOnlyList<GeplandeActie> UitstroomInplannen(IEnumerable<Persoon> personen, DateTimeOffset wanneer, string door)
        => personen.Where(p => !p.IsGast)
            .Select(p => GeplandPlannen.Uitstroom(p, new UitstroomOpties { Datum = DateOnly.FromDateTime(wanneer.LocalDateTime) }, wanneer, door))
            .ToList();

    private static PlanUitkomst Samen(IEnumerable<PlanUitkomst> uitkomsten)
    {
        var lijst = uitkomsten.ToList();
        return new PlanUitkomst(lijst.SelectMany(u => u.Plan).ToList(), lijst.SelectMany(u => u.Fouten).ToList());
    }
}
