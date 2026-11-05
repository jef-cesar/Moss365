using System.Globalization;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.UI.Services;

/// <summary>Wat er live geladen wordt.</summary>
public enum LaadSoort
{
    AdEnEntra,
    Rooms,
    Postvakken,
    Alles,
}

/// <summary>
/// Achtergrondtaken (live laden, objecten vernieuwen) en hoe actueel de gegevens zijn. De taak leeft hier en niet in een
/// scherm, zodat de voortgang blijft als je van scherm verandert; het menu toont een korte status.
/// </summary>
public sealed partial class BeheerState
{
    /// <summary>Hoeveel live bewaarde inventarissen de app bijhoudt; oudere worden opgeruimd.</summary>
    public const int BewaardeLiveInventarissen = 10;

    private readonly object _taakSlot = new();
    private readonly Dictionary<string, DateTime> _liveOp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _gelezen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _gewijzigd = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>De laatste (of lopende) achtergrondtaak.</summary>
    public AchtergrondTaak? Taak { get; private set; }

    public bool TaakBezig => Taak?.Bezig == true;

    /// <summary>Per onderdeel ("AD", "Entra", "Rooms", "Postvakken") wanneer het volledig live geladen is.</summary>
    public IReadOnlyDictionary<string, DateTime> LiveOp => _liveOp;

    public bool IsLive => _liveOp.Count > 0;

    /// <summary>Objecten die gewijzigd zijn maar nog niet opnieuw gelezen.</summary>
    public int AantalGewijzigd
    {
        get
        {
            lock (_taakSlot)
            {
                return _gewijzigd.Count;
            }
        }
    }

    /// <summary>De map waarin de laatste live gegevens als inventaris bewaard zijn.</summary>
    public string? BewaardeInventaris { get; private set; }

    public bool IsGewijzigd(ObjectRef r)
    {
        lock (_taakSlot)
        {
            return _gewijzigd.Contains(VernieuwDoelen.Sleutel(r));
        }
    }

    /// <summary>Waar de getoonde gegevens van een object vandaan komen, eerlijk: per object, per onderdeel of de inventaris.</summary>
    public string HerkomstVan(ObjectRef r)
    {
        lock (_taakSlot)
        {
            if (_gelezen.TryGetValue(VernieuwDoelen.Sleutel(r), out var t))
            {
                return $"live gelezen om {t:HH:mm}";
            }
        }

        var (delen, bestand) = r.Soort switch
        {
            ObjectSoort.Postvak => (new[] { "Postvakken" }, InventarisLader.ExoPostvakken),
            ObjectSoort.Room => (new[] { "Rooms" }, InventarisLader.ExoRooms),
            _ => (new[] { "AD", "Entra" }, InventarisLader.AdGebruikers),
        };
        var live = delen.Where(_liveOp.ContainsKey).Select(d => $"{d} live om {_liveOp[d]:HH:mm}").ToList();
        return live.Count > 0 ? string.Join(", ", live) : "uit de inventaris" + (InventarisDatum(bestand) is { } dt ? $" van {dt:dd/MM HH:mm}" : string.Empty);
    }

