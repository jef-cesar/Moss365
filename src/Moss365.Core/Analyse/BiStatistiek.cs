using System.Globalization;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Core.Analyse;

/// <summary>
/// Een deel van een verdeling (gestapelde staaf of lijst): naam, aantal en de KPI met precies die rijen.
/// Kleur is de rol van de kleur, niet de kleur zelf: "reeks", "schaal-1".."schaal-4", "goed", "kritiek" of "neutraal".
/// </summary>
public sealed record GrafiekDeel(string Naam, int Aantal, Kpi? Kpi, string Kleur = "reeks");

/// <summary>Eén kolom in een kolomgrafiek (bv. een maand), met een waarde per reeks.</summary>
public sealed record GrafiekKolom(string Label, string Tip, IReadOnlyList<int> Waarden, Kpi? Kpi = null)
{
    public int Totaal => Waarden.Sum();
}

/// <summary>Het verloop van één KPI: de bewaarde standen per dag.</summary>
public sealed record KpiVerloop(Kpi Kpi, IReadOnlyList<(DateOnly Datum, int Waarde)> Punten)
{
    /// <summary>Het verschil met de stand van ongeveer zoveel dagen geleden (de oudste stand die minstens zo oud is), of null.</summary>
    public int? Verschil(int dagen, int nu)
    {
        if (Punten.Count == 0)
        {
            return null;
        }

        var grens = Punten[^1].Datum.AddDays(-dagen);
        var oud = Punten.LastOrDefault(p => p.Datum <= grens);
        return oud == default ? null : nu - oud.Waarde;
    }
}

/// <summary>
/// De grafieken van Statistieken (BI): verdelingen en reeksen uit de geladen gegevens. Elk deel heeft zijn KPI, zodat
/// een klik de lijst met precies die rijen opent. Met een afdeling telt enkel die afdeling (de filter boven de grafieken).
/// </summary>
public static class BiStatistiek
{
    /// <summary>Waar de afdelingsfilter mee filtert: de KPI-sleutel met de afdeling erbij, of de KPI zelf.</summary>
    public static Kpi MetAfdeling(Kpi k, string? afdeling)
        => string.IsNullOrEmpty(afdeling) ? k : Kpis.Zoek($"{k.Sleutel}|afdeling-alle:{afdeling}")!;

    private static GrafiekDeel Deel(Kpi k, KpiBron b, string? afdeling, string kleur)
    {
        var kpi = MetAfdeling(k, afdeling);
        return new GrafiekDeel(k.Label, Kpis.Aantal(kpi, b), kpi, kleur);
    }

    /// <summary>Actieve medewerkers naar hun laatste aanmelding (een schaal van recent naar lang geleden).</summary>
    public static IReadOnlyList<GrafiekDeel> Aanmeldrecentheid(KpiBron b, string? afdeling = null) => new[]
    {
        Deel(Kpis.Aanmelding("0-30"), b, afdeling, "schaal-1"),
        Deel(Kpis.Aanmelding("30-90"), b, afdeling, "schaal-2"),
        Deel(Kpis.Aanmelding("90-180"), b, afdeling, "schaal-3"),
        Deel(Kpis.Aanmelding("180+"), b, afdeling, "schaal-4"),
        Deel(Kpis.Aanmelding("nooit"), b, afdeling, "neutraal"),
    };

    /// <summary>Actieve AD-accounts naar de leeftijd van hun wachtwoord.</summary>
    public static IReadOnlyList<GrafiekDeel> Wachtwoordleeftijd(KpiBron b, string? afdeling = null) => new[]
    {
        Deel(Kpis.Wachtwoord("0-90"), b, afdeling, "schaal-1"),
        Deel(Kpis.Wachtwoord("90-365"), b, afdeling, "schaal-2"),
        Deel(Kpis.Wachtwoord("365-730"), b, afdeling, "schaal-3"),
        Deel(Kpis.Wachtwoord("730+"), b, afdeling, "schaal-4"),
        Deel(Kpis.Wachtwoord("onbekend"), b, afdeling, "neutraal"),
    };

    /// <summary>Actieve medewerkers met en zonder MFA (onbekend als de registratie niet gelezen is).</summary>
    public static IReadOnlyList<GrafiekDeel> Mfa(KpiBron b, string? afdeling = null) => new[]
    {
        Deel(Kpis.MfaStatus("ja"), b, afdeling, "goed"),
        Deel(Kpis.MfaStatus("nee"), b, afdeling, "kritiek"),
        Deel(Kpis.MfaStatus("onbekend"), b, afdeling, "neutraal"),
    };

