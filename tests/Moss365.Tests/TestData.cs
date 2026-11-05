using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Tests;

/// <summary>Verzonnen testdata in de vorm van de CSV-bestanden van Export-Inventaris.ps1.</summary>
public static class TestData
{
    public const string Gemeente = "OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=test,DC=internal";
    public const string Ocmw = "OU=GEBRUIKERS,OU=OCMW,DC=test,DC=internal";
    public const string Vpn = "OU=VPN_USERS,OU=ARCHIVE,DC=test,DC=internal";
    public const string Groepen = "OU=GROEPEN,OU=GEMEENTEBESTUUR,DC=test,DC=internal";

    public static string Dn(string naam, string ou) => $"CN={naam},{ou}";

    public static Dictionary<string, string> AdGebruiker(string sam, string ou, string upn, string department = "", bool enabled = true,
        string lastLogon = "2026-10-01 08:00:00", params string[] groepen) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["OU"] = ou,
        ["SamAccountName"] = sam,
        ["UserPrincipalName"] = upn,
        ["DisplayName"] = sam.Replace('.', ' '),
        ["Department"] = department,
        ["EmailAddress"] = upn,
        ["Enabled"] = enabled ? "True" : "False",
        ["DistinguishedName"] = Dn(sam, ou),
        ["LastLogonDate"] = lastLogon,
        ["MemberOf"] = string.Join('|', groepen.Select(g => Dn(g, Groepen))),
    };

    public static Dictionary<string, string> AdGroep(string naam, string scope = "Global", string? ou = null) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = naam,
        ["SamAccountName"] = naam,
        ["DistinguishedName"] = Dn(naam, ou ?? Groepen),
        ["OU"] = ou ?? Groepen,
        ["Category"] = "Security",
        ["Scope"] = scope,
    };

    public static Dictionary<string, string> EntraGebruiker(string id, string upn, string? sam = null, string department = "",
        bool enabled = true, string userType = "Member", string displayName = "") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = id,
        ["UserPrincipalName"] = upn,
        ["DisplayName"] = displayName.Length > 0 ? displayName : (sam ?? id).Replace('.', ' '),
        ["Mail"] = upn,
        ["AccountEnabled"] = enabled ? "True" : "False",
        ["UserType"] = userType,
        ["Department"] = department,
        ["OnPremisesSyncEnabled"] = sam is null ? "" : "True",
        ["OnPremisesSamAccountName"] = sam ?? "",
    };

    public static Dictionary<string, string> CloudGroep(string id, string naam, string type, bool dynamisch = false, string bron = "Cloud", string regel = "",
        string mail = "", string onPremSam = "")
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["DisplayName"] = naam,
            ["Type"] = type,
            ["Bron"] = bron,
            ["Dynamisch"] = dynamisch ? "True" : "False",
            ["Lidmaatschapsregel"] = regel,
            ["Mail"] = mail,
            ["OnPremisesSamAccountName"] = onPremSam,
        };

    public static Dictionary<string, string> AdLid(string groep, string lidDn, string? ou = null) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groep"] = groep,
        ["GroepDN"] = Dn(groep, ou ?? Groepen),
        ["LidDN"] = lidDn,
    };

    public static Dictionary<string, string> ExoGroep(string naam, string type, string smtp) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = type,
        ["DisplayName"] = naam,
        ["Alias"] = naam.ToLowerInvariant(),
        ["PrimarySmtpAddress"] = smtp,
        ["MemberJoinRestriction"] = "Closed",
        ["ManagedBy"] = "an.peeters@test.be",
    };

    public static Dictionary<string, string> ExoLid(string groep, string smtp) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groep"] = groep,
        ["Lid"] = smtp,
        ["LidNaam"] = smtp.Split('@')[0],
    };

    public static Dictionary<string, string> Postvak(string naam, string smtp, string type = "SharedMailbox") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = type,
        ["DisplayName"] = naam,
        ["Alias"] = smtp.Split('@')[0],
        ["PrimarySmtpAddress"] = smtp,
        ["Hidden"] = "False",
    };

    public static Dictionary<string, string> Recht(string postvak, string recht, string gebruiker) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Postvak"] = postvak,
        ["Recht"] = recht,
        ["Gebruiker"] = gebruiker,
    };

    public static Dictionary<string, string> Lid(string groepId, string lidId) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["GroepId"] = groepId,
        ["LidId"] = lidId,
        ["LidType"] = "user",
    };

    /// <summary>Een kleine omgeving: twee gemeentegebruikers (referentie en doel), een OCMW-gebruiker, een vrijwilliger zonder Entra, ...</summary>
    public static InventarisData Omgeving() => InventarisLader.Bouw(Bestanden());

    /// <summary>Schrijft de verzonnen inventaris als CSV-bestanden, zoals Export-Inventaris.ps1 ze maakt.</summary>
    public static void SchrijfInventaris(string map, Dictionary<string, List<Dictionary<string, string>>>? bestanden = null)
    {
        Directory.CreateDirectory(map);
        foreach (var (bestand, rijen) in bestanden ?? Bestanden())
        {
            var kolommen = rijen.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            static string Q(string? w) => "\"" + (w ?? string.Empty).Replace("\"", "\"\"") + "\"";
            var regels = new List<string> { string.Join(';', kolommen.Select(Q)) };
            regels.AddRange(rijen.Select(r => string.Join(';', kolommen.Select(k => Q(r.GetValueOrDefault(k))))));
            File.WriteAllLines(Path.Combine(map, bestand), regels);
        }
    }

    public static Dictionary<string, List<Dictionary<string, string>>> Bestanden() => new()
    {
        [InventarisLader.AdGebruikers] = new()
        {
            AdGebruiker("an.peeters", Gemeente, "an.peeters@test.be", "cultuur", groepen: new[] { "gg_wifiIntern", "gg_rww_foxbeleid", "Domain Admins" }),
            AdGebruiker("bo.janssens", Gemeente, "bo.janssens@test.be", "cultuur ", groepen: new[] { "gg_wifiIntern", "gg_eigen" }),
            AdGebruiker("cas.vos", Ocmw, "Cas.Vos@test.be", "gezinszorg"),
            AdGebruiker("dirk.intern", Gemeente, "dirk.intern@TEST.INTERNAL", lastLogon: ""),
            AdGebruiker("oud.account", Gemeente, "oud.account@test.be", enabled: false),
            AdGebruiker("vpn.leverancier", Vpn, "vpn.leverancier@test.be"),
        },
        [InventarisLader.AdGroepen] = new()
        {
            AdGroep("gg_wifiIntern"),
            AdGroep("gg_rww_foxbeleid"),
            AdGroep("gg_eigen"),
            AdGroep("Domain Admins", ou: "CN=Users,DC=test,DC=internal"),
        },
        [InventarisLader.EntraGebruikers] = new()
        {
            EntraGebruiker("e-an", "an.peeters@test.be", "an.peeters", "cultuur"),
            EntraGebruiker("e-bo", "bo.janssens@test.be", "BO.JANSSENS", "cultuur"),
            EntraGebruiker("e-cas", "cas.vos@test.be", "cas.vos", "gezinszorg"),
            EntraGebruiker("e-oud", "oud.account@test.be", "oud.account", enabled: true),
            EntraGebruiker("e-info", "info@test.onmicrosoft.com", displayName: "Info"),
            EntraGebruiker("e-gast", "gast_example.net#EXT#@test.onmicrosoft.com", userType: "Guest", displayName: "Gast"),
        },
        [InventarisLader.EntraGroepen] = new()
        {
            CloudGroep("g-alle", "Alle gebruikers", "Beveiliging", dynamisch: true, regel: "All Users"),
            CloudGroep("g-sm", "SG-SM-info-Full", "MailBeveiliging", mail: "sg-sm-info-full@test.be"),
            CloudGroep("g-team", "DNST_CULTUUR", "M365"),
            CloudGroep("g-dl", "Gemeentehuis", "Distributie", mail: "gemeentehuis@test.be"),
            CloudGroep("g-ad", "Gesynchroniseerd", "Beveiliging", bron: "AD", onPremSam: "gg_rww_foxbeleid"),
            CloudGroep("g-afd", "AFD_VRIJETIJD", "M365"),
            CloudGroep("g-eigen", "WKG_Eigen", "M365"),
        },
        [InventarisLader.EntraLicenties] = new()
        {
            new(StringComparer.OrdinalIgnoreCase) { ["SkuId"] = "sku-spb", ["SkuPartNumber"] = "SPB", ["Toegewezen"] = "10", ["Totaal"] = "10" },
            new(StringComparer.OrdinalIgnoreCase) { ["SkuId"] = "sku-f3", ["SkuPartNumber"] = "DESKLESSPACK", ["Toegewezen"] = "5", ["Totaal"] = "8" },
        },
        [InventarisLader.ExoRooms] = new()
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Smtp"] = "rm-vz-a@test.be", ["DisplayName"] = "Vergaderzaal A", ["Gebouw"] = "Gemeentehuis", ["Verdieping"] = "2", ["Capaciteit"] = "8",
                ["AutomateProcessing"] = "AutoAccept", ["BookingWindowInDays"] = "600", ["MaximumDurationInMinutes"] = "1440", ["AllowConflicts"] = "False",
                ["AllowRecurringMeetings"] = "True", ["AddOrganizerToSubject"] = "True", ["DeleteSubject"] = "True", ["AgendaRechten"] = "Default=LimitedDetails|Anonymous=None",
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Smtp"] = "rm-vz-b@test.be", ["DisplayName"] = "Vergaderzaal B", ["Gebouw"] = "Gemeentehuis", ["Verdieping"] = "2", ["Capaciteit"] = "8",
                ["AutomateProcessing"] = "AutoAccept", ["BookingWindowInDays"] = "180", ["MaximumDurationInMinutes"] = "1440", ["AllowConflicts"] = "False",
                ["AllowRecurringMeetings"] = "True", ["AddOrganizerToSubject"] = "True", ["DeleteSubject"] = "False", ["AgendaRechten"] = "Default=Reviewer|Anonymous=None",
            },
        },
        [InventarisLader.AdGroepsleden] = new()
        {
            AdLid("gg_wifiIntern", Dn("an.peeters", Gemeente)), AdLid("gg_wifiIntern", Dn("bo.janssens", Gemeente)),
            AdLid("gg_rww_foxbeleid", Dn("an.peeters", Gemeente)), AdLid("gg_eigen", Dn("bo.janssens", Gemeente)),
            AdLid("gg_eigen", Dn("gg_rww_foxbeleid", Groepen)),
            AdLid("Domain Admins", Dn("an.peeters", Gemeente), "CN=Users,DC=test,DC=internal"),
        },
        [InventarisLader.ExoGroepen] = new()
        {
            ExoGroep("SG-SM-info-Full", "MailUniversalSecurityGroup", "sg-sm-info-full@test.be"),
            ExoGroep("Gemeentehuis", "MailUniversalDistributionGroup", "gemeentehuis@test.be"),
            ExoGroep("rl-gh", "RoomList", "rl-gh@test.be"),
        },
        [InventarisLader.ExoGroepsleden] = new()
        {
            ExoLid("SG-SM-info-Full", "an.peeters@test.be"),
            ExoLid("Gemeentehuis", "an.peeters@test.be"),
            ExoLid("rl-gh", "rm-vz-a@test.be"),
        },
        [InventarisLader.ExoPostvakken] = new()
        {
            Postvak("Info", "info@test.be"),
            Postvak("Boekhouding", "boekhouding@test.be"),
            Postvak("Vergaderzaal A", "rm-vz-a@test.be", "RoomMailbox"),
            Postvak("Afspraken", "afspraken@test.be", "SchedulingMailbox"),
        },
        [InventarisLader.ExoPostvakRechten] = new()
        {
            Recht("info@test.be", "FullAccess", "sg-sm-info-full@test.be"),
            Recht("info@test.be", "SendAs", "bo.janssens@test.be"),
            Recht("info@test.be", "FullAccess", "S-1-5-21-111-222-333-1001"),
            Recht("boekhouding@test.be", "FullAccess,SendAs", "an.peeters@test.be"),
        },
        [InventarisLader.EntraGroepsleden] = new()
        {
            Lid("g-alle", "e-an"), Lid("g-alle", "e-bo"), Lid("g-sm", "e-an"), Lid("g-team", "e-an"), Lid("g-team", "e-bo"),
            Lid("g-dl", "e-an"), Lid("g-ad", "e-an"), Lid("g-eigen", "e-bo"),
        },
    };

    public static OrganisatieConfig Config() => new()
    {
        Company = "Contoso",
        AdDomein = "TEST.INTERNAL",
        GesynchroniseerdeOus = new() { "GEMEENTEBESTUUR/GEBRUIKERS", "OCMW/GEBRUIKERS" },
        Populaties = new()
        {
            new Populatie { Naam = "Gemeente", Ou = "GEMEENTEBESTUUR/GEBRUIKERS", UpnDomein = "test.be", StandaardAdGroepen = new() { "gg_wifiIntern", "bestaat_niet" } },
            new Populatie { Naam = "Vrijwilliger", Ou = "GEMEENTEBESTUUR/GASTGEBRUIKERS", UpnDomein = "example.org", City = "intranet", GeenLicentie = true },
        },
        Uitstroom = new UitstroomRegel { Ou = "ARCHIVE/disabled_users" },
        Afdelingen = new() { new Afdeling { Naam = "Vrije tijd", Groep = "AFD_VRIJETIJD" }, new Afdeling { Naam = "Burger en welzijn" } },
        Diensten = new()
        {
            new Dienst { Department = "cultuur", Afdeling = "Vrije tijd", Logonscript = "cultuur.bat" },
            new Dienst { Department = "gezinszorg", Afdeling = null },
        },
        Kantoren = new() { "gemeentehuis", "bibliotheek" },
        Licenties = new() { new LicentieKeuze { Sku = "DESKLESSPACK", Naam = "Microsoft 365 F3" }, new LicentieKeuze { Sku = "SPB", Naam = "Business Premium" } },
        DynamischeRegels = new() { new DynamischeRegel { Groep = "Alle gebruikers", Regel = "alle gebruikers" } },
    };

    /// <summary>Verzonnen Conditional Access: MFA voor iedereen (behalve Info en WKG_Eigen), verouderd blokkeren, beheerders (rapport), buitenland blokkeren.</summary>
    public static CaGegevens Ca(DateTimeOffset nu, bool groepLeden = true) => new(
        new[]
        {
            new CaBeleid
            {
                Id = "ca-mfa", Naam = "MFA voor iedereen", Status = CaStatus.Aan, GebruikersIn = new[] { "All" }, GebruikersUit = new[] { "e-info" },
                GroepenUit = new[] { "g-eigen" }, AppsIn = new[] { "All" }, Controles = new[] { "mfa" }, Gewijzigd = nu.AddDays(-100),
            },
            new CaBeleid
            {
                Id = "ca-legacy", Naam = "Verouderd blokkeren", Status = CaStatus.Aan, GebruikersIn = new[] { "All" }, GebruikersUit = new[] { "e-info" },
                AppsIn = new[] { "All" }, ClientApps = new[] { "exchangeActiveSync", "other" }, Controles = new[] { "block" },
            },
            new CaBeleid
            {
                Id = "ca-beheer", Naam = "Beheerders phishingbestendig", Status = CaStatus.Rapport, RollenIn = new[] { CaGegevens.RolGlobalAdmin },
                AppsIn = new[] { "All" }, SterkteId = CaGegevens.SterktePhishingbestendig, SterkteNaam = "Phishingbestendige MFA", Gewijzigd = nu.AddDays(-60),
            },
            new CaBeleid
            {
                Id = "ca-land", Naam = "Buitenland blokkeren", Status = CaStatus.Aan, GebruikersIn = new[] { "All" }, AppsIn = new[] { "All" },
                LocatiesIn = new[] { "All" }, LocatiesUit = new[] { "loc-be" }, Controles = new[] { "block" },
            },
        },
        new[]
        {
            new BenoemdeLocatie("loc-be", "België", false, false, new[] { "BE" }),
            new BenoemdeLocatie("loc-kantoor", "Gemeentehuis", true, true, new[] { "203.0.113.0/24" }),
        },
        new[] { new Authenticatiesterkte(CaGegevens.SterktePhishingbestendig, "Phishingbestendige MFA", true, new[] { "fido2", "windowsHelloForBusiness" }) },
        new Dictionary<string, string> { [CaGegevens.RolGlobalAdmin] = "Global Administrator" },
        false,
        null,
        groepLeden ? new Dictionary<string, IReadOnlyList<string>> { ["g-eigen"] = new[] { "e-bo" } } : null);
}