    /// <summary>Datum van de inventarismap waaruit een bestand geladen is (de naam bevat yyyyMMdd-HHmm).</summary>
    public DateTime? InventarisDatum(string bestand)
    {
        var map = Data.Bronnen.FirstOrDefault(b => b.Bestand == bestand)?.Map;
        var naam = map is null ? null : Path.GetFileName(map);
        return naam is { Length: >= 24 } && DateTime.TryParseExact(naam.Substring(11, 13), "yyyyMMdd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    // ------------------------------------------------------------------ starten

    /// <summary>
    /// Een stap van een achtergrondtaak. Stappen met hetzelfde <see cref="Spoor"/> lopen na elkaar; verschillende sporen
    /// lopen tegelijk (AD, Entra en Exchange). Stappen zonder spoor lopen pas als alle sporen klaar zijn.
    /// </summary>
    private sealed record TaakWerk(string Naam, string? Spoor, Func<IProgress<string>, CancellationToken, Task> Werk);

    private readonly object _dataSlot = new();

    /// <summary>Werkt de gegevens bij met wat een stap las; één tegelijk, zodat sporen elkaars resultaat niet overschrijven.</summary>
    private InventarisData WerkDataBij(Func<InventarisData, InventarisData> wijziging)
    {
        lock (_dataSlot)
        {
            Data = wijziging(Data);
            return Data;
        }
    }

    /// <summary>Start live laden op de achtergrond; false als er al een taak loopt of verbinden niet kan.</summary>
    public bool StartLaden(LaadSoort soort)
    {
        if (_fabriek is null)
        {
            return false;
        }

        var stappen = new List<TaakWerk>();
        var onderdelen = InventarisOnderdeel.Geen;
        void Gelezen(InventarisOnderdeel o)
        {
            lock (_taakSlot)
            {
                onderdelen |= o;
            }
        }

        if (soort is LaadSoort.AdEnEntra or LaadSoort.Alles)
        {
            if (Instellingen.AdIngevuld)
            {
                stappen.Add(new("AD lezen", "AD", async (p, ct) =>
                {
                    var ad = _fabriek.Ad(Instellingen, AdWachtwoord) ?? throw new InvalidOperationException("AD is hier niet beschikbaar.");
                    var gelezen = await ad.LeesAsync(AdBasisVoorSnel(), p, ct);
                    WerkDataBij(d => LiveLader.Samenvoegen(d, gelezen, null));
                    OnthoudStand(gelezen);
                    Live("AD", ObjectSoort.Gebruiker, ObjectSoort.Groep);
                    Gelezen(InventarisOnderdeel.Ad);
                }));
            }

            if (Instellingen.GraphIngevuld)
            {
                stappen.Add(new("Entra lezen", "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld.");
                    var gelezen = await graph.LeesAsync(GraphBasisVoorSnel(), p, ct);
                    WerkDataBij(d => LiveLader.Samenvoegen(d, null, gelezen));
                    OnthoudDelta(gelezen);
                    Live("Entra", ObjectSoort.Gebruiker, ObjectSoort.Groep);
                    Gelezen(InventarisOnderdeel.Entra);
                }));

                stappen.Add(new("Beveiliging lezen (MFA en beheerrollen)", "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld.");
                    if (await graph.LeesBeveiligingAsync(p, ct) is not { } beveiliging)
                    {
                        p.Report("Niet beschikbaar in deze omgeving.");
                        return;
                    }

                    WerkDataBij(d => LiveLader.MetBeveiliging(d, beveiliging));
                    var problemen = new[] { beveiliging.MfaMelding, beveiliging.RollenMelding }.Where(m => m is not null).ToList();
                    if (problemen.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(" ", problemen));
                    }
                }));

