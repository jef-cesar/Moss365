namespace Moss365.Core.Inventaris;

/// <summary>
/// Eén afspraak in de agenda van een room (Graph calendarView: terugkerende afspraken komen als losse items).
/// Tijden in de tijdzone van de pc. Het onderwerp is wat de room toont: vaak de naam van de organisator
/// (AddOrganizerToSubject), of leeg als de room het onderwerp weghaalt; bij een privéafspraak toont de app "Privé".
/// </summary>
public sealed record Afspraak(
    DateTime Begin,
    DateTime Einde,
    string Onderwerp,
    string Organisator,
    string OrganisatorAdres,
    bool HeleDag,
    bool Prive,
    string Status,
    bool Geannuleerd)
{
    public string OnderwerpTekst => Prive ? "Privé" : Onderwerp.Length > 0 ? Onderwerp : "(bezet)";

    public string StatusTekst => Status switch
    {
        "busy" => "bezet",
        "tentative" => "voorlopig",
        "free" => "vrij",
        "oof" => "afwezig",
        "workingElsewhere" => "elders",
        _ => Status,
    };

    public TimeSpan Duur => Einde - Begin;
}

/// <summary>Welke periode van de agenda je bekijkt.</summary>
public enum AgendaKeuze
{
    DezeWeek,
    VolgendeWeek,
    Gekozen,
}

public static class AgendaPeriode
{
    /// <summary>Hoogstens zoveel dagen in één keer (een room heeft al snel honderden afspraken per maand).</summary>
    public const int MaxDagen = 62;

    /// <summary>Maandag 0:00 van de week van deze dag.</summary>
    public static DateTime Maandag(DateTime dag) => dag.Date.AddDays(-(((int)dag.DayOfWeek + 6) % 7));

    /// <summary>Van (inbegrepen) tot (niet inbegrepen), of een fout als de gekozen periode niet kan.</summary>
    public static (DateTime Van, DateTime Tot, string? Fout) Bereken(AgendaKeuze keuze, DateTime nu, DateOnly? van = null, DateOnly? tot = null)
    {
        switch (keuze)
        {
            case AgendaKeuze.DezeWeek:
                return (Maandag(nu), Maandag(nu).AddDays(7), null);
            case AgendaKeuze.VolgendeWeek:
                return (Maandag(nu).AddDays(7), Maandag(nu).AddDays(14), null);
        }

        if (van is null || tot is null)
        {
            return (default, default, "Kies een begin- en een einddatum.");
        }

        var begin = van.Value.ToDateTime(TimeOnly.MinValue);
        var einde = tot.Value.ToDateTime(TimeOnly.MinValue).AddDays(1); // de einddatum telt mee
        if (einde <= begin)
        {
            return (begin, einde, "De einddatum ligt voor de begindatum.");
        }

        return (einde - begin).TotalDays > MaxDagen
            ? (begin, einde, $"Kies hoogstens {MaxDagen} dagen.")
            : (begin, einde, null);
    }
}
