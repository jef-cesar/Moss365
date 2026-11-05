using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>Instellingen om live te verbinden; geen wachtwoorden (die vraagt de app per sessie).</summary>
public sealed class VerbindingInstellingen
{
    /// <summary>Domeincontroller (naam of IP), bv. dc01.contoso.internal.</summary>
    public string AdServer { get; set; } = string.Empty;

    /// <summary>AD-account, bv. CONTOSO\naam; leeg = de Windows-aanmelding van de pc (alleen op een domein-pc).</summary>
    public string AdGebruiker { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Application (client) ID van de app-registratie "Moss365".</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Beheeraccount voor Exchange Online (vult de aanmelding vooraf in).</summary>
    public string BeheerUpn { get; set; } = string.Empty;

    /// <summary>Bij het starten zoeken naar een nieuwe versie op GitHub.</summary>
    public bool UpdatesZoeken { get; set; } = true;

    /// <summary>Live laden: enkel wat veranderde sinds de vorige keer (AD en Entra-groepen), als dat kan.</summary>
    public bool SnelLaden { get; set; } = true;

    /// <summary>Elke 15 minuten vernieuwen zolang de app open is (0.8): enkel als je al aangemeld bent, nooit een aanmeldlink.</summary>
    public bool AutoVernieuwen { get; set; }

    public bool AdIngevuld => AdServer.Trim().Length > 0;
    public bool GraphIngevuld => TenantId.Trim().Length > 0 && ClientId.Trim().Length > 0;
}

/// <summary>Wat een live AD-lezing oplevert. Stand = vanaf waar de volgende lezing enkel de wijzigingen kan vragen.</summary>
public sealed record AdGegevens(IReadOnlyList<AdGebruiker> Gebruikers, IReadOnlyList<AdGroep> Groepen, string DomeinDn,
    AdStand? Stand = null, bool Snel = false);

/// <summary>
/// De stand van één domeincontroller: zijn naam (dsServiceName) en het hoogste USN. Wijzigingen tellen per
/// domeincontroller; met een andere domeincontroller leest de app alles opnieuw.
/// </summary>
public sealed record AdStand(string Domeincontroller, long HoogsteUsn);

/// <summary>De vorige AD-lezing, om enkel de wijzigingen te vragen.</summary>
public sealed record AdBasis(AdStand Stand, IReadOnlyList<AdGebruiker> Gebruikers, IReadOnlyList<AdGroep> Groepen);

/// <summary>Wat een live Graph-lezing oplevert. DeltaLink = vanaf waar de volgende lezing enkel de groepswijzigingen vraagt.</summary>
public sealed record GraphGegevens(
    IReadOnlyList<EntraGebruiker> Gebruikers,
    IReadOnlyList<CloudGroep> Groepen,
    IReadOnlyDictionary<string, HashSet<string>> Lidmaatschap,
    IReadOnlyList<Licentie> Licenties,
    string? DeltaLink = null,
    bool Snel = false);

/// <summary>De vorige Graph-lezing van de groepen, om enkel de wijzigingen te vragen.</summary>
public sealed record GraphBasis(string DeltaLink, IReadOnlyDictionary<string, CloudGroep> Groepen, IReadOnlyDictionary<string, IReadOnlySet<string>> Lidmaatschap);

public interface IAdVerbinding
{
    Task<AdGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren);

    /// <summary>Met een basis: enkel wat veranderde (valt zelf terug op alles lezen als dat niet kan).</summary>
    Task<AdGegevens> LeesAsync(AdBasis? basis, IProgress<string>? voortgang, CancellationToken annuleren) => LeesAsync(voortgang, annuleren);

    /// <summary>Eén gebruiker opnieuw lezen op sAMAccountName (blijft geldig als het account verplaatst is); null = bestaat niet.</summary>
    Task<AdGebruiker?> LeesGebruikerAsync(string samAccountName, CancellationToken annuleren);

    /// <summary>Eén groep opnieuw lezen op sAMAccountName, met de leden; null = bestaat niet.</summary>
    Task<AdGroep?> LeesGroepAsync(string samAccountName, CancellationToken annuleren);
}

/// <summary>Eén Entra-gebruiker met de id's van de groepen waarvan hij rechtstreeks lid is.</summary>
public sealed record GraphGebruikerLezing(EntraGebruiker Gebruiker, IReadOnlyList<string> GroepIds);

/// <summary>Eén cloudgroep met de id's van de gebruikers die rechtstreeks lid zijn.</summary>
public sealed record GraphGroepLezing(CloudGroep Groep, IReadOnlyList<string> LedenIds);

