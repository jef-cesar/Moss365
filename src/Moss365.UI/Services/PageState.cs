using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.UI.Services;

/// <summary>
/// What you filled in on a page (search term, filters, a half-filled form), while the app is open: with Back, Forward
/// or via the menu you come back where you were. Only in memory; everything is gone when the app closes. A page asks
/// for an object with <see cref="Of{T}"/> and binds directly to that object, so nothing has to be saved separately. A
/// form is forgotten after running or with Clear (<see cref="Forget"/>).
/// </summary>
public sealed class PageState
{
    // Names that point to a secret: such a field never belongs here (passwords, tokens). Every type is checked.
    private static readonly string[] Forbidden = { "wachtwoord", "password", "token", "geheim", "secret" };

    private readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>The saved object for this key (e.g. "gebruikers" or "groepen/ad:x/wijzigen"), or a new one.</summary>
    public T Of<T>(string key, Func<T>? newValue = null) where T : class, new()
    {
        CheckType(typeof(T));
        lock (_lock)
        {
            if (_items.TryGetValue(key, out var o) && o is T t)
            {
                return t;
            }

            t = newValue?.Invoke() ?? new T();
            _items[key] = t;
            return t;
        }
    }

    private sealed record FormInfo(string FilePath, string Screen, Func<bool> Filled);

    private readonly Dictionary<string, FormInfo> _forms = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Compare = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };

    /// <summary>A filled-in form on a screen, like <see cref="Of{T}"/>; it counts as "pending" (tint and dot in the
    /// menu) as soon as it differs from the start, or when <paramref name="filled"/> says so. Gone after
    /// <see cref="Forget"/>.</summary>
    public T Form<T>(string key, string path, string screen, Func<T>? newValue = null, Func<T, bool>? filled = null) where T : class, new()
    {
        lock (_lock)
        {
            var existed = _items.ContainsKey(key);
            var t = Of(key, newValue);
            if (!existed || !_forms.ContainsKey(key))
            {
                var begin = JsonSerializer.Serialize(newValue?.Invoke() ?? new T(), Compare);
                _forms[key] = new FormInfo(path, screen,
                    filled is not null ? () => filled(t) : () => JsonSerializer.Serialize(t, Compare) != begin);
            }

            return t;
        }
    }

    /// <summary>The forms that now differ from empty (for <see cref="Pending"/>).</summary>
    public IReadOnlyList<(string FilePath, string Screen)> FilledForms()
    {
        List<FormInfo> list;
        lock (_lock)
        {
            list = _forms.Where(f => _items.ContainsKey(f.Key)).Select(f => f.Value).ToList();
        }

        return list.Where(f => f.Filled()).Select(f => (f.FilePath, f.Screen)).ToList();
    }

    /// <summary>Is something already saved for this key (the page was opened before)?</summary>
    public bool Has(string key)
    {
        lock (_lock)
        {
            return _items.ContainsKey(key);
        }
    }

    /// <summary>Forgets what is saved for this key (after running or Clear).</summary>
    public void Forget(string key)
    {
        lock (_lock)
        {
            _items.Remove(key);
            _forms.Remove(key);
        }
    }

    /// <summary>All saved objects (for tests and checks).</summary>
    public IReadOnlyDictionary<string, object> Everything
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, object>(_items, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Refuses a type with a text field whose name points to a secret (password, token), also in nested types. So
    /// nobody can later put a password in a saved form by accident.
    /// </summary>
    public static void CheckType(Type type)
    {
        lock (Checked)
        {
            if (Checked.Contains(type))
            {
                return;
            }
        }

        Check(type, new HashSet<Type>());
        lock (Checked)
        {
            Checked.Add(type);
        }
    }

    private static readonly HashSet<Type> Checked = new();

    private static void Check(Type type, HashSet<Type> seen)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid)
            || !seen.Add(type))
        {
            return;
        }

        if (Nullable.GetUnderlyingType(type) is { } under)
        {
            Check(under, seen);
            return;
        }

        if (type.IsGenericType && type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                Check(arg, seen);
            }

            return;
        }

        // System types and data that was read (a chosen person as a mirror) contain no filled-in secrets themselves
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace == typeof(Moss365.Core.Inventory.Person).Namespace)
        {
            return;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var members = type.GetProperties(flags).Select(p => (p.Name, p.PropertyType))
            .Concat(type.GetFields(flags).Where(f => !f.Name.Contains("k__BackingField", StringComparison.Ordinal)).Select(f => (f.Name, f.FieldType)));
        foreach (var (name, memberType) in members)
        {
            var text = memberType == typeof(string) || memberType == typeof(char[]) || memberType.Name == "SecureString";
            if (text && Forbidden.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{type.Name}.{name} lijkt een geheim en mag niet bewaard worden in de paginastatus.");
            }

            Check(memberType, seen);
        }
    }
}
