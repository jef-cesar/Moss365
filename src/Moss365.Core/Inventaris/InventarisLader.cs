namespace Moss365.Core.Inventaris;

/// <summary>
/// Laadt de uitvoer van Export-Inventaris.ps1. Per bestand neemt de lader de nieuwste inventarismap waarin dat bestand
/// bestaat, zodat AD, Entra en Exchange uit verschillende runs mogen komen.
/// </summary>
public static class InventarisLader
{
    public const string AdGebruikers = "ad-gebruikers.csv";
    public const string AdGroepen = "ad-groepen.csv";
    public const string EntraGebruikers = "entra-gebruikers.csv";
    public const string EntraGroepen = "entra-groepen.csv";
    public const string EntraGroepsleden = "entra-groepsleden.csv";
    public const string EntraLicenties = "entra-licenties.csv";
    public const string ExoRooms = "exo-rooms-overzicht.csv";
    public const string AdGroepsleden = "ad-groepsleden.csv";
    public const string ExoGroepen = "exo-groepen.csv";
    public const string ExoGroepsleden = "exo-groepsleden.csv";
    public const string ExoPostvakken = "exo-postvakken-niet-gebruikers.csv";
    public const string ExoPostvakRechten = "exo-gedeelde-postvakrechten.csv";
    public const string EntraMfa = "entra-mfa.csv";
    public const string EntraRollen = "entra-rollen.csv";

    /// <summary>Conditional Access (JSON, geschreven door de app; Export-Inventaris.ps1 maakt het niet).</summary>
    public const string EntraCa = "entra-ca.json";

    private static readonly string[] Bestanden =
    {
        AdGebruikers, AdGroepen, AdGroepsleden, EntraGebruikers, EntraGroepen, EntraGroepsleden, EntraLicenties,
        ExoRooms, ExoGroepen, ExoGroepsleden, ExoPostvakken, ExoPostvakRechten, EntraMfa, EntraRollen,
    };

