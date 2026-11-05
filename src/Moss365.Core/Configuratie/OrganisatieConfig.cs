using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Configuratie;

/// <summary>Keuzelijsten en vaste regels van de organisatie (config/organisatie.json).</summary>
public sealed class OrganisatieConfig
{
    [JsonPropertyName("_uitleg")]
    public string? Uitleg { get; set; }

    public string Company { get; set; } = "Contoso";

    /// <summary>DNS-naam van het AD-domein, bv. CONTOSO.LOCAL; daaruit volgt de DN (DC=CONTOSO,DC=INTERNAL).</summary>
    public string? AdDomein { get; set; }

    public List<string> GesynchroniseerdeOus { get; set; } = new();
    public List<Populatie> Populaties { get; set; } = new();
    public List<Afdeling> Afdelingen { get; set; } = new();
    public List<Dienst> Diensten { get; set; } = new();
    public List<string> Kantoren { get; set; } = new();
    public List<DynamischeRegel> DynamischeRegels { get; set; } = new();
    public VrijwilligersRegel? Vrijwilligers { get; set; }
    public UitstroomRegel? Uitstroom { get; set; }
    public GedeeldePostvakkenRegel? GedeeldePostvakken { get; set; }
    public List<string> Logonscripts { get; set; } = new();
    public List<LicentieKeuze> Licenties { get; set; } = new();
    public RoomInstellingen Rooms { get; set; } = new();

    /// <summary>
    /// UPN's van de noodaccounts (break glass): cloud-only Global Administrators die buiten alle Conditional
    /// Access-beleiden blijven, zodat niemand zich kan buitensluiten.
    /// </summary>
    public List<string> Noodaccounts { get; set; } = new();

    /// <summary>
    /// Profielen verplicht (0.8): wie zich aanmeldt zonder app-rol (Moss365.Lezer, ...) mag enkel lezen. Uit (standaard,
    /// de overgang): zonder rol mag iedereen alles, met een melding.
    /// </summary>
    public bool ProfielenVerplicht { get; set; }

    /// <summary>Hoe lang het logboek in de databank blijft (keuze K4, af te stemmen met de DPO); daarna archiveren.</summary>
    public int LogboekBewaarJaren { get; set; } = 2;

    /// <summary>OU voor nieuwe AD-groepen, als pad (bv. GEMEENTEBESTUUR/GROEPEN).</summary>
    public string? GroepenOu { get; set; }

    /// <summary>Voorvoegsels volgens de naamgeving van groepen (gg_, AFD_, DNST_, SG-, WKG_); leeg = die standaardlijst.</summary>
    public List<string> GroepVoorvoegsels { get; set; } = new();

    /// <summary>DN van het domein, afgeleid uit AdDomein; leeg als dat niet ingevuld is.</summary>
    [JsonIgnore]
    public string DomeinDn => string.IsNullOrWhiteSpace(AdDomein)
        ? string.Empty
        : string.Join(',', AdDomein.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(d => "DC=" + d));

    /// <summary>Zet een OU-pad ("GEMEENTEBESTUUR/GEBRUIKERS") om naar een DN binnen het domein.</summary>
    public string OuDn(string ouPad, string? domeinDn = null)
    {
        var domein = string.IsNullOrWhiteSpace(domeinDn) ? DomeinDn : domeinDn;
        var ous = Enumerable.Reverse(ouPad.Split('/', StringSplitOptions.RemoveEmptyEntries)).Select(o => "OU=" + o.Replace(",", "\\,"));
        return string.Join(',', ous.Append(domein).Where(s => s.Length > 0));
    }

    public Populatie? ZoekPopulatie(string? naam)
        => Populaties.FirstOrDefault(p => string.Equals(p.Naam, naam, StringComparison.OrdinalIgnoreCase));

    /// <summary>De dienst met deze waarde (spaties en hoofdletters tellen niet mee), of null.</summary>
    public Dienst? ZoekDienst(string? department)
    {
        var sleutel = Normaliseer(department);
        return sleutel.Length == 0 ? null : Diensten.FirstOrDefault(d => Normaliseer(d.Department) == sleutel);
    }

