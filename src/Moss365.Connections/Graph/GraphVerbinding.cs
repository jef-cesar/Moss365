using System.Globalization;
using System.Net;
using System.Text.Json;
using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Connections.Graph;

/// <summary>Leest Entra ID via Microsoft Graph en voert Graph-wijzigingen uit, met de aanmelding van de beheerder.</summary>
public sealed class GraphVerbinding : IGraphVerbinding, IUitvoerder
{
    private const string GebruikerVelden = "id,userPrincipalName,displayName,givenName,surname,mail,mailNickname,accountEnabled,userType,department,"
                                           + "jobTitle,officeLocation,companyName,city,usageLocation,createdDateTime,onPremisesSyncEnabled,"
                                           + "onPremisesSamAccountName,onPremisesDistinguishedName,assignedLicenses,businessPhones,mobilePhone";

    private const string GroepVelden = "id,displayName,description,mail,mailEnabled,securityEnabled,groupTypes,membershipRule,"
                                       + "onPremisesSyncEnabled,onPremisesSamAccountName,assignedLicenses";

    private readonly GraphKlant _klant;
    private readonly Func<string?> _aangemeldAls;
    private readonly Func<IReadOnlyList<string>?> _appRollen;
    private readonly Func<CancellationToken, Task>? _aanmelden;
    private readonly Func<CancellationToken, Task<bool>>? _stil;

    public GraphVerbinding(GraphKlant klant, Func<string?>? aangemeldAls = null, Func<IReadOnlyList<string>?>? appRollen = null,
        Func<CancellationToken, Task>? aanmelden = null, Func<CancellationToken, Task<bool>>? stil = null)
    {
        _klant = klant;
        _aangemeldAls = aangemeldAls ?? (() => null);
        _appRollen = appRollen ?? (() => null);
        _aanmelden = aanmelden;
        _stil = stil;
    }

    public Task<bool> StilAangemeldAsync(CancellationToken annuleren) => _stil?.Invoke(annuleren) ?? Task.FromResult(false);

    public IReadOnlyList<string>? AppRollen => _appRollen();

    public Task AanmeldenAsync(CancellationToken annuleren) => _aanmelden?.Invoke(annuleren) ?? Task.CompletedTask;

    public Systeem Systeem => Systeem.Graph;

    public string? AangemeldAls => _aangemeldAls();

    // ------------------------------------------------------------------ lezen

