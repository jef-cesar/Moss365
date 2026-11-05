using System.Text.Json;
using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Opslag;
using Moss365.Core.Rechten;
using Moss365.Core.Wijzigingen;

namespace Moss365.UI.Services;

/// <summary>Waar de app zijn bestanden vindt. De Windows-app vult dit in; tests gebruiken tijdelijke mappen.</summary>
public interface IOmgeving
{
    /// <summary>Basismap met de inventarismappen (Inventaris-yyyyMMdd-HHmm).</summary>
    string InventarisBasis { get; }

    /// <summary>De meegeleverde organisatie.json (alleen lezen).</summary>
    string ConfigStandaardPad { get; }

    /// <summary>Waar de app wijzigingen aan de organisatie bewaart.</summary>
    string ConfigGebruikersPad { get; }

    /// <summary>Map voor de eigen bestanden van de app: verbindingen, logboek, opvolging.</summary>
    string GegevensMap => Path.GetDirectoryName(ConfigGebruikersPad) ?? ".";

    /// <summary>Waar exports (lijsten, opkuisrondes) komen.</summary>
    string ExportMap => Path.Combine(GegevensMap, "export");

    /// <summary>De ingebouwde scripts (de map scripts naast de app).</summary>
    string IngebouwdeScripts => Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>Eigen scripts, laatste waarden en runs.</summary>
    string ScriptsGegevens => Path.Combine(GegevensMap, "scripts");

    /// <summary>Toont een bestand in Verkenner (enkel in de Windows-app).</summary>
    void ToonBestand(string pad)
    {
    }
}

public sealed record VasteOmgeving(string InventarisBasis, string ConfigStandaardPad, string ConfigGebruikersPad, string? Export = null, string? Scripts = null) : IOmgeving
{
    public string IngebouwdeScripts => Scripts ?? Path.Combine(AppContext.BaseDirectory, "scripts");

    public string ExportMap => Export ?? Path.Combine(Path.GetDirectoryName(ConfigGebruikersPad) ?? ".", "export");
}

/// <summary>De gedeelde toestand van de app: gegevens, organisatie, verbindingen, logboek en opvolging.</summary>
public sealed partial class BeheerState
{
    private static readonly JsonSerializerOptions JsonOpties = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly IOmgeving _omgeving;
    private readonly IVerbindingsFabriek? _fabriek;
    private IGraphVerbinding? _graph;
    private string? _graphSleutel;

    public BeheerState(IOmgeving omgeving, IVerbindingsFabriek? fabriek = null, AanmeldStatus? aanmelding = null)
    {
        _omgeving = omgeving;
        _fabriek = fabriek;
        Aanmelding = aanmelding ?? new AanmeldStatus();

        // Sinds 0.8 één databank (SQLite); de losse bestanden van vroeger worden eenmalig overgezet (en blijven als .bak)
        Databank = new Databank(Path.Combine(omgeving.GegevensMap, Databank.Bestandsnaam));
        try
        {
            Overgezet = Overzetten.VanBestanden(Databank, omgeving.GegevensMap);
        }
        catch (Exception ex)
        {
            Overgezet = new[] { "Overzetten naar de databank mislukt: " + ex.Message };
        }

        StartLog();
        Log.Information("Moss365 {Versie} gestart; databank versie {Schema}", AppVersie, Databank.Versie);
        foreach (var m in Overgezet)
        {
            Log.Information("Databank: {Melding}", m);
        }

        Logboek = new DatabankLogboek(Databank);
        Opvolging = new OpvolgOpslag(new DatabankTekst(Databank, "opvolging"));
        Gepland = new GeplandOpslag(new DatabankTekst(Databank, "gepland"));
        Weergave = new WeergaveOpslag(new DatabankTekst(Databank, "weergave"));
        _instellingenOpslag = new DatabankTekst(Databank, "verbinding");
        Instellingen = LeesInstellingen();
        _instellingenBewaard = JsonSerializer.Serialize(Instellingen, JsonOpties);
        LaadConfig();
        LaadInventaris();
        RuimScriptRunsOp();
        StartAutoVernieuwen();
        if (Instellingen.UpdatesZoeken && _fabriek?.Updates is not null)
        {
            _ = ZoekUpdateAsync();
        }
    }

