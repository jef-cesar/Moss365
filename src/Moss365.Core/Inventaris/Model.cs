namespace Moss365.Core.Inventaris;

public sealed record AdGebruiker
{
    public string SamAccountName { get; init; } = string.Empty;
    public string UserPrincipalName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string GivenName { get; init; } = string.Empty;
    public string Surname { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string Office { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string EmailAddress { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string DistinguishedName { get; init; } = string.Empty;

    /// <summary>OU-pad van de container waarin het account staat, bv. "GEMEENTEBESTUUR/GEBRUIKERS".</summary>
    public string OuPad { get; init; } = string.Empty;

    public string WhenCreated { get; init; } = string.Empty;
    public string LastLogonDate { get; init; } = string.Empty;
    public string PasswordLastSet { get; init; } = string.Empty;
    public bool PasswordNeverExpires { get; init; }

    /// <summary>Vergrendeld na te veel foute wachtwoorden.</summary>
    public bool LockedOut { get; init; }

    public string ScriptPath { get; init; } = string.Empty;
    public IReadOnlyList<string> MemberOf { get; init; } = Array.Empty<string>();

    /// <summary>telephoneNumber (0.8.1).</summary>
    public string Telefoon { get; init; } = string.Empty;

    /// <summary>mobile (0.8.1).</summary>
    public string Gsm { get; init; } = string.Empty;

    /// <summary>DN van de manager (0.8.1).</summary>
    public string Manager { get; init; } = string.Empty;
}

public sealed record EntraGebruiker
{
    public string Id { get; init; } = string.Empty;
    public string UserPrincipalName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string GivenName { get; init; } = string.Empty;
    public string Surname { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;
    public string MailNickname { get; init; } = string.Empty;
    public bool AccountEnabled { get; init; }
    public string UserType { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public string OfficeLocation { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string UsageLocation { get; init; } = string.Empty;
    public string CreatedDateTime { get; init; } = string.Empty;

    /// <summary>Het eerste nummer van businessPhones (0.8.1).</summary>
    public string Telefoon { get; init; } = string.Empty;

    /// <summary>mobilePhone (0.8.1).</summary>
    public string Gsm { get; init; } = string.Empty;

    /// <summary>Object-ID van de manager (0.8.1); leeg als onbekend of geen.</summary>
    public string ManagerId { get; init; } = string.Empty;
    public bool OnPremisesSyncEnabled { get; init; }
    public string OnPremisesSamAccountName { get; init; } = string.Empty;
    public string OnPremisesDistinguishedName { get; init; } = string.Empty;
    public IReadOnlyList<string> Licenties { get; init; } = Array.Empty<string>();
    public string LaatsteAanmelding { get; init; } = string.Empty;

    public bool IsGast => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase)
                          || UserPrincipalName.Contains("#EXT#", StringComparison.OrdinalIgnoreCase);
}

public sealed record AdGroep
{
    public string Name { get; init; } = string.Empty;
    public string SamAccountName { get; init; } = string.Empty;
    public string DistinguishedName { get; init; } = string.Empty;
    public string OuPad { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;

    /// <summary>DN van de beheerder (managedBy), leeg als er geen is.</summary>
    public string ManagedBy { get; init; } = string.Empty;

    public int? AantalLeden { get; init; }

    /// <summary>DN's van de rechtstreekse leden (gebruikers, groepen, computers).</summary>
    public IReadOnlyList<string> Leden { get; init; } = Array.Empty<string>();

    /// <summary>False als de ledenlijst niet ingelezen is (dan leidt de app de gebruikers af uit memberOf).</summary>
    public bool LedenBekend { get; init; }
}

public enum CloudGroepType
{
    Microsoft365,
    Beveiliging,
    MailBeveiliging,
    Distributie,
    Onbekend,
}

public sealed record CloudGroep
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;

    /// <summary>sAMAccountName van de AD-groep als de groep uit AD gesynchroniseerd wordt.</summary>
    public string OnPremisesSamAccountName { get; init; } = string.Empty;

    public CloudGroepType Type { get; init; }

    /// <summary>True als de groep uit on-prem AD gesynchroniseerd wordt.</summary>
    public bool UitAd { get; init; }

    public bool Dynamisch { get; init; }
    public string Lidmaatschapsregel { get; init; } = string.Empty;
    public bool GroepLicentie { get; init; }
    public int? AantalLeden { get; init; }

    public string TypeTekst => Type switch
    {
        CloudGroepType.Microsoft365 => "Microsoft 365-groep",
        CloudGroepType.Beveiliging => "Beveiligingsgroep",
        CloudGroepType.MailBeveiliging => "Mail-beveiligingsgroep",
        CloudGroepType.Distributie => "Distributielijst",
        _ => "Onbekend",
    };
}

/// <summary>Een vergaderruimte (room mailbox) met de instellingen die de app beheert.</summary>
public sealed record Room
{
    public string Smtp { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string Gebouw { get; init; } = string.Empty;
    public string Verdieping { get; init; } = string.Empty;
    public string VerdiepingLabel { get; init; } = string.Empty;
    public int? Capaciteit { get; init; }
    public bool Verborgen { get; init; }
    public string AutomateProcessing { get; init; } = string.Empty;
    public bool AllBookInPolicy { get; init; }
    public bool AllRequestInPolicy { get; init; }
    public string BookInPolicy { get; init; } = string.Empty;
    public string ResourceDelegates { get; init; } = string.Empty;
    public int? BookingWindowInDays { get; init; }
    public int? MaximumDurationInMinutes { get; init; }
    public bool AllowConflicts { get; init; }
    public bool AllowRecurringMeetings { get; init; }
    public bool AddOrganizerToSubject { get; init; }
    public bool DeleteSubject { get; init; }

    /// <summary>Alle agendarechten als tekst, bv. "Default=Reviewer|Anonymous=None|(2 anderen)".</summary>
    public string AgendaRechten { get; init; } = string.Empty;

    /// <summary>Het recht van de gebruiker Default (iedereen in de organisatie) op de agenda.</summary>
    public string StandaardAgendaRecht
    {
        get
        {
            foreach (var deel in AgendaRechten.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var is_ = deel.IndexOf('=');
                if (is_ > 0 && (deel[..is_].Equals("Default", StringComparison.OrdinalIgnoreCase) || deel[..is_].Equals("Standaard", StringComparison.OrdinalIgnoreCase)))
                {
                    return deel[(is_ + 1)..];
                }
            }

            return string.Empty;
        }
    }
}

/// <summary>Een licentie (SKU) van de tenant met het aantal toegewezen en beschikbare eenheden.</summary>
public sealed record Licentie(string SkuId, string SkuPartNumber, int Toegewezen, int Totaal)
{
    public int Vrij => Math.Max(0, Totaal - Toegewezen);
}

/// <summary>Een persoon of account: de koppeling van een AD-account en een Entra-account (een van beide kan ontbreken).</summary>
public sealed class Persoon
{
    public required string Sleutel { get; init; }
    public AdGebruiker? Ad { get; init; }
    public EntraGebruiker? Entra { get; init; }

    public string Naam => FirstNonEmpty(Entra?.DisplayName, Ad?.DisplayName, Ad?.SamAccountName, Entra?.UserPrincipalName);
    public string Upn => FirstNonEmpty(Ad?.UserPrincipalName, Entra?.UserPrincipalName);
    public string Dienst => FirstNonEmpty(Ad?.Department, Entra?.Department).Trim();
    public bool IsGast => Entra?.IsGast == true && Ad is null;
    public bool Ingeschakeld => Ad?.Enabled ?? Entra?.AccountEnabled ?? false;

    public string Bron => (Ad, Entra) switch
    {
        (not null, not null) => "AD + Entra",
        (not null, null) => "Alleen AD",
        (null, not null) => Entra.IsGast ? "Gast" : "Alleen Entra",
        _ => "?",
    };

    private static string FirstNonEmpty(params string?[] waarden)
        => waarden.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w)) ?? string.Empty;
}

/// <summary>Waar elk bronbestand vandaan kwam (de app kiest per bestand de nieuwste inventarismap).</summary>
public sealed record BronBestand(string Bestand, string? Map, int Rijen);

public sealed record InventarisData
{
    public IReadOnlyList<AdGebruiker> AdGebruikers { get; init; } = Array.Empty<AdGebruiker>();
    public IReadOnlyList<EntraGebruiker> EntraGebruikers { get; init; } = Array.Empty<EntraGebruiker>();
    public IReadOnlyDictionary<string, AdGroep> AdGroepenOpDn { get; init; } = new Dictionary<string, AdGroep>();
    public IReadOnlyDictionary<string, CloudGroep> CloudGroepen { get; init; } = new Dictionary<string, CloudGroep>();

    /// <summary>Entra-gebruikers-id naar de id's van de cloudgroepen waarvan hij rechtstreeks lid is.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> CloudLidmaatschap { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    public IReadOnlyList<Persoon> Personen { get; init; } = Array.Empty<Persoon>();
    public IReadOnlyList<Room> Rooms { get; init; } = Array.Empty<Room>();
    public IReadOnlyList<Licentie> Licenties { get; init; } = Array.Empty<Licentie>();
    public IReadOnlyList<BronBestand> Bronnen { get; init; } = Array.Empty<BronBestand>();

    /// <summary>Distributielijsten, mail-beveiligingsgroepen en room lists uit Exchange Online, met hun leden.</summary>
    public IReadOnlyList<ExoGroep> ExoGroepen { get; init; } = Array.Empty<ExoGroep>();

    /// <summary>Postvakken die geen gebruikerspostvak zijn (gedeeld, planning, ...), zonder de rooms.</summary>
    public IReadOnlyList<Postvak> Postvakken { get; init; } = Array.Empty<Postvak>();

    /// <summary>FullAccess- en SendAs-rechten op gedeelde postvakken.</summary>
    public IReadOnlyList<PostvakRecht> PostvakRechten { get; init; } = Array.Empty<PostvakRecht>();

    /// <summary>Leden van cloudgroepen die geen gebruiker zijn (apparaten, contactpersonen): id naar (soort, naam).</summary>
    public IReadOnlyDictionary<string, (string Soort, string Naam)> CloudLidNamen { get; init; } = new Dictionary<string, (string, string)>();

    // Per instantie (ook na "with") een eigen index; een veld zou bij "with" mee gekopieerd worden
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InventarisData, ObjectIndex> Indexen = new();

    /// <summary>Opzoeken en relaties over AD, Entra en Exchange heen; wordt bij het eerste gebruik opgebouwd.</summary>
    public ObjectIndex Index => Indexen.GetValue(this, d => new ObjectIndex(d));

    /// <summary>MFA-registraties en beheerrollen uit Entra; null als ze niet geladen zijn.</summary>
    public BeveiligingGegevens? Beveiliging { get; init; }

    /// <summary>Conditional Access-beleiden, benoemde locaties en sterktes uit Entra; null als ze niet geladen zijn.</summary>
    public CaGegevens? ConditionalAccess { get; init; }

    /// <summary>"Inventaris" of "Live" (rechtstreeks uit AD, Graph en Exchange).</summary>
    public string Herkomst { get; init; } = "Inventaris";

    /// <summary>DN van het AD-domein als die bekend is (live), anders leeg.</summary>
    public string DomeinDn { get; init; } = string.Empty;

    /// <summary>Zoekt de AD-groep met deze sAMAccountName (voor gesynchroniseerde cloudgroepen).</summary>
    public AdGroep? ZoekAdGroepOpSam(string sam)
        => AdGroepenOpDn.Values.FirstOrDefault(g => string.Equals(g.SamAccountName, sam, StringComparison.OrdinalIgnoreCase));

    public AdGroep? ZoekAdGroepOpNaam(string naam)
        => AdGroepenOpDn.Values.FirstOrDefault(g => string.Equals(g.Name, naam, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(g.SamAccountName, naam, StringComparison.OrdinalIgnoreCase));

    public CloudGroep? ZoekCloudGroepOpNaam(string naam)
        => CloudGroepen.Values.FirstOrDefault(g => string.Equals(g.DisplayName, naam, StringComparison.OrdinalIgnoreCase));

    public bool HeeftAd => AdGebruikers.Count > 0;
    public bool HeeftEntra => EntraGebruikers.Count > 0;

    public Persoon? ZoekPersoon(string sleutel)
        => Personen.FirstOrDefault(p => string.Equals(p.Sleutel, sleutel, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<AdGroep> AdGroepenVan(Persoon persoon)
    {
        if (persoon.Ad is null)
        {
            yield break;
        }

        foreach (var dn in persoon.Ad.MemberOf)
        {
            if (AdGroepenOpDn.TryGetValue(dn, out var groep))
            {
                yield return groep;
            }
            else
            {
                yield return new AdGroep { Name = DnHulp.Naam(dn), DistinguishedName = dn, OuPad = DnHulp.OuPadVanObject(dn) };
            }
        }
    }

    public IEnumerable<CloudGroep> CloudGroepenVan(Persoon persoon)
    {
        if (persoon.Entra is null || !CloudLidmaatschap.TryGetValue(persoon.Entra.Id, out var ids))
        {
            yield break;
        }

        foreach (var id in ids)
        {
            if (CloudGroepen.TryGetValue(id, out var groep))
            {
                yield return groep;
            }
        }
    }
}
