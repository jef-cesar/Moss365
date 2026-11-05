namespace Moss365.UI.Services;

/// <summary>
/// De pagina's die je in de app bezocht, met een positie: Terug en Vooruit gaan een stap terug of verder. Een nieuwe
/// pagina na Terug wist de stappen erna (zoals in een browser). Alleen een andere zoekterm in de adresregel van
/// dezelfde pagina (bv. <c>opkuis?dagen=90</c> na <c>opkuis</c>) vervangt de huidige stap in plaats van er een te maken.
/// </summary>
public sealed class Geschiedenis
{
    private readonly List<string> _stappen = new();
    private int _positie = -1;
    private int? _verwacht;

    public IReadOnlyList<string> Stappen => _stappen;
    public int Positie => _positie;
    public string? Huidige => _positie >= 0 ? _stappen[_positie] : null;
    public bool KanTerug => _positie > 0;
    public bool KanVooruit => _positie >= 0 && _positie < _stappen.Count - 1;

    /// <summary>
    /// De pagina waar de laatste Terug of Vooruit naartoe ging, tot de volgende gewone navigatie. Zo weet een pagina
    /// (ook voor <see cref="Bezocht"/> loopt) of ze terugkwam en wat er ingevuld was moet blijven.
    /// </summary>
    private string? _sprong;

    /// <summary>Kwam de app met Terug of Vooruit op deze pagina?</summary>
    public bool IsSprongNaar(string uri) => _sprong is not null && Gelijk(_sprong, uri);

    /// <summary>Waar Terug naartoe gaat (en onthoudt dat de volgende navigatie die stap is), of null.</summary>
    public string? Terug() => Ga(-1);

    public string? Vooruit() => Ga(1);

    private string? Ga(int richting)
    {
        var doel = _positie + richting;
        if (_positie < 0 || doel < 0 || doel >= _stappen.Count)
        {
            return null;
        }

        _verwacht = doel;
        _sprong = _stappen[doel];
        return _stappen[doel];
    }

    /// <summary>De app is op deze pagina gekomen. Geeft terug of het een stap terug of vooruit was.</summary>
    public bool Bezocht(string uri)
    {
        if (_positie < 0)
        {
            _stappen.Add(uri);
            _positie = 0;
            return false;
        }

        // Via onze knoppen (of de muis en Alt+pijl, die langs dezelfde weg komen)
        if (_verwacht is { } doel && doel < _stappen.Count && Gelijk(_stappen[doel], uri))
        {
            _verwacht = null;
            _positie = doel;
            return true;
        }

        _verwacht = null;
        _sprong = null;
        if (Gelijk(_stappen[_positie], uri))
        {
            return false;
        }

        // Dezelfde pagina met een andere zoekterm: de stap bijwerken
        if (ZonderZoekterm(_stappen[_positie]) == ZonderZoekterm(uri))
        {
            _stappen[_positie] = uri;
            return false;
        }

        _stappen.RemoveRange(_positie + 1, _stappen.Count - _positie - 1);
        _stappen.Add(uri);
        _positie = _stappen.Count - 1;
        return false;
    }

    private static bool Gelijk(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string ZonderZoekterm(string uri)
    {
        var i = uri.IndexOfAny(new[] { '?', '#' });
        return (i < 0 ? uri : uri[..i]).TrimEnd('/').ToLowerInvariant();
    }
}
