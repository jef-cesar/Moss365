namespace Moss365.UI.Services;

public enum OpenKind
{
    /// <summary>Changed settings that are not saved yet (Organisation, Connections).</summary>
    Settings,

    /// <summary>A filled-in form that has not run yet (kept in the page state).</summary>
    Form,

    /// <summary>A plan that is ready but has not run yet.</summary>
    Plan,
}

/// <summary>Something that is still open on a screen. <see cref="FilePath"/> is the address of the screen, without a
/// search term.</summary>
public sealed record OpenItem(string FilePath, OpenKind Kind, string Text)
{
    public int Count { get; init; }

    /// <summary>Name of the screen for the list at the bottom of the menu and on closing, e.g. "Organisatie".</summary>
    public string Screen { get; init; } = string.Empty;

    public Func<Task>? Saving { get; init; }
    public Func<Task>? Discard { get; init; }

    /// <summary>CSS selector of the plan on the page, for the button "Naar het plan".</summary>
    public string? Anchor { get; init; }

    public string Icon => Kind switch { OpenKind.Plan => "▶", _ => "✎" };

    /// <summary>The first part of the address: it links the item to a menu item (groep/x belongs to Groups).</summary>
    public string Menu => Pending.MenuOf(FilePath);
}

/// <summary>
/// What is still open in the app: unsaved settings, plans that are ready and filled-in forms. A screen registers and
/// unregisters (<see cref="Set"/>, <see cref="Route"/>); <c>PlanView</c> does that itself for every plan. The work area
/// then gets a light tint with a bar, and the menu a dot. Forms come from the <see cref="PageState"/> (only when they
/// differ from empty).
/// </summary>
public sealed class Pending
{
    private readonly Dictionary<(string, OpenKind, object?), OpenItem> _items = new();
    private readonly PageState _pageState;
    private readonly object _lock = new();

    public Pending(PageState pageState, Func<IEnumerable<OpenItem>>? fixedValue = null)
    {
        _pageState = pageState;
        _fixed = fixedValue;
    }

    // What the app knows itself, apart from a screen: unsaved organisation and connection settings
    private readonly Func<IEnumerable<OpenItem>>? _fixed;

    public event Action? Changed;

    /// <summary>Registers something (or updates it). One item per screen, kind and owner (e.g. the PlanView).</summary>
    public void Set(OpenItem item, object? owner = null)
    {
        var key = (Normalise(item.FilePath), item.Kind, owner);
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var old) && old.Text == item.Text && old.Count == item.Count)
            {
                _items[key] = item with { FilePath = key.Item1 };
                return;
            }

            _items[key] = item with { FilePath = key.Item1 };
        }

        Changed?.Invoke();
    }

    public void Route(string path, OpenKind kind, object? owner = null)
    {
        bool route;
        lock (_lock)
        {
            route = _items.Remove((Normalise(path), kind, owner));
        }

        if (route)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Something changed in a form: redraw the menu and the bar.</summary>
    public void Reload() => Changed?.Invoke();

    /// <summary>Everything that is open. A form does not count when a plan is already ready on the same
    /// screen.</summary>
    public IReadOnlyList<OpenItem> Everything
    {
        get
        {
            List<OpenItem> list;
            lock (_lock)
            {
                list = _items.Values.ToList();
            }

            if (_fixed is not null)
            {
                list.AddRange(_fixed().Select(i => i with { FilePath = Normalise(i.FilePath) }));
            }

            var withPlan = list.Where(i => i.Kind == OpenKind.Plan).Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in _pageState.FilledForms())
            {
                var path = Normalise(f.FilePath);
                if (!withPlan.Contains(path))
                {
                    list.Add(new OpenItem(path, OpenKind.Form, "Ingevuld, nog niet uitgevoerd") { Screen = f.Screen });
                }
            }

            return list.OrderBy(i => i.Kind).ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>What is open on this screen (the address without a search term).</summary>
    public IReadOnlyList<OpenItem> Op(string path)
    {
        var p = Normalise(path);
        return Everything.Where(i => string.Equals(i.FilePath, p, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Is something open under this menu item?</summary>
    public bool UnderMenu(string href)
    {
        var m = MenuOf(href);
        return Everything.Any(i => string.Equals(i.Menu, m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Address without a search term, without slashes at the edges and without %-codes.</summary>
    public static string Normalise(string path)
    {
        var i = path.IndexOfAny(new[] { '?', '#' });
        var p = (i < 0 ? path : path[..i]).Trim('/');
        try
        {
            return Uri.UnescapeDataString(p);
        }
        catch (UriFormatException)
        {
            return p;
        }
    }

    /// <summary>Detail pages belong to their list in the menu: groep/x to groepen, gebruiker/x to gebruikers.</summary>
    public static string MenuOf(string path)
    {
        var p = Normalise(path);
        var first = p.Split('/')[0].ToLowerInvariant();
        return first switch
        {
            "gebruiker" => "gebruikers",
            "groep" => "groepen",
            "postvak" => "postvakken",
            "room" => "rooms",
            "postvakken" when p.Equals("postvakken/nieuw", StringComparison.OrdinalIgnoreCase) => "postvakken",
            _ => first,
        };
    }
}
