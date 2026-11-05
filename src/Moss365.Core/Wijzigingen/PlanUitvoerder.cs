using System.Text.Encodings.Web;
using System.Text.Json;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Eén regel in het logboek: wie, wanneer, wat, met welk resultaat. Bevat nooit geheimen. PlanId, WijzigingId en Vorige
/// (de waarden vóór de wijziging) bestaan sinds 0.5; oudere regels hebben ze niet.
/// </summary>
public sealed record LogRegel(
    DateTimeOffset Tijd,
    string Gebruiker,
    string Context,
    string Systeem,
    string Actie,
    string Doel,
    string DoelNaam,
    string Omschrijving,
    IReadOnlyDictionary<string, string> Parameters,
    string Status,
    string Melding,
    Guid? PlanId = null,
    Guid? WijzigingId = null,
    IReadOnlyDictionary<string, string>? Vorige = null);

public interface ILogboek
{
    void Schrijf(LogRegel regel);

    IReadOnlyList<LogRegel> Recent(int aantal);
}

/// <summary>Logboek als JSON-regels, één bestand per maand (logboek-yyyy-MM.jsonl).</summary>
public sealed class JsonlLogboek : ILogboek
{
    private static readonly JsonSerializerOptions Opties = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _map;
    private readonly object _slot = new();

    public JsonlLogboek(string map) => _map = map;

    public string Map => _map;

    public void Schrijf(LogRegel regel)
    {
        lock (_slot)
        {
            Directory.CreateDirectory(_map);
            var pad = Path.Combine(_map, $"logboek-{regel.Tijd:yyyy-MM}.jsonl");
            File.AppendAllText(pad, JsonSerializer.Serialize(regel, Opties) + Environment.NewLine);
        }
    }

    public IReadOnlyList<LogRegel> Recent(int aantal)
    {
        lock (_slot)
        {
            if (!Directory.Exists(_map))
            {
                return Array.Empty<LogRegel>();
            }

            var regels = new List<LogRegel>();
            foreach (var bestand in Directory.GetFiles(_map, "logboek-*.jsonl").OrderByDescending(f => f, StringComparer.Ordinal))
            {
                foreach (var lijn in File.ReadLines(bestand).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(lijn))
                    {
                        continue;
                    }

                    try
                    {
                        var regel = JsonSerializer.Deserialize<LogRegel>(lijn, Opties);
                        if (regel is not null)
                        {
                            regels.Add(regel);
                        }
                    }
                    catch (JsonException)
                    {
                        // Een beschadigde regel slaan we over; de rest van het logboek blijft leesbaar
                    }

                    if (regels.Count >= aantal)
                    {
                        return regels;
                    }
                }
            }

            return regels;
        }
    }
}

/// <summary>
/// Voert een plan uit in de volgorde van het plan. Opeenvolgende wijzigingen van hetzelfde systeem gaan samen naar de
/// uitvoerder (Exchange start dan maar één keer een PowerShell-proces). Elke wijziging komt in het logboek.
/// </summary>
public sealed class PlanUitvoerder
{
    private readonly IReadOnlyDictionary<Systeem, IUitvoerder> _uitvoerders;
    private readonly ILogboek _logboek;
    private readonly Func<string> _gebruiker;
    private readonly Func<Wijziging, string?>? _weiger;

    /// <param name="weiger">Waarom een wijziging niet mag (het profiel volstaat niet), of null. Een geweigerde wijziging
    /// wordt niet uitgevoerd en komt als "Mislukt" met de reden in het logboek.</param>
    public PlanUitvoerder(IEnumerable<IUitvoerder> uitvoerders, ILogboek logboek, Func<string> gebruiker, Func<Wijziging, string?>? weiger = null)
    {
        _uitvoerders = uitvoerders.ToDictionary(u => u.Systeem);
        _logboek = logboek;
        _gebruiker = gebruiker;
        _weiger = weiger;
    }

    public async Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(
        IReadOnlyList<Wijziging> plan,
        string context,
        IProgress<string>? voortgang = null,
        CancellationToken annuleren = default)
    {
        var resultaten = new Dictionary<Guid, WijzigingResultaat>();
        var nietGedaan = plan.Where(w => !w.Geselecteerd).Select(w => w.Id).ToHashSet();

        var index = 0;
        while (index < plan.Count)
        {
            var systeem = plan[index].Systeem;
            var reeks = new List<Wijziging>();
            while (index < plan.Count && plan[index].Systeem == systeem)
            {
                var w = plan[index];

                // Hangt deze stap af van een stap in de huidige reeks, dan eerst die reeks uitvoeren
                if (w.AfhankelijkVan is { } binnen && reeks.Any(x => x.Id == binnen))
                {
                    break;
                }

                index++;
                if (!w.Geselecteerd)
                {
                    continue;
                }

                if (w.AfhankelijkVan is { } van && (nietGedaan.Contains(van)
                                                   || (resultaten.TryGetValue(van, out var r) && r.Status != ResultaatStatus.Gelukt)))
                {
                    resultaten[w.Id] = WijzigingResultaat.Overslaan(w, "Overgeslagen: een eerdere stap waar deze van afhangt, is niet gelukt.");
                    nietGedaan.Add(w.Id);
                    continue;
                }

                // De motor controleert zelf het profiel: ook wie de schermen omzeilt, voert niets uit wat niet mag
                if (_weiger?.Invoke(w) is { } reden)
                {
                    resultaten[w.Id] = WijzigingResultaat.Fout(w, "Geweigerd: " + reden);
                    nietGedaan.Add(w.Id);
                    continue;
                }

                reeks.Add(w);
            }

            if (reeks.Count == 0)
            {
                continue;
            }

            voortgang?.Report($"{reeks.Count} wijziging(en) in {reeks[0].SysteemTekst}...");
            IReadOnlyList<WijzigingResultaat> uitkomst;
            if (!_uitvoerders.TryGetValue(systeem, out var uitvoerder))
            {
                uitkomst = reeks.Select(w => WijzigingResultaat.Fout(w, "Geen verbinding met dit systeem.")).ToList();
            }
            else
            {
                try
                {
                    uitkomst = await uitvoerder.VoerUitAsync(reeks, annuleren);
                }
                catch (Exception ex)
                {
                    uitkomst = reeks.Select(w => WijzigingResultaat.Fout(w, ex.Message)).ToList();
                }
            }

            foreach (var w in reeks)
            {
                resultaten[w.Id] = uitkomst.FirstOrDefault(u => u.WijzigingId == w.Id)
                                   ?? WijzigingResultaat.Fout(w, "Geen resultaat ontvangen.");
                if (resultaten[w.Id].Status != ResultaatStatus.Gelukt)
                {
                    nietGedaan.Add(w.Id);
                }
            }
        }

        var gebruiker = _gebruiker();
        var planId = Guid.NewGuid();
        foreach (var w in plan.Where(w => resultaten.ContainsKey(w.Id)))
        {
            var r = resultaten[w.Id];
            _logboek.Schrijf(new LogRegel(DateTimeOffset.Now, gebruiker, context, w.SysteemTekst, w.Actie.ToString(), w.Doel, w.DoelNaam,
                w.Omschrijving, new Dictionary<string, string>(w.Parameters), r.Status.ToString(), r.Melding,
                planId, w.Id, w.Vorige is null ? null : new Dictionary<string, string>(w.Vorige)));
        }

        return plan.Where(w => resultaten.ContainsKey(w.Id)).Select(w => resultaten[w.Id]).ToList();
    }
}
