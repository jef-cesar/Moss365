using System.Text.Encodings.Web;
using System.Text.Json;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

public enum GeplandSoort
{
    /// <summary>De uitstroom wordt op het moment zelf opnieuw berekend uit de dan geladen gegevens.</summary>
    Uitstroom,

    /// <summary>Een vast plan (bv. een nieuwe gebruiker); het startwachtwoord wordt pas bij het uitvoeren gemaakt.</summary>
    Instroom,

    /// <summary>Een script met vaste waarden; op het moment zelf start de beheerder de run (nooit vanzelf).</summary>
    Script,

    /// <summary>Einde van een tijdelijke uitzondering op een Conditional Access-beleid (sinds 0.9): de persoon gaat er weer onder vallen.</summary>
    CaUitzonderingEinde,
}

/// <summary>Hoe vaak een geplande actie terugkomt (enkel bij scripts).</summary>
public enum Herhaling
{
    Eenmalig,
    Dagelijks,
    Wekelijks,
    Maandelijks,
}

/// <summary>
/// Een wijziging die klaargezet is voor later. De app voert nooit iets uit zonder jou: op het geplande moment toont hij de
/// actie als "klaar om uit te voeren", je bekijkt het plan en bevestigt. Geen geheimen: een startwachtwoord wordt pas
/// bij het uitvoeren gemaakt.
/// </summary>
public sealed class GeplandeActie
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required GeplandSoort Soort { get; init; }
    public required DateTimeOffset Wanneer { get; set; }
    public required string Titel { get; init; }
    public DateTimeOffset Aangemaakt { get; init; } = DateTimeOffset.Now;
    public string AangemaaktDoor { get; init; } = string.Empty;
    public string? Notitie { get; set; }

    // Uitstroom
    public string? PersoonSleutel { get; init; }
    public UitstroomOpties? Opties { get; init; }

    // Instroom
    public List<Wijziging> Wijzigingen { get; init; } = new();
    public List<Wijziging> NaSynchronisatie { get; init; } = new();
    public string? Upn { get; init; }

    // Script: de bestandsnaam (zonder pad) en de waarden; relatieve waarden (@werkjaar.van, ...) worden bij de run ingevuld
    public string? ScriptSleutel { get; init; }
    public bool ScriptIngebouwd { get; init; }
    public Dictionary<string, string> ScriptWaarden { get; init; } = new();
    public Herhaling Herhaling { get; init; } = Herhaling.Eenmalig;

    // Einde van een uitzondering op een Conditional Access-beleid (de persoon staat in PersoonSleutel)
    public string? CaBeleidId { get; init; }

    public bool IsVervallen(DateTimeOffset nu) => Wanneer <= nu;

    /// <summary>
    /// Het volgende moment na een run (null bij eenmalig). Altijd in de toekomst: een gemiste week wordt niet ingehaald.
    /// Maandelijks op een dag die er niet is (31 in februari) wordt de laatste dag van die maand.
    /// </summary>
    public DateTimeOffset? VolgendeKeer(DateTimeOffset nu)
    {
        if (Herhaling == Herhaling.Eenmalig)
        {
            return null;
        }

        var dag = Wanneer.Day;
        var volgende = Wanneer;
        var maanden = 0;
        while (volgende <= nu)
        {
            switch (Herhaling)
            {
                case Herhaling.Dagelijks:
                    volgende = volgende.AddDays(1);
                    break;
                case Herhaling.Wekelijks:
                    volgende = volgende.AddDays(7);
                    break;
                default:
                    maanden++;
                    var basis = Wanneer.AddMonths(maanden);
                    var max = DateTime.DaysInMonth(basis.Year, basis.Month);
                    volgende = new DateTimeOffset(basis.Year, basis.Month, Math.Min(dag, max), Wanneer.Hour, Wanneer.Minute, 0, basis.Offset);
                    break;
            }
        }

        return volgende;
    }
}

/// <summary>Het plan van een geplande actie op het moment van uitvoeren, of waarom dat niet kan.</summary>
public sealed record GeplandPlan(IReadOnlyList<Wijziging> Plan, string? Wachtwoord, IReadOnlyList<string> Fouten);

public static class GeplandPlannen
{
    public static GeplandeActie Uitstroom(Persoon persoon, UitstroomOpties opties, DateTimeOffset wanneer, string door) => new()
    {
        Soort = GeplandSoort.Uitstroom,
        Wanneer = wanneer,
        Titel = $"Uitstroom {persoon.Naam}",
        AangemaaktDoor = door,
        PersoonSleutel = persoon.Sleutel,
        Opties = opties,
    };

    /// <summary>Een nieuwe gebruiker inplannen; het startwachtwoord van het voorbeeld wordt niet bewaard.</summary>
    public static GeplandeActie Instroom(NieuweGebruikerVoorstel voorstel, DateTimeOffset wanneer, string door) => new()
    {
        Soort = GeplandSoort.Instroom,
        Wanneer = wanneer,
        Titel = $"Nieuwe gebruiker {voorstel.WeergaveNaam}",
        AangemaaktDoor = door,
        Upn = voorstel.Upn,
        Wijzigingen = voorstel.Nu.Select(Kopie).ToList(),
        NaSynchronisatie = voorstel.NaSynchronisatie.Select(Kopie).ToList(),
    };