                stappen.Add(new("Conditional Access lezen", "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld.");
                    if (await graph.LeesConditionalAccessAsync(p, ct) is not { } ca)
                    {
                        p.Report("Niet beschikbaar in deze omgeving.");
                        return;
                    }

                    WerkDataBij(d => LiveLader.MetConditionalAccess(d, ca));
                    if (ca.Melding is not null)
                    {
                        throw new InvalidOperationException(ca.Melding);
                    }

                    BewaarCaBackup(ca, p);
                }));
            }

            if (stappen.Count == 0)
            {
                Fout = "Vul eerst de AD- en/of Entra-gegevens in (scherm Verbindingen).";
                Gewijzigd?.Invoke();
                return false;
            }
        }

        if (soort == LaadSoort.Alles)
        {
            // Rooms en postvakken in één Exchange-sessie: één keer aanmelden en de module één keer laden
            stappen.Add(new("Rooms, postvakken en groepen lezen (Exchange)", "Exchange", async (p, ct) =>
            {
                var exchange = _fabriek.Exchange(Instellingen) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                var lezing = await exchange.LeesAllesAsync(p, ct);
                if (lezing.Rooms is { } rooms)
                {
                    WerkDataBij(d => LiveLader.MetRooms(d, rooms));
                    Live("Rooms", ObjectSoort.Room);
                    Gelezen(InventarisOnderdeel.Rooms);
                }

                if (lezing.Postvakken is { } postvakken)
                {
                    WerkDataBij(d => LiveLader.MetExchange(d, postvakken));
                    Live("Postvakken", ObjectSoort.Postvak, ObjectSoort.Groep);
                    Gelezen(InventarisOnderdeel.Postvakken);
                }

                var fouten = new[] { lezing.RoomsFout is null ? null : "Rooms: " + lezing.RoomsFout, lezing.PostvakkenFout is null ? null : "Postvakken: " + lezing.PostvakkenFout }
                    .Where(f => f is not null).ToList();
                if (fouten.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(" ", fouten));
                }
            }));
        }

        if (soort == LaadSoort.Rooms)
        {
            stappen.Add(new("Rooms lezen (Exchange)", "Exchange", async (p, ct) =>
            {
                var exchange = _fabriek.Exchange(Instellingen) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                var rooms = await exchange.LeesRoomsAsync(p, ct);
                WerkDataBij(d => LiveLader.MetRooms(d, rooms));
                Live("Rooms", ObjectSoort.Room);
                Gelezen(InventarisOnderdeel.Rooms);
            }));
        }

        if (soort == LaadSoort.Postvakken)
        {
            stappen.Add(new("Postvakken en groepen lezen (Exchange)", "Exchange", async (p, ct) =>
            {
                var exchange = _fabriek.Exchange(Instellingen) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                var gegevens = await exchange.LeesPostvakkenAsync(p, ct);
                WerkDataBij(d => LiveLader.MetExchange(d, gegevens));
                Live("Postvakken", ObjectSoort.Postvak, ObjectSoort.Groep);
                Gelezen(InventarisOnderdeel.Postvakken);
            }));
        }

        // Pas als alle sporen klaar zijn
        stappen.Add(new("Bewaren als inventaris", null, (p, _) =>
        {
            if (onderdelen == InventarisOnderdeel.Geen)
            {
                p.Report("Niets live gelezen; niets bewaard.");
                return Task.CompletedTask;
            }

            BewaarAlsInventaris(onderdelen, p);
            return Task.CompletedTask;
        }));

        var naam = soort switch
        {
            LaadSoort.AdEnEntra => "Live laden (AD en Entra)",
            LaadSoort.Rooms => "Rooms laden",
            LaadSoort.Postvakken => "Postvakken laden",
            _ => "Alles live laden",
        };
        return Start(naam, stappen);
    }

    /// <summary>Enkele objecten opnieuw lezen (knop Vernieuwen).</summary>
    public bool StartVernieuwen(IEnumerable<ObjectRef> objecten)
    {
        var lijst = objecten.ToList();
        var doelen = VernieuwDoelen.Voor(Data, lijst);
        return StartVernieuwen(doelen, lijst.Count == 1 ? $"Vernieuwen: {lijst[0].Naam}" : $"Vernieuwen ({doelen.Aantal})");
    }

    private bool StartVernieuwen(VernieuwDoelen doelen, string naam)
    {
        if (_fabriek is null || doelen.Leeg)
        {
            return false;
        }

        var stappen = new List<TaakWerk>();
        if (doelen.AdGebruikers.Count + doelen.AdGroepen.Count > 0)
        {
            stappen.Add(new($"AD ({doelen.AdGebruikers.Count + doelen.AdGroepen.Count})", "AD", async (p, ct) =>
            {
                var ad = _fabriek.Ad(Instellingen, AdWachtwoord) ?? throw new InvalidOperationException("AD is niet ingesteld (scherm Verbindingen).");
                foreach (var sam in doelen.AdGebruikers)
                {
                    p.Report($"gebruiker {sam}");
                    var g = await ad.LeesGebruikerAsync(sam, ct);
                    WerkDataBij(d => ObjectVernieuwer.MetAdGebruiker(d, sam, g));
                }

                foreach (var sam in doelen.AdGroepen)
                {
                    p.Report($"groep {sam}");
                    var g = await ad.LeesGroepAsync(sam, ct);
                    WerkDataBij(d => ObjectVernieuwer.MetAdGroep(d, sam, g));
                }
            }));
        }

        if (doelen.EntraGebruikers.Count + doelen.CloudGroepen.Count > 0)
        {
            stappen.Add(new($"Entra ({doelen.EntraGebruikers.Count + doelen.CloudGroepen.Count})", "Entra", async (p, ct) =>
            {
                var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld (scherm Verbindingen).");
                foreach (var id in doelen.EntraGebruikers)
                {
                    p.Report($"gebruiker {id}");
                    var g = await graph.LeesGebruikerAsync(id, ct);
                    WerkDataBij(d => ObjectVernieuwer.MetEntraGebruiker(d, id, g));
                }

                foreach (var id in doelen.CloudGroepen)
                {
                    p.Report($"groep {id}");
                    var g = await graph.LeesGroepAsync(id, ct);
                    WerkDataBij(d => ObjectVernieuwer.MetCloudGroep(d, id, g));
                }
            }));
        }

        if (doelen.ConditionalAccess)
        {
            stappen.Add(new("Conditional Access", "Entra", async (p, ct) =>
            {
                var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld (scherm Verbindingen).");
                if (await graph.LeesConditionalAccessAsync(p, ct) is { Melding: null } ca)
                {
                    WerkDataBij(d => LiveLader.MetConditionalAccess(d, ca));
                    BewaarCaBackup(ca, p);
                }
            }));
        }

        if (doelen.ExchangeAdressen.Count > 0)
        {
            stappen.Add(new($"Exchange ({doelen.ExchangeAdressen.Count})", "Exchange", async (p, ct) =>
            {
                var exchange = _fabriek.Exchange(Instellingen) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                p.Report(string.Join(", ", doelen.ExchangeAdressen));
                var objecten = await exchange.LeesObjectenAsync(doelen.ExchangeAdressen.ToList(), ct);
                WerkDataBij(d => ObjectVernieuwer.MetExchange(d, objecten));
            }));
        }

        return Start(naam, stappen, klaar: taak =>
        {
            // Enkel als alles gelukt is, zijn de objecten weer actueel
            if (taak.Stappen.All(s => s.Status == StapStatus.Klaar))
            {
                lock (_taakSlot)
                {
                    foreach (var sleutel in doelen.Objecten)
                    {
                        _gelezen[sleutel] = DateTime.Now;
                        _gewijzigd.Remove(sleutel);
                    }
                }
            }
        });
    }

    public void AnnuleerTaak()
    {
        try
        {
            Taak?.Annuleren.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Net klaar
        }
    }

    // ------------------------------------------------------------------ uitvoeren

    private bool Start(string naam, List<TaakWerk> stappen, Action<AchtergrondTaak>? klaar = null)
    {
        AchtergrondTaak taak;
        lock (_taakSlot)
        {
            if (TaakBezig)
            {
                return false;
            }

            taak = new AchtergrondTaak(naam, stappen.Select(s => s.Naam));
            Taak = taak;
        }

        Fout = null;
        Gewijzigd?.Invoke();
        _ = Task.Run(() => VoerUitAsync(taak, stappen, klaar));
        return true;
    }

    private async Task VoerUitAsync(AchtergrondTaak taak, List<TaakWerk> stappen, Action<AchtergrondTaak>? klaar)
    {
        var ct = taak.Annuleren.Token;

        // Elk spoor na elkaar, de sporen tegelijk; een fout in één spoor laat de andere verder lopen
        var sporen = stappen.Select((s, i) => (s, i)).Where(x => x.s.Spoor is not null).GroupBy(x => x.s.Spoor!)
            .Select(g => Task.Run(async () =>
            {
                foreach (var (_, i) in g)
                {
                    await VoerStapUitAsync(taak, i, stappen[i], ct);
                }
            }))
            .ToList();
        await Task.WhenAll(sporen);

        foreach (var (s, i) in stappen.Select((s, i) => (s, i)).Where(x => x.s.Spoor is null))
        {
            await VoerStapUitAsync(taak, i, s, ct);
        }

        taak.Geannuleerd |= ct.IsCancellationRequested;
        GeladenOp = DateTime.Now;
        try
        {
            klaar?.Invoke(taak);
        }
        catch (Exception ex)
        {
            taak.Schrijf("Afronden mislukt: " + ex.Message);
        }

        taak.Einde = DateTime.Now;
        Log.Information("{Taak} klaar in {Seconden:0} s: {Stappen}", LogNaam(taak.Naam), (taak.Einde.Value - taak.Start).TotalSeconds,
            string.Join(", ", taak.Stappen.Select(s => $"{s.Naam} {s.Status}")));
        taak.Annuleren.Dispose();
        Gewijzigd?.Invoke();
    }

    private async Task VoerStapUitAsync(AchtergrondTaak taak, int index, TaakWerk werk, CancellationToken ct)
    {
        var stap = taak.Stappen[index];
        if (ct.IsCancellationRequested)
        {
            stap.Status = StapStatus.Geannuleerd;
            return;
        }

        stap.Status = StapStatus.Bezig;
        stap.Start = DateTime.Now;
        taak.Schrijf($"{stap.Naam}...");
        Gewijzigd?.Invoke();
        var melder = new Melder(t =>
        {
            stap.Melding = t;
            taak.Schrijf($"{stap.Naam}: {t}");
            Gewijzigd?.Invoke();
        });
        try
        {
            await werk.Werk(melder, ct);
            stap.Status = StapStatus.Klaar;
            stap.Melding = null;
            taak.Schrijf($"{stap.Naam}: klaar.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            stap.Status = StapStatus.Geannuleerd;
            taak.Geannuleerd = true;
            taak.Schrijf($"{stap.Naam}: geannuleerd.");
        }
        catch (Exception ex)
        {
            stap.Status = StapStatus.Fout;
            stap.Melding = ex.Message;
            taak.Schrijf($"{stap.Naam}: MISLUKT - {ex.Message}");
            Log.Warning("{Taak} - {Stap} mislukt: {Fout}", LogNaam(taak.Naam), stap.Naam, ex.Message);
        }

        stap.Einde = DateTime.Now;
        Gewijzigd?.Invoke();
    }

    /// <summary>Synchrone voortgang: Progress&lt;T&gt; zou meldingen via de threadpool en dus in willekeurige volgorde sturen.</summary>
    private sealed class Melder(Action<string> doe) : IProgress<string>
    {
        public void Report(string value) => doe(value);
    }

    private void Live(string onderdeel, params ObjectSoort[] soorten)
    {
        lock (_taakSlot)
        {
            _liveOp[onderdeel] = DateTime.Now;

            // Wat volledig opnieuw gelezen is, is niet langer "gewijzigd, nog niet vernieuwd"
            var voorvoegsels = soorten.Select(s => s.ToString().ToLowerInvariant() + ":").ToList();
            _gewijzigd.RemoveWhere(k => voorvoegsels.Any(v => k.StartsWith(v, StringComparison.Ordinal)));
        }
    }

    private void BewaarAlsInventaris(InventarisOnderdeel onderdelen, IProgress<string> p)
    {
        var map = InventarisSchrijver.Bewaar(Data, _omgeving.InventarisBasis, onderdelen, DateTime.Now);
        BewaardeInventaris = map;
        BewaarSnelStand(map, onderdelen);
        p.Report($"Bewaard in {Path.GetFileName(map)}.");
        if ((onderdelen & (InventarisOnderdeel.Ad | InventarisOnderdeel.Entra)) != 0)
        {
            BewaarKpiStand();
        }

        // Enkel de laatste live inventarissen houden (die van Export-Inventaris.ps1 blijven staan)
        foreach (var oud in InventarisLader.ZoekMappen(_omgeving.InventarisBasis)
                     .Where(m => m.EndsWith("-live", StringComparison.OrdinalIgnoreCase))
                     .Skip(BewaardeLiveInventarissen))
        {
            try
            {
                Directory.Delete(oud, recursive: true);
            }
            catch (Exception)
            {
                // Niet erg; volgende keer opnieuw
            }
        }

        Mappen = InventarisLader.ZoekMappen(_omgeving.InventarisBasis);
    }

    /// <summary>Na een uitgevoerd plan: betrokken objecten markeren en op de achtergrond opnieuw lezen.</summary>
    private void NaUitvoeren(IReadOnlyList<Wijziging> plan, IReadOnlyList<WijzigingResultaat> resultaten)
    {
        var doelen = VernieuwDoelen.NaPlan(Data, plan, resultaten);
        lock (_taakSlot)
        {
            foreach (var sleutel in doelen.Objecten)
            {
                _gewijzigd.Add(sleutel);
                _gelezen.Remove(sleutel);
            }
        }

        StartVernieuwen(doelen, "Vernieuwen na wijziging");
    }

    /// <summary>Na het opnieuw laden van een inventaris van schijf is niets meer live.</summary>
    private void WisLiveStatus()
    {
        lock (_taakSlot)
        {
            _liveOp.Clear();
            _gelezen.Clear();
        }
    }

    /// <summary>De naam van een taak voor het technisch log, zonder de naam van een object ("Vernieuwen: An Peeters").</summary>
    private static string LogNaam(string taak) => taak.Contains(": ", StringComparison.Ordinal) ? taak[..taak.IndexOf(": ", StringComparison.Ordinal)] : taak;
}
