using System.Globalization;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.UI.Services;

/// <summary>
/// Snel laden: de app onthoudt vanaf waar AD (stand van de domeincontroller) en Entra (deltaLink van de groepen) bij de
/// vorige lezing waren, en vraagt de volgende keer enkel de wijzigingen. Dat kan alleen als de geladen gegevens uit die
/// lezing komen (live, of de live bewaarde inventaris met hetzelfde startpunt). Na 7 dagen leest de app weer alles, als
/// vangnet.
/// </summary>
public sealed partial class BeheerState
{
    public const string AdStandBestand = "ad-stand.txt";
    public const string EntraDeltaBestand = "entra-delta.txt";

    /// <summary>Hoe lang de app met wijzigingen verder mag voor hij weer alles leest.</summary>
    public static readonly TimeSpan VolledigLezenNa = TimeSpan.FromDays(7);

    private (AdStand Stand, DateTime Volledig)? _adStand;
    private (string Link, DateTime Volledig)? _graphDelta;

    /// <summary>Of de volgende AD-lezing enkel de wijzigingen kan vragen.</summary>
    public bool AdKanSnel => Instellingen.SnelLaden && _adStand is { } s && DateTime.Now - s.Volledig < VolledigLezenNa && Data.HeeftAd;

    /// <summary>Of de volgende Entra-lezing de groepen via hun wijzigingen kan lezen.</summary>
    public bool EntraKanSnel => Instellingen.SnelLaden && _graphDelta is { } d && DateTime.Now - d.Volledig < VolledigLezenNa && Data.HeeftEntra;

    private AdBasis? AdBasisVoorSnel()
        => AdKanSnel ? new AdBasis(_adStand!.Value.Stand, Data.AdGebruikers, Data.AdGroepenOpDn.Values.ToList()) : null;

    private GraphBasis? GraphBasisVoorSnel()
        => EntraKanSnel ? new GraphBasis(_graphDelta!.Value.Link, Data.CloudGroepen, Data.CloudLidmaatschap) : null;

    private void OnthoudStand(AdGegevens? gelezen)
    {
        if (gelezen?.Stand is not { } stand)
        {
            _adStand = null;
            return;
        }

        // Een snelle lezing houdt het moment van de laatste volledige lezing
        var volledig = gelezen.Snel && _adStand is { } vorig ? vorig.Volledig : DateTime.Now;
        _adStand = (stand, volledig);
    }

    private void OnthoudDelta(GraphGegevens? gelezen)
    {
        if (gelezen?.DeltaLink is not { } link)
        {
            _graphDelta = null;
            return;
        }

        var volledig = gelezen.Snel && _graphDelta is { } vorig ? vorig.Volledig : DateTime.Now;
        _graphDelta = (link, volledig);
    }

    /// <summary>Het startpunt mee bewaren in de live inventaris, zodat de app na een herstart ook snel kan laden.</summary>
    private void BewaarSnelStand(string map, InventarisOnderdeel onderdelen)
    {
        try
        {
            if (onderdelen.HasFlag(InventarisOnderdeel.Ad) && _adStand is { } a)
            {
                File.WriteAllLines(Path.Combine(map, AdStandBestand), new[]
                {
                    a.Stand.Domeincontroller, a.Stand.HoogsteUsn.ToString(CultureInfo.InvariantCulture), a.Volledig.ToString("o", CultureInfo.InvariantCulture),
                });
            }

            if (onderdelen.HasFlag(InventarisOnderdeel.Entra) && _graphDelta is { } g)
            {
                File.WriteAllLines(Path.Combine(map, EntraDeltaBestand), new[] { g.Link, g.Volledig.ToString("o", CultureInfo.InvariantCulture) });
            }
        }
        catch (Exception)
        {
            // Niet erg: de volgende keer leest de app alles
        }
    }

    /// <summary>
    /// Na het laden van een inventaris van schijf: het startpunt enkel overnemen als alle betrokken bestanden uit dezelfde
    /// live bewaarde map komen als het startpunt; anders past het niet bij de gegevens en leest de app de volgende keer alles.
    /// </summary>
    private void HerstelSnelStand()
    {
        _adStand = null;
        _graphDelta = null;
        try
        {
            if (EnigeMap(InventarisLader.AdGebruikers, InventarisLader.AdGroepen, InventarisLader.AdGroepsleden) is { } adMap
                && File.Exists(Path.Combine(adMap, AdStandBestand)))
            {
                var r = File.ReadAllLines(Path.Combine(adMap, AdStandBestand));
                if (r.Length >= 3 && long.TryParse(r[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usn)
                    && DateTime.TryParse(r[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var volledig))
                {
                    _adStand = (new AdStand(r[0], usn), volledig);
                }
            }

            if (EnigeMap(InventarisLader.EntraGroepen, InventarisLader.EntraGroepsleden) is { } entraMap
                && File.Exists(Path.Combine(entraMap, EntraDeltaBestand)))
            {
                var r = File.ReadAllLines(Path.Combine(entraMap, EntraDeltaBestand));
                if (r.Length >= 2 && r[0].StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase)
                    && DateTime.TryParse(r[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var volledig))
                {
                    _graphDelta = (r[0], volledig);
                }
            }
        }
        catch (Exception)
        {
            _adStand = null;
            _graphDelta = null;
        }
    }

    /// <summary>De map waaruit al deze bestanden geladen zijn, als dat één en dezelfde map is.</summary>
    private string? EnigeMap(params string[] bestanden)
    {
        var mappen = bestanden.Select(b => Data.Bronnen.FirstOrDefault(x => x.Bestand == b)?.Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return mappen.Count == 1 && mappen[0] is { } m && Directory.Exists(m) ? m : null;
    }
}
