namespace Moss365.UI.Services;

public enum StapStatus
{
    Wacht,
    Bezig,
    Klaar,
    Fout,
    Geannuleerd,
}

/// <summary>Eén stap van een achtergrondtaak, bv. "AD lezen".</summary>
public sealed class TaakStap
{
    public required string Naam { get; init; }
    public StapStatus Status { get; internal set; }

    /// <summary>Laatste voortgangsmelding of de fout.</summary>
    public string? Melding { get; internal set; }

    public DateTime? Start { get; internal set; }
    public DateTime? Einde { get; internal set; }

    public TimeSpan? Duur => Start is { } s ? (Einde ?? DateTime.Now) - s : null;
}

/// <summary>
/// Een taak die op de achtergrond loopt (live laden, objecten vernieuwen). Hij leeft in <see cref="BeheerState"/>, dus
/// de voortgang blijft bewaard als je van scherm verandert.
/// </summary>
public sealed class AchtergrondTaak
{
    private readonly List<string> _log = new();

    public AchtergrondTaak(string naam, IEnumerable<string> stappen)
    {
        Naam = naam;
        Stappen = stappen.Select(s => new TaakStap { Naam = s }).ToList();
    }

    public string Naam { get; }
    public IReadOnlyList<TaakStap> Stappen { get; }
    public DateTime Start { get; } = DateTime.Now;
    public DateTime? Einde { get; internal set; }
    public bool Bezig => Einde is null;
    public bool Geannuleerd { get; internal set; }
    public bool MetFouten => Stappen.Any(s => s.Status == StapStatus.Fout);

    internal CancellationTokenSource Annuleren { get; } = new();

    public TaakStap? Huidig => Stappen.FirstOrDefault(s => s.Status == StapStatus.Bezig);

    /// <summary>0..1: afgewerkte stappen, een lopende stap telt half.</summary>
    public double Voortgang => Stappen.Count == 0 ? 1
        : Stappen.Sum(s => s.Status is StapStatus.Klaar or StapStatus.Fout or StapStatus.Geannuleerd ? 1.0 : s.Status == StapStatus.Bezig ? 0.5 : 0) / Stappen.Count;

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToList();
            }
        }
    }

    internal void Schrijf(string tekst)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {tekst}");
        }
    }

    /// <summary>Korte tekst voor de status onderaan het menu.</summary>
    public string Samenvatting => Bezig
        ? Stappen.Count(s => s.Status == StapStatus.Bezig) > 1
            ? $"{Stappen.Count(s => s.Status == StapStatus.Bezig)} bezig: {string.Join(", ", Stappen.Where(s => s.Status == StapStatus.Bezig).Select(s => s.Naam))}"
            : $"{Huidig?.Naam ?? Naam}: {Huidig?.Melding ?? "bezig..."}"
        : Geannuleerd ? $"{Naam}: geannuleerd"
        : MetFouten ? $"{Naam}: mislukt om {Einde:HH:mm}"
        : $"{Naam}: klaar om {Einde:HH:mm}";
}