    public Task<GraphGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren) => LeesAsync(null, voortgang, annuleren);

    // Kenmerken die /groups/delta meegeeft (assignedLicenses niet: dat blijft van de vorige lezing)
    private const string DeltaVelden = "displayName,description,mail,mailEnabled,securityEnabled,groupTypes,membershipRule,"
                                       + "onPremisesSyncEnabled,onPremisesSamAccountName,members";

    /// <summary>
    /// Gebruikers en licenties altijd volledig (de laatste aanmelding verandert zonder delta). De groepen met hun leden
    /// (het meeste werk: één vraag per groep) via /groups/delta als er een vorige lezing is; anders alles.
    /// </summary>
    public async Task<GraphGegevens> LeesAsync(GraphBasis? basis, IProgress<string>? voortgang, CancellationToken annuleren)
    {
        var (gebruikers, licenties) = await LeesGebruikersEnLicentiesAsync(voortgang, annuleren);

        if (basis is not null)
        {
            try
            {
                voortgang?.Report("Entra: enkel de gewijzigde groepen...");
                var (items, link) = await LeesGroepDeltaAsync(basis.DeltaLink, annuleren);
                var (groepen, lid) = GraphDelta.Toepassen(basis, items);
                voortgang?.Report($"Entra: {gebruikers.Count} gebruikers gelezen, {items.Count} groepswijzigingen toegepast ({groepen.Count} groepen).");
                return new GraphGegevens(gebruikers, groepen, lid, licenties, link, Snel: true);
            }
            catch (GraphFout ex)
            {
                // Bv. 410 Gone (het startpunt is te oud): dan alles lezen
                voortgang?.Report($"Entra: wijzigingen niet beschikbaar ({ex.Message}); alle groepen lezen.");
            }
        }

        // Eerst een startpunt voor de volgende keer vragen (zonder gegevens), dan alles lezen
        string? deltaLink = null;
        try
        {
            var start = await _klant.GetAsync($"/groups/delta?$select={DeltaVelden}&$deltatoken=latest", annuleren);
            deltaLink = start.TryGetProperty("@odata.deltaLink", out var dl) ? dl.GetString() : null;
        }
        catch (GraphFout)
        {
            // Geen startpunt: de volgende keer opnieuw alles
        }

        var (alleGroepen, lidmaatschap) = await LeesGroepenVolledigAsync(voortgang, annuleren);
        voortgang?.Report($"Entra: {gebruikers.Count} gebruikers en {alleGroepen.Count} groepen gelezen.");
        return new GraphGegevens(gebruikers, alleGroepen, lidmaatschap, licenties, deltaLink);
    }

    /// <summary>Alle pagina's van een delta-ronde, tot de nieuwe deltaLink.</summary>
    private async Task<(List<GroepDeltaItem> Items, string? DeltaLink)> LeesGroepDeltaAsync(string deltaLink, CancellationToken annuleren)
    {
        var items = new List<GroepDeltaItem>();
        string? volgende = deltaLink;
        while (true)
        {
            var pagina = await _klant.GetAsync(volgende, annuleren);
            if (pagina.TryGetProperty("value", out var waarden))
            {
                items.AddRange(waarden.EnumerateArray().Select(e => NaarDeltaItem(e.Clone())));
            }

            if (pagina.TryGetProperty("@odata.nextLink", out var nl) && nl.GetString() is { } n)
            {
                volgende = n;
                continue;
            }

            return (items, pagina.TryGetProperty("@odata.deltaLink", out var dl) ? dl.GetString() : null);
        }
    }

    /// <summary>Eén groep uit /groups/delta: verwijderd, gewijzigde kenmerken en/of gewijzigde (gebruikers)leden.</summary>
    internal static GroepDeltaItem NaarDeltaItem(JsonElement g)
    {
        var id = S(g, "id");
        if (g.TryGetProperty("@removed", out _))
        {
            return new GroepDeltaItem(id, true, null, Array.Empty<string>(), Array.Empty<string>());
        }

        var erbij = new List<string>();
        var eraf = new List<string>();
        if (g.TryGetProperty("members@delta", out var md) && md.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in md.EnumerateArray().Where(m => S(m, "@odata.type") is "#microsoft.graph.user" or ""))
            {
                (m.TryGetProperty("@removed", out _) ? eraf : erbij).Add(S(m, "id"));
            }
        }

        var kenmerken = new[] { "displayName", "description", "mail", "mailEnabled", "securityEnabled", "groupTypes", "membershipRule", "onPremisesSyncEnabled", "onPremisesSamAccountName" };
        var veranderd = kenmerken.Any(k => g.TryGetProperty(k, out _));
        return new GroepDeltaItem(id, false, veranderd ? bestaand => Samenvoegen(bestaand, g) : null, erbij, eraf);
    }

    /// <summary>De kenmerken die in het delta-antwoord staan, over de vorige versie van de groep.</summary>
    internal static CloudGroep Samenvoegen(CloudGroep? bestaand, JsonElement g)
    {
        var b = bestaand ?? new CloudGroep();
        bool Heeft(string k) => g.TryGetProperty(k, out _);
        var nieuw = NaarGroep(g, b.AantalLeden ?? 0);
        return b with
        {
            DisplayName = Heeft("displayName") ? nieuw.DisplayName : b.DisplayName,
            Description = Heeft("description") ? nieuw.Description : b.Description,
            Mail = Heeft("mail") ? nieuw.Mail : b.Mail,
            OnPremisesSamAccountName = Heeft("onPremisesSamAccountName") ? nieuw.OnPremisesSamAccountName : b.OnPremisesSamAccountName,
            UitAd = Heeft("onPremisesSyncEnabled") ? nieuw.UitAd : b.UitAd,
            Lidmaatschapsregel = Heeft("membershipRule") ? nieuw.Lidmaatschapsregel : b.Lidmaatschapsregel,
            Dynamisch = Heeft("groupTypes") ? nieuw.Dynamisch : b.Dynamisch,

            // De soort volgt uit drie kenmerken samen; enkel herberekenen als ze er alle drie zijn
            Type = Heeft("groupTypes") && Heeft("mailEnabled") && Heeft("securityEnabled") ? nieuw.Type : bestaand?.Type ?? nieuw.Type,
        };
    }

    private async Task<(List<EntraGebruiker> Gebruikers, List<Licentie> Licenties)> LeesGebruikersEnLicentiesAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Entra: licenties...");
        var skus = await _klant.GetAlleAsync("/subscribedSkus?$select=skuId,skuPartNumber,consumedUnits,prepaidUnits", annuleren);
        var licenties = skus.Select(s => new Licentie(
            S(s, "skuId"), S(s, "skuPartNumber"), s.TryGetProperty("consumedUnits", out var c) ? c.GetInt32() : 0,
            s.TryGetProperty("prepaidUnits", out var p) && p.TryGetProperty("enabled", out var e) ? e.GetInt32() : 0)).ToList();
        var skuNaam = licenties.ToDictionary(l => l.SkuId, l => l.SkuPartNumber, StringComparer.OrdinalIgnoreCase);

        voortgang?.Report("Entra: gebruikers...");
        List<JsonElement> ruweGebruikers;
        try
        {
            ruweGebruikers = await _klant.GetAlleAsync($"/users?$select={GebruikerVelden},signInActivity&$top=999", annuleren);
        }
        catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // Laatste aanmelding vraagt AuditLog.Read.All en Entra ID P1; zonder die gegevens verder
            voortgang?.Report("Entra: laatste aanmelding niet beschikbaar, verder zonder.");
            ruweGebruikers = await _klant.GetAlleAsync($"/users?$select={GebruikerVelden}&$top=999", annuleren);
        }

        var gebruikers = ruweGebruikers.Select(u => NaarGebruiker(u, skuNaam)).ToList();

        // Managers apart (een $expand in de grote lezing zou bij een weigering ook de laatste aanmelding kosten)
        try
        {
            var managers = await _klant.GetAlleAsync("/users?$select=id&$expand=manager($select=id)&$top=999", annuleren);
            var opId = managers.Where(m => m.TryGetProperty("manager", out var mg) && mg.ValueKind == JsonValueKind.Object)
                .ToDictionary(m => S(m, "id"), m => S(m.GetProperty("manager"), "id"), StringComparer.OrdinalIgnoreCase);
            gebruikers = gebruikers.Select(g => opId.TryGetValue(g.Id, out var mid) ? g with { ManagerId = mid } : g).ToList();
        }
        catch (GraphFout)
        {
            voortgang?.Report("Entra: managers niet beschikbaar, verder zonder.");
        }

        return (gebruikers, licenties);
    }

    private async Task<(List<CloudGroep> Groepen, Dictionary<string, HashSet<string>> Lidmaatschap)> LeesGroepenVolledigAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Entra: groepen...");
        var ruweGroepen = await _klant.GetAlleAsync($"/groups?$select={GroepVelden}&$top=999", annuleren);

        var lidmaatschap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var aantallen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var teller = 0;
        using var rem = new SemaphoreSlim(4);
        var taken = ruweGroepen.Select(async g =>
        {
            var id = S(g, "id");
            await rem.WaitAsync(annuleren);
            try
            {
                var leden = await _klant.GetAlleAsync($"/groups/{id}/members/microsoft.graph.user?$select=id&$top=999", annuleren);
                lock (lidmaatschap)
                {
                    aantallen[id] = leden.Count;
                    foreach (var lid in leden)
                    {
                        var lidId = S(lid, "id");
                        if (!lidmaatschap.TryGetValue(lidId, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            lidmaatschap[lidId] = set;
                        }

                        set.Add(id);
                    }

                    teller++;
                    if (teller % 50 == 0)
                    {
                        voortgang?.Report($"Entra: groepsleden {teller}/{ruweGroepen.Count}...");
                    }
                }
            }
            finally
            {
                rem.Release();
            }
        });
        await Task.WhenAll(taken);

        return (ruweGroepen.Select(g => NaarGroep(g, aantallen.GetValueOrDefault(S(g, "id")))).ToList(), lidmaatschap);
    }

    public async Task<GraphGebruikerLezing?> LeesGebruikerAsync(string idOfUpn, CancellationToken annuleren)
    {
        var skus = await _klant.GetAlleAsync("/subscribedSkus?$select=skuId,skuPartNumber", annuleren);
        var skuNaam = skus.ToDictionary(s => S(s, "skuId"), s => S(s, "skuPartNumber"), StringComparer.OrdinalIgnoreCase);
        var pad = $"/users/{Uri.EscapeDataString(idOfUpn)}";
        JsonElement u;
        try
        {
            try
            {
                u = await _klant.GetAsync($"{pad}?$select={GebruikerVelden},signInActivity&$expand=manager($select=id)", annuleren);
            }
            catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                u = await _klant.GetAsync($"{pad}?$select={GebruikerVelden}", annuleren);
            }
        }
        catch (GraphFout ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        var gebruiker = NaarGebruiker(u, skuNaam);
        var groepen = await _klant.GetAlleAsync($"/users/{gebruiker.Id}/memberOf/microsoft.graph.group?$select=id&$top=999", annuleren);
        return new GraphGebruikerLezing(gebruiker, groepen.Select(g => S(g, "id")).ToList());
    }

    public async Task<GraphGroepLezing?> LeesGroepAsync(string id, CancellationToken annuleren)
    {
        JsonElement g;
        try
        {
            g = await _klant.GetAsync($"/groups/{Uri.EscapeDataString(id)}?$select={GroepVelden}", annuleren);
        }
        catch (GraphFout ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        var leden = await _klant.GetAlleAsync($"/groups/{Uri.EscapeDataString(id)}/members/microsoft.graph.user?$select=id&$top=999", annuleren);
        return new GraphGroepLezing(NaarGroep(g, leden.Count), leden.Select(l => S(l, "id")).ToList());
    }

    public async Task<string?> ZoekGebruikerIdAsync(string upn, CancellationToken annuleren)
    {
        try
        {
            var u = await _klant.GetAsync($"/users/{Uri.EscapeDataString(upn)}?$select=id", annuleren);
            return S(u, "id");
        }
        catch (GraphFout ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ beveiliging

    public async Task<BeveiligingGegevens?> LeesBeveiligingAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Entra: MFA-registraties...");
        IReadOnlyList<MfaRegistratie> mfa = Array.Empty<MfaRegistratie>();
        string? mfaMelding = null;
        try
        {
            var ruw = await _klant.GetAlleAsync("/reports/authenticationMethods/userRegistrationDetails?$top=999", annuleren);
            mfa = ruw.Select(NaarMfa).ToList();
        }
        catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            mfaMelding = "Niet leesbaar: je account heeft de rol Reports Reader, Security Reader of Global Reader nodig, en de tenant Entra ID P1 (" + ex.Message + ")";
        }

        voortgang?.Report("Entra: beheerrollen...");
        var rollen = new List<RolLid>();
        string? rollenMelding = null;
        try
        {
            foreach (var rol in await _klant.GetAlleAsync("/directoryRoles?$select=id,displayName,roleTemplateId", annuleren))
            {
                var leden = await _klant.GetAlleAsync($"/directoryRoles/{S(rol, "id")}/members?$select=id,displayName,userPrincipalName", annuleren);
                rollen.AddRange(leden.Select(l => new RolLid(S(rol, "displayName"), S(l, "id"), S(l, "displayName"), S(l, "userPrincipalName"),
                    S(l, "@odata.type").Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal), S(rol, "roleTemplateId"))));
            }
        }
        catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            rollenMelding = "Niet leesbaar: " + ex.Message;
        }

        voortgang?.Report($"Entra: {mfa.Count} MFA-registraties en {rollen.Count} rollidmaatschappen gelezen.");
        return new BeveiligingGegevens(mfa, rollen, mfaMelding, rollenMelding);
    }

    public async Task<IReadOnlyList<Aanmelding>> LeesAanmeldingenAsync(string? gebruikerId, int dagen, bool alleenMislukt, CancellationToken annuleren)
    {
        var vanaf = DateTimeOffset.UtcNow.AddDays(-dagen).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var filter = $"createdDateTime ge {vanaf}";
        if (gebruikerId is not null)
        {
            filter += $" and userId eq '{gebruikerId.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        if (alleenMislukt)
        {
            filter += " and status/errorCode ne 0";
        }

        // Eén pagina volstaat voor het scherm (de nieuwste eerst); aanmeldlogs kunnen erg groot zijn
        var pagina = await _klant.GetAsync($"/auditLogs/signIns?$filter={Uri.EscapeDataString(filter)}&$top={(gebruikerId is null ? 500 : 100)}", annuleren);
        return pagina.TryGetProperty("value", out var waarden) ? waarden.EnumerateArray().Select(NaarAanmelding).ToList() : new List<Aanmelding>();
    }

    public async Task<IReadOnlyList<Aanmelding>> LeesAanmeldingenAsync(int dagen, int max, CancellationToken annuleren)
    {
        var vanaf = DateTimeOffset.UtcNow.AddDays(-dagen).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var uit = new List<Aanmelding>();
        string? volgende = $"/auditLogs/signIns?$filter={Uri.EscapeDataString($"createdDateTime ge {vanaf}")}&$top=999";
        while (volgende is not null && uit.Count < max)
        {
            var pagina = await _klant.GetAsync(volgende, annuleren);
            if (pagina.TryGetProperty("value", out var waarden))
            {
                uit.AddRange(waarden.EnumerateArray().Select(NaarAanmelding));
            }

            volgende = pagina.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }

        return uit.Count > max ? uit.GetRange(0, max) : uit;
    }

    // ------------------------------------------------------------------ agenda van een room

    public async Task<IReadOnlyList<Afspraak>> LeesAgendaAsync(string adres, DateTime van, DateTime tot, CancellationToken annuleren)
    {
        static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var pad = $"/users/{Uri.EscapeDataString(adres)}/calendarView?startDateTime={Utc(van)}&endDateTime={Utc(tot)}"
            + "&$select=subject,organizer,start,end,isAllDay,sensitivity,showAs,isCancelled&$orderby=start/dateTime&$top=250";
        try
        {
            return (await _klant.GetAlleAsync(pad, annuleren)).Select(NaarAfspraak).OrderBy(a => a.Begin).ToList();
        }
        catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            throw new GeenRechtFout(ex.Message);
        }
    }

    /// <summary>Graph geeft de tijden in UTC (zonder Prefer-kop); een afspraak van een hele dag blijft op haar datum.</summary>
    internal static Afspraak NaarAfspraak(JsonElement e)
    {
        var heleDag = B(e, "isAllDay");
        DateTime Tijd(string naam)
        {
            if (!e.TryGetProperty(naam, out var t) || !DateTime.TryParse(S(t, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var waarde))
            {
                return default;
            }

            return heleDag ? waarde.Date : DateTime.SpecifyKind(waarde, DateTimeKind.Utc).ToLocalTime();
        }

        var organisator = e.TryGetProperty("organizer", out var o) && o.TryGetProperty("emailAddress", out var m) ? m : default;
        return new Afspraak(
            Tijd("start"),
            Tijd("end"),
            S(e, "subject"),
            organisator.ValueKind == JsonValueKind.Object ? S(organisator, "name") : string.Empty,
            organisator.ValueKind == JsonValueKind.Object ? S(organisator, "address") : string.Empty,
            heleDag,
            S(e, "sensitivity") is "private" or "confidential",
            S(e, "showAs"),
            B(e, "isCancelled"));
    }

    // ------------------------------------------------------------------ conditional access

    public async Task<CaGegevens?> LeesConditionalAccessAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Entra: Conditional Access-beleiden...");
        List<CaBeleid> beleiden;
        try
        {
            beleiden = (await _klant.GetAlleAsync("/identity/conditionalAccess/policies", annuleren)).Select(NaarCaBeleid).ToList();
        }
        catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return new CaGegevens(Array.Empty<CaBeleid>(), Array.Empty<BenoemdeLocatie>(), Array.Empty<Authenticatiesterkte>(),
                new Dictionary<string, string>(), null,
                "Niet leesbaar: de app-registratie heeft het recht Policy.Read.All nodig (Registreer-App.ps1 opnieuw uitvoeren) en je account "
                + "de rol Security Reader, Global Reader of Conditional Access Administrator (" + ex.Message + ")");
        }

        var locaties = new List<BenoemdeLocatie>();
        var sterktes = new List<Authenticatiesterkte>();
        var rollen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool? standaarden = null;
        try
        {
            locaties = (await _klant.GetAlleAsync("/identity/conditionalAccess/namedLocations", annuleren)).Select(NaarLocatie).ToList();
        }
        catch (GraphFout)
        {
            // Zonder locaties tonen de schermen de id's
        }

        try
        {
            sterktes = (await _klant.GetAlleAsync("/policies/authenticationStrengthPolicies", annuleren)).Select(NaarSterkte).ToList();
        }
        catch (GraphFout)
        {
        }

        try
        {
            foreach (var r in await _klant.GetAlleAsync("/directoryRoleTemplates?$select=id,displayName", annuleren))
            {
                rollen[S(r, "id")] = S(r, "displayName");
            }
        }
        catch (GraphFout)
        {
        }

        try
        {
            var sd = await _klant.GetAsync("/policies/identitySecurityDefaultsEnforcementPolicy", annuleren);
            standaarden = sd.TryGetProperty("isEnabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
        }
        catch (GraphFout)
        {
        }

        // Groepen in de beleiden: alle leden, ook via geneste groepen (zo werkt Conditional Access zelf)
        Dictionary<string, IReadOnlyList<string>>? groepLeden = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var id in beleiden.SelectMany(b => b.GroepenIn.Concat(b.GroepenUit)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var leden = await _klant.GetAlleAsync($"/groups/{Uri.EscapeDataString(id)}/transitiveMembers/microsoft.graph.user?$select=id&$top=999", annuleren);
                groepLeden[id] = leden.Select(l => S(l, "id")).Where(x => x.Length > 0).ToList();
            }
        }
        catch (GraphFout)
        {
            groepLeden = null;
        }

        voortgang?.Report($"Entra: {beleiden.Count} Conditional Access-beleiden en {locaties.Count} benoemde locaties gelezen.");
        return new CaGegevens(beleiden.OrderBy(b => b.Naam, StringComparer.CurrentCultureIgnoreCase).ToList(), locaties, sterktes, rollen, standaarden,
            null, groepLeden);
    }

    internal static CaBeleid NaarCaBeleid(JsonElement p) => CaJson.Lees(p);

    internal static BenoemdeLocatie NaarLocatie(JsonElement l) => CaJson.LeesLocatie(l);

    internal static Authenticatiesterkte NaarSterkte(JsonElement a)
    {
        var combinaties = a.TryGetProperty("allowedCombinations", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new Authenticatiesterkte(S(a, "id"), S(a, "displayName"), S(a, "policyType") == "builtIn", combinaties);
    }

    internal static MfaRegistratie NaarMfa(JsonElement m)
    {
        var methodes = m.TryGetProperty("methodsRegistered", out var mr) && mr.ValueKind == JsonValueKind.Array
            ? mr.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : new List<string>();
        var standaard = S(m, "defaultMfaMethod");
        if (standaard.Length == 0 || standaard == "none")
        {
            standaard = S(m, "userPreferredMethodForSecondaryAuthentication");
        }

        return new MfaRegistratie(S(m, "id"), S(m, "userPrincipalName"), B(m, "isMfaRegistered"), B(m, "isMfaCapable"), methodes,
            standaard == "none" ? string.Empty : standaard, B(m, "isAdmin"));
    }

    internal static Aanmelding NaarAanmelding(JsonElement a)
    {
        var tijd = DateTimeOffset.TryParse(S(a, "createdDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTimeOffset.MinValue;
        var locatie = a.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object
            ? string.Join(", ", new[] { S(l, "city"), S(l, "countryOrRegion") }.Where(x => x.Length > 0))
            : string.Empty;
        var code = 0;
        var reden = string.Empty;
        if (a.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            code = st.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            reden = S(st, "failureReason");
        }

        List<CaToepassing>? ca = null;
        if (a.TryGetProperty("appliedConditionalAccessPolicies", out var cap) && cap.ValueKind == JsonValueKind.Array)
        {
            ca = cap.EnumerateArray().Select(x => new CaToepassing(S(x, "id"), S(x, "displayName"), S(x, "result"))).Where(x => x.BeleidId.Length > 0).ToList();
        }

        return new Aanmelding(tijd, S(a, "userId"), S(a, "userPrincipalName"), S(a, "userDisplayName"), S(a, "appDisplayName"),
            S(a, "ipAddress"), locatie, code, code == 0 ? string.Empty : reden, S(a, "clientAppUsed"), ca);
    }

    internal static EntraGebruiker NaarGebruiker(JsonElement u, IReadOnlyDictionary<string, string> skuNaam)
    {
        var licenties = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Select(l => S(l, "skuId")).Select(id => skuNaam.GetValueOrDefault(id, id)).ToList()
            : new List<string>();
        string laatste = string.Empty;
        if (u.TryGetProperty("signInActivity", out var sa) && sa.ValueKind == JsonValueKind.Object)
        {
            var datums = new[] { S(sa, "lastSignInDateTime"), S(sa, "lastNonInteractiveSignInDateTime") }
                .Select(d => DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : (DateTimeOffset?)null)
                .Where(d => d is not null)
                .ToList();
            laatste = datums.Count == 0 ? string.Empty : datums.Max()!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return new EntraGebruiker
        {
            Id = S(u, "id"),
            UserPrincipalName = S(u, "userPrincipalName"),
            DisplayName = S(u, "displayName"),
            GivenName = S(u, "givenName"),
            Surname = S(u, "surname"),
            Mail = S(u, "mail"),
            MailNickname = S(u, "mailNickname"),
            AccountEnabled = B(u, "accountEnabled"),
            UserType = S(u, "userType"),
            Department = S(u, "department"),
            JobTitle = S(u, "jobTitle"),
            OfficeLocation = S(u, "officeLocation"),
            Telefoon = u.TryGetProperty("businessPhones", out var bp) && bp.ValueKind == JsonValueKind.Array && bp.GetArrayLength() > 0 ? bp[0].GetString() ?? string.Empty : string.Empty,
            Gsm = S(u, "mobilePhone"),
            ManagerId = u.TryGetProperty("manager", out var mgr) && mgr.ValueKind == JsonValueKind.Object ? S(mgr, "id") : string.Empty,
            CompanyName = S(u, "companyName"),
            City = S(u, "city"),
            UsageLocation = S(u, "usageLocation"),
            CreatedDateTime = S(u, "createdDateTime"),
            OnPremisesSyncEnabled = B(u, "onPremisesSyncEnabled"),
            OnPremisesSamAccountName = S(u, "onPremisesSamAccountName"),
            OnPremisesDistinguishedName = S(u, "onPremisesDistinguishedName"),
            Licenties = licenties,
            LaatsteAanmelding = laatste,
        };
    }

    internal static CloudGroep NaarGroep(JsonElement g, int aantalLeden)
    {
        var types = g.TryGetProperty("groupTypes", out var gt) && gt.ValueKind == JsonValueKind.Array
            ? gt.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : new List<string>();
        var mailEnabled = B(g, "mailEnabled");
        var securityEnabled = B(g, "securityEnabled");
        var type = types.Contains("Unified") ? CloudGroepType.Microsoft365
            : mailEnabled && securityEnabled ? CloudGroepType.MailBeveiliging
            : mailEnabled ? CloudGroepType.Distributie
            : CloudGroepType.Beveiliging;
        return new CloudGroep
        {
            Id = S(g, "id"),
            DisplayName = S(g, "displayName"),
            Description = S(g, "description"),
            Mail = S(g, "mail"),
            OnPremisesSamAccountName = S(g, "onPremisesSamAccountName"),
            Type = type,
            UitAd = B(g, "onPremisesSyncEnabled"),
            Dynamisch = types.Contains("DynamicMembership"),
            Lidmaatschapsregel = S(g, "membershipRule"),
            GroepLicentie = g.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array && al.GetArrayLength() > 0,
            AantalLeden = aantalLeden,
        };
    }

    // ------------------------------------------------------------------ uitvoeren

    public async Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren)
    {
        var uit = new List<WijzigingResultaat>();
        foreach (var w in wijzigingen)
        {
            uit.Add(await VoerAsync(w, annuleren));
        }

        return uit;
    }

    public async Task<IReadOnlyList<GraphEigenaar>> LeesEigenaarsAsync(string groepId, CancellationToken annuleren)
    {
        var lijst = await _klant.GetAlleAsync($"/groups/{Uri.EscapeDataString(groepId)}/owners?$select=id,displayName,userPrincipalName", annuleren);
        return lijst.Select(e => new GraphEigenaar(S(e, "id"), S(e, "displayName"), S(e, "userPrincipalName"))).ToList();
    }

    /// <summary>Wijzigingen aan een groep zelf: Doel is het groeps-id, een eventuele gebruiker staat in de parameter "lid".</summary>
    private async Task<WijzigingResultaat> VoerGroepAsync(Wijziging w, CancellationToken annuleren)
    {
        var groep = Uri.EscapeDataString(w.Doel);
        if (w.Actie == Actie.GraphGroepKenmerkenZetten)
        {
            await _klant.PatchAsync($"/groups/{groep}", KenmerkenInhoud(w.Parameters), annuleren);
            return WijzigingResultaat.Ok(w);
        }

        var lid = w.P("lid");
        var lidId = lid.Contains('@') ? await ZoekGebruikerIdAsync(lid, annuleren) : lid;
        if (string.IsNullOrEmpty(lidId))
        {
            return WijzigingResultaat.Fout(w, $"{lid} bestaat niet in Entra.");
        }

        if (w.Actie == Actie.GraphEigenaarToevoegen)
        {
            try
            {
                await _klant.PostAsync($"/groups/{groep}/owners/$ref",
                    new Dictionary<string, string> { ["@odata.id"] = $"{GraphKlant.Basis}/directoryObjects/{lidId}" }, annuleren);
            }
            catch (GraphFout ex) when (ex.Status == HttpStatusCode.BadRequest && ex.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase))
            {
                return WijzigingResultaat.Ok(w, "Was al eigenaar.");
            }

            return WijzigingResultaat.Ok(w);
        }

        await _klant.DeleteAsync($"/groups/{groep}/owners/{lidId}/$ref", annuleren);
        return WijzigingResultaat.Ok(w);
    }

    /// <summary>Conditional Access: de JSON gaat zoals ze in het plan staat (het plan is gecontroleerd en in het logboek).</summary>
    private async Task<WijzigingResultaat> VoerCaAsync(Wijziging w, CancellationToken annuleren)
    {
        var inhoud = System.Text.Json.Nodes.JsonNode.Parse(w.P("json")) ?? throw new InvalidOperationException("Geen JSON in de wijziging.");
        switch (w.Actie)
        {
            case Actie.GraphCaBeleidMaken:
            {
                var nieuw = await _klant.PostMetAntwoordAsync("/identity/conditionalAccess/policies", inhoud, annuleren);
                return WijzigingResultaat.Ok(w, $"Gemaakt; id {S(nieuw, "id")}");
            }

            case Actie.GraphCaBeleidWijzigen:
                await _klant.PatchAsync($"/identity/conditionalAccess/policies/{Uri.EscapeDataString(w.Doel)}", inhoud, annuleren);
                return WijzigingResultaat.Ok(w);

            case Actie.GraphCaLocatieMaken:
            {
                var nieuw = await _klant.PostMetAntwoordAsync("/identity/conditionalAccess/namedLocations", inhoud, annuleren);
                return WijzigingResultaat.Ok(w, $"Gemaakt; id {S(nieuw, "id")}");
            }

            default:
                await _klant.PatchAsync($"/identity/conditionalAccess/namedLocations/{Uri.EscapeDataString(w.Doel)}", inhoud, annuleren);
                return WijzigingResultaat.Ok(w);
        }
    }

    /// <summary>Wie wijzigde Conditional Access (auditlogboek van Entra, de laatste dagen).</summary>
    public async Task<IReadOnlyList<CaAuditRegel>> LeesCaAuditAsync(int dagen, CancellationToken annuleren)
    {
        var vanaf = DateTimeOffset.UtcNow.AddDays(-dagen).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var filter = $"activityDateTime ge {vanaf} and loggedByService eq 'Conditional Access'";
        try
        {
            var lijst = await _klant.GetAlleAsync($"/auditLogs/directoryAudits?$filter={Uri.EscapeDataString(filter)}&$top=200", annuleren);
            return lijst.Select(NaarCaAudit).OrderByDescending(a => a.Tijd).ToList();
        }
        catch (GraphFout ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new GeenRechtFout(ex.Message);
        }
    }

    internal static CaAuditRegel NaarCaAudit(JsonElement a)
    {
        var wie = a.TryGetProperty("initiatedBy", out var i)
            ? (i.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? S(u, "userPrincipalName")
                : i.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object ? S(app, "displayName") : string.Empty)
            : string.Empty;
        var doel = a.TryGetProperty("targetResources", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().FirstOrDefault() : default;
        DateTimeOffset.TryParse(S(a, "activityDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var tijd);
        return new CaAuditRegel(tijd, wie, S(a, "activityDisplayName"),
            doel.ValueKind == JsonValueKind.Object ? S(doel, "id") : string.Empty,
            doel.ValueKind == JsonValueKind.Object ? S(doel, "displayName") : string.Empty,
            S(a, "result"));
    }

    /// <summary>Een cloudbeveiligingsgroep of Microsoft 365-groep, met eigenaar en leden in dezelfde opdracht (de groep heeft nog geen id).</summary>
    private async Task<WijzigingResultaat> GroepAanmakenAsync(Wijziging w, CancellationToken annuleren)
    {
        var m365 = w.P("soort") == "M365";
        var inhoud = new Dictionary<string, object>
        {
            ["displayName"] = w.P("displayName"),
            ["mailNickname"] = w.P("mailNickname"),
            ["mailEnabled"] = m365,
            ["securityEnabled"] = !m365,
            ["groupTypes"] = m365 ? new[] { "Unified" } : Array.Empty<string>(),
        };
        if (m365)
        {
            inhoud["visibility"] = "Private";
        }

        if (w.P("description").Length > 0)
        {
            inhoud["description"] = w.P("description");
        }

        string Ref(string id) => $"{GraphKlant.Basis}/directoryObjects/{id}";
        if (w.P("eigenaar").Length > 0)
        {
            inhoud["owners@odata.bind"] = new[] { Ref(w.P("eigenaar")) };
        }

        var leden = w.P("leden").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (leden.Length > 0)
        {
            inhoud["members@odata.bind"] = leden.Select(Ref).ToArray();
        }

        var groep = await _klant.PostMetAntwoordAsync("/groups", inhoud, annuleren);
        return WijzigingResultaat.Ok(w, $"Aangemaakt; id {S(groep, "id")}");
    }

    private async Task<WijzigingResultaat> VoerAsync(Wijziging w, CancellationToken annuleren)
    {
        try
        {
            if (w.Actie is Actie.GraphGroepKenmerkenZetten or Actie.GraphEigenaarToevoegen or Actie.GraphEigenaarVerwijderen)
            {
                return await VoerGroepAsync(w, annuleren);
            }

            if (w.Actie == Actie.GraphGastUitnodigen)
            {
                var uitnodiging = await _klant.PostMetAntwoordAsync("/invitations", new Dictionary<string, object>
                {
                    ["invitedUserEmailAddress"] = w.Doel,
                    ["invitedUserDisplayName"] = w.P("naam"),
                    ["inviteRedirectUrl"] = "https://myapps.microsoft.com",
                    ["sendInvitationMessage"] = string.Equals(w.P("bericht"), "True", StringComparison.OrdinalIgnoreCase),
                }, annuleren);
                var id = uitnodiging.TryGetProperty("invitedUser", out var iu) ? S(iu, "id") : string.Empty;
                return WijzigingResultaat.Ok(w, $"Uitgenodigd; id {id}");
            }

            if (w.Actie == Actie.GraphGroepAanmaken)
            {
                return await GroepAanmakenAsync(w, annuleren);
            }

            if (w.Actie is Actie.GraphCaBeleidMaken or Actie.GraphCaBeleidWijzigen or Actie.GraphCaLocatieMaken or Actie.GraphCaLocatieWijzigen)
            {
                return await VoerCaAsync(w, annuleren);
            }

            if (w.Actie == Actie.GraphGastVerwijderen)
            {
                // Veiligheid: enkel gastaccounts; een gewone gebruiker gaat via uitstroom en wordt nooit verwijderd
                var gast = await _klant.GetAsync($"/users/{Uri.EscapeDataString(w.Doel)}?$select=id,userType,userPrincipalName", annuleren);
                if (!string.Equals(S(gast, "userType"), "Guest", StringComparison.OrdinalIgnoreCase))
                {
                    return WijzigingResultaat.Fout(w, "Dit is geen gastaccount; de app verwijdert enkel gasten.");
                }

                await _klant.DeleteAsync($"/users/{S(gast, "id")}", annuleren);
                return WijzigingResultaat.Ok(w, "Verwijderd; 30 dagen terug te zetten via Entra > Verwijderde gebruikers.");
            }

            var gebruikerId = w.Doel.Contains('@') ? await ZoekGebruikerIdAsync(w.Doel, annuleren) : w.Doel;
            if (gebruikerId is null)
            {
                return WijzigingResultaat.Fout(w, $"{w.Doel} bestaat (nog) niet in Entra.");
            }

            switch (w.Actie)
            {
                case Actie.GraphGroepLidToevoegen:
                    try
                    {
                        await _klant.PostAsync($"/groups/{w.P("groepId")}/members/$ref",
                            new Dictionary<string, string> { ["@odata.id"] = $"{GraphKlant.Basis}/directoryObjects/{gebruikerId}" }, annuleren);
                    }
                    catch (GraphFout ex) when (ex.Status == HttpStatusCode.BadRequest && ex.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase))
                    {
                        return WijzigingResultaat.Ok(w, "Was al lid.");
                    }

                    return WijzigingResultaat.Ok(w);

                case Actie.GraphGroepLidVerwijderen:
                    await _klant.DeleteAsync($"/groups/{w.P("groepId")}/members/{gebruikerId}/$ref", annuleren);
                    return WijzigingResultaat.Ok(w);

                case Actie.GraphKenmerkenZetten:
                    await _klant.PatchAsync($"/users/{gebruikerId}", KenmerkenInhoud(w.Parameters), annuleren);
                    return WijzigingResultaat.Ok(w);

                case Actie.GraphManagerZetten:
                    if (w.P("managerId").Length == 0)
                    {
                        try
                        {
                            await _klant.DeleteAsync($"/users/{gebruikerId}/manager/$ref", annuleren);
                        }
                        catch (GraphFout ex) when (ex.Status == HttpStatusCode.NotFound)
                        {
                            return WijzigingResultaat.Ok(w, "Had al geen manager.");
                        }
                    }
                    else
                    {
                        await _klant.PutAsync($"/users/{gebruikerId}/manager/$ref",
                            new Dictionary<string, string> { ["@odata.id"] = $"{GraphKlant.Basis}/users/{w.P("managerId")}" }, annuleren);
                    }

                    return WijzigingResultaat.Ok(w);

                case Actie.GraphLicentieToekennen:
                {
                    var skuId = w.P("skuId");
                    if (skuId.Length == 0)
                    {
                        var skus = await _klant.GetAlleAsync("/subscribedSkus?$select=skuId,skuPartNumber", annuleren);
                        skuId = skus.Where(s => string.Equals(S(s, "skuPartNumber"), w.P("sku"), StringComparison.OrdinalIgnoreCase))
                            .Select(s => S(s, "skuId")).FirstOrDefault() ?? string.Empty;
                        if (skuId.Length == 0)
                        {
                            return WijzigingResultaat.Fout(w, $"Licentie {w.P("sku")} bestaat niet in deze tenant.");
                        }
                    }

                    await _klant.PostAsync($"/users/{gebruikerId}/assignLicense", new
                    {
                        addLicenses = new[] { new { skuId, disabledPlans = Array.Empty<string>() } },
                        removeLicenses = Array.Empty<string>(),
                    }, annuleren);
                    return WijzigingResultaat.Ok(w);
                }

                case Actie.GraphLicentiesVerwijderen:
                {
                    var u = await _klant.GetAsync($"/users/{gebruikerId}?$select=assignedLicenses", annuleren);
                    var ids = u.GetProperty("assignedLicenses").EnumerateArray().Select(l => S(l, "skuId")).ToArray();
                    if (ids.Length == 0)
                    {
                        return WijzigingResultaat.Ok(w, "Had geen licenties.");
                    }

                    await _klant.PostAsync($"/users/{gebruikerId}/assignLicense",
                        new { addLicenses = Array.Empty<object>(), removeLicenses = ids }, annuleren);
                    return WijzigingResultaat.Ok(w, $"{ids.Length} licentie(s) verwijderd.");
                }

                case Actie.GraphSessiesIntrekken:
                    await _klant.PostAsync($"/users/{gebruikerId}/revokeSignInSessions", null, annuleren);
                    return WijzigingResultaat.Ok(w);

                default:
                    return WijzigingResultaat.Fout(w, $"Actie {w.Actie} hoort niet bij Graph.");
            }
        }
        catch (Exception ex)
        {
            return WijzigingResultaat.Fout(w, ex.Message);
        }
    }

    /// <summary>Tekstwaarden naar JSON: "true"/"false" worden booleans (bv. accountEnabled), de rest blijft tekst.</summary>
    internal static Dictionary<string, object?> KenmerkenInhoud(IReadOnlyDictionary<string, string> parameters)
        => parameters.ToDictionary(
            p => p.Key,
            p => p.Key == "businessPhones"
                ? (p.Value.Length == 0 ? Array.Empty<string>() : new[] { p.Value })
                : p.Value switch
            {
                "true" or "True" => true,
                "false" or "False" => (object?)false,
                "" => null,
                _ => p.Value,
            });

    private static string S(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;

    private static bool B(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var w) && w.ValueKind == JsonValueKind.True;
}