    /// <summary>De versie van deze app, bv. "0.5.0".</summary>
    public static string AppVersie
    {
        get
        {
            var v = typeof(BeheerState).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>Resultaat van de laatste zoektocht naar een nieuwe versie (null = nog niet gezocht).</summary>
    public UpdateInfo? Update { get; private set; }

    public bool UpdateBeschikbaar => Update?.IsNieuwer(AppVersie) == true;

    /// <summary>Waar alle releases staan, ook als zoeken niet lukt.</summary>
    public string ReleasesLink => _fabriek?.Updates?.ReleasesLink ?? "https://github.com/jef-cesar/Moss365/releases";

    public async Task ZoekUpdateAsync()
    {
        if (_fabriek?.Updates is not { } updates)
        {
            return;
        }

        try
        {
            Update = await updates.ControleerAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Update = new UpdateInfo(null, ReleasesLink, ex.Message);
        }

        Gewijzigd?.Invoke();
    }

    public event Action? Gewijzigd;

    public IOmgeving Omgeving => _omgeving;
    public InventarisData Data { get; private set; } = new();
    public OrganisatieConfig Config { get; private set; } = new();
    public string? ConfigBron { get; private set; }
    public IReadOnlyList<string> Mappen { get; private set; } = Array.Empty<string>();

    /// <summary>Map die voorrang krijgt; null = per bestand automatisch de nieuwste.</summary>
    public string? VoorkeurMap { get; private set; }

    public string? Fout { get; private set; }
    public DateTime? GeladenOp { get; private set; }

    public ILogboek Logboek { get; }
    public OpvolgOpslag Opvolging { get; }

    /// <summary>Uit- en instroom die klaargezet is voor een later moment.</summary>
    public GeplandOpslag Gepland { get; }

    /// <summary>Geplande acties waarvan het moment voorbij is (wachten op jouw bevestiging).</summary>
    public int AantalVervallen
    {
        get
        {
            try
            {
                return Gepland.Lees().Count(a => a.IsVervallen(DateTimeOffset.Now));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Persoonlijke weergavekeuzes: kolommen van lijsten, dichtgeklapte menugroepen.</summary>
    public WeergaveOpslag Weergave { get; }

    private readonly Dictionary<string, TabelSessie> _tabelSessies = new();

    /// <summary>Filters, pagina en selectie van een lijst, zolang de app open is.</summary>
    public TabelSessie TabelSessie(string id)
    {
        lock (_tabelSessies)
        {
            return _tabelSessies.TryGetValue(id, out var t) ? t : _tabelSessies[id] = new TabelSessie();
        }
    }

    /// <summary>Zoektermen, filters en half ingevulde formulieren per pagina, zolang de app open is (nooit geheimen).</summary>
    public Paginastatus Paginastatus { get; } = new();

    /// <summary>Wat er nog openstaat (niet bewaard, plan klaar, formulier ingevuld): tint, balk en bolletjes in het menu.</summary>
    public Openstaand Openstaand => _openstaand ??= new Openstaand(Paginastatus, VastOpenstaand);
    private Openstaand? _openstaand;

    /// <summary>Organisatie aangepast maar nog niet bewaard (aantal aanpassingen); 0 na bewaren of opnieuw laden.</summary>
    public int ConfigAanpassingen { get; private set; }

    /// <summary>Het scherm Organisatie paste de configuratie aan (in het geheugen, nog niet bewaard).</summary>
    public void ConfigAangepast()
    {
        ConfigAanpassingen++;
        Openstaand.Ververs();
    }

    /// <summary>Fouten in de organisatie die bewaren blokkeren.</summary>
    public static List<string> ConfigFouten(OrganisatieConfig config)
    {
        var fouten = new List<string>();
        foreach (var dubbel in config.Diensten.GroupBy(d => OrganisatieConfig.Normaliseer(d.Department)).Where(g => g.Count() > 1))
        {
            fouten.Add($"De dienst \"{dubbel.First().Department.Trim()}\" staat {dubbel.Count()} keer in de lijst.");
        }

        if (config.Diensten.Any(d => string.IsNullOrWhiteSpace(d.Department)))
        {
            fouten.Add("Een dienst heeft geen naam.");
        }

        foreach (var dubbel in config.Afdelingen.GroupBy(a => OrganisatieConfig.Normaliseer(a.Naam)).Where(g => g.Count() > 1))
        {
            fouten.Add($"De afdeling \"{dubbel.First().Naam}\" staat {dubbel.Count()} keer in de lijst.");
        }

        return fouten;
    }

    private IEnumerable<OpenItem> VastOpenstaand()
    {
        if (ConfigAanpassingen > 0)
        {
            yield return new OpenItem("organisatie", OpenSoort.Instellingen,
                $"Niet bewaard: {ConfigAanpassingen} {(ConfigAanpassingen == 1 ? "wijziging" : "wijzigingen")}")
            {
                Aantal = ConfigAanpassingen,
                Scherm = "Organisatie",
                Bewaren = ConfigFouten(Config).Count == 0 ? () => { BewaarConfig(); return Task.CompletedTask; } : null,
                Verwerpen = () => { LaadConfig(); return Task.CompletedTask; },
            };
        }

        if (InstellingenGewijzigd)
        {
            yield return new OpenItem("verbindingen", OpenSoort.Instellingen, "Niet bewaard: de verbindingsinstellingen zijn aangepast")
            {
                Aantal = 1,
                Scherm = "Verbindingen",
                Bewaren = () => { BewaarInstellingen(); return Task.CompletedTask; },
                Verwerpen = () => { VerwerpInstellingen(); return Task.CompletedTask; },
            };
        }
    }

    /// <summary>De verbindingsinstellingen wijken af van wat bewaard is (het AD-wachtwoord telt nooit mee).</summary>
    public bool InstellingenGewijzigd => JsonSerializer.Serialize(Instellingen, JsonOpties) != _instellingenBewaard;
    private string? _instellingenBewaard;

    /// <summary>Terug naar de bewaarde verbindingsinstellingen.</summary>
    public void VerwerpInstellingen()
    {
        Instellingen = LeesInstellingen();
        _instellingenBewaard = JsonSerializer.Serialize(Instellingen, JsonOpties);
        Gewijzigd?.Invoke();
    }

    /// <summary>De bezochte pagina's, voor Terug en Vooruit.</summary>
    public Geschiedenis Geschiedenis { get; } = new();

    public VerbindingInstellingen Instellingen { get; private set; }

    /// <summary>Het AD-wachtwoord van deze sessie; wordt nooit bewaard.</summary>
    public string? AdWachtwoord { get; set; }

    public bool KanVerbinden => _fabriek is not null;

    /// <summary>Controle van deze pc; null buiten de Windows-app.</summary>
    public ISysteemControle? Controle => _fabriek?.Controle;

    /// <summary>Lopende aanmelding bij Microsoft: de link die de app toont om te kopiëren.</summary>
    public AanmeldStatus Aanmelding { get; }

    /// <summary>Waarom Exchange niet met dezelfde aanmelding als Graph kon (dan meldt Exchange apart aan).</summary>
    public string? ExchangeMelding => _fabriek?.ExchangeMelding;

    /// <summary>Wie wijzigingen uitvoert: de Graph-aanmelding als die er is, anders de Windows-gebruiker.</summary>
    public string Gebruiker => Graph()?.AangemeldAls ?? Environment.UserName;

    /// <summary>De profielen van wie aangemeld is (app-rollen uit Entra), of de overgangsregel zolang er geen zijn.</summary>
    public Rol Rollen => Rechtentabel.Bepaal(Graph()?.AppRollen, Config.ProfielenVerplicht);

    public bool Mag(Recht recht) => Rechtentabel.Mag(Rollen, recht);

    /// <summary>Geen rollen in de aanmelding: tekst voor onderaan het menu (overgang of enkel lezen), anders null.</summary>
    public string? ProfielMelding => Graph()?.AppRollen is { Count: > 0 } r && Rechtentabel.UitClaims(r) != Rol.Geen
        ? null
        : Config.ProfielenVerplicht
            ? Graph()?.AppRollen is null ? "Nog niet aangemeld: enkel lezen tot je aanmeldt." : "Geen profiel in Moss365: enkel lezen."
            : "Profielen nog niet ingesteld: iedereen mag alles.";

    public bool ConfigIsAangepast => ConfigBron is not null
                                      && string.Equals(Path.GetFullPath(ConfigBron), Path.GetFullPath(_omgeving.ConfigGebruikersPad), StringComparison.OrdinalIgnoreCase);

    public int AantalOpvolgtaken => Opvolging.Lees().Count;

    private readonly ITekstOpslag _instellingenOpslag;

    /// <summary>De databank van de app (logboek, gepland, opvolging, weergave, verbinding, toegangslog).</summary>
    public Databank Databank { get; }

    /// <summary>Wat er bij deze start van losse bestanden naar de databank overgezet werd.</summary>
    public IReadOnlyList<string> Overgezet { get; }

    // ------------------------------------------------------------------ inventaris en organisatie

    public void KiesMap(string? map)
    {
        VoorkeurMap = string.IsNullOrWhiteSpace(map) ? null : map.Trim();
        LaadInventaris();
    }

    public void LaadInventaris()
    {
        try
        {
            Mappen = InventarisLader.ZoekMappen(_omgeving.InventarisBasis);
            var volgorde = VoorkeurMap is null
                ? Mappen
                : new[] { VoorkeurMap }.Concat(Mappen.Where(m => !string.Equals(m, VoorkeurMap, StringComparison.OrdinalIgnoreCase))).ToList();
            Data = InventarisLader.Laad(volgorde);
            GeladenOp = DateTime.Now;
            Fout = null;
            WisLiveStatus();
            HerstelSnelStand();
        }
        catch (Exception ex)
        {
            Fout = "Inventaris laden mislukt: " + ex.Message;
        }

        Gewijzigd?.Invoke();
    }

    /// <summary>Waarom de organisatie niet (volledig) geladen kon worden; blijft staan tot het opgelost is.</summary>
    public string? ConfigWaarschuwing { get; private set; }

    public void LaadConfig()
    {
        try
        {
            (Config, ConfigBron, ConfigWaarschuwing) = ConfigOpslag.LaadVeilig(_omgeving.ConfigStandaardPad, _omgeving.ConfigGebruikersPad);
        }
        catch (Exception ex)
        {
            Config = new OrganisatieConfig();
            ConfigBron = null;
            ConfigWaarschuwing = "Organisatie laden mislukt: " + ex.Message;
        }

        ConfigAanpassingen = 0;
        Gewijzigd?.Invoke();
    }

    public void BewaarConfig()
    {
        ConfigOpslag.Bewaar(Config, _omgeving.ConfigGebruikersPad);
        ConfigBron = _omgeving.ConfigGebruikersPad;
        ConfigAanpassingen = 0;
        Gewijzigd?.Invoke();
    }

    /// <summary>Verwijdert de eigen wijzigingen en laadt opnieuw de meegeleverde organisatie.json.</summary>
    public void HerstelStandaardConfig()
    {
        if (File.Exists(_omgeving.ConfigGebruikersPad))
        {
            File.Delete(_omgeving.ConfigGebruikersPad);
        }

        LaadConfig();
    }

    // ------------------------------------------------------------------ verbindingen

    private VerbindingInstellingen LeesInstellingen()
    {
        try
        {
            return _instellingenOpslag.Lees() is { } json
                ? JsonSerializer.Deserialize<VerbindingInstellingen>(json, JsonOpties) ?? new VerbindingInstellingen()
                : new VerbindingInstellingen();
        }
        catch (Exception)
        {
            return new VerbindingInstellingen();
        }
    }

    public void BewaarInstellingen()
    {
        Directory.CreateDirectory(_omgeving.GegevensMap);
        _instellingenBewaard = JsonSerializer.Serialize(Instellingen, JsonOpties);
        _instellingenOpslag.Bewaar(_instellingenBewaard);
        Gewijzigd?.Invoke();
    }

    private IGraphVerbinding? Graph()
    {
        if (_fabriek is null || !Instellingen.GraphIngevuld)
        {
            return null;
        }

        var sleutel = $"{Instellingen.TenantId}|{Instellingen.ClientId}|{Instellingen.BeheerUpn}";
        if (_graph is null || _graphSleutel != sleutel)
        {
            _graph = _fabriek.Graph(Instellingen);
            _graphSleutel = sleutel;
        }

        return _graph;
    }

    /// <summary>Eigenaars van een cloudgroep, live uit Graph; null als Graph niet ingesteld is.</summary>
    public async Task<IReadOnlyList<GraphEigenaar>?> LeesEigenaarsAsync(string groepId, CancellationToken annuleren = default)
        => Graph() is { } graph ? await graph.LeesEigenaarsAsync(groepId, annuleren) : null;

    /// <summary>Aanmeldingen live uit Entra (één gebruiker, of iedereen met id null); null als Graph niet ingesteld is.</summary>
    public async Task<IReadOnlyList<Aanmelding>?> LeesAanmeldingenAsync(string? gebruikerId, int dagen, bool alleenMislukt, CancellationToken annuleren = default)
    {
        if (Graph() is not { } graph)
        {
            return null;
        }

        // Aanmeldingen zijn persoonsgegevens (IP-adressen, plaatsen): wie ze bekijkt, komt in het toegangslog
        Toegang("Aanmeldingen bekeken", $"{(gebruikerId is null ? "alle gebruikers" : "gebruiker " + gebruikerId)}, {dagen} dagen{(alleenMislukt ? ", enkel mislukt" : string.Empty)}");
        return await graph.LeesAanmeldingenAsync(gebruikerId, dagen, alleenMislukt, annuleren);
    }

    /// <summary>
    /// De agenda van een room, live uit Graph. Geeft de afspraken, of een melding die zegt waarom het niet lukt (geen
    /// Graph, geen leesrecht op de agenda). Wie een agenda bekijkt, komt in het toegangslog (organisatoren zijn personen).
    /// </summary>
    public async Task<(IReadOnlyList<Afspraak>? Afspraken, string? Melding)> LeesAgendaAsync(Room room, DateTime van, DateTime tot, CancellationToken annuleren = default)
    {
        if (Graph() is not { } graph)
        {
            return (null, "Graph is niet ingesteld (Verbindingen).");
        }

        Toegang("Agenda bekeken", $"room {room.Smtp}, {van:dd/MM/yyyy} tot {tot.AddDays(-1):dd/MM/yyyy}");
        try
        {
            return (await graph.LeesAgendaAsync(room.Smtp, van, tot, annuleren), null);
        }
        catch (GeenRechtFout ex)
        {
            return (null, "Je account mag de agenda van deze room niet lezen. Geef jezelf minstens het recht Reviewer (of LimitedDetails) "
                + "op de agenda: bij Wijzigen > Agendarechten op deze pagina, of in Exchange met Add-MailboxFolderPermission "
                + $"\"{room.Smtp}:\\Calendar\". Daarna opnieuw laden. ({ex.Message})");
        }
    }

    /// <summary>Het effect van de Conditional Access-beleiden in de laatste aanmeldingen (gedeeld door de schermen).</summary>
    public IReadOnlyList<Core.Analyse.CaEffectRegel>? CaEffect { get; private set; }

    /// <summary>Uitleg bij het effect: periode en aantal gelezen aanmeldingen.</summary>
    public string? CaEffectUitleg { get; private set; }

    public const int CaEffectMax = 5000;

    /// <summary>Leest de aanmeldingen (tot <see cref="CaEffectMax"/>) en telt per beleid hoe het uitviel.</summary>
    public async Task LaadCaEffectAsync(int dagen, CancellationToken annuleren = default)
    {
        if (Data.ConditionalAccess is not { } ca)
        {
            return;
        }

        if (Graph() is not { } graph)
        {
            throw new InvalidOperationException("Vul eerst de Entra-gegevens in (scherm Verbindingen).");
        }

        Toegang("Aanmeldingen bekeken", $"effect Conditional Access, alle gebruikers, {dagen} dagen");
        var aanmeldingen = await graph.LeesAanmeldingenAsync(dagen, CaEffectMax, annuleren);
        CaEffect = Core.Analyse.CaEffect.Bereken(aanmeldingen, ca);
        CaEffectUitleg = $"{aanmeldingen.Count} aanmeldingen van de laatste {dagen} dagen"
                         + (aanmeldingen.Count >= CaEffectMax ? $" (de nieuwste {CaEffectMax}; er zijn er meer)" : string.Empty)
                         + $", gelezen om {DateTime.Now:HH:mm}.";
        Gewijzigd?.Invoke();
    }

    /// <summary>De systemen in het plan waarvoor nog geen verbinding ingesteld is.</summary>
    public IReadOnlyList<Systeem> OntbrekendeVerbindingen(IEnumerable<Wijziging> plan)
    {
        var nodig = plan.Where(w => w.Geselecteerd).Select(w => w.Systeem).Distinct();
        return nodig.Where(s => s switch
        {
            Systeem.ActiveDirectory => _fabriek is null || !Instellingen.AdIngevuld,
            Systeem.Graph => _fabriek is null || !Instellingen.GraphIngevuld,
            _ => _fabriek is null,
        }).ToList();
    }

    public async Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> plan, string context, IProgress<string>? voortgang, CancellationToken annuleren = default)
    {
        if (_fabriek is null)
        {
            throw new InvalidOperationException("Wijzigingen uitvoeren is niet beschikbaar in deze omgeving.");
        }

        var uitvoerders = new List<IUitvoerder>();
        if (_fabriek.Ad(Instellingen, AdWachtwoord) is IUitvoerder ad)
        {
            uitvoerders.Add(ad);
        }

        if (Graph() is IUitvoerder graph)
        {
            uitvoerders.Add(graph);
        }

        if (_fabriek.Exchange(Instellingen) is IUitvoerder exchange)
        {
            uitvoerders.Add(exchange);
        }

        // Eerst aanmelden, zodat het profiel gekend is; daarna controleert de motor elke wijziging tegen dat profiel
        if (Config.ProfielenVerplicht && Graph() is { AppRollen: null } g)
        {
            try
            {
                await g.AanmeldenAsync(annuleren);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Niet aangemeld: de motor weigert dan alles wat meer vraagt dan lezen
            }
        }

        var rollen = Rollen;
        var data = Data;
        var config = Config;
        var gebruiker = Gebruiker;
        // De motor weigert wat het profiel niet toelaat, en elke wijziging aan Conditional Access die de noodaccounts niet
        // uitsluit of jezelf buitensluit (ook bij ongedaan maken of een gepland plan)
        var resultaten = await new PlanUitvoerder(uitvoerders, Logboek, () => Gebruiker,
                w => Rechtentabel.Weigering(rollen, w, data) ?? CaVeiligheid.Weigering(w, data, config, gebruiker))
            .VoerUitAsync(plan, context, voortgang, annuleren);
        // Geen context: die bevat vaak een naam ("Uitstroom ..."); enkel aantallen en soorten acties
        Log.Information("Plan uitgevoerd: {Gelukt} gelukt, {Mislukt} mislukt, {Overgeslagen} overgeslagen; acties {Acties}",
            resultaten.Count(r => r.Status == ResultaatStatus.Gelukt), resultaten.Count(r => r.Status == ResultaatStatus.Mislukt),
            resultaten.Count(r => r.Status == ResultaatStatus.Overgeslagen), string.Join(", ", plan.Where(w => w.Geselecteerd).Select(w => w.Actie).Distinct()));
        NaUitvoeren(plan, resultaten);
        Gewijzigd?.Invoke();
        return resultaten;
    }

    /// <summary>Controleert een opvolgtaak: bestaat de gebruiker al in Entra, dan worden de resterende stappen uitgevoerd.</summary>
    public async Task<string> WerkOpvolgingAfAsync(Guid taakId, IProgress<string>? voortgang, CancellationToken annuleren = default)
    {
        var taken = Opvolging.Lees();
        var taak = taken.FirstOrDefault(t => t.Id == taakId) ?? throw new InvalidOperationException("Taak niet gevonden.");
        var graph = Graph() ?? throw new InvalidOperationException("Vul eerst de Entra-gegevens in (scherm Verbindingen).");

        taak.LaatstGecontroleerd = DateTimeOffset.Now;
        var id = await graph.ZoekGebruikerIdAsync(taak.Upn, annuleren);
        if (id is null)
        {
            taak.LaatsteMelding = "Nog niet in Entra. Entra Connect synchroniseert standaard om de 30 minuten.";
        }
        else
        {
            foreach (var w in taak.Wijzigingen)
            {
                w.Geselecteerd = true;
            }

            var resultaten = await VoerUitAsync(taak.Wijzigingen, "Opvolging " + taak.Omschrijving, voortgang, annuleren);
            var gelukt = resultaten.Where(r => r.Status == ResultaatStatus.Gelukt).Select(r => r.WijzigingId).ToHashSet();
            taak.Wijzigingen.RemoveAll(w => gelukt.Contains(w.Id));
            taak.LaatsteMelding = taak.Wijzigingen.Count == 0
                ? "Alles afgewerkt."
                : $"{gelukt.Count} gelukt, {taak.Wijzigingen.Count} nog open: " + string.Join("; ", resultaten.Where(r => r.Status != ResultaatStatus.Gelukt).Select(r => r.Melding).Distinct());
        }

        var melding = taak.LaatsteMelding;
        Opvolging.Bewaar(taken.Where(t => t.Wijzigingen.Count > 0));
        Gewijzigd?.Invoke();
        return melding;
    }

    public void Inplannen(GeplandeActie actie)
    {
        Gepland.Voegtoe(actie);
        Gewijzigd?.Invoke();
    }

    public void VerwijderGepland(Guid id)
    {
        Gepland.Verwijder(id);
        Gewijzigd?.Invoke();
    }

    public void VoegOpvolgingToe(OpvolgTaak taak)
    {
        Opvolging.Voegtoe(taak);
        Gewijzigd?.Invoke();
    }

    public void VerwijderOpvolging(Guid taakId)
    {
        Opvolging.Bewaar(Opvolging.Lees().Where(t => t.Id != taakId));
        Gewijzigd?.Invoke();
    }
}