    public Afdeling? AfdelingVoor(string? department)
    {
        var dienst = ZoekDienst(department);
        return dienst?.Afdeling is null
            ? null
            : Afdelingen.FirstOrDefault(a => string.Equals(a.Naam, dienst.Afdeling, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsDynamischeGroep(string groepsnaam)
        => DynamischeRegels.Any(r => string.Equals(r.Groep, groepsnaam, StringComparison.OrdinalIgnoreCase));

    public static string Normaliseer(string? waarde) => (waarde ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed class Afdeling
{
    public string Naam { get; set; } = string.Empty;
    public string? Groep { get; set; }
}

public sealed class Dienst
{
    public string Department { get; set; } = string.Empty;
    public string? Afdeling { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Logonscript { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Opmerking { get; set; }
}

/// <summary>Een soort gebruiker (gemeente, OCMW, vrijwilliger) met zijn OU, UPN-domein en standaardwaarden.</summary>
public sealed class Populatie
{
    public string Naam { get; set; } = string.Empty;
    public string Ou { get; set; } = string.Empty;
    public string UpnDomein { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    public bool GeenLicentie { get; set; }
    public List<string> StandaardAdGroepen { get; set; } = new();
}

public sealed class LicentieKeuze
{
    public string Sku { get; set; } = string.Empty;
    public string Naam { get; set; } = string.Empty;
}

public sealed class RoomInstellingen
{
    public RoomStandaard Standaard { get; set; } = new();
}

/// <summary>De gewenste instellingen voor een vergaderruimte; afwijkingen toont de app.</summary>
public sealed class RoomStandaard
{
    public string AutomateProcessing { get; set; } = "AutoAccept";
    public int BookingWindowInDays { get; set; } = 180;
    public int MaximumDurationInMinutes { get; set; } = 1440;
    public bool AllowConflicts { get; set; }
    public bool AllowRecurringMeetings { get; set; } = true;
    public bool AddOrganizerToSubject { get; set; } = true;
    public bool DeleteSubject { get; set; }
    public string AgendaRechtStandaard { get; set; } = "Reviewer";
}

public sealed class DynamischeRegel
{
    public string Groep { get; set; } = string.Empty;
    public string Regel { get; set; } = string.Empty;
}

public sealed class VrijwilligersRegel
{
    public string? Ou { get; set; }
    public string? UpnDomein { get; set; }
    public string? City { get; set; }
}

public sealed class UitstroomRegel
{
    public string? Ou { get; set; }
    public bool Verwijderen { get; set; }
}

public sealed class GedeeldePostvakkenRegel
{
    public string? Groep { get; set; }
    public List<string> Rechten { get; set; } = new();
}

/// <summary>
/// Leest en bewaart de configuratie. De meegeleverde versie (standaard) wordt nooit overschreven; wijzigingen van de
/// gebruiker gaan naar een eigen bestand dat daarna voorrang krijgt.
/// </summary>
public static class ConfigOpslag
{
    public static readonly JsonSerializerOptions Opties = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Laadt het gebruikersbestand als het bestaat, anders het standaardbestand, anders een lege configuratie.</summary>
    public static (OrganisatieConfig Config, string? Bron) Laad(string? standaardPad, string? gebruikersPad)
    {
        foreach (var pad in new[] { gebruikersPad, standaardPad })
        {
            if (!string.IsNullOrWhiteSpace(pad) && File.Exists(pad))
            {
                var config = JsonSerializer.Deserialize<OrganisatieConfig>(File.ReadAllText(pad), Opties) ?? new OrganisatieConfig();
                return (config, pad);
            }
        }

        return (new OrganisatieConfig(), null);
    }

    /// <summary>
    /// Zoals <see cref="Laad"/>, maar een kapot of leeg eigen bestand valt terug op het standaardbestand, met een
    /// waarschuwing die de app toont (zonder soorten of diensten kan je bv. geen nieuwe gebruiker maken).
    /// </summary>
    public static (OrganisatieConfig Config, string? Bron, string? Waarschuwing) LaadVeilig(string? standaardPad, string? gebruikersPad)
    {
        OrganisatieConfig? standaard = null;
        string? standaardFout = null;
        if (!string.IsNullOrWhiteSpace(standaardPad) && File.Exists(standaardPad))
        {
            try
            {
                standaard = JsonSerializer.Deserialize<OrganisatieConfig>(File.ReadAllText(standaardPad), Opties);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                standaardFout = ex.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(gebruikersPad) && File.Exists(gebruikersPad))
        {
            try
            {
                var eigen = JsonSerializer.Deserialize<OrganisatieConfig>(File.ReadAllText(gebruikersPad), Opties) ?? new OrganisatieConfig();
                if (eigen.Populaties.Count == 0 && standaard is { Populaties.Count: > 0 })
                {
                    return (standaard, standaardPad,
                        $"Je eigen organisatie ({gebruikersPad}) heeft geen soorten (gemeente, OCMW, ...); de app gebruikt de meegeleverde. Herstel of pas ze aan in het scherm Organisatie.");
                }

                return (eigen, gebruikersPad, null);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                if (standaard is not null)
                {
                    return (standaard, standaardPad, $"Je eigen organisatie ({gebruikersPad}) is niet leesbaar ({ex.Message}); de app gebruikt de meegeleverde.");
                }

                return (new OrganisatieConfig(), null, $"Organisatie niet leesbaar: {gebruikersPad} ({ex.Message}).");
            }
        }

        if (standaard is not null)
        {
            return (standaard, standaardPad, null);
        }

        return (new OrganisatieConfig(), null, standaardFout is not null
            ? $"De meegeleverde organisatie ({standaardPad}) is niet leesbaar: {standaardFout}"
            : $"Geen organisatie gevonden (verwacht: {standaardPad}). Pak de volledige zip uit en start Moss365.exe uit die map (niet rechtstreeks vanuit de zip).");
    }

    public static void Bewaar(OrganisatieConfig config, string pad)
    {
        var map = Path.GetDirectoryName(pad);
        if (!string.IsNullOrEmpty(map))
        {
            Directory.CreateDirectory(map);
        }

        // Eerst naar een tijdelijk bestand, dan vervangen: een onderbroken schrijfactie laat zo geen half bestand achter
        var tijdelijk = pad + ".tmp";
        File.WriteAllText(tijdelijk, Naar(config));
        File.Move(tijdelijk, pad, overwrite: true);
    }

    public static string Naar(OrganisatieConfig config) => JsonSerializer.Serialize(config, Opties);
}
