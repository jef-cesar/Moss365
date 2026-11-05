namespace Moss365.Core.Inventory;

/// <summary>
/// One appointment in the calendar of a room (Graph calendarView: recurring appointments come as separate items). Times
/// in the time zone of the PC. The subject is what the room shows: often the name of the organiser
/// (AddOrganizerToSubject), or empty when the room removes the subject; for a private appointment the app shows
/// "Privé".
/// </summary>
public sealed record Appointment(
    DateTime Begin,
    DateTime End,
    string Subject,
    string Organiser,
    string OrganiserAddress,
    bool WholeDay,
    bool Private,
    string Status,
    bool Cancelled)
{
    public string SubjectText => Private ? "Privé" : Subject.Length > 0 ? Subject : "(bezet)";

    public string StatusText => Status switch
    {
        "busy" => "bezet",
        "tentative" => "voorlopig",
        "free" => "vrij",
        "oof" => "afwezig",
        "workingElsewhere" => "elders",
        _ => Status,
    };

    public TimeSpan Duration => End - Begin;
}

/// <summary>Which period of the calendar you look at.</summary>
public enum CalendarChoice
{
    ThisWeek,
    NextWeek,
    Selected,
}

public static class CalendarPeriod
{
    /// <summary>At most this many days at a time (a room quickly has hundreds of appointments per month).</summary>
    public const int MaxDays = 62;

    /// <summary>Monday 0:00 of the week of this day.</summary>
    public static DateTime Monday(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>From (included) to (not included), or an error when the chosen period is not possible.</summary>
    public static (DateTime Of, DateTime Until, string? Error) Compute(CalendarChoice choice, DateTime now, DateOnly? start = null, DateOnly? until = null)
    {
        switch (choice)
        {
            case CalendarChoice.ThisWeek:
                return (Monday(now), Monday(now).AddDays(7), null);
            case CalendarChoice.NextWeek:
                return (Monday(now).AddDays(7), Monday(now).AddDays(14), null);
        }

        if (start is null || until is null)
        {
            return (default, default, "Kies een begin- en een einddatum.");
        }

        var begin = start.Value.ToDateTime(TimeOnly.MinValue);
        var end = until.Value.ToDateTime(TimeOnly.MinValue).AddDays(1); // the end date counts
        if (end <= begin)
        {
            return (begin, end, "De einddatum ligt voor de begindatum.");
        }

        return (end - begin).TotalDays > MaxDays
            ? (begin, end, $"Kies hoogstens {MaxDays} dagen.")
            : (begin, end, null);
    }
}
