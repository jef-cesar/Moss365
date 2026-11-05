using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Core.Rechten;

/// <summary>
/// De profielen van Moss365: app-rollen in de app-registratie, toegekend via Entra-groepen. Rollen tellen op: wie
/// Helpdesk en Lezer heeft, mag alles van beide.
/// </summary>
[Flags]
public enum Rol
{
    Geen = 0,
    Lezer = 1,
    Helpdesk = 2,
    Beheerder = 4,
    Auditor = 8,
    Appbeheer = 16,

    /// <summary>Zolang profielen niet verplicht zijn en de aanmelding geen rol bevat: alles, zoals vóór 0.8.</summary>
    Alles = Lezer | Helpdesk | Beheerder | Auditor | Appbeheer,
}

/// <summary>Wat iemand in de app mag (de rechtentabel uit hoofdstuk 2 van de roadmap).</summary>
public enum Recht
{
    /// <summary>Zoeken en bekijken: gebruikers, groepen, postvakken, rooms, gasten, vergelijken, statistieken.</summary>
    Bekijken,

    /// <summary>Spiegelgebruiker vergelijken (enkel tonen).</summary>
    SpiegelVergelijken,

    /// <summary>Lijsten exporteren (komt in het toegangslog).</summary>
    Exporteren,

    /// <summary>Beveiliging: MFA, beheerrollen, bevoorrechte groepen, Conditional Access.</summary>
    Beveiliging,

    /// <summary>Aanmeldingen (IP-adressen, plaatsen); komt in het toegangslog.</summary>
    Aanmeldingen,

    /// <summary>Logboek: de eigen wijzigingen.</summary>
    LogboekEigen,

    /// <summary>Logboek: alle wijzigingen.</summary>
    LogboekAlles,

    /// <summary>Wachtwoord opnieuw instellen, ontgrendelen, inschakelen (gewone accounts).</summary>
    Helpdesk,

    /// <summary>Lid toevoegen of verwijderen in gewone groepen.</summary>
    GroepLeden,

    GastenUitnodigen,

    /// <summary>Spiegel toepassen, nieuwe gebruiker, uitstroom, inplannen, opvolging.</summary>
    Acties,

    /// <summary>Rooms, gedeelde postvakken, groepen en gebruikers beheren; opkuis, gasten verwijderen, ongedaan maken.</summary>
    Beheren,

    /// <summary>Bevoorrechte accounts en groepen (Domain Admins, ...).</summary>
    Bevoorrecht,

    /// <summary>Scripts uit de bibliotheek en eigen scripts.</summary>
    Scripts,

    /// <summary>Organisatie (keuzelijsten en vaste regels).</summary>
    Instellingen,
}

