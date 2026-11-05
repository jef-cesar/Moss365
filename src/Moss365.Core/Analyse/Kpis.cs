using System.Globalization;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Core.Analyse;

/// <summary>Op welke lijst de rijen achter een KPI staan.</summary>
public enum KpiLijst
{
    Gebruikers,
    Postvakken,
    Groepen,
    Rooms,
    Logboek,
    CaBeleiden,
    CaControle,
}

/// <summary>
/// Een KPI: een getal op een tegel of staaf, en de lijst met precies de rijen achter dat getal. Het getal is altijd het
/// aantal rijen van die lijst (<see cref="Kpis.Aantal"/>), zodat tegel en lijst nooit verschillen. Met een parameter
/// (bv. <c>afdeling:Vrije tijd</c>) is het één KPI per waarde.
/// </summary>
public sealed record Kpi(string Sleutel, string Label, KpiLijst Lijst, bool Aandacht = false)
{
    public string Pagina => Lijst switch
    {
        KpiLijst.Gebruikers => "gebruikers",
        KpiLijst.Postvakken => "postvakken",
        KpiLijst.Groepen => "groepen",
        KpiLijst.Rooms => "rooms",
        KpiLijst.Logboek => "logboek",
        _ => "conditional-access",
    };

    /// <summary>De pagina met de filter al toegepast.</summary>
    public string Link => $"{Pagina}?kpi={Uri.EscapeDataString(Sleutel)}";

    /// <summary>De waarde na de dubbele punt (bij een KPI met parameter), anders leeg.</summary>
    public string Waarde => Sleutel.IndexOf(':') is var i and >= 0 ? Sleutel[(i + 1)..] : string.Empty;
}

/// <summary>
/// De gegevens waaruit de KPI's rekenen, met wat duur is (de controle van Conditional Access) één keer berekend.
/// Maak een nieuwe bron als de gegevens veranderen.
/// </summary>
public sealed class KpiBron
{
    private IReadOnlyList<Persoon>? _medewerkers;
    private IReadOnlyList<Persoon>? _actief;
    private IReadOnlyList<CaBevinding>? _ca;

    public KpiBron(InventarisData data, OrganisatieConfig config, DateTime nu)
    {
        Data = data;
        Config = config;
        Nu = nu;
    }

    public InventarisData Data { get; }
    public OrganisatieConfig Config { get; }
    public DateTime Nu { get; }

    /// <summary>Medewerkers: geen gasten, geen accounts achter gedeelde postvakken en rooms.</summary>
    public IReadOnlyList<Persoon> Medewerkers => _medewerkers ??= Data.Personen.Where(p => !p.IsGast && !Data.Index.IsPostvakAccount(p)).ToList();

    public IReadOnlyList<Persoon> Actief => _actief ??= Medewerkers.Where(p => p.Ingeschakeld).ToList();

    public IReadOnlyList<CaBevinding> CaBevindingen => _ca ??= Data.ConditionalAccess is { Melding: null } ? CaControle.Controleer(Data, Config, Nu) : Array.Empty<CaBevinding>();
}

/// <summary>Alle KPI's van de app op één plek: het label, de lijst waar ze naartoe linken en welke rijen erbij horen.</summary>
public static class Kpis
{
    // Accounts
    public static readonly Kpi Actief = new("actief", "actieve medewerkers", KpiLijst.Gebruikers);
    public static readonly Kpi Uitgeschakeld = new("uitgeschakeld", "uitgeschakeld", KpiLijst.Gebruikers);
    public static readonly Kpi Gasten = new("gasten", "gastgebruikers", KpiLijst.Gebruikers);
    public static readonly Kpi AdEnEntra = new("ad-en-entra", "AD + Entra", KpiLijst.Gebruikers);
    public static readonly Kpi AlleenAd = new("alleen-ad", "alleen AD", KpiLijst.Gebruikers);
    public static readonly Kpi AlleenEntra = new("alleen-entra", "alleen Entra", KpiLijst.Gebruikers);
    public static readonly Kpi PostvakAccounts = new("postvakaccounts", "postvak- en roomaccounts (niet meegeteld)", KpiLijst.Gebruikers);

