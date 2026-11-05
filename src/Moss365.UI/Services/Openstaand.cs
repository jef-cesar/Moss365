namespace Moss365.UI.Services;

public enum OpenSoort
{
    /// <summary>Aangepaste instellingen die nog niet bewaard zijn (Organisatie, Verbindingen).</summary>
    Instellingen,

    /// <summary>Een ingevuld formulier dat nog niet uitgevoerd is (bewaard in de paginastatus).</summary>
    Formulier,

    /// <summary>Een plan dat klaarstaat maar nog niet uitgevoerd is.</summary>
    Plan,
}

/// <summary>Iets dat op een scherm nog openstaat. <see cref="Pad"/> is het adres van het scherm, zonder zoekterm.</summary>
public sealed record OpenItem(string Pad, OpenSoort Soort, string Tekst)
{
    public int Aantal { get; init; }

    /// <summary>Naam van het scherm voor de lijst onderaan het menu en bij het sluiten, bv. "Organisatie".</summary>
    public string Scherm { get; init; } = string.Empty;

    public Func<Task>? Bewaren { get; init; }
    public Func<Task>? Verwerpen { get; init; }

    /// <summary>CSS-selector van het plan op de pagina, voor de knop "Naar het plan".</summary>
    public string? Anker { get; init; }

    public string Icoon => Soort switch { OpenSoort.Plan => "▶", _ => "✎" };

    /// <summary>Het eerste stuk van het adres: daarmee hoort het bij een menu-item (groep/x hoort bij Groepen).</summary>
    public string Menu => Openstaand.MenuVan(Pad);
}

/// <summary>
/// Wat er in de app nog openstaat: niet-bewaarde instellingen, plannen die klaarstaan en ingevulde formulieren. Een
/// scherm meldt zich aan en af (<see cref="Zet"/>, <see cref="Weg"/>); <c>PlanWeergave</c> doet dat zelf voor elk plan.
/// Het werkvlak krijgt dan een lichte tint met een balk, en het menu een bolletje. Formulieren komen uit de
/// <see cref="Paginastatus"/> (enkel als ze anders zijn dan leeg).
/// </summary>
public sealed class Openstaand
{
    private readonly Dictionary<(string, OpenSoort, object?), OpenItem> _items = new();
    private readonly Paginastatus _paginastatus;
    private readonly object _slot = new();

    public Openstaand(Paginastatus paginastatus, Func<IEnumerable<OpenItem>>? vast = null)
    {
        _paginastatus = paginastatus;
        _vast = vast;
    }

    // Wat de app zelf weet, los van een scherm: niet-bewaarde organisatie en verbindingsinstellingen
    private readonly Func<IEnumerable<OpenItem>>? _vast;

    public event Action? Gewijzigd;

    /// <summary>Meldt iets aan (of werkt het bij). Eén item per scherm, soort en eigenaar (bv. de PlanWeergave).</summary>
    public void Zet(OpenItem item, object? eigenaar = null)
    {
        var sleutel = (Normaliseer(item.Pad), item.Soort, eigenaar);
        lock (_slot)
        {
            if (_items.TryGetValue(sleutel, out var oud) && oud.Tekst == item.Tekst && oud.Aantal == item.Aantal)
            {
                _items[sleutel] = item with { Pad = sleutel.Item1 };
                return;
            }

            _items[sleutel] = item with { Pad = sleutel.Item1 };
        }

        Gewijzigd?.Invoke();
    }

    public void Weg(string pad, OpenSoort soort, object? eigenaar = null)
    {
        bool weg;
        lock (_slot)
        {
            weg = _items.Remove((Normaliseer(pad), soort, eigenaar));
        }

        if (weg)
        {
            Gewijzigd?.Invoke();
        }
    }

    /// <summary>Iets veranderde aan een formulier: het menu en de balk opnieuw tekenen.</summary>
    public void Ververs() => Gewijzigd?.Invoke();

    /// <summary>Alles wat openstaat. Een formulier telt niet als er op hetzelfde scherm al een plan klaarstaat.</summary>
    public IReadOnlyList<OpenItem> Alles
    {
        get
        {
            List<OpenItem> lijst;
            lock (_slot)
            {
                lijst = _items.Values.ToList();
            }

            if (_vast is not null)
            {
                lijst.AddRange(_vast().Select(i => i with { Pad = Normaliseer(i.Pad) }));
            }

            var metPlan = lijst.Where(i => i.Soort == OpenSoort.Plan).Select(i => i.Pad).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _paginastatus.IngevuldeFormulieren())
            {
                var pad = Normaliseer(f.Pad);
                if (!metPlan.Contains(pad))
                {
                    lijst.Add(new OpenItem(pad, OpenSoort.Formulier, "Ingevuld, nog niet uitgevoerd") { Scherm = f.Scherm });
                }
            }

            return lijst.OrderBy(i => i.Soort).ThenBy(i => i.Pad, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>Wat openstaat op dit scherm (het adres zonder zoekterm).</summary>
    public IReadOnlyList<OpenItem> Op(string pad)
    {
        var p = Normaliseer(pad);
        return Alles.Where(i => string.Equals(i.Pad, p, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Staat er iets open onder dit menu-item?</summary>
    public bool OnderMenu(string href)
    {
        var m = MenuVan(href);
        return Alles.Any(i => string.Equals(i.Menu, m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adres zonder zoekterm, zonder schuine strepen aan de randen en zonder %-codes.</summary>
    public static string Normaliseer(string pad)
    {
        var i = pad.IndexOfAny(new[] { '?', '#' });
        var p = (i < 0 ? pad : pad[..i]).Trim('/');
        try
        {
            return Uri.UnescapeDataString(p);
        }
        catch (UriFormatException)
        {
            return p;
        }
    }

    /// <summary>Detailpagina's horen bij hun lijst in het menu: groep/x bij groepen, gebruiker/x bij gebruikers.</summary>
    public static string MenuVan(string pad)
    {
        var p = Normaliseer(pad);
        var eerste = p.Split('/')[0].ToLowerInvariant();
        return eerste switch
        {
            "gebruiker" => "gebruikers",
            "groep" => "groepen",
            "postvak" => "postvakken",
            "room" => "rooms",
            "postvakken" when p.Equals("postvakken/nieuw", StringComparison.OrdinalIgnoreCase) => "postvakken",
            _ => eerste,
        };
    }
}
