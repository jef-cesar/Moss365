namespace Moss365.Core.Inventaris;

/// <summary>Een groep zoals Exchange Online hem kent: distributielijst, mail-beveiligingsgroep of room list.</summary>
public sealed record ExoGroep
{
    public string Type { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Interne naam in Exchange; zo verschijnt de groep soms als rechthebbende.</summary>
    public string Name { get; init; } = string.Empty;

    public string Alias { get; init; } = string.Empty;
    public string Smtp { get; init; } = string.Empty;
    public bool IsDirSynced { get; init; }
    public string MemberJoinRestriction { get; init; } = string.Empty;
    public IReadOnlyList<string> ManagedBy { get; init; } = Array.Empty<string>();
    public int? AantalLeden { get; init; }
    public IReadOnlyList<ExoLid> Leden { get; init; } = Array.Empty<ExoLid>();

    public string TypeTekst => Type switch
    {
        "MailUniversalSecurityGroup" => "Mail-beveiligingsgroep",
        "MailUniversalDistributionGroup" => "Distributielijst",
        "RoomList" => "Room list",
        _ => Type,
    };
}

/// <summary>Een lid van een Exchange-groep: adres en weergavenaam.</summary>
public sealed record ExoLid(string Smtp, string Naam);

/// <summary>Een postvak dat geen gebruikerspostvak is: gedeeld, planning (Bookings) of discovery.</summary>
public sealed record Postvak
{
    public string Type { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string Smtp { get; init; } = string.Empty;
    public bool Verborgen { get; init; }
    public bool IsDirSynced { get; init; }
    public string WhenCreated { get; init; } = string.Empty;

    /// <summary>"intern", "extern" of leeg (inventaris); live het doorstuuradres zelf.</summary>
    public string Doorsturen { get; init; } = string.Empty;

    public string DoorsturenNaar { get; init; } = string.Empty;
    public bool? DoorsturenEnBewaren { get; init; }

    /// <summary>Enabled, Disabled of Scheduled; leeg als niet ingelezen (alleen live).</summary>
    public string AutoAntwoord { get; init; } = string.Empty;

    public string AutoAntwoordIntern { get; init; } = string.Empty;
    public string AutoAntwoordExtern { get; init; } = string.Empty;

    public bool IsGedeeld => string.Equals(Type, "SharedMailbox", StringComparison.OrdinalIgnoreCase);

    public string TypeTekst => Type switch
    {
        "SharedMailbox" => "Gedeeld postvak",
        "SchedulingMailbox" => "Planning (Bookings)",
        "DiscoveryMailbox" => "Discovery",
        "EquipmentMailbox" => "Apparatuur",
        _ => Type,
    };

    /// <summary>Het deel van het adres voor de @; de toegangsgroep heet SG-SM-&lt;dit deel&gt;-Full.</summary>
    public string LokaalDeel => Smtp.Split('@')[0];
}

/// <summary>Een recht op een gedeeld postvak. Trustee is zoals Exchange hem teruggeeft (adres, naam of alias).</summary>
public sealed record PostvakRecht(string Postvak, string Recht, string Trustee);
