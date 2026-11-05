using Moss365.Core.Storage;
using System.Text.Json;

namespace Moss365.UI.Services;

/// <summary>How a list is shown: order of the columns, hidden columns and sorting.</summary>
public sealed class TableView
{
    public List<string> Order { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    /// <summary>Columns that are hidden by default but that the user wants to see.</summary>
    public List<string> Shown { get; set; } = new();

    public string? Sort { get; set; }
    public bool Descending { get; set; }

    /// <summary>Rows per page: 250, 500 or 0 (everything); null = the default of the list.</summary>
    public int? PerPage { get; set; }
}

public enum FilterKind
{
    /// <summary>From the values: dates and numbers a range, few distinct values a choice, otherwise text.</summary>
    Automatic,
    No,
    Text,
    Choice,
    Scope,
}

/// <summary>The filter on a column: text that must be in it, chosen values, or a range (from, to).</summary>
public sealed class ColumnFilter
{
    public string Text { get; set; } = string.Empty;
    public HashSet<string> Choices { get; } = new(StringComparer.CurrentCultureIgnoreCase);
    public string Of { get; set; } = string.Empty;
    public string Until { get; set; } = string.Empty;

    public bool Active => Text.Trim().Length > 0 || Choices.Count > 0 || Of.Trim().Length > 0 || Until.Trim().Length > 0;
}

/// <summary>
/// What you did in a list while the app is open: filters per column, the page and the selection. Kept when you go to
/// another screen and come back (Back and Forward), not after a restart.
/// </summary>
public sealed class TableSession
{
    public Dictionary<string, ColumnFilter> Filters { get; } = new();
    public int Page { get; set; }
    public bool ShowFilters { get; set; }
    public HashSet<string> Selected { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ColumnFilter Filter(string column) => Filters.TryGetValue(column, out var f) ? f : Filters[column] = new ColumnFilter();

    public int CountActive => Filters.Values.Count(f => f.Active);
}

/// <summary>Personal view choices (tables, menu), saved in weergave.json so that they survive a restart.</summary>
public sealed class ViewStorage
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true }.WithLegacyNames();

    private readonly Moss365.Core.Storage.ITextStorage _storage;
    private readonly object _lock = new();
    private File _content;

    public ViewStorage(string path) : this(new Moss365.Core.Storage.FileText(path))
    {
    }

    /// <summary>Since 0.8 in the database (document "weergave").</summary>
    public ViewStorage(Moss365.Core.Storage.ITextStorage storage)
    {
        _storage = storage;
        _content = Read();
    }

    public sealed class File
    {
        public Dictionary<string, TableView> Tables { get; set; } = new();
        public List<string> CollapsedMenuGroups { get; set; } = new();
    }

    public TableView Table(string id)
    {
        lock (_lock)
        {
            return _content.Tables.TryGetValue(id, out var t) ? t : _content.Tables[id] = new TableView();
        }
    }

    public void RestoreTable(string id)
    {
        lock (_lock)
        {
            _content.Tables.Remove(id);
        }

        Save();
    }

    public bool MenuGroupClosed(string groupValue)
    {
        lock (_lock)
        {
            return _content.CollapsedMenuGroups.Contains(groupValue);
        }
    }

    public void SetMenuGroup(string groupValue, bool closed)
    {
        lock (_lock)
        {
            _content.CollapsedMenuGroups.Remove(groupValue);
            if (closed)
            {
                _content.CollapsedMenuGroups.Add(groupValue);
            }
        }

        Save();
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_content, Options);
            }

            _storage.Save(json);
        }
        catch (Exception)
        {
            // View choices are not important enough to let the app fail
        }
    }

    private File Read()
    {
        try
        {
            return _storage.Read() is { } json ? JsonSerializer.Deserialize<File>(json, Options) ?? new File() : new File();
        }
        catch (Exception)
        {
            return new File();
        }
    }
}

/// <summary>A column of a <c>KbTable</c>.</summary>
public sealed class Column<T>
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required Microsoft.AspNetCore.Components.RenderFragment<T> Cell { get; init; }

    /// <summary>Value to sort by; null = not sortable.</summary>
    public Func<T, object?>? SortBy { get; init; }

    public bool Number { get; init; }

    /// <summary>Hidden by default (the user can show it).</summary>
    public bool Hidden { get; init; }

    /// <summary>How you filter on this column; derived from the values by default. Columns without a value: no
    /// filter.</summary>
    public FilterKind Filter { get; init; } = FilterKind.Automatic;

    /// <summary>Text for the export; the sort value by default.</summary>
    public Func<T, string?>? Text { get; init; }

    public string TextOf(T item)
    {
        if (Text is not null)
        {
            return Text(item) ?? string.Empty;
        }

        return SortBy?.Invoke(item) switch
        {
            null => string.Empty,
            DateTime d => d.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset d => d.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
            var w => w.ToString() ?? string.Empty,
        };
    }
}

/// <summary>Compares sort values: text case-insensitively, numbers and dates as such, empty at the end.</summary>
public sealed class SortComparer : IComparer<object?>
{
    public static readonly SortComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        var xEmpty = x is null || (x is string xs && xs.Length == 0);
        var yEmpty = y is null || (y is string ys && ys.Length == 0);
        if (xEmpty || yEmpty)
        {
            return xEmpty == yEmpty ? 0 : xEmpty ? 1 : -1;
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
