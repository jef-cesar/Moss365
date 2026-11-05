using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Inventaris;

/// <summary>Staat van een Conditional Access-beleid.</summary>
public enum CaStatus
{
    Aan,
    Uit,

    /// <summary>Report-only: Entra meet wat het beleid zou doen, maar dwingt niets af.</summary>
    Rapport,
}

/// <summary>
/// Eén Conditional Access-beleid, vertaald uit Graph. Id's van gebruikers, groepen en rollen blijven id's; de schermen
/// zoeken de namen op. Speciale waarden zoals in Graph: "All", "None", "GuestsOrExternalUsers", "Office365",
/// "MicrosoftAdminPortals", "AllTrusted".
/// </summary>
public sealed record CaBeleid
{
    public string Id { get; init; } = string.Empty;
    public string Naam { get; init; } = string.Empty;
    public CaStatus Status { get; init; }
    public DateTimeOffset? Aangemaakt { get; init; }
    public DateTimeOffset? Gewijzigd { get; init; }

    // Wie
    public IReadOnlyList<string> GebruikersIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GebruikersUit { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GroepenIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GroepenUit { get; init; } = Array.Empty<string>();

    /// <summary>Rollen als roleTemplateId (zoals in Graph).</summary>
    public IReadOnlyList<string> RollenIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RollenUit { get; init; } = Array.Empty<string>();

    public bool GastenIn { get; init; }
    public bool GastenUit { get; init; }

    // Wat
    public IReadOnlyList<string> AppsIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AppsUit { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Gebruikersacties { get; init; } = Array.Empty<string>();

    // Voorwaarden
    public IReadOnlyList<string> ClientApps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PlatformsIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PlatformsUit { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LocatiesIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LocatiesUit { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AanmeldRisico { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GebruikersRisico { get; init; } = Array.Empty<string>();
    public bool Toestelfilter { get; init; }

    /// <summary>Overdrachtsmethoden, bv. "deviceCodeFlow", "authenticationTransfer".</summary>
    public IReadOnlyList<string> Overdracht { get; init; } = Array.Empty<string>();

    // Gevolg
    /// <summary>"OR" (één van) of "AND" (allemaal).</summary>
    public string Operator { get; init; } = "OR";

    /// <summary>Ingebouwde controles: block, mfa, compliantDevice, domainJoinedDevice, approvedApplication, compliantApplication, passwordChange.</summary>
    public IReadOnlyList<string> Controles { get; init; } = Array.Empty<string>();

    public string? SterkteId { get; init; }
    public string? SterkteNaam { get; init; }
    public bool Gebruiksvoorwaarden { get; init; }

    // Sessie
    public string? AanmeldFrequentie { get; init; }
    public string? BlijvendeBrowser { get; init; }

    /// <summary>Het beleid zoals Graph het gaf (voor back-up, vergelijken en terugzetten); leeg als het niet uit Graph komt.</summary>
    public string Json { get; init; } = string.Empty;

    [JsonIgnore]
    public bool Blokkeert => Controles.Contains("block", StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool VraagtMfa => Controles.Contains("mfa", StringComparer.OrdinalIgnoreCase) || SterkteId is not null;

    /// <summary>Phishingbestendige MFA (de ingebouwde sterkte van Microsoft).</summary>
    [JsonIgnore]
    public bool VraagtPhishingbestendig => SterkteId == CaGegevens.SterktePhishingbestendig;
}

/// <summary>Een benoemde locatie: IP-bereiken of landen.</summary>
public sealed record BenoemdeLocatie(string Id, string Naam, bool IsIp, bool Vertrouwd, IReadOnlyList<string> Waarden, bool OnbekendeLanden = false)
{
    /// <summary>De locatie zoals Graph ze gaf; leeg als ze niet uit Graph komt.</summary>
    public string Json { get; init; } = string.Empty;

    [JsonIgnore]
    public bool BevatBelgie => !IsIp && Waarden.Contains("BE", StringComparer.OrdinalIgnoreCase);
}

/// <summary>Een authenticatiesterkte (welke combinaties van methodes volstaan).</summary>
public sealed record Authenticatiesterkte(string Id, string Naam, bool Ingebouwd, IReadOnlyList<string> Combinaties);

/// <summary>
/// Alles over Conditional Access in de tenant. Een melding = (deels) niet leesbaar. GroepLeden: voor elke groep die in
/// een beleid staat, alle gebruikers die er (ook via een groep in de groep) lid van zijn; null = niet gelezen.
/// </summary>
public sealed record CaGegevens(
    IReadOnlyList<CaBeleid> Beleiden,
    IReadOnlyList<BenoemdeLocatie> Locaties,
    IReadOnlyList<Authenticatiesterkte> Sterktes,
    IReadOnlyDictionary<string, string> Rollen,
    bool? Beveiligingsstandaarden,
    string? Melding = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GroepLeden = null)
{
    public const string RolGlobalAdmin = "62e90394-69f5-4237-9190-012177145e10";
    public const string SterktePhishingbestendig = "00000000-0000-0000-0000-000000000004";

    public string RolNaam(string templateId) => Rollen.TryGetValue(templateId, out var n) ? n : templateId;

    public BenoemdeLocatie? Locatie(string id) => Locaties.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Eén regel uit het auditlogboek van Entra over Conditional Access: wie wijzigde welk beleid.</summary>
public sealed record CaAuditRegel(DateTimeOffset Tijd, string Wie, string Activiteit, string DoelId, string DoelNaam, string Resultaat);

/// <summary>Hoe een beleid bij één aanmelding uitviel (appliedConditionalAccessPolicies).</summary>
public sealed record CaToepassing(string BeleidId, string Naam, string Resultaat);

/// <summary>Bewaart Conditional Access als JSON in de inventarismap (entra-ca.json).</summary>
public static class CaOpslag
{
    private static readonly JsonSerializerOptions Opties = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string NaarJson(CaGegevens ca) => JsonSerializer.Serialize(ca, Opties);

    /// <summary>Null als het bestand leeg of kapot is: dan is Conditional Access gewoon "niet geladen".</summary>
    public static CaGegevens? UitJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CaGegevens>(json, Opties);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