public static class Rechtentabel
{
    /// <summary>De app-rollen zoals ze in de app-registratie staan (waarde in het token, en de vaste id).</summary>
    public static IReadOnlyList<(string Waarde, Rol Rol, string Groep, string Id)> AppRollen { get; } = new[]
    {
        ("Moss365.Lezer", Rol.Lezer, "GG-Moss365-Lezers", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8001"),
        ("Moss365.Helpdesk", Rol.Helpdesk, "GG-Moss365-Helpdesk", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8002"),
        ("Moss365.Beheerder", Rol.Beheerder, "GG-Moss365-Beheerders", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8003"),
        ("Moss365.Auditor", Rol.Auditor, "GG-Moss365-Auditors", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8004"),
        ("Moss365.Appbeheer", Rol.Appbeheer, "GG-Moss365-Appbeheer", "6a0f6a1e-3f1c-4d6b-9c0a-3b5e0d1a8005"),
    };

    private const Rol Lezen = Rol.Lezer | Rol.Helpdesk | Rol.Beheerder | Rol.Auditor;

    private static readonly Dictionary<Recht, Rol> Wie = new()
    {
        [Recht.Bekijken] = Lezen,
        [Recht.SpiegelVergelijken] = Rol.Lezer | Rol.Helpdesk | Rol.Beheerder,
        [Recht.Exporteren] = Rol.Helpdesk | Rol.Beheerder | Rol.Auditor,
        [Recht.Beveiliging] = Rol.Beheerder | Rol.Auditor,
        [Recht.Aanmeldingen] = Rol.Beheerder | Rol.Auditor,
        [Recht.LogboekEigen] = Rol.Helpdesk | Rol.Beheerder | Rol.Auditor,
        [Recht.LogboekAlles] = Rol.Beheerder | Rol.Auditor,
        [Recht.Helpdesk] = Rol.Helpdesk | Rol.Beheerder,
        [Recht.GroepLeden] = Rol.Helpdesk | Rol.Beheerder,
        [Recht.GastenUitnodigen] = Rol.Helpdesk | Rol.Beheerder,
        [Recht.Acties] = Rol.Beheerder,
        [Recht.Beheren] = Rol.Beheerder,
        [Recht.Bevoorrecht] = Rol.Beheerder,
        [Recht.Scripts] = Rol.Beheerder,
        [Recht.Instellingen] = Rol.Appbeheer,
    };

    public static bool Mag(Rol rollen, Recht recht) => (rollen & Wie[recht]) != 0;

    /// <summary>Welke rollen een recht geven (voor meldingen: "vraagt het profiel Beheerder").</summary>
    public static Rol RollenVoor(Recht recht) => Wie[recht];

    /// <summary>De rollen uit de "roles"-claims van de aanmelding; onbekende waarden tellen niet.</summary>
    public static Rol UitClaims(IEnumerable<string> claims)
    {
        var rol = Rol.Geen;
        foreach (var c in claims)
        {
            var gevonden = AppRollen.FirstOrDefault(r => string.Equals(r.Waarde, c, StringComparison.OrdinalIgnoreCase));
            rol |= gevonden.Rol;
        }

        return rol;
    }

    /// <summary>
    /// De rollen waarmee de app werkt. Bevat de aanmelding rollen, dan die. Zo niet: zolang profielen niet verplicht
    /// zijn alles (zoals vóór 0.8, met een melding); als ze verplicht zijn enkel lezen (ook vóór het aanmelden).
    /// </summary>
    public static Rol Bepaal(IEnumerable<string>? claims, bool verplicht)
    {
        var uitToken = claims is null ? Rol.Geen : UitClaims(claims);
        if (uitToken != Rol.Geen)
        {
            return uitToken;
        }

        return verplicht ? Rol.Lezer : Rol.Alles;
    }

    /// <summary>Het minimale recht voor een actie (los van bevoorrechte doelen, zie <see cref="Weigering"/>).</summary>
    public static Recht VoorActie(Actie actie) => actie switch
    {
        Actie.AdWachtwoordInstellen or Actie.AdAccountOntgrendelen or Actie.AdAccountInschakelen => Recht.Helpdesk,
        Actie.AdGroepLidToevoegen or Actie.AdGroepLidVerwijderen
            or Actie.GraphGroepLidToevoegen or Actie.GraphGroepLidVerwijderen
            or Actie.ExchangeGroepLidToevoegen or Actie.ExchangeGroepLidVerwijderen => Recht.GroepLeden,
        Actie.GraphGastUitnodigen => Recht.GastenUitnodigen,
        _ => Recht.Beheren,
    };

    /// <summary>
    /// Waarom deze rollen deze wijziging niet mogen uitvoeren, of null als het mag. Een bevoorrecht account (lid van een
    /// bevoorrechte AD-groep) of een bevoorrechte groep vraagt altijd het profiel Beheerder.
    /// </summary>
    public static string? Weigering(Rol rollen, Wijziging w, InventarisData? data = null)
    {
        var recht = VoorActie(w.Actie);
        if (!Mag(rollen, recht))
        {
            return $"Je profiel ({Tekst(rollen, "en")}) laat dit niet toe; het vraagt {Tekst(RollenVoor(recht))}.";
        }

        if (data is not null && recht != Recht.Beheren && !Mag(rollen, Recht.Bevoorrecht) && RaaktBevoorrecht(w, data) is { } wat)
        {
            return $"{wat} is bevoorrecht; dat vraagt het profiel Beheerder.";
        }

        return null;
    }

    private static string? RaaktBevoorrecht(Wijziging w, InventarisData data)
    {
        if (w.Parameters.TryGetValue("groepDn", out var groepDn) && data.AdGroepenOpDn.TryGetValue(groepDn, out var groep)
            && Spiegelgebruiker.IsBevoorrecht(groep))
        {
            return $"De groep {groep.Name}";
        }

        if (w.Systeem == Systeem.ActiveDirectory
            && data.AdGebruikers.FirstOrDefault(u => string.Equals(u.DistinguishedName, w.Doel, StringComparison.OrdinalIgnoreCase)) is { } gebruiker
            && (gebruiker.MemberOf.Any(dn => data.AdGroepenOpDn.TryGetValue(dn, out var g) && Spiegelgebruiker.IsBevoorrecht(g))
                || data.AdGroepenOpDn.Values.Any(g => Spiegelgebruiker.IsBevoorrecht(g)
                                                      && g.Leden.Contains(gebruiker.DistinguishedName, StringComparer.OrdinalIgnoreCase))))
        {
            return $"Het account {gebruiker.SamAccountName}";
        }

        return null;
    }

    /// <summary>Leesbare namen, bv. "Helpdesk of Beheerder" (of met "en" voor de eigen rollen).</summary>
    public static string Tekst(Rol rollen, string voegwoord = "of")
    {
        if (rollen == Rol.Geen)
        {
            return "geen profiel";
        }

        var namen = Enum.GetValues<Rol>().Where(r => r is not Rol.Geen and not Rol.Alles && rollen.HasFlag(r)).Select(r => r.ToString()).ToList();
        return namen.Count == 1 ? namen[0] : string.Join(", ", namen.Take(namen.Count - 1)) + $" {voegwoord} " + namen[^1];
    }
}
