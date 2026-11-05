using System.Text.Json;

namespace Moss365.UI.Services;

/// <summary>Hoe een lijst getoond wordt: volgorde van de kolommen, verborgen kolommen en sortering.</summary>
public sealed class TabelWeergave
{
    public List<string> Volgorde { get; set; } = new();
    public List<string> Verborgen { get; set; } = new();

    /// <summary>Kolommen die standaard verborgen zijn maar die de gebruiker wil zien.</summary>
    public List<string> Getoond { get; set; } = new();

    public string? Sorteer { get; set; }
    public bool Aflopend { get; set; }

    /// <summary>Rijen per pagina: 250, 500 of 0 (alles); null = de standaard van de lijst.</summary>
    public int? PerPagina { get; set; }
}

public enum FilterSoort
{
    /// <summary>Uit de waarden: datums en getallen een bereik, weinig verschillende waarden een keuze, anders tekst.</summary>
    Automatisch,
    Geen,
    Tekst,
    Keuze,
    Bereik,
}

/// <summary>Het filter op een kolom: tekst die erin moet staan, gekozen waarden, of een bereik (van, tot).</summary>
public sealed class KolomFilter
{
    public string Tekst { get; set; } = string.Empty;
    public HashSet<string> Keuzes { get; } = new(StringComparer.CurrentCultureIgnoreCase);
    public string Van { get; set; } = string.Empty;
    public string Tot { get; set; } = string.Empty;

    public bool Actief => Tekst.Trim().Length > 0 || Keuzes.Count > 0 || Van.Trim().Length > 0 || Tot.Trim().Length > 0;
}

/// <summary>
/// Wat je in een lijst deed zolang de app open is: filters per kolom, de pagina en de selectie. Blijft bewaard als je
/// naar een ander scherm gaat en terugkomt (Terug en Vooruit), niet na een herstart.
/// </summary>
public sealed class TabelSessie
{
    public Dictionary<string, KolomFilter> Filters { get; } = new();
    public int Pagina { get; set; }
    public bool FiltersTonen { get; set; }
    public HashSet<string> Gekozen { get; } = new(StringComparer.OrdinalIgnoreCase);

    public KolomFilter Filter(string kolom) => Filters.TryGetValue(kolom, out var f) ? f : Filters[kolom] = new KolomFilter();

    public int AantalActief => Filters.Values.Count(f => f.Actief);
}

/// <summary>Persoonlijke weergavekeuzes (tabellen, menu), bewaard in weergave.json zodat ze een herstart overleven.</summary>
public sealed class WeergaveOpslag
{
    private static readonly JsonSerializerOptions Opties = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly Moss365.Core.Opslag.ITekstOpslag _opslag;
    private readonly object _slot = new();
    private Bestand _inhoud;

    public WeergaveOpslag(string pad) : this(new Moss365.Core.Opslag.BestandTekst(pad))
    {
    }

    /// <summary>Sinds 0.8 in de databank (document "weergave").</summary>
    public WeergaveOpslag(Moss365.Core.Opslag.ITekstOpslag opslag)
    {
        _opslag = opslag;
        _inhoud = Lees();
    }

    public sealed class Bestand
    {
        public Dictionary<string, TabelWeergave> Tabellen { get; set; } = new();
        public List<string> DichtgeklapteMenugroepen { get; set; } = new();
    }

    public TabelWeergave Tabel(string id)
    {
        lock (_slot)
        {
            return _inhoud.Tabellen.TryGetValue(id, out var t) ? t : _inhoud.Tabellen[id] = new TabelWeergave();
        }
    }

    public void HerstelTabel(string id)
    {
        lock (_slot)
        {
            _inhoud.Tabellen.Remove(id);
        }

        Bewaar();
    }

    public bool MenugroepDicht(string groep)
    {
        lock (_slot)
        {
            return _inhoud.DichtgeklapteMenugroepen.Contains(groep);
        }
    }

    public void ZetMenugroep(string groep, bool dicht)
    {
        lock (_slot)
        {
            _inhoud.DichtgeklapteMenugroepen.Remove(groep);
            if (dicht)
            {
                _inhoud.DichtgeklapteMenugroepen.Add(groep);
            }
        }

        Bewaar();
    }

    public void Bewaar()
    {
        try
        {
            string json;
            lock (_slot)
            {
                json = JsonSerializer.Serialize(_inhoud, Opties);
            }

            _opslag.Bewaar(json);
        }
        catch (Exception)
        {
            // Weergavekeuzes zijn niet belangrijk genoeg om de app te laten falen
        }
    }

    private Bestand Lees()
    {
        try
        {
            return _opslag.Lees() is { } json ? JsonSerializer.Deserialize<Bestand>(json, Opties) ?? new Bestand() : new Bestand();
        }
        catch (Exception)
        {
            return new Bestand();
        }
    }
}

/// <summary>Een kolom van een <c>KbTabel</c>.</summary>
public sealed class Kolom<T>
{
    public required string Sleutel { get; init; }
    public required string Titel { get; init; }
    public required Microsoft.AspNetCore.Components.RenderFragment<T> Cel { get; init; }

    /// <summary>Waarde om op te sorteren; null = niet sorteerbaar.</summary>
    public Func<T, object?>? SorteerOp { get; init; }

    public bool Getal { get; init; }

    /// <summary>Standaard verborgen (de gebruiker kan hem tonen).</summary>
    public bool Verborgen { get; init; }

    /// <summary>Hoe je op deze kolom filtert; standaard afgeleid uit de waarden. Kolommen zonder waarde: geen filter.</summary>
    public FilterSoort Filter { get; init; } = FilterSoort.Automatisch;

    /// <summary>Tekst voor de export; standaard de sorteerwaarde.</summary>
    public Func<T, string?>? Tekst { get; init; }

    public string TekstVan(T item)
    {
        if (Tekst is not null)
        {
            return Tekst(item) ?? string.Empty;
        }

        return SorteerOp?.Invoke(item) switch
        {
            null => string.Empty,
            DateTime d => d.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset d => d.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
            var w => w.ToString() ?? string.Empty,
        };
    }
}

/// <summary>Vergelijkt sorteerwaarden: tekst zonder hoofdlettergevoeligheid, getallen en datums als zodanig, leeg achteraan.</summary>
public sealed class SorteerVergelijker : IComparer<object?>
{
    public static readonly SorteerVergelijker Instantie = new();

    public int Compare(object? x, object? y)
    {
        var xLeeg = x is null || (x is string xs && xs.Length == 0);
        var yLeeg = y is null || (y is string ys && ys.Length == 0);
        if (xLeeg || yLeeg)
        {
            return xLeeg == yLeeg ? 0 : xLeeg ? 1 : -1;
        }

        if (x is string a && y is string b)
        {
            return StringComparer.CurrentCultureIgnoreCase.Compare(a, b);
        }

        if (x is IComparable c && x.GetType() == y!.GetType())
        {
            return c.CompareTo(y);
        }

        return StringComparer.CurrentCultureIgnoreCase.Compare(Convert.ToString(x), Convert.ToString(y));
    }
}
