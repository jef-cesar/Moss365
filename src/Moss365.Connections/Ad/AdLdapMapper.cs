using System.Globalization;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Connections.Ad;

/// <summary>Zet LDAP-kenmerken (naam naar waarden) om naar het model. Puur, dus testbaar zonder AD.</summary>
public static class AdLdapMapper
{
    public const int UacUitgeschakeld = 0x2;
    public const int UacWachtwoordVerlooptNooit = 0x10000;
    public const int UacNormaalAccount = 0x200;

    /// <summary>Bit in msDS-User-Account-Control-Computed: het account is nu vergrendeld.</summary>
    public const int UacVergrendeld = 0x10;

    public static readonly string[] GebruikerKenmerken =
    {
        "sAMAccountName", "userPrincipalName", "displayName", "givenName", "sn", "title", "department", "company",
        "physicalDeliveryOfficeName", "l", "description", "mail", "userAccountControl", "distinguishedName", "whenCreated",
        "lastLogonTimestamp", "pwdLastSet", "scriptPath", "memberOf", "telephoneNumber", "mobile", "manager",
        "msDS-User-Account-Control-Computed", "lockoutTime",
    };

    public static readonly string[] GroepKenmerken = { "name", "sAMAccountName", "distinguishedName", "groupType", "description", "member", "displayName", "mail", "managedBy" };

    public static AdGebruiker NaarGebruiker(IReadOnlyDictionary<string, object?[]> k)
    {
        var uac = Getal(k, "userAccountControl") ?? 0;
        var dn = Tekst(k, "distinguishedName");
        return new AdGebruiker
        {
            SamAccountName = Tekst(k, "sAMAccountName"),
            UserPrincipalName = Tekst(k, "userPrincipalName"),
            DisplayName = Tekst(k, "displayName"),
            GivenName = Tekst(k, "givenName"),
            Surname = Tekst(k, "sn"),
            Title = Tekst(k, "title"),
            Department = Tekst(k, "department"),
            Company = Tekst(k, "company"),
            Office = Tekst(k, "physicalDeliveryOfficeName"),
            City = Tekst(k, "l"),
            Description = Tekst(k, "description"),
            EmailAddress = Tekst(k, "mail"),
            Enabled = (uac & UacUitgeschakeld) == 0,
            PasswordNeverExpires = (uac & UacWachtwoordVerlooptNooit) != 0,
            LockedOut = Vergrendeld(k),
            DistinguishedName = dn,
            OuPad = DnHulp.OuPadVanObject(dn),
            WhenCreated = Datum(k, "whenCreated"),
            LastLogonDate = Datum(k, "lastLogonTimestamp"),
            PasswordLastSet = Datum(k, "pwdLastSet"),
            ScriptPath = Tekst(k, "scriptPath"),
            MemberOf = Lijst(k, "memberOf"),
            Telefoon = Tekst(k, "telephoneNumber"),
            Gsm = Tekst(k, "mobile"),
            Manager = Tekst(k, "manager"),
        };
    }

    public static AdGroep NaarGroep(IReadOnlyDictionary<string, object?[]> k)
    {
        var type = Getal(k, "groupType") ?? 0;
        var dn = Tekst(k, "distinguishedName");
        return new AdGroep
        {
            Name = Tekst(k, "name"),
            SamAccountName = Tekst(k, "sAMAccountName"),
            DistinguishedName = dn,
            OuPad = DnHulp.OuPadVanObject(dn),
            Category = (type & unchecked((int)0x80000000)) != 0 ? "Security" : "Distribution",
            Scope = (type & 0x2) != 0 ? "Global" : (type & 0x4) != 0 ? "DomainLocal" : (type & 0x8) != 0 ? "Universal" : string.Empty,
            Description = Tekst(k, "description"),
            DisplayName = Tekst(k, "displayName"),
            Mail = Tekst(k, "mail"),
            ManagedBy = Tekst(k, "managedBy"),
            Leden = Lijst(k, "member"),
            LedenBekend = true,
            AantalLeden = k.TryGetValue("member", out var leden) ? leden.Length : 0,
        };
    }