    /// <summary>Actieve medewerkers per kantoor: de grootste zeven, de rest samen.</summary>
    public static IReadOnlyList<GrafiekDeel> PerKantoor(KpiBron b, string? afdeling = null)
    {
        var namen = b.Actief.Select(p => Kpis.KantoorVan(p)).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var delen = namen.Select(n => Deel(Kpis.Kantoor(n), b, afdeling, "reeks")).Where(d => d.Aantal > 0).OrderByDescending(d => d.Aantal).ToList();
        return delen.Count <= 8 ? delen : delen.Take(7).Append(new GrafiekDeel("Overige", delen.Skip(7).Sum(d => d.Aantal), null)).ToList();
    }

    /// <summary>Gastgebruikers per domein van hun eigen organisatie: de grootste zeven, de rest samen.</summary>
    public static IReadOnlyList<GrafiekDeel> GastenPerDomein(KpiBron b)
    {
        var delen = b.Data.Personen.Where(p => p.IsGast).GroupBy(Kpis.GastDomein)
            .Select(g => new GrafiekDeel(g.Key, g.Count(), Kpis.GastDomeinKpi(g.Key)))
            .OrderByDescending(d => d.Aantal).ThenBy(d => d.Naam, StringComparer.Ordinal).ToList();
        return delen.Count <= 8 ? delen : delen.Take(7).Append(new GrafiekDeel("Overige", delen.Skip(7).Sum(d => d.Aantal), null)).ToList();
    }

    /// <summary>Nieuwe accounts (medewerkers) per maand, de laatste maanden tot en met de huidige.</summary>
    public static IReadOnlyList<GrafiekKolom> AangemaaktPerMaand(KpiBron b, string? afdeling = null, int maanden = 12)
    {
        var eerste = new DateTime(b.Nu.Year, b.Nu.Month, 1).AddMonths(-(maanden - 1));
        return Enumerable.Range(0, maanden).Select(i =>
        {
            var maand = eerste.AddMonths(i);
            var kpi = MetAfdeling(Kpis.Aangemaakt(maand.ToString("yyyy-MM", CultureInfo.InvariantCulture)), afdeling);
            var aantal = Kpis.Aantal(kpi, b);
            var naam = maand.ToString("MMM yyyy", CultureInfo.GetCultureInfo("nl-BE"));
            return new GrafiekKolom(maand.ToString("MMM", CultureInfo.GetCultureInfo("nl-BE")).TrimEnd('.'), $"{naam}: {aantal} nieuwe accounts", new[] { aantal }, kpi);
        }).ToList();
    }

    /// <summary>Uitgevoerde wijzigingen per week (gelukt en mislukt), de laatste weken tot en met deze week.</summary>
    public static IReadOnlyList<GrafiekKolom> LogboekPerWeek(IEnumerable<LogRegel> regels, DateTime nu, int weken = 12)
    {
        var maandag = AgendaPeriode.Maandag(nu).AddDays(-7 * (weken - 1));
        var lijst = regels.ToList();
        return Enumerable.Range(0, weken).Select(i =>
        {
            var van = maandag.AddDays(7 * i);
            var tot = van.AddDays(7);
            var inWeek = lijst.Where(r => r.Tijd.LocalDateTime >= van && r.Tijd.LocalDateTime < tot).ToList();
            var gelukt = inWeek.Count(r => r.Status == "Gelukt");
            var mislukt = inWeek.Count(r => r.Status == "Mislukt");
            var week = ISOWeek.GetWeekOfYear(van);
            return new GrafiekKolom($"w{week}", $"Week {week} (vanaf {van:dd/MM}): {gelukt} gelukt, {mislukt} mislukt", new[] { gelukt, mislukt });
        }).ToList();
    }

    /// <summary>De KPI's waarvan de app elke dag de stand bewaart (enkel aantallen) en het verloop toont.</summary>
    public static IReadOnlyList<Kpi> MetVerloop { get; } = new[]
    {
        Kpis.Actief, Kpis.Uitgeschakeld, Kpis.Gasten, Kpis.Inactief90, Kpis.NooitAangemeld, Kpis.ZonderMfa,
        Kpis.BeheerdersZonderMfa, Kpis.BevoorrechtAlle, Kpis.KunnenAanmelden, Kpis.MetRechtstreekseRechten, Kpis.LegeGroepen, Kpis.CaProblemen,
    };

    /// <summary>De stand van vandaag: elke vaste KPI behalve die van het logboek (dat heeft zijn eigen reeks).</summary>
    public static IReadOnlyDictionary<string, int> Stand(KpiBron b)
        => Kpis.Vast.Where(k => k.Lijst != KpiLijst.Logboek).ToDictionary(k => k.Sleutel, k => Kpis.Aantal(k, b), StringComparer.Ordinal);
}
