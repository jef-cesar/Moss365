namespace Moss365.UI.Services;

/// <summary>
/// The pages you visited in the app, with a position: Back and Forward go one step back or further. A new page after
/// Back clears the steps after it (as in a browser). Only another search term in the address of the same page (e.g.
/// <c>opkuis?dagen=90</c> after <c>opkuis</c>) replaces the current step instead of creating one.
/// </summary>
public sealed class History
{
    private readonly List<string> _steps = new();
    private int _position = -1;
    private int? _expected;

    public IReadOnlyList<string> Steps => _steps;
    public int Position => _position;
    public string? Current => _position >= 0 ? _steps[_position] : null;
    public bool CanUndo => _position > 0;
    public bool CanForward => _position >= 0 && _position < _steps.Count - 1;

    /// <summary>
    /// The page the last Back or Forward went to, until the next normal navigation. So a page knows (also before
    /// <see cref="Visited"/> runs) whether it came back and what was filled in has to stay.
    /// </summary>
    private string? _jump;

    /// <summary>Did the app come to this page with Back or Forward?</summary>
    public bool IsJumpTo(string uri) => _jump is not null && Equal(_jump, uri);

    /// <summary>Where Back goes (and remembers that the next navigation is that step), or null.</summary>
    public string? Back() => Go(-1);

    public string? Forward() => Go(1);

    private string? Go(int direction)
    {
        var target = _position + direction;
        if (_position < 0 || target < 0 || target >= _steps.Count)
        {
            return null;
        }

        _expected = target;
        _jump = _steps[target];
        return _steps[target];
    }

    /// <summary>The app came to this page. Returns whether it was a step back or forward.</summary>
    public bool Visited(string uri)
    {
        if (_position < 0)
        {
            _steps.Add(uri);
            _position = 0;
            return false;
        }

        // Via our buttons (or the mouse and Alt+arrow, which come the same way)
        if (_expected is { } target && target < _steps.Count && Equal(_steps[target], uri))
        {
            _expected = null;
            _position = target;
            return true;
        }

        _expected = null;
        _jump = null;
        if (Equal(_steps[_position], uri))
        {
            return false;
        }

        // The same page with another search term: update the step
        if (WithoutSearchTerm(_steps[_position]) == WithoutSearchTerm(uri))
        {
            _steps[_position] = uri;
            return false;
        }

        _steps.RemoveRange(_position + 1, _steps.Count - _position - 1);
        _steps.Add(uri);
        _position = _steps.Count - 1;
        return false;
    }

    private static bool Equal(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string WithoutSearchTerm(string uri)
    {
        var i = uri.IndexOfAny(new[] { '?', '#' });
        return (i < 0 ? uri : uri[..i]).TrimEnd('/').ToLowerInvariant();
    }
}