    /// <summary>
    /// Vergrendeld volgens het berekende kenmerk; ontbreekt dat, dan lockoutTime (dat blijft staan tot de volgende
    /// aanmelding, ook als de vergrendeling al verlopen is).
    /// </summary>
    private static bool Vergrendeld(IReadOnlyDictionary<string, object?[]> k)
    {
        if (Getal(k, "msDS-User-Account-Control-Computed") is { } berekend)
        {
            return (berekend & UacVergrendeld) != 0;
        }

        return k.TryGetValue("lockoutTime", out var w) && w.Length > 0 && w[0] switch
        {
            long l => l > 0,
            int i => i > 0,
            _ => false,
        };
    }

    /// <summary>De stand van de domeincontroller uit RootDSE (dsServiceName en highestCommittedUSN).</summary>
    public static AdStand? NaarStand(object? domeincontroller, object? usn)
    {
        var dc = Convert.ToString(domeincontroller, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(dc) || !long.TryParse(Convert.ToString(usn, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? null
            : new AdStand(dc, n);
    }

    /// <summary>
    /// Enkel de wijzigingen lezen kan als het dezelfde domeincontroller is (USN's tellen per domeincontroller) en zijn
    /// teller niet teruggesprongen is (bv. na een terugzetting).
    /// </summary>
    public static bool KanVerderVan(AdStand vorige, AdStand nu)
        => string.Equals(vorige.Domeincontroller, nu.Domeincontroller, StringComparison.OrdinalIgnoreCase) && nu.HoogsteUsn >= vorige.HoogsteUsn;

    /// <summary>Een waarde veilig in een LDAP-filter zetten (RFC 4515): \ * ( ) en NUL worden ontsnapt.</summary>
    public static string Filterwaarde(string waarde)
    {
        var uit = new System.Text.StringBuilder(waarde.Length);
        foreach (var c in waarde)
        {
            uit.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => c.ToString(),
            });
        }

        return uit.ToString();
    }

    /// <summary>DNS-naam "CONTOSO.LOCAL" naar DN "DC=CONTOSO,DC=INTERNAL".</summary>
    public static string DomeinNaarDn(string domein)
        => string.Join(',', domein.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(d => "DC=" + d));

    /// <summary>Een ADsPath mag geen gewone schuine streep bevatten; in een DN moet die ontsnapt worden.</summary>
    public static string LdapPad(string server, string dn)
        => string.IsNullOrWhiteSpace(server) ? $"LDAP://{dn.Replace("/", "\\/")}" : $"LDAP://{server.Trim()}/{dn.Replace("/", "\\/")}";

    private static string Tekst(IReadOnlyDictionary<string, object?[]> k, string naam)
        => k.TryGetValue(naam, out var w) && w.Length > 0 && w[0] is not null ? Convert.ToString(w[0], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> Lijst(IReadOnlyDictionary<string, object?[]> k, string naam)
        => k.TryGetValue(naam, out var w) ? w.Where(x => x is not null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToList() : Array.Empty<string>();

    private static int? Getal(IReadOnlyDictionary<string, object?[]> k, string naam)
    {
        if (!k.TryGetValue(naam, out var w) || w.Length == 0 || w[0] is null)
        {
            return null;
        }

        return w[0] switch
        {
            int i => i,
            long l => unchecked((int)l),
            _ => int.TryParse(Convert.ToString(w[0], CultureInfo.InvariantCulture), out var n) ? n : null,
        };
    }

    /// <summary>LDAP levert datums als DateTime (whenCreated) of als FILETIME-getal (lastLogonTimestamp, pwdLastSet).</summary>
    private static string Datum(IReadOnlyDictionary<string, object?[]> k, string naam)
    {
        if (!k.TryGetValue(naam, out var w) || w.Length == 0 || w[0] is null)
        {
            return string.Empty;
        }

        DateTime? datum = w[0] switch
        {
            DateTime d => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d,
            long ft when ft > 0 && ft < DateTime.MaxValue.ToFileTimeUtc() => DateTime.FromFileTimeUtc(ft).ToLocalTime(),
            _ => null,
        };
        return datum?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