/// <summary>Het resultaat van het opnieuw lezen van enkele Exchange-objecten (rooms, postvakken, groepen).</summary>
public sealed record ExchangeObjecten(IReadOnlyList<Room> Rooms, ExchangeGegevens Gegevens, IReadOnlyList<string> NietGevonden);

public interface IGraphVerbinding
{
    Task<GraphGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren);

    /// <summary>Met een basis: de groepen via hun wijzigingen (delta); valt zelf terug op alles lezen als dat niet kan.</summary>
    Task<GraphGegevens> LeesAsync(GraphBasis? basis, IProgress<string>? voortgang, CancellationToken annuleren) => LeesAsync(voortgang, annuleren);

    /// <summary>Het id van de gebruiker met deze UPN, of null als hij (nog) niet bestaat.</summary>
    Task<string?> ZoekGebruikerIdAsync(string upn, CancellationToken annuleren);

    /// <summary>De UPN van de aangemelde beheerder (voor het logboek), of null als nog niet aangemeld.</summary>
    string? AangemeldAls { get; }

    /// <summary>De app-rollen (profielen) uit de aanmelding, bv. "Moss365.Helpdesk"; null als nog niet aangemeld.</summary>
    IReadOnlyList<string>? AppRollen => null;

    /// <summary>Aanmelden (of de aanmelding verlengen), zodat de rollen gekend zijn vóór er iets uitgevoerd wordt.</summary>
    Task AanmeldenAsync(CancellationToken annuleren) => Task.CompletedTask;

    /// <summary>Is er een geldige aanmelding zonder iets te tonen (geen aanmeldlink)? Voor automatisch vernieuwen.</summary>
    Task<bool> StilAangemeldAsync(CancellationToken annuleren) => Task.FromResult(AangemeldAls is not null);

    /// <summary>Eén gebruiker opnieuw lezen (id of UPN), met zijn groepen; null = bestaat (nog) niet.</summary>
    Task<GraphGebruikerLezing?> LeesGebruikerAsync(string idOfUpn, CancellationToken annuleren);

    /// <summary>Eén groep opnieuw lezen, met de leden; null = bestaat niet.</summary>
    Task<GraphGroepLezing?> LeesGroepAsync(string id, CancellationToken annuleren);

    /// <summary>Eigenaars van een cloudgroep (staan niet in de inventaris).</summary>
    Task<IReadOnlyList<GraphEigenaar>> LeesEigenaarsAsync(string groepId, CancellationToken annuleren);

    /// <summary>MFA-registraties en leden van beheerrollen; null waar dat niet kan.</summary>
    Task<BeveiligingGegevens?> LeesBeveiligingAsync(IProgress<string>? voortgang, CancellationToken annuleren)
        => Task.FromResult<BeveiligingGegevens?>(null);

    /// <summary>Aanmeldingen van de laatste dagen (nieuwste eerst), van één gebruiker of (id null) van iedereen.</summary>
    Task<IReadOnlyList<Aanmelding>> LeesAanmeldingenAsync(string? gebruikerId, int dagen, bool alleenMislukt, CancellationToken annuleren)
        => Task.FromResult<IReadOnlyList<Aanmelding>>(Array.Empty<Aanmelding>());

    /// <summary>Aanmeldingen van iedereen over meerdere pagina's (tot max), voor het effect van Conditional Access.</summary>
    Task<IReadOnlyList<Aanmelding>> LeesAanmeldingenAsync(int dagen, int max, CancellationToken annuleren)
        => LeesAanmeldingenAsync(null, dagen, false, annuleren);

    /// <summary>
    /// De agenda van een room (of een ander postvak) tussen van en tot, met terugkerende afspraken als losse items.
    /// Gedelegeerd (Calendars.Read.Shared): je eigen account heeft leesrecht op die agenda nodig. Geen recht: <see cref="GeenRechtFout"/>.
    /// </summary>
    Task<IReadOnlyList<Afspraak>> LeesAgendaAsync(string adres, DateTime van, DateTime tot, CancellationToken annuleren)
        => Task.FromResult<IReadOnlyList<Afspraak>>(Array.Empty<Afspraak>());

    /// <summary>Wie wijzigde Conditional Access de laatste dagen (auditlogboek); geen recht: <see cref="GeenRechtFout"/>.</summary>
    Task<IReadOnlyList<CaAuditRegel>> LeesCaAuditAsync(int dagen, CancellationToken annuleren)
        => Task.FromResult<IReadOnlyList<CaAuditRegel>>(Array.Empty<CaAuditRegel>());

    /// <summary>Conditional Access-beleiden, benoemde locaties, sterktes en rolnamen; null als de verbinding dat niet kan.</summary>
    Task<CaGegevens?> LeesConditionalAccessAsync(IProgress<string>? voortgang, CancellationToken annuleren)
        => Task.FromResult<CaGegevens?>(null);
}