    /// <summary>Alle inventarismappen onder de basismap, nieuwste eerst (de naam bevat yyyyMMdd-HHmm).</summary>
    public static IReadOnlyList<string> ZoekMappen(string basisMap)
    {
        if (string.IsNullOrWhiteSpace(basisMap) || !Directory.Exists(basisMap))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(basisMap, "Inventaris-*")
            .OrderByDescending(m => Path.GetFileName(m), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Laadt uit de opgegeven mappen; de eerste map die een bestand bevat, wint.</summary>
    public static InventarisData Laad(IEnumerable<string> mappenNieuwsteEerst)
    {
        var mappen = mappenNieuwsteEerst.ToList();
        var bronnen = new List<BronBestand>();
        var tabellen = new Dictionary<string, List<Dictionary<string, string>>>();

        foreach (var bestand in Bestanden)
        {
            var map = mappen.FirstOrDefault(m => File.Exists(Path.Combine(m, bestand)));
            var rijen = map is null ? new List<Dictionary<string, string>>() : CsvLezer.LeesBestand(Path.Combine(map, bestand));
            tabellen[bestand] = rijen;
            bronnen.Add(new BronBestand(bestand, map, rijen.Count));
        }

        var data = Bouw(tabellen, bronnen);
        var caMap = mappen.FirstOrDefault(m => File.Exists(Path.Combine(m, EntraCa)));
        if (caMap is null)
        {
            return data;
        }

        var ca = CaOpslag.UitJson(File.ReadAllText(Path.Combine(caMap, EntraCa)));
        return data with
        {
            ConditionalAccess = ca,
            Bronnen = bronnen.Append(new BronBestand(EntraCa, caMap, ca?.Beleiden.Count ?? 0)).ToList(),
        };
    }

    /// <summary>Bouwt het model uit reeds ingelezen tabellen (bestandsnaam naar rijen). Ook gebruikt door tests.</summary>
    public static InventarisData Bouw(IReadOnlyDictionary<string, List<Dictionary<string, string>>> tabellen, IReadOnlyList<BronBestand>? bronnen = null)
    {
        List<Dictionary<string, string>> T(string naam) => tabellen.TryGetValue(naam, out var t) ? t : new();

        var adGebruikers = T(AdGebruikers).Select(MaakAdGebruiker).Where(g => g.SamAccountName.Length > 0).ToList();
        var adLeden = T(AdGroepsleden)
            .Where(r => K(r, "GroepDN").Length > 0 && K(r, "LidDN").Length > 0)
            .GroupBy(r => K(r, "GroepDN"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => K(r, "LidDN")).ToList(), StringComparer.OrdinalIgnoreCase);
        var ledenBekend = tabellen.ContainsKey(AdGroepsleden) && T(AdGroepsleden).Count > 0;
        var adGroepen = T(AdGroepen).Select(r => MaakAdGroep(r, adLeden, ledenBekend))
            .Where(g => g.DistinguishedName.Length > 0)
            .GroupBy(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var entraGebruikers = T(EntraGebruikers).Select(MaakEntraGebruiker).Where(g => g.Id.Length > 0).ToList();
        var cloudGroepen = T(EntraGroepen).Select(MaakCloudGroep)
            .Where(g => g.Id.Length > 0)
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var lidmaatschap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var lidNamen = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rij in T(EntraGroepsleden))
        {
            var lidId = W(rij, "LidId");
            var groepId = W(rij, "GroepId");
            if (lidId.Length == 0 || groepId.Length == 0)
            {
                continue;
            }

            var lidType = K(rij, "LidType");
            if (lidType.Length > 0 && !lidType.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                lidNamen[lidId] = (lidType, W(rij, "Lid"));
            }

            if (!lidmaatschap.TryGetValue(lidId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lidmaatschap[lidId] = set;
            }

            set.Add(groepId);
        }

        var exoLeden = T(ExoGroepsleden)
            .GroupBy(r => W(r, "Groep").Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExoLid>)g.Select(r => new ExoLid(K(r, "Lid"), W(r, "LidNaam"))).ToList(), StringComparer.OrdinalIgnoreCase);

        return Samenstellen(adGebruikers, adGroepen.Values.ToList(), entraGebruikers, cloudGroepen.Values.ToList(), lidmaatschap,
            T(ExoRooms).Select(MaakRoom).Where(r => r.Smtp.Length > 0).ToList(),
            T(EntraLicenties).Select(MaakLicentie).Where(l => l.SkuPartNumber.Length > 0).ToList(),
            bronnen ?? Array.Empty<BronBestand>(), "Inventaris", string.Empty) with
        {
            CloudLidNamen = lidNamen,
            ExoGroepen = T(ExoGroepen).Select(r => MaakExoGroep(r, exoLeden)).Where(g => g.DisplayName.Length > 0).ToList(),
            Postvakken = T(ExoPostvakken).Select(MaakPostvak)
                .Where(p => p.Smtp.Length > 0 && !p.Type.Equals("RoomMailbox", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            PostvakRechten = T(ExoPostvakRechten)
                .Select(r => new PostvakRecht(K(r, "Postvak"), K(r, "Recht"), K(r, "Adres").Length > 0 ? K(r, "Adres") : K(r, "Gebruiker")))
                .Where(r => r.Postvak.Length > 0 && r.Trustee.Length > 0)
                .SelectMany(r => r.Recht.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => r with { Recht = x }))
                .ToList(),
            Beveiliging = T(EntraMfa).Count + T(EntraRollen).Count == 0 ? null : new BeveiligingGegevens(
                T(EntraMfa).Select(r => new MfaRegistratie(K(r, "Id"), K(r, "UserPrincipalName"), B(r, "IsMfaRegistered"), B(r, "IsMfaCapable"),
                    K(r, "Methods").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), K(r, "DefaultMethod"), B(r, "IsAdmin")))
                    .Where(m => m.GebruikerId.Length > 0).ToList(),
                T(EntraRollen).Select(r => new RolLid(K(r, "Rol"), K(r, "LidId"), W(r, "LidNaam"), K(r, "LidUpn"), K(r, "LidSoort"), K(r, "RoleTemplateId")))
                    .Where(r => r.Rol.Length > 0).ToList(),
                T(EntraMfa).Count == 0 ? "Niet in de inventaris." : null,
                T(EntraRollen).Count == 0 ? "Niet in de inventaris." : null),
        };
    }

    /// <summary>Stelt het model samen uit losse onderdelen; gebruikt door de inventaris en door het live laden.</summary>
    public static InventarisData Samenstellen(
        IReadOnlyList<AdGebruiker> adGebruikers,
        IReadOnlyList<AdGroep> adGroepen,
        IReadOnlyList<EntraGebruiker> entraGebruikers,
        IReadOnlyList<CloudGroep> cloudGroepen,
        IReadOnlyDictionary<string, HashSet<string>> lidmaatschap,
        IReadOnlyList<Room> rooms,
        IReadOnlyList<Licentie> licenties,
        IReadOnlyList<BronBestand> bronnen,
        string herkomst,
        string domeinDn) => new()
    {
        AdGebruikers = adGebruikers,
        EntraGebruikers = entraGebruikers,
        AdGroepenOpDn = adGroepen.Where(g => g.DistinguishedName.Length > 0)
            .GroupBy(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
        CloudGroepen = cloudGroepen.Where(g => g.Id.Length > 0)
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
        CloudLidmaatschap = lidmaatschap.ToDictionary(k => k.Key, k => (IReadOnlySet<string>)k.Value, StringComparer.OrdinalIgnoreCase),
        Personen = KoppelPersonen(adGebruikers, entraGebruikers),
        Rooms = rooms.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        Licenties = licenties,
        Bronnen = bronnen,
        Herkomst = herkomst,
        DomeinDn = domeinDn,
    };

    /// <summary>Koppelt AD- en Entra-accounts via sAMAccountName (onPremisesSamAccountName in Entra).</summary>
    public static IReadOnlyList<Persoon> KoppelPersonen(IReadOnlyList<AdGebruiker> ad, IReadOnlyList<EntraGebruiker> entra)
    {
        var entraOpSam = entra
            .Where(e => e.OnPremisesSyncEnabled && e.OnPremisesSamAccountName.Length > 0)
            .GroupBy(e => e.OnPremisesSamAccountName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var gebruikt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var personen = new List<Persoon>(ad.Count + entra.Count);
        foreach (var a in ad)
        {
            entraOpSam.TryGetValue(a.SamAccountName, out var e);
            if (e is not null)
            {
                gebruikt.Add(e.Id);
            }

            personen.Add(new Persoon { Sleutel = "ad:" + a.SamAccountName.ToLowerInvariant(), Ad = a, Entra = e });
        }

        foreach (var e in entra.Where(e => !gebruikt.Contains(e.Id)))
        {
            personen.Add(new Persoon { Sleutel = "entra:" + e.Id.ToLowerInvariant(), Entra = e });
        }

        return personen.OrderBy(p => p.Naam, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AdGebruiker MaakAdGebruiker(Dictionary<string, string> r)
    {
        var dn = K(r, "DistinguishedName");
        var ouDn = K(r, "OU");
        return new AdGebruiker
        {
            SamAccountName = K(r, "SamAccountName"),
            UserPrincipalName = W(r, "UserPrincipalName"),
            DisplayName = W(r, "DisplayName"),
            GivenName = W(r, "GivenName"),
            Surname = W(r, "Surname"),
            Title = W(r, "Title"),
            Department = W(r, "Department"),
            Company = W(r, "Company"),
            Office = W(r, "Office"),
            City = W(r, "City"),
            Description = W(r, "Description"),
            EmailAddress = W(r, "EmailAddress"),
            Enabled = B(r, "Enabled"),
            DistinguishedName = dn,
            OuPad = ouDn.Length > 0 ? DnHulp.OuPad(ouDn) : DnHulp.OuPadVanObject(dn),
            WhenCreated = W(r, "whenCreated"),
            LastLogonDate = W(r, "LastLogonDate"),
            PasswordLastSet = W(r, "PasswordLastSet"),
            PasswordNeverExpires = B(r, "PasswordNeverExpires"),
            LockedOut = B(r, "LockedOut"),
            ScriptPath = W(r, "ScriptPath"),
            MemberOf = Lijst(r, "MemberOf"),
            Telefoon = W(r, "OfficePhone"),
            Gsm = W(r, "MobilePhone"),
            Manager = W(r, "Manager"),
        };
    }

    private static AdGroep MaakAdGroep(Dictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<string>> leden, bool ledenBekend)
    {
        var dn = K(r, "DistinguishedName");
        var ou = K(r, "OU");
        var managedBy = K(r, "ManagedBy");
        return new AdGroep
        {
            Mail = K(r, "Mail"),
            DisplayName = W(r, "DisplayName"),
            ManagedBy = managedBy.Contains('=') ? managedBy : string.Empty,
            Leden = leden.GetValueOrDefault(dn) ?? Array.Empty<string>(),
            LedenBekend = ledenBekend,
            Name = W(r, "Name"),
            SamAccountName = K(r, "SamAccountName"),
            DistinguishedName = dn,
            OuPad = ou.Length > 0 ? DnHulp.OuPad(ou) : DnHulp.OuPadVanObject(dn),
            Category = W(r, "Category"),
            Scope = W(r, "Scope"),
            Description = W(r, "Description"),
            AantalLeden = int.TryParse(K(r, "AantalLeden"), out var n) ? n : null,
        };
    }

    private static EntraGebruiker MaakEntraGebruiker(Dictionary<string, string> r) => new()
    {
        Id = K(r, "Id"),
        UserPrincipalName = W(r, "UserPrincipalName"),
        DisplayName = W(r, "DisplayName"),
        GivenName = W(r, "GivenName"),
        Surname = W(r, "Surname"),
        Mail = W(r, "Mail"),
        MailNickname = W(r, "MailNickname"),
        AccountEnabled = B(r, "AccountEnabled"),
        UserType = W(r, "UserType"),
        Department = W(r, "Department"),
        JobTitle = W(r, "JobTitle"),
        OfficeLocation = W(r, "OfficeLocation"),
        Telefoon = W(r, "BusinessPhone"),
        Gsm = W(r, "MobilePhone"),
        ManagerId = W(r, "ManagerId"),
        CompanyName = W(r, "CompanyName"),
        City = W(r, "City"),
        UsageLocation = W(r, "UsageLocation"),
        CreatedDateTime = W(r, "CreatedDateTime"),
        OnPremisesSyncEnabled = B(r, "OnPremisesSyncEnabled"),
        OnPremisesSamAccountName = K(r, "OnPremisesSamAccountName"),
        OnPremisesDistinguishedName = W(r, "OnPremisesDistinguishedName"),
        Licenties = Lijst(r, "Licenties"),
        LaatsteAanmelding = W(r, "LaatsteAanmelding"),
    };

    private static CloudGroep MaakCloudGroep(Dictionary<string, string> r) => new()
    {
        Id = K(r, "Id"),
        DisplayName = W(r, "DisplayName"),
        Description = W(r, "Description"),
        Mail = K(r, "Mail"),
        OnPremisesSamAccountName = K(r, "OnPremisesSamAccountName"),
        Type = K(r, "Type") switch
        {
            "M365" => CloudGroepType.Microsoft365,
            "Beveiliging" => CloudGroepType.Beveiliging,
            "MailBeveiliging" => CloudGroepType.MailBeveiliging,
            "Distributie" => CloudGroepType.Distributie,
            _ => CloudGroepType.Onbekend,
        },
        UitAd = string.Equals(K(r, "Bron"), "AD", StringComparison.OrdinalIgnoreCase),
        Dynamisch = B(r, "Dynamisch"),
        Lidmaatschapsregel = W(r, "Lidmaatschapsregel"),
        GroepLicentie = B(r, "GroepLicentie"),
        AantalLeden = int.TryParse(K(r, "AantalLeden"), out var n) ? n : null,
    };

    /// <summary>Ruwe waarde, niet getrimd: schrijfvarianten zoals een spatie achteraan moeten zichtbaar blijven.</summary>
    private static Room MaakRoom(Dictionary<string, string> r) => new()
    {
        Smtp = K(r, "Smtp"),
        DisplayName = W(r, "DisplayName"),
        Alias = K(r, "Alias"),
        Gebouw = W(r, "Gebouw"),
        Verdieping = K(r, "Verdieping"),
        VerdiepingLabel = W(r, "VerdiepingLabel"),
        Capaciteit = I(r, "Capaciteit"),
        Verborgen = B(r, "Hidden"),
        AutomateProcessing = K(r, "AutomateProcessing"),
        AllBookInPolicy = B(r, "AllBookInPolicy"),
        AllRequestInPolicy = B(r, "AllRequestInPolicy"),
        BookInPolicy = W(r, "BookInPolicy"),
        ResourceDelegates = W(r, "ResourceDelegates"),
        BookingWindowInDays = I(r, "BookingWindowInDays"),
        MaximumDurationInMinutes = I(r, "MaximumDurationInMinutes"),
        AllowConflicts = B(r, "AllowConflicts"),
        AllowRecurringMeetings = B(r, "AllowRecurringMeetings"),
        AddOrganizerToSubject = B(r, "AddOrganizerToSubject"),
        DeleteSubject = B(r, "DeleteSubject"),
        AgendaRechten = W(r, "AgendaRechten"),
    };

    private static ExoGroep MaakExoGroep(Dictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<ExoLid>> leden) => new()
    {
        Type = K(r, "Type"),
        DisplayName = W(r, "DisplayName").Trim(),
        Name = K(r, "Name"),
        Alias = K(r, "Alias"),
        Smtp = K(r, "PrimarySmtpAddress"),
        IsDirSynced = B(r, "IsDirSynced"),
        MemberJoinRestriction = K(r, "MemberJoinRestriction"),
        ManagedBy = K(r, "ManagedBy") is "ja" or "nee" ? Array.Empty<string>() : Lijst(r, "ManagedBy"),
        AantalLeden = I(r, "AantalLeden"),
        Leden = leden.GetValueOrDefault(W(r, "DisplayName").Trim()) ?? Array.Empty<ExoLid>(),
    };

    private static Postvak MaakPostvak(Dictionary<string, string> r) => new()
    {
        Type = K(r, "Type"),
        DisplayName = W(r, "DisplayName"),
        Alias = K(r, "Alias"),
        Smtp = K(r, "PrimarySmtpAddress"),
        Verborgen = B(r, "Hidden"),
        IsDirSynced = B(r, "IsDirSynced"),
        WhenCreated = W(r, "WhenCreated"),
        Doorsturen = K(r, "Doorsturen") is "geen" ? string.Empty : K(r, "Doorsturen"),
        DoorsturenNaar = K(r, "DoorsturenNaar"),
        DoorsturenEnBewaren = K(r, "DoorsturenEnBewaren").Length > 0 ? B(r, "DoorsturenEnBewaren") : null,
        AutoAntwoord = K(r, "AutoAntwoord"),
        AutoAntwoordIntern = W(r, "AutoAntwoordIntern"),
        AutoAntwoordExtern = W(r, "AutoAntwoordExtern"),
    };

    private static Licentie MaakLicentie(Dictionary<string, string> r)
        => new(K(r, "SkuId"), K(r, "SkuPartNumber"), I(r, "Toegewezen") ?? 0, I(r, "Totaal") ?? 0);

    private static int? I(Dictionary<string, string> r, string kolom) => int.TryParse(K(r, kolom), out var n) ? n : null;

    private static string W(Dictionary<string, string> r, string kolom) => r.TryGetValue(kolom, out var v) ? v : string.Empty;

    /// <summary>Getrimde waarde voor sleutels en technische velden.</summary>
    private static string K(Dictionary<string, string> r, string kolom) => W(r, kolom).Trim();

    private static bool B(Dictionary<string, string> r, string kolom) => string.Equals(K(r, kolom), "True", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Lijst(Dictionary<string, string> r, string kolom)
        => W(r, kolom).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
