using System.Text.Json.Serialization;

namespace Moss365.Core.Wijzigingen;

public enum Systeem
{
    ActiveDirectory,
    Graph,
    Exchange,
}

public enum Actie
{
    // On-prem AD (Doel = DN van de gebruiker, behalve bij aanmaken)
    AdGroepLidToevoegen,
    AdGroepLidVerwijderen,
    AdKenmerkenZetten,
    AdGebruikerAanmaken,
    AdAccountUitschakelen,
    AdVerplaatsen,

    // Microsoft Graph (Doel = id of UPN van de gebruiker)
    GraphGroepLidToevoegen,
    GraphGroepLidVerwijderen,
    GraphKenmerkenZetten,
    GraphLicentieToekennen,
    GraphLicentiesVerwijderen,
    GraphSessiesIntrekken,

    // Exchange Online (Doel = UPN of SMTP-adres)
    ExchangeGroepLidToevoegen,
    ExchangeGroepLidVerwijderen,
    ExchangeRoomAgendaverwerking,
    ExchangeRoomPlace,
    ExchangeAgendaRecht,
    ExchangeNaarGedeeldPostvak,

    // Nieuw in 0.3: altijd achteraan toevoegen (oudere opvolgtaken bewaarden de waarde als getal)
    GraphGroepKenmerkenZetten,      // Doel = groeps-id; parameters displayName, description
    GraphEigenaarToevoegen,         // Doel = groeps-id; parameter lid = id of UPN
    GraphEigenaarVerwijderen,
    ExchangeGroepInstellen,         // Doel = groep; DisplayName, PrimarySmtpAddress
    ExchangeGroepBeheerder,         // Doel = groep; Toevoegen of Verwijderen
    ExchangePostvakInstellen,       // Doel = postvak; DisplayName, verbergen, doorsturen
    ExchangeAutoAntwoord,           // Doel = postvak; AutoReplyState, InternalMessage, ExternalMessage
    ExchangePostvakRechtToevoegen,  // Doel = postvak; Recht (FullAccess/SendAs), Trustee
    ExchangePostvakRechtVerwijderen,
    ExchangeGedeeldPostvakAanmaken, // Doel = adres; Name, DisplayName, Alias
    ExchangeGroepAanmaken,          // Doel = adres; Name, DisplayName, Alias (mail-beveiligingsgroep)

    // Nieuw in 0.4
    GraphGastUitnodigen,            // Doel = e-mailadres; naam, bericht (True/False)
    GraphGastVerwijderen,           // Doel = id; enkel gastaccounts (30 dagen terug te zetten in Entra)

    // Nieuw in 0.5
    AdWachtwoordInstellen,          // Doel = DN; wijzigenBijAanmelding (True/False); het wachtwoord staat in Geheim
    AdAccountOntgrendelen,          // Doel = DN
    AdAccountInschakelen,           // Doel = DN (ongedaan maken van uitschakelen)

    // Nieuw in 0.8
    AdGroepAanmaken,                // Doel = DN van de nieuwe groep; ou, cn, sAMAccountName, groupType, description, managedBy
    GraphGroepAanmaken,             // Doel = alias of adres; displayName, mailNickname, soort (Beveiliging/M365), description, eigenaar, leden

    // Nieuw in 0.8.1
    GraphManagerZetten,             // Doel = gebruikers-id; managerId (leeg = geen manager); Vorige managerId

    // Nieuw in 0.9: Conditional Access beheren (json = wat Graph krijgt; bij wijzigen enkel de velden die veranderen)
    GraphCaBeleidMaken,             // Doel = "nieuw"; json = het volledige beleid
    GraphCaBeleidWijzigen,          // Doel = beleid-id; json = gedeeltelijke JSON; Vorige json = dezelfde velden van voordien
    GraphCaLocatieMaken,            // Doel = "nieuw"; json = de benoemde locatie
    GraphCaLocatieWijzigen,         // Doel = locatie-id; json = de locatie; Vorige json = de locatie van voordien
}

/// <summary>
/// Eén geplande wijziging. Een plan is een lijst wijzigingen die de app eerst toont; de gebruiker kiest wat er
/// uitgevoerd wordt. Parameters zijn tekst (waarden van kenmerken, DN's, id's); geheimen staan apart en worden nooit gelogd.
/// </summary>
public sealed class Wijziging
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Systeem Systeem { get; init; }
    public required Actie Actie { get; init; }

    /// <summary>Technische identiteit van het doelobject (DN, id, UPN of SMTP-adres).</summary>
    public required string Doel { get; set; }

    /// <summary>Leesbare naam van het doelobject voor het scherm en het logboek.</summary>
    public string DoelNaam { get; init; } = string.Empty;

    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Wat er gebeurt, in gewone taal.</summary>
    public required string Omschrijving { get; init; }

    public string? HuidigeWaarde { get; init; }
    public string? NieuweWaarde { get; init; }

    /// <summary>Een waarschuwing die de gebruiker moet lezen voor hij bevestigt.</summary>
    public string? Waarschuwing { get; init; }

    /// <summary>Standaard aangevinkt in het voorbeeld.</summary>
    public bool Geselecteerd { get; set; } = true;

    /// <summary>Deze wijziging wordt overgeslagen als de wijziging met dit id mislukte of niet geselecteerd werd.</summary>
    public Guid? AfhankelijkVan { get; init; }

    /// <summary>
    /// De waarden van de gewijzigde kenmerken vóór de wijziging (komen in het logboek), zodat de wijziging ongedaan gemaakt
    /// kan worden. Nooit voor geheimen.
    /// </summary>
    public Dictionary<string, string>? Vorige { get; init; }

    /// <summary>Geheime waarde (bv. het startwachtwoord). Nooit bewaren of loggen.</summary>
    [JsonIgnore]
    public string? Geheim { get; set; }

    public string P(string sleutel) => Parameters.TryGetValue(sleutel, out var v) ? v : string.Empty;

    public string SysteemTekst => Systeem switch
    {
        Systeem.ActiveDirectory => "AD",
        Systeem.Graph => "Entra (Graph)",
        _ => "Exchange",
    };
}

public enum ResultaatStatus
{
    Gelukt,
    Mislukt,
    Overgeslagen,
}

public sealed record WijzigingResultaat(Guid WijzigingId, ResultaatStatus Status, string Melding)
{
    public static WijzigingResultaat Ok(Wijziging w, string melding = "") => new(w.Id, ResultaatStatus.Gelukt, melding);
    public static WijzigingResultaat Fout(Wijziging w, string melding) => new(w.Id, ResultaatStatus.Mislukt, melding);
    public static WijzigingResultaat Overslaan(Wijziging w, string melding) => new(w.Id, ResultaatStatus.Overgeslagen, melding);
}

/// <summary>Voert wijzigingen uit voor één systeem. Eén aanroep krijgt een opeenvolgende reeks van dat systeem.</summary>
public interface IUitvoerder
{
    Systeem Systeem { get; }

    Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren);
}