/// <summary>Je account mag dit niet lezen (Graph 401, 403 of 404); het bericht komt van Microsoft.</summary>
public sealed class GeenRechtFout(string bericht) : Exception(bericht);

public sealed record GraphEigenaar(string Id, string Naam, string Upn);

/// <summary>Wat een live Exchange-lezing van groepen en postvakken oplevert.</summary>
public sealed record ExchangeGegevens(IReadOnlyList<ExoGroep> Groepen, IReadOnlyList<Postvak> Postvakken, IReadOnlyList<PostvakRecht> Rechten);

public interface IExchangeVerbinding
{
    Task<IReadOnlyList<Room>> LeesRoomsAsync(IProgress<string>? voortgang, CancellationToken annuleren);

    /// <summary>Gedeelde postvakken (met rechten, doorsturen en automatisch antwoord) en Exchange-groepen met leden.</summary>
    Task<ExchangeGegevens> LeesPostvakkenAsync(IProgress<string>? voortgang, CancellationToken annuleren);

    /// <summary>Enkele rooms, postvakken of groepen opnieuw lezen (op adres), in één Exchange-sessie.</summary>
    Task<ExchangeObjecten> LeesObjectenAsync(IReadOnlyList<string> adressen, CancellationToken annuleren);

    /// <summary>Rooms én postvakken met groepen in één Exchange-sessie (één keer aanmelden). Een fout in het ene deel
    /// houdt het andere niet tegen: die staat dan in <see cref="ExchangeLezing"/>.</summary>
    async Task<ExchangeLezing> LeesAllesAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        IReadOnlyList<Room>? rooms = null;
        ExchangeGegevens? postvakken = null;
        string? roomsFout = null, postvakkenFout = null;
        try
        {
            rooms = await LeesRoomsAsync(voortgang, annuleren);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            roomsFout = ex.Message;
        }

        try
        {
            postvakken = await LeesPostvakkenAsync(voortgang, annuleren);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            postvakkenFout = ex.Message;
        }

        return new ExchangeLezing(rooms, roomsFout, postvakken, postvakkenFout);
    }
}

/// <summary>Rooms en postvakken uit één Exchange-sessie; wat mislukte, is null met de fout ernaast.</summary>
public sealed record ExchangeLezing(IReadOnlyList<Room>? Rooms, string? RoomsFout, ExchangeGegevens? Postvakken, string? PostvakkenFout);

/// <summary>Maakt verbindingen en uitvoerders op basis van de instellingen en het AD-wachtwoord van deze sessie.</summary>
public interface IVerbindingsFabriek
{
    IAdVerbinding? Ad(VerbindingInstellingen instellingen, string? wachtwoord);

    IGraphVerbinding? Graph(VerbindingInstellingen instellingen);

    IExchangeVerbinding? Exchange(VerbindingInstellingen instellingen);

    IReadOnlyList<IUitvoerder> Uitvoerders(VerbindingInstellingen instellingen, string? adWachtwoord);

    /// <summary>Waarom Exchange niet met dezelfde aanmelding als Graph kon, of null.</summary>
    string? ExchangeMelding => null;

    /// <summary>Draait scripts uit de bibliotheek met de aanmelding van de app; null waar dat niet kan.</summary>
    Core.Scripts.IScriptUitvoerder? Scripts(VerbindingInstellingen instellingen, string? adWachtwoord) => null;

    /// <summary>Controle van deze pc (PowerShell, modules, netwerk); null waar dat niet kan.</summary>
    ISysteemControle? Controle => null;

    /// <summary>Zoeken naar een nieuwe versie van de app; null waar dat niet kan.</summary>
    IUpdateControle? Updates => null;
}

public enum ControleStatus
{
    Goed,
    Waarschuwing,
    Fout,
}

/// <summary>
/// Eén controle. Is <see cref="Module"/> ingevuld, dan kan de app de module installeren in de PowerShell van
/// <see cref="PowerShell"/> (het pad naar powershell.exe of pwsh.exe).
/// </summary>
public sealed record ControleRegel(string Groep, string Onderdeel, ControleStatus Status, string Waarde, string Uitleg,
    string? Oplossing = null, string? Module = null, string? PowerShell = null);

public interface ISysteemControle
{
    Task<IReadOnlyList<ControleRegel>> ControleerAsync(VerbindingInstellingen instellingen, IProgress<string>? voortgang, CancellationToken annuleren);

    /// <summary>Installeert een module voor de huidige gebruiker; geeft "Gelukt" of de foutmelding terug.</summary>
    Task<string> InstalleerAsync(string powerShell, string module, CancellationToken annuleren);
}
