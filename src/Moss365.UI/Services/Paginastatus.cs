using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.UI.Services;

/// <summary>
/// Wat je op een pagina invulde (zoekterm, filters, een half ingevuld formulier), zolang de app open is: met Terug,
/// Vooruit of via het menu kom je terug waar je was. Alleen in het geheugen; bij het afsluiten is alles weg.
/// Een pagina vraagt een object op met <see cref="Van{T}"/> en bindt rechtstreeks aan dat object, zodat er niets
/// apart bewaard hoeft te worden. Een formulier vergeet je na het uitvoeren of met Leegmaken (<see cref="Vergeet"/>).
/// </summary>
public sealed class Paginastatus
{
    // Namen die op een geheim wijzen: zo'n veld hoort hier nooit (wachtwoorden, tokens). Elk type wordt gecontroleerd.
    private static readonly string[] Verboden = { "wachtwoord", "password", "token", "geheim", "secret" };

    private readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _slot = new();

    /// <summary>Het bewaarde object voor deze sleutel (bv. "gebruikers" of "groepen/ad:x/wijzigen"), of een nieuw.</summary>
    public T Van<T>(string sleutel, Func<T>? nieuw = null) where T : class, new()
    {
        ControleerType(typeof(T));
        lock (_slot)
        {
            if (_items.TryGetValue(sleutel, out var o) && o is T t)
            {
                return t;
            }

            t = nieuw?.Invoke() ?? new T();
            _items[sleutel] = t;
            return t;
        }
    }

    private sealed record FormulierInfo(string Pad, string Scherm, Func<bool> Ingevuld);

    private readonly Dictionary<string, FormulierInfo> _formulieren = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Vergelijk = new() { ReferenceHandler = ReferenceHandler.IgnoreCycles };

    /// <summary>Een ingevuld formulier op een scherm, zoals <see cref="Van{T}"/>; het telt als "openstaand" (tint en bolletje in
    /// het menu) zodra het anders is dan bij het begin, of als <paramref name="ingevuld"/> dat zegt. Weg na <see cref="Vergeet"/>.</summary>
    public T Formulier<T>(string sleutel, string pad, string scherm, Func<T>? nieuw = null, Func<T, bool>? ingevuld = null) where T : class, new()
    {
        lock (_slot)
        {
            var bestond = _items.ContainsKey(sleutel);
            var t = Van(sleutel, nieuw);
            if (!bestond || !_formulieren.ContainsKey(sleutel))
            {
                var begin = JsonSerializer.Serialize(nieuw?.Invoke() ?? new T(), Vergelijk);
                _formulieren[sleutel] = new FormulierInfo(pad, scherm,
                    ingevuld is not null ? () => ingevuld(t) : () => JsonSerializer.Serialize(t, Vergelijk) != begin);
            }

            return t;
        }
    }

    /// <summary>De formulieren die nu anders zijn dan leeg (voor <see cref="Openstaand"/>).</summary>
    public IReadOnlyList<(string Pad, string Scherm)> IngevuldeFormulieren()
    {
        List<FormulierInfo> lijst;
        lock (_slot)
        {
            lijst = _formulieren.Where(f => _items.ContainsKey(f.Key)).Select(f => f.Value).ToList();
        }

        return lijst.Where(f => f.Ingevuld()).Select(f => (f.Pad, f.Scherm)).ToList();
    }

    /// <summary>Is er voor deze sleutel al iets bewaard (de pagina werd eerder geopend)?</summary>
    public bool Heeft(string sleutel)
    {
        lock (_slot)
        {
            return _items.ContainsKey(sleutel);
        }
    }

    /// <summary>Vergeet wat er voor deze sleutel bewaard is (na uitvoeren of Leegmaken).</summary>
    public void Vergeet(string sleutel)
    {
        lock (_slot)
        {
            _items.Remove(sleutel);
            _formulieren.Remove(sleutel);
        }
    }

    /// <summary>Alle bewaarde objecten (voor tests en controle).</summary>
    public IReadOnlyDictionary<string, object> Alles
    {
        get
        {
            lock (_slot)
            {
                return new Dictionary<string, object>(_items, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// Weigert een type met een tekstveld waarvan de naam op een geheim wijst (wachtwoord, token), ook in geneste types.
    /// Zo kan niemand later per ongeluk een wachtwoord in een bewaard formulier zetten.
    /// </summary>
    public static void ControleerType(Type type)
    {
        lock (Gecontroleerd)
        {
            if (Gecontroleerd.Contains(type))
            {
                return;
            }
        }

        Controleer(type, new HashSet<Type>());
        lock (Gecontroleerd)
        {
            Gecontroleerd.Add(type);
        }
    }

    private static readonly HashSet<Type> Gecontroleerd = new();

    private static void Controleer(Type type, HashSet<Type> gezien)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime)
            || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid)
            || !gezien.Add(type))
        {
            return;
        }

        if (Nullable.GetUnderlyingType(type) is { } onder)
        {
            Controleer(onder, gezien);
            return;
        }

        if (type.IsGenericType && type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                Controleer(arg, gezien);
            }

            return;
        }

        // Systeemtypes en gelezen gegevens (een gekozen persoon als spiegel) bevatten zelf geen ingevulde geheimen
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
            || type.Namespace == "Moss365.Core.Inventaris")
        {
            return;
        }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var leden = type.GetProperties(flags).Select(p => (p.Name, p.PropertyType))
            .Concat(type.GetFields(flags).Where(f => !f.Name.Contains("k__BackingField", StringComparison.Ordinal)).Select(f => (f.Name, f.FieldType)));
        foreach (var (naam, lidType) in leden)
        {
            var tekst = lidType == typeof(string) || lidType == typeof(char[]) || lidType.Name == "SecureString";
            if (tekst && Verboden.Any(v => naam.Contains(v, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"{type.Name}.{naam} lijkt een geheim en mag niet bewaard worden in de paginastatus.");
            }

            Controleer(lidType, gezien);
        }
    }
}