    // Aandachtspunten (actieve medewerkers)
    public static readonly Kpi Inactief90 = new("inactief-90", "niet aangemeld in 90 dagen", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi Inactief180 = new("inactief-180", "niet aangemeld in 180 dagen", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi NooitAangemeld = new("nooit-aangemeld", "nooit aangemeld (of onbekend)", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi WachtwoordVerlooptNooit = new("wachtwoord-verloopt-nooit", "wachtwoord verloopt nooit", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi Bevoorrecht = new("bevoorrecht", "in een bevoorrechte AD-groep", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi BevoorrechtAlle = new("bevoorrecht-alle", "in een bevoorrechte AD-groep (ook uitgeschakeld)", KpiLijst.Gebruikers);

    // Beveiliging
    public static readonly Kpi ZonderMfa = new("zonder-mfa", "actieve medewerkers zonder MFA", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi BeheerdersZonderMfa = new("beheerders-zonder-mfa", "beheerders zonder MFA", KpiLijst.Gebruikers, Aandacht: true);
    public static readonly Kpi MetBeheerrol = new("beheerrol", "accounts met een Entra-beheerrol", KpiLijst.Gebruikers);
    public static readonly Kpi GlobaleBeheerders = new("global-admin", "Global Administrators", KpiLijst.Gebruikers);
    public static readonly Kpi BuitenMfaBeleid = new("buiten-mfa-beleid", "medewerkers buiten elk MFA-beleid", KpiLijst.Gebruikers, Aandacht: true);

    // Gedeelde postvakken
    public static readonly Kpi KunnenAanmelden = new("kunnen-aanmelden", "gedeelde postvakken waarmee je kan aanmelden", KpiLijst.Postvakken, Aandacht: true);
    public static readonly Kpi MetRechtstreekseRechten = new("rechtstreeks", "met rechtstreekse rechten op personen", KpiLijst.Postvakken, Aandacht: true);
    public static readonly Kpi ZonderToegangsgroep = new("zonder-toegangsgroep", "zonder toegangsgroep", KpiLijst.Postvakken, Aandacht: true);

    // Groepen en rooms
    public static readonly Kpi DynamischeGroepen = new("dynamisch", "dynamisch", KpiLijst.Groepen);
    public static readonly Kpi LegeGroepen = new("zonder-leden", "zonder leden", KpiLijst.Groepen, Aandacht: true);
    public static readonly Kpi RoomsMetAfwijking = new("afwijkend", "wijken af van de standaard", KpiLijst.Rooms, Aandacht: true);

    // Logboek (7 dagen)
    public static readonly Kpi Gelukt7 = new("gelukt-7", "wijzigingen gelukt (7 dagen)", KpiLijst.Logboek);
    public static readonly Kpi Mislukt7 = new("mislukt-7", "mislukt (7 dagen)", KpiLijst.Logboek, Aandacht: true);

    // Conditional Access
    public static readonly Kpi CaAan = new("ca-aan", "beleiden aan", KpiLijst.CaBeleiden);
    public static readonly Kpi CaRapport = new("ca-rapport", "alleen rapport", KpiLijst.CaBeleiden);
    public static readonly Kpi CaUit = new("ca-uit", "uit", KpiLijst.CaBeleiden);
    public static readonly Kpi CaProblemen = new("ca-problemen", "problemen in de controle", KpiLijst.CaControle, Aandacht: true);
    public static readonly Kpi CaAandacht = new("ca-aandacht", "punten om na te kijken", KpiLijst.CaControle, Aandacht: true);

    /// <summary>De vaste KPI's (zonder parameter).</summary>
    public static IReadOnlyList<Kpi> Vast { get; } = typeof(Kpis).GetFields()
        .Where(f => f.IsStatic && f.FieldType == typeof(Kpi))
        .Select(f => (Kpi)f.GetValue(null)!)
        .ToList();

    // KPI's met een parameter: één per waarde
    public static Kpi Bron(string bron) => new($"bron:{bron}", bron, KpiLijst.Gebruikers);
    public static Kpi Afdeling(string afdeling) => new($"afdeling:{afdeling}", afdeling, KpiLijst.Gebruikers);
    public static Kpi Licentie(string sku) => new($"licentie:{sku}", sku, KpiLijst.Gebruikers);
    /// <summary>Per MFA-methode, op de leesbare naam (<see cref="BeveiligingStatistiek.MethodeNaam"/>).</summary>
    public static Kpi Methode(string naam) => new($"methode:{naam}", naam, KpiLijst.Gebruikers);
    public static Kpi Rechtenmodel(string model) => new($"rechtenmodel:{model}", model, KpiLijst.Postvakken);

    // Voor de grafieken van Statistieken (BiStatistiek)
    public static Kpi Aanmelding(string schijf) => new($"aanmelding:{schijf}", schijf switch
    {
        "0-30" => "aangemeld in de laatste 30 dagen",
        "30-90" => "laatst aangemeld 30 tot 90 dagen geleden",
        "90-180" => "laatst aangemeld 90 tot 180 dagen geleden",
        "180+" => "laatst aangemeld meer dan 180 dagen geleden",
        _ => "nooit aangemeld (of onbekend)",
    }, KpiLijst.Gebruikers);

    public static Kpi Wachtwoord(string schijf) => new($"wachtwoord:{schijf}", schijf switch
    {
        "0-90" => "wachtwoord jonger dan 90 dagen",
        "90-365" => "wachtwoord 90 dagen tot 1 jaar oud",
        "365-730" => "wachtwoord 1 tot 2 jaar oud",
        "730+" => "wachtwoord ouder dan 2 jaar",
        _ => "leeftijd van het wachtwoord onbekend",
    }, KpiLijst.Gebruikers);

    public static Kpi MfaStatus(string status) => new($"mfa:{status}", status switch
    {
        "ja" => "met MFA",
        "nee" => "zonder MFA",
        _ => "MFA onbekend",
    }, KpiLijst.Gebruikers);

    public static Kpi Kantoor(string kantoor) => new($"kantoor:{kantoor}", kantoor, KpiLijst.Gebruikers);
    public static Kpi GastDomeinKpi(string domein) => new($"gastdomein:{domein}", $"gasten van {domein}", KpiLijst.Gebruikers);
    public static Kpi Aangemaakt(string maand) => new($"aangemaakt:{maand}", $"aangemaakt in {maand}", KpiLijst.Gebruikers);

    /// <summary>Alle accounts (ook uitgeschakeld) van een afdeling: de filter boven de grafieken.</summary>
    public static Kpi AfdelingAlle(string afdeling) => new($"afdeling-alle:{afdeling}", $"afdeling {afdeling}", KpiLijst.Gebruikers);

    public const string GeenKantoor = "(geen kantoor)";

    public static string KantoorVan(Persoon p)
        => p.Ad?.Office is { Length: > 0 } o ? o.Trim() : p.Entra?.OfficeLocation is { Length: > 0 } e ? e.Trim() : GeenKantoor;

    /// <summary>Het domein van de eigen organisatie van een gast (uit het e-mailadres of de UPN met #EXT#).</summary>
    public static string GastDomein(Persoon p)
    {
        if (p.Entra?.Mail is { } mail && mail.IndexOf('@') is var at and > 0 && !mail.Contains("#EXT#", StringComparison.OrdinalIgnoreCase))
        {
            return mail[(at + 1)..].ToLowerInvariant();
        }

        var upn = p.Upn;
        var ext = upn.IndexOf("#EXT#", StringComparison.OrdinalIgnoreCase);
        if (ext > 0 && upn.LastIndexOf('_', ext) is var streep and > 0)
        {
            return upn[(streep + 1)..ext].ToLowerInvariant();
        }

        return "(onbekend)";
    }

    private static DateTime? Datum(string? tekst)
        => DateTime.TryParse(tekst, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    public static Kpi Groepsoort(string soort) => new($"groepsoort:{soort}", soort, KpiLijst.Groepen);

    /// <summary>De KPI bij een sleutel uit de adresregel, of null als die niet bestaat.</summary>
    public static Kpi? Zoek(string? sleutel)
    {
        if (string.IsNullOrWhiteSpace(sleutel))
        {
            return null;
        }

        if (Vast.FirstOrDefault(k => k.Sleutel == sleutel) is { } vast)
        {
            return vast;
        }

        // Samengesteld (bv. een staaf met de afdelingsfilter): de rijen die in elk deel zitten
        if (sleutel.Contains('|', StringComparison.Ordinal))
        {
            var delen = sleutel.Split('|').Select(d => d.Contains('|', StringComparison.Ordinal) ? null : Zoek(d)).ToList();
            return delen.All(d => d is { Lijst: KpiLijst.Gebruikers }) && delen.Count > 1
                ? new Kpi(sleutel, string.Join(" · ", delen.Select(d => d!.Label)), KpiLijst.Gebruikers, delen[0]!.Aandacht)
                : null;
        }

        var i = sleutel.IndexOf(':');
        if (i <= 0 || i == sleutel.Length - 1)
        {
            return null;
        }

        var waarde = sleutel[(i + 1)..];
        return sleutel[..i] switch
        {
            "bron" => Bron(waarde),
            "afdeling" => Afdeling(waarde),
            "licentie" => Licentie(waarde),
            "methode" => Methode(waarde),
            "rechtenmodel" => Rechtenmodel(waarde),
            "groepsoort" => Groepsoort(waarde),
            "aanmelding" when waarde is "0-30" or "30-90" or "90-180" or "180+" or "nooit" => Aanmelding(waarde),
            "wachtwoord" when waarde is "0-90" or "90-365" or "365-730" or "730+" or "onbekend" => Wachtwoord(waarde),
            "mfa" when waarde is "ja" or "nee" or "onbekend" => MfaStatus(waarde),
            "kantoor" => Kantoor(waarde),
            "gastdomein" => GastDomeinKpi(waarde),
            "aangemaakt" when DateTime.TryParseExact(waarde, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => Aangemaakt(waarde),
            "afdeling-alle" => AfdelingAlle(waarde),
            _ => null,
        };
    }

    /// <summary>Het getal op de tegel: het aantal rijen in de lijst erachter.</summary>
    public static int Aantal(Kpi k, KpiBron b, IEnumerable<LogRegel>? logboek = null) => k.Lijst switch
    {
        KpiLijst.Gebruikers => Personen(k, b).Count,
        KpiLijst.Postvakken => Postvakken(k, b).Count,
        KpiLijst.Groepen => Groepen(k, b).Count,
        KpiLijst.Rooms => Rooms(k, b).Count,
        KpiLijst.Logboek => Logregels(k, logboek ?? Array.Empty<LogRegel>(), new DateTimeOffset(b.Nu)).Count,
        KpiLijst.CaBeleiden => Beleiden(k, b).Count,
        _ => Bevindingen(k, b).Count,
    };

    // ---------------------------------------------------------------- personen

    public static IReadOnlyList<Persoon> Personen(Kpi k, KpiBron b)
    {
        if (k.Sleutel.Contains('|', StringComparison.Ordinal))
        {
            var delen = k.Sleutel.Split('|').Select(Zoek).ToList();
            if (delen.Any(x => x is null))
            {
                return Array.Empty<Persoon>();
            }

            var rest = delen.Skip(1).Select(x => Personen(x!, b).ToHashSet()).ToList();
            return Personen(delen[0]!, b).Where(p => rest.All(r => r.Contains(p))).ToList();
        }

        var d = b.Data;
        var nu = b.Nu;
        var beveiliging = d.Beveiliging;
        IEnumerable<Persoon> rijen = k.Sleutel switch
        {
            "actief" => b.Actief,
            "uitgeschakeld" => b.Medewerkers.Where(p => !p.Ingeschakeld),
            "gasten" => d.Personen.Where(p => p.IsGast),
            "ad-en-entra" => b.Medewerkers.Where(p => p.Ad is not null && p.Entra is not null),
            "alleen-ad" => b.Medewerkers.Where(p => p.Ad is not null && p.Entra is null),
            "alleen-entra" => b.Medewerkers.Where(p => p.Ad is null && p.Entra is not null),
            "postvakaccounts" => d.Personen.Where(p => !p.IsGast && d.Index.IsPostvakAccount(p)),
            "inactief-90" => b.Actief.Where(p => Statistiek.LaatsteAanmelding(p) is { } l && l < nu.AddDays(-90)),
            "inactief-180" => b.Actief.Where(p => Statistiek.LaatsteAanmelding(p) is { } l && l < nu.AddDays(-180)),
            "nooit-aangemeld" => b.Actief.Where(p => Statistiek.LaatsteAanmelding(p) is null),
            "wachtwoord-verloopt-nooit" => b.Actief.Where(p => p.Ad?.PasswordNeverExpires == true),
            "bevoorrecht" => b.Actief.Where(p => d.AdGroepenVan(p).Any(Spiegelgebruiker.IsBevoorrecht)),
            "bevoorrecht-alle" => d.Personen.Where(p => p.Ad is not null && d.AdGroepenVan(p).Any(Spiegelgebruiker.IsBevoorrecht)),
            "zonder-mfa" => beveiliging is null ? Enumerable.Empty<Persoon>() : b.Actief.Where(p => beveiliging.MfaVan(p.Entra?.Id) is { MfaGeregistreerd: false }),
            "beheerders-zonder-mfa" => MetRol(d, r => BeveiligingGegevens.IsKritiekeRol(r.Rol) && beveiliging!.MfaVan(r.LidId) is { MfaGeregistreerd: false }),
            "beheerrol" => MetRol(d, _ => true),
            "global-admin" => MetRol(d, r => r.Rol == "Global Administrator"),
            "buiten-mfa-beleid" => b.CaBevindingen.FirstOrDefault(x => x.Onderwerp == "Medewerkers zonder MFA-beleid")?.Personen ?? Enumerable.Empty<Persoon>(),
            _ when k.Sleutel.StartsWith("bron:", StringComparison.Ordinal) => d.Personen.Where(p => p.Bron == k.Waarde),
            _ when k.Sleutel.StartsWith("aanmelding:", StringComparison.Ordinal) => b.Actief.Where(p => Statistiek.LaatsteAanmelding(p) is var l && k.Waarde switch
            {
                "0-30" => l >= nu.AddDays(-30),
                "30-90" => l < nu.AddDays(-30) && l >= nu.AddDays(-90),
                "90-180" => l < nu.AddDays(-90) && l >= nu.AddDays(-180),
                "180+" => l < nu.AddDays(-180),
                _ => l is null,
            }),
            _ when k.Sleutel.StartsWith("wachtwoord:", StringComparison.Ordinal) => b.Actief.Where(p => p.Ad is not null).Where(p => Datum(p.Ad!.PasswordLastSet) is var w && k.Waarde switch
            {
                "0-90" => w >= nu.AddDays(-90),
                "90-365" => w < nu.AddDays(-90) && w >= nu.AddDays(-365),
                "365-730" => w < nu.AddDays(-365) && w >= nu.AddDays(-730),
                "730+" => w < nu.AddDays(-730),
                _ => w is null,
            }),
            _ when k.Sleutel.StartsWith("mfa:", StringComparison.Ordinal) => b.Actief.Where(p => (beveiliging?.MfaVan(p.Entra?.Id)) is var m && k.Waarde switch
            {
                "ja" => m is { MfaGeregistreerd: true },
                "nee" => m is { MfaGeregistreerd: false },
                _ => m is null,
            }),
            _ when k.Sleutel.StartsWith("kantoor:", StringComparison.Ordinal) => b.Actief.Where(p => KantoorVan(p) == k.Waarde),
            _ when k.Sleutel.StartsWith("gastdomein:", StringComparison.Ordinal) => d.Personen.Where(p => p.IsGast && GastDomein(p) == k.Waarde),
            _ when k.Sleutel.StartsWith("aangemaakt:", StringComparison.Ordinal) => b.Medewerkers.Where(p => Datum(p.Ad?.WhenCreated is { Length: > 0 } w ? w : p.Entra?.CreatedDateTime) is { } a
                && a.ToString("yyyy-MM", CultureInfo.InvariantCulture) == k.Waarde),
            _ when k.Sleutel.StartsWith("afdeling-alle:", StringComparison.Ordinal) => d.Personen.Where(p => !p.IsGast && (b.Config.AfdelingVoor(p.Dienst)?.Naam ?? Statistiek.GeenAfdeling) == k.Waarde),
            _ when k.Sleutel.StartsWith("afdeling:", StringComparison.Ordinal) => b.Actief.Where(p => (b.Config.AfdelingVoor(p.Dienst)?.Naam ?? Statistiek.GeenAfdeling) == k.Waarde),
            _ when k.Sleutel.StartsWith("licentie:", StringComparison.Ordinal) => d.Personen.Where(p => p.Entra?.Licenties.Contains(k.Waarde, StringComparer.OrdinalIgnoreCase) == true),
            _ when k.Sleutel.StartsWith("methode:", StringComparison.Ordinal) => beveiliging is null ? Enumerable.Empty<Persoon>()
                : b.Actief.Where(p => beveiliging.MfaVan(p.Entra?.Id) is { } m && m.Methodes.Any(x => BeveiligingStatistiek.MethodeNaam(x) == k.Waarde)),
            _ => Enumerable.Empty<Persoon>(),
        };
        return rijen.Distinct().ToList();
    }

    /// <summary>Gebruikers (geen groepen of apps) met een Entra-beheerrol die aan de voorwaarde voldoet.</summary>
    private static IEnumerable<Persoon> MetRol(InventarisData d, Func<RolLid, bool> voorwaarde)
    {
        if (d.Beveiliging is not { } b)
        {
            return Enumerable.Empty<Persoon>();
        }

        var ids = b.Rollen.Where(r => r.LidSoort is "user" or "").Where(voorwaarde).Select(r => r.LidId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return d.Personen.Where(p => p.Entra?.Id is { } id && ids.Contains(id));
    }

    // ---------------------------------------------------------------- postvakken, groepen, rooms

    /// <summary>Hoe de toegang tot een gedeeld postvak geregeld is.</summary>
    public static string RechtenmodelVan(InventarisData d, Postvak p)
    {
        var r = d.Index.RechtenOp(p);
        return (r.Any(x => !x.Direct), r.Any(x => x.Direct)) switch
        {
            (true, false) => "Alleen via groep",
            (true, true) => "Groep en rechtstreeks",
            (false, true) => "Alleen rechtstreeks",
            _ => "Zonder rechten",
        };
    }

    public static IReadOnlyList<string> Rechtenmodellen { get; } = new[] { "Alleen via groep", "Groep en rechtstreeks", "Alleen rechtstreeks", "Zonder rechten" };

    public static IReadOnlyList<Postvak> Postvakken(Kpi k, KpiBron b)
    {
        var d = b.Data;
        var gedeeld = d.Postvakken.Where(p => p.IsGedeeld);
        return (k.Sleutel switch
        {
            "kunnen-aanmelden" => gedeeld.Where(p => d.Index.AccountVan(p.Smtp, p.Alias) is { Ingeschakeld: true }),
            "rechtstreeks" => gedeeld.Where(p => d.Index.RechtenOp(p).Any(r => r.Direct)),
            "zonder-toegangsgroep" => gedeeld.Where(p => d.Index.ToegangsgroepVan(p) is null),
            _ when k.Sleutel.StartsWith("rechtenmodel:", StringComparison.Ordinal) => gedeeld.Where(p => RechtenmodelVan(d, p) == k.Waarde),
            _ => Enumerable.Empty<Postvak>(),
        }).ToList();
    }

    public static IReadOnlyList<Groep> Groepen(Kpi k, KpiBron b)
    {
        var groepen = b.Data.Index.Groepen;
        return (k.Sleutel switch
        {
            "dynamisch" => groepen.Where(g => g.Dynamisch),
            "zonder-leden" => groepen.Where(g => !g.Dynamisch && (g.Cloud?.AantalLeden ?? g.Exo?.AantalLeden ?? g.Ad?.AantalLeden) == 0),
            _ when k.Sleutel.StartsWith("groepsoort:", StringComparison.Ordinal) => groepen.Where(g => g.SoortTekst == k.Waarde),
            _ => Enumerable.Empty<Groep>(),
        }).ToList();
    }

    public static IReadOnlyList<Room> Rooms(Kpi k, KpiBron b) => k.Sleutel switch
    {
        "afwijkend" => b.Data.Rooms.Where(r => PlanBouwer.RoomAfwijkingen(r, b.Config.Rooms.Standaard).Count > 0).ToList(),
        _ => Array.Empty<Room>(),
    };

    // ---------------------------------------------------------------- logboek en Conditional Access

    public static IReadOnlyList<LogRegel> Logregels(Kpi k, IEnumerable<LogRegel> regels, DateTimeOffset nu)
    {
        var grens = nu.AddDays(-7);
        return (k.Sleutel switch
        {
            "gelukt-7" => regels.Where(r => r.Tijd >= grens && r.Status == "Gelukt"),
            "mislukt-7" => regels.Where(r => r.Tijd >= grens && r.Status == "Mislukt"),
            _ => Enumerable.Empty<LogRegel>(),
        }).ToList();
    }

    public static IReadOnlyList<CaBeleid> Beleiden(Kpi k, KpiBron b)
    {
        var beleiden = b.Data.ConditionalAccess is { Melding: null } ca ? ca.Beleiden : Array.Empty<CaBeleid>();
        CaStatus? status = k.Sleutel switch { "ca-aan" => CaStatus.Aan, "ca-rapport" => CaStatus.Rapport, "ca-uit" => CaStatus.Uit, _ => null };
        return status is null ? Array.Empty<CaBeleid>() : beleiden.Where(x => x.Status == status).ToList();
    }

    public static IReadOnlyList<CaBevinding> Bevindingen(Kpi k, KpiBron b)
    {
        CaOordeel? oordeel = k.Sleutel switch { "ca-problemen" => CaOordeel.Probleem, "ca-aandacht" => CaOordeel.Aandacht, _ => null };
        return oordeel is null ? Array.Empty<CaBevinding>() : b.CaBevindingen.Where(x => x.Oordeel == oordeel).ToList();
    }
}
