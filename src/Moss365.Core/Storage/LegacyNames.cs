using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Moss365.Core.Storage;

/// <summary>
/// Reads data saved before 0.11, when property and enum names were Dutch. Writing always uses the current names, so a
/// file or row is upgraded the next time it is saved. The tables are in <c>LegacyNames.g.cs</c>.
/// </summary>
public static partial class LegacyNames
{
    /// <summary>
    /// Adds the old property names as extra, read-only names to every type these options read, and reads enum values by
    /// their old names. Use it on every <see cref="JsonSerializerOptions"/> that reads saved data.
    /// </summary>
    public static JsonSerializerOptions WithLegacyNames(this JsonSerializerOptions options)
    {
        var resolver = options.TypeInfoResolver as DefaultJsonTypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info => AddAliases(info, options));
        options.TypeInfoResolver = resolver;
        for (var i = 0; i < options.Converters.Count; i++)
        {
            if (options.Converters[i] is JsonStringEnumConverter)
            {
                options.Converters[i] = new LegacyEnumConverter();
            }
        }

        return options;
    }

    /// <summary>Parses an enum value by its current name, or by the name it had before 0.11.</summary>
    public static bool TryParseEnum<T>(string? text, out T value)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        if (Enum.TryParse(text, ignoreCase: true, out value) && !int.TryParse(text, out _))
        {
            return true;
        }

        if (EnumMembers.TryGetValue(typeof(T).Name, out var members) && members.TryGetValue(text.Trim(), out var current))
        {
            return Enum.TryParse(current, out value);
        }

        // A number is accepted only when it is a defined value (older versions saved some enums as numbers)
        if (int.TryParse(text, out var n) && Enum.IsDefined(typeof(T), n))
        {
            value = (T)Enum.ToObject(typeof(T), n);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Like <see cref="TryParseEnum{T}"/>, but throws when the text is not a known name.</summary>
    public static T ParseEnum<T>(string text)
        where T : struct, Enum
        => TryParseEnum<T>(text, out var value) ? value : throw new ArgumentException($"Unknown {typeof(T).Name}: {text}", nameof(text));

    /// <summary>The current name of a property that may still carry its old name (a key in saved data).</summary>
    public static string CurrentProperty(string name)
    {
        foreach (var (current, old) in Properties)
        {
            if (old.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return current;
            }
        }

        return name;
    }

    private static void AddAliases(JsonTypeInfo info, JsonSerializerOptions options)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var comparer = options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var usedNames = new HashSet<string>(info.Properties.Select(p => p.Name), comparer);
        foreach (var property in info.Properties.ToList())
        {
            if (property.AttributeProvider is not MemberInfo member || property.Set is null
                || !Properties.TryGetValue(member.Name, out var oldNames))
            {
                continue;
            }

            foreach (var old in oldNames)
            {
                var name = options.PropertyNamingPolicy?.ConvertName(old) ?? old;
                if (!usedNames.Add(name))
                {
                    continue;
                }

                // A required property may now come under its old name: the check moves to the type (see OnDeserialized)
                property.IsRequired = false;
                var alias = info.CreateJsonPropertyInfo(property.PropertyType, name);
                alias.Get = null;
                alias.Set = property.Set;
                alias.CustomConverter = property.CustomConverter;
                info.Properties.Add(alias);
            }
        }
    }
}

/// <summary>
/// Writes enums as their name and reads the current name, the name before 0.11 or a number. Replaces
/// <see cref="JsonStringEnumConverter"/> for saved data.
/// </summary>
public sealed class LegacyEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => (Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert).IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var underlying = Nullable.GetUnderlyingType(typeToConvert);
        return underlying is null
            ? (JsonConverter)Activator.CreateInstance(typeof(EnumConverter<>).MakeGenericType(typeToConvert))!
            : (JsonConverter)Activator.CreateInstance(typeof(NullableEnumConverter<>).MakeGenericType(underlying))!;
    }

    private sealed class EnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
            {
                return (T)Enum.ToObject(typeof(T), n);
            }

            var text = reader.GetString();
            return LegacyNames.TryParseEnum<T>(text, out var value)
                ? value
                : throw new JsonException($"Unknown {typeof(T).Name}: {text}");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class NullableEnumConverter<T> : JsonConverter<T?>
        where T : struct, Enum
    {
        private readonly EnumConverter<T> _inner = new();

        public override bool HandleNull => true;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Null ? null : _inner.Read(ref reader, typeof(T), options);

        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
        {
            if (value is { } v)
            {
                _inner.Write(writer, v, options);
            }
            else
            {
                writer.WriteNullValue();
            }
        }
    }
}
