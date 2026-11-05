using System.Globalization;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

/// <summary>Eén staaf in een lijst (naam, aantal, eventueel een totaal om tegen af te zetten).</summary>
public sealed record Staaf(string Naam, int Aantal, int? Totaal = null, string? Link = null)
{
    public double Deel => Totaal is > 0 ? (double)Aantal / Totaal.Value : 0;
}

/// <summary>De indicatoren voor het scherm Statistieken, berekend uit de geladen gegevens.</summary>
public sealed record Statistiek(
    int Accounts,
    int Actief,
    int Uitgeschakeld,
    int AdEnEntra,
    int AlleenAd,
    int AlleenEntra,
    int Gasten,
    int Inactief90,
    int Inactief180,
    int NooitAangemeld,
    int WachtwoordVerlooptNooit,
    int BevoorrechteLeden,
    IReadOnlyList<Staaf> Licenties,
    IReadOnlyList<Staaf> PerAfdeling,
    int Groepen,
    int LegeGroepen,
    int DynamischeGroepen,
    IReadOnlyList<Staaf> GroepenPerSoort,
    int GedeeldePostvakken,
    IReadOnlyList<Staaf> Rechtenmodel,
    int RechtstreekseRechten,
    int ZonderToegangsgroep,
    int Rooms,
    int RoomsMetAfwijking,
    IReadOnlyList<Staaf> VerschillenAdEntra,
    BeveiligingStatistiek? Beveiliging = null,
    int PostvakAccounts = 0)
{
    /// <summary>Naam van de groep medewerkers zonder afdeling in de staven per afdeling.</summary>
    public const string GeenAfdeling = "(geen afdeling)";

    /// <summary>Licenties waarvan minder dan 5% (of hoogstens 2) vrij zijn.</summary>
    public static bool BijnaVol(Staaf licentie) => licentie.Totaal is > 0 && licentie.Totaal.Value - licentie.Aantal <= Math.Max(2, licentie.Totaal.Value / 20);

    public static Statistiek Bereken(InventarisData d, OrganisatieConfig config, DateTime nu)
    {
        // Postvak- en roomaccounts zijn geen medewerkers: niemand meldt er zich mee aan
        var medewerkers = d.Personen.Where(p => !p.IsGast && !d.Index.IsPostvakAccount(p)).ToList();
        var actief = medewerkers.Where(p => p.Ingeschakeld).ToList();
        var laatste = actief.Select(LaatsteAanmelding).ToList();

        var index = d.Index;
        var gedeeld = d.Postvakken.Where(p => p.IsGedeeld).ToList();
        var model = gedeeld.Select(p => Kpis.RechtenmodelVan(d, p)).ToList();

        var groepen = index.Groepen.ToList();
        var verschillen = d.HeeftAd && d.HeeftEntra ? AdEntraVergelijking.Vergelijk(d, config) : Array.Empty<Verschil>();

        return new Statistiek(
            Accounts: medewerkers.Count,
            Actief: actief.Count,
            Uitgeschakeld: medewerkers.Count - actief.Count,
            AdEnEntra: medewerkers.Count(p => p.Ad is not null && p.Entra is not null),
            AlleenAd: medewerkers.Count(p => p.Ad is not null && p.Entra is null),
            AlleenEntra: medewerkers.Count(p => p.Ad is null && p.Entra is not null),
            Gasten: d.Personen.Count(p => p.IsGast),
            Inactief90: laatste.Count(l => l is { } x && x < nu.AddDays(-90)),
            Inactief180: laatste.Count(l => l is { } x && x < nu.AddDays(-180)),
            NooitAangemeld: laatste.Count(l => l is null),
            WachtwoordVerlooptNooit: actief.Count(p => p.Ad?.PasswordNeverExpires == true),
            BevoorrechteLeden: actief.Count(p => d.AdGroepenVan(p).Any(Spiegelgebruiker.IsBevoorrecht)),
            Licenties: d.Licenties.Where(l => l.Totaal > 0)
                .OrderByDescending(l => l.Totaal).Select(l => new Staaf(l.SkuPartNumber, l.Toegewezen, l.Totaal, Kpis.Licentie(l.SkuPartNumber).Link)).ToList(),
            PerAfdeling: actief.GroupBy(p => config.AfdelingVoor(p.Dienst)?.Naam ?? GeenAfdeling)
                .Select(g => new Staaf(g.Key, g.Count(), Link: Kpis.Afdeling(g.Key).Link)).OrderByDescending(s => s.Aantal).ToList(),
            Groepen: groepen.Count,
            LegeGroepen: groepen.Count(g => !g.Dynamisch && (g.Cloud?.AantalLeden ?? g.Exo?.AantalLeden ?? g.Ad?.AantalLeden) == 0),
            DynamischeGroepen: groepen.Count(g => g.Dynamisch),
            GroepenPerSoort: groepen.GroupBy(g => g.SoortTekst).Select(g => new Staaf(g.Key, g.Count(), Link: Kpis.Groepsoort(g.Key).Link)).OrderByDescending(s => s.Aantal).ToList(),
            GedeeldePostvakken: gedeeld.Count,
            Rechtenmodel: Kpis.Rechtenmodellen.Select(m => new Staaf(m, model.Count(x => x == m), gedeeld.Count, Kpis.Rechtenmodel(m).Link)).ToList(),
            RechtstreekseRechten: index.Rechten.Count(r => r.Direct),
            ZonderToegangsgroep: gedeeld.Count(p => index.ToegangsgroepVan(p) is null),
            Rooms: d.Rooms.Count,
            RoomsMetAfwijking: d.Rooms.Count(r => Wijzigingen.PlanBouwer.RoomAfwijkingen(r, config.Rooms.Standaard).Count > 0),
            VerschillenAdEntra: verschillen.GroupBy(v => v.SoortTekst).Select(g => new Staaf(g.Key, g.Count())).OrderByDescending(s => s.Aantal).ToList(),
            Beveiliging: d.Beveiliging is { } b ? BeveiligingStatistiek.Bereken(b, actief) : null,
            PostvakAccounts: d.Personen.Count(p => !p.IsGast && d.Index.IsPostvakAccount(p)));
    }

    /// <summary>De recentste aanmelding uit AD en Entra; null = nooit (of onbekend).</summary>
    public static DateTime? LaatsteAanmelding(Persoon p)
    {
        DateTime? beste = null;
        foreach (var tekst in new[] { p.Ad?.LastLogonDate, p.Entra?.LaatsteAanmelding })
        {
            if (DateTime.TryParse(tekst, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && (beste is null || d > beste))
            {
                beste = d;
            }
        }

        return beste;
    }
}

/// <summary>Indicatoren over MFA en beheerrollen (alleen als de beveiligingsgegevens geladen zijn).</summary>
public sealed record BeveiligingStatistiek(
    bool MfaBekend,
    bool RollenBekend,
    int MetMfaGegevens,
    int ZonderMfa,
    int MetBeheerrol,
    int GlobaleBeheerders,
    int KritiekZonderMfa,
    IReadOnlyList<Staaf> Rollen,
    IReadOnlyList<Staaf> Methodes)
{
    public static BeveiligingStatistiek Bereken(BeveiligingGegevens b, IReadOnlyList<Persoon> actieveMedewerkers)
    {
        var registraties = actieveMedewerkers.Select(p => b.MfaVan(p.Entra?.Id)).Where(m => m is not null).Select(m => m!).ToList();
        var zonderMfa = registraties.Where(m => !m.MfaGeregistreerd).Select(m => m.GebruikerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gebruikersInRol = b.Rollen.Where(r => r.LidSoort is "user" or "").ToList();
        return new BeveiligingStatistiek(
            b.MfaBekend,
            b.RollenBekend,
            registraties.Count,
            zonderMfa.Count,
            gebruikersInRol.Select(r => r.LidId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            gebruikersInRol.Count(r => r.Rol == "Global Administrator"),
            gebruikersInRol.Where(r => BeveiligingGegevens.IsKritiekeRol(r.Rol) && b.MfaVan(r.LidId) is { MfaGeregistreerd: false })
                .Select(r => r.LidId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            b.Rollen.GroupBy(r => r.Rol).Select(g => new Staaf(g.Key, g.Count(), Link: "beveiliging")).OrderByDescending(s => s.Aantal).ToList(),
            registraties.SelectMany(m => m.Methodes.Select(MethodeNaam).Distinct()).GroupBy(n => n)
                .Select(g => new Staaf(g.Key, g.Count(), registraties.Count, Kpis.Methode(g.Key).Link))
                .OrderByDescending(s => s.Aantal).ToList());
    }

    /// <summary>Leesbare naam van een MFA-methode uit het rapport.</summary>
    public static string MethodeNaam(string methode) => methode switch
    {
        "microsoftAuthenticatorPush" => "Authenticator (melding)",
        "microsoftAuthenticatorPasswordless" => "Authenticator (zonder wachtwoord)",
        "softwareOneTimePasscode" => "Code uit een app",
        "hardwareOneTimePasscode" => "Hardwaretoken",
        "mobilePhone" => "Sms of telefoon",
        "alternateMobilePhone" => "Tweede gsm-nummer",
        "officePhone" => "Kantoortelefoon",
        "email" => "E-mail (enkel wachtwoordherstel)",
        "fido2SecurityKey" or "passKeyDeviceBound" or "passKeyDeviceBoundAuthenticator" => "Beveiligingssleutel of passkey",
        "windowsHelloForBusiness" => "Windows Hello",
        "temporaryAccessPass" => "Tijdelijke toegangspas",
        "securityQuestion" => "Beveiligingsvragen",
        _ => methode,
    };
}