    public static GeplandeActie Script(string sleutel, bool ingebouwd, string naam, IReadOnlyDictionary<string, string> waarden, DateTimeOffset wanneer, Herhaling herhaling, string door) => new()
    {
        Soort = GeplandSoort.Script,
        Wanneer = wanneer,
        Titel = $"Script {naam}",
        AangemaaktDoor = door,
        ScriptSleutel = sleutel,
        ScriptIngebouwd = ingebouwd,
        ScriptWaarden = new Dictionary<string, string>(waarden, StringComparer.OrdinalIgnoreCase),
        Herhaling = herhaling,
    };

    /// <summary>Het einde van een tijdelijke uitzondering op een Conditional Access-beleid.</summary>
    public static GeplandeActie CaUitzonderingEinde(CaBeleid beleid, Persoon persoon, DateTimeOffset wanneer, string door) => new()
    {
        Soort = GeplandSoort.CaUitzonderingEinde,
        Wanneer = wanneer,
        Titel = $"Einde uitzondering op \"{beleid.Naam}\" voor {persoon.Naam}",
        AangemaaktDoor = door,
        PersoonSleutel = persoon.Sleutel,
        CaBeleidId = beleid.Id,
    };

    /// <summary>Het plan om nu uit te voeren: een uitstroom opnieuw berekend uit de huidige gegevens, een instroom met een nieuw startwachtwoord.</summary>
    public static GeplandPlan PlanVoor(GeplandeActie a, InventarisData data, Configuratie.OrganisatieConfig config)
    {
        if (a.Soort == GeplandSoort.CaUitzonderingEinde)
        {
            var beleid = data.ConditionalAccess?.Beleiden.FirstOrDefault(b => b.Id == a.CaBeleidId);
            var wie = a.PersoonSleutel is null ? null : data.ZoekPersoon(a.PersoonSleutel);
            if (beleid is null || wie is null)
            {
                return new GeplandPlan(Array.Empty<Wijziging>(), null,
                    new[] { "Het beleid of de persoon staat niet (meer) in de geladen gegevens. Laad Conditional Access live." });
            }

            var u = Analyse.CaPlannen.Uitzondering(beleid, wie, toevoegen: false, data, config);
            return new GeplandPlan(u.Plan, null, u.Fouten);
        }

        if (a.Soort == GeplandSoort.Uitstroom)
        {
            var persoon = a.PersoonSleutel is null ? null : data.ZoekPersoon(a.PersoonSleutel);
            if (persoon is null)
            {
                return new GeplandPlan(Array.Empty<Wijziging>(), null, new[] { "De gebruiker staat niet (meer) in de geladen gegevens. Laad live of kies opnieuw." });
            }

            return new GeplandPlan(PlanBouwer.Uitstroom(data, config, persoon, a.Opties ?? new UitstroomOpties()), null, Array.Empty<string>());
        }

        var fouten = new List<string>();
        if (a.Upn is { } upn && (data.AdGebruikers.Any(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase))
                                 || data.EntraGebruikers.Any(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase))))
        {
            fouten.Add($"{upn} bestaat al. Is deze instroom al uitgevoerd?");
        }

        var plan = a.Wijzigingen.Select(Kopie).ToList();
        string? wachtwoord = null;
        foreach (var w in plan.Where(w => w.Actie == Actie.AdGebruikerAanmaken))
        {
            wachtwoord ??= NaamRegels.Startwachtwoord();
            w.Geheim = wachtwoord;
        }

        return new GeplandPlan(plan, wachtwoord, fouten);
    }

    /// <summary>Een kopie met hetzelfde id (zodat AfhankelijkVan blijft kloppen), zonder geheim.</summary>
    private static Wijziging Kopie(Wijziging w) => new()
    {
        Id = w.Id,
        Systeem = w.Systeem,
        Actie = w.Actie,
        Doel = w.Doel,
        DoelNaam = w.DoelNaam,
        Parameters = new Dictionary<string, string>(w.Parameters, StringComparer.OrdinalIgnoreCase),
        Omschrijving = w.Omschrijving,
        HuidigeWaarde = w.HuidigeWaarde,
        NieuweWaarde = w.NieuweWaarde,
        Waarschuwing = w.Waarschuwing,
        Geselecteerd = w.Geselecteerd,
        AfhankelijkVan = w.AfhankelijkVan,
        Vorige = w.Vorige is null ? null : new Dictionary<string, string>(w.Vorige, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>Geplande acties in gepland.json in de gegevensmap.</summary>
public sealed class GeplandOpslag
{
    private static readonly JsonSerializerOptions Opties = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Opslag.ITekstOpslag _opslag;
    private readonly object _slot = new();

    public GeplandOpslag(string pad) : this(new Opslag.BestandTekst(pad))
    {
    }

    /// <summary>Sinds 0.8 in de databank (document "gepland").</summary>
    public GeplandOpslag(Opslag.ITekstOpslag opslag) => _opslag = opslag;

    public List<GeplandeActie> Lees()
    {
        lock (_slot)
        {
            return _opslag.Lees() is { } json ? JsonSerializer.Deserialize<List<GeplandeActie>>(json, Opties) ?? new List<GeplandeActie>() : new List<GeplandeActie>();
        }
    }

    public void Bewaar(IEnumerable<GeplandeActie> acties)
    {
        lock (_slot)
        {
            _opslag.Bewaar(JsonSerializer.Serialize(acties.OrderBy(a => a.Wanneer).ToList(), Opties));
        }
    }

    public void Voegtoe(GeplandeActie actie) => Bewaar(Lees().Append(actie));

    public void Verwijder(Guid id) => Bewaar(Lees().Where(a => a.Id != id));
}
