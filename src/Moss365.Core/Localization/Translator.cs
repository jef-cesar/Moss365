using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Moss365.Core.Localization;

/// <summary>A language of the interface: its code (the dictionary), its own name and the culture for dates and numbers.</summary>
public sealed record LanguageInfo(string Code, string Name, string Culture);

/// <summary>
/// The interface in five languages (R15, 0.38). Every text of the app lives in a dictionary per language
/// (<c>Localization/Texts/&lt;code&gt;.json</c>, embedded), by a stable key. A missing text falls back to English, and
/// only then to the key itself, so a button is never empty. Data from AD and Microsoft 365 and the content of scripts
/// are never translated.
/// </summary>
public sealed class Translator
{
    public const string Fallback = "en";

    public static IReadOnlyList<LanguageInfo> Languages { get; } = new[]
    {
        new LanguageInfo("en", "English", "en-GB"),
        new LanguageInfo("nl", "Nederlands", "nl-BE"),
        new LanguageInfo("pt-BR", "Português (Brasil)", "pt-BR"),
        new LanguageInfo("fr", "Français", "fr-BE"),
        new LanguageInfo("de", "Deutsch", "de-DE"),
    };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Embedded = new(LoadEmbedded);

    private readonly IReadOnlyDictionary<string, string> _texts;
    private readonly IReadOnlyDictionary<string, string> _fallback;

    public Translator(string language, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? dictionaries = null)
    {
        var all = dictionaries ?? Embedded.Value;
        Language = Languages.FirstOrDefault(l => l.Code.Equals(language, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
        _texts = all.GetValueOrDefault(Language.Code) ?? new Dictionary<string, string>();
        _fallback = all.GetValueOrDefault(Fallback) ?? new Dictionary<string, string>();
    }

    public LanguageInfo Language { get; }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language.Culture);

    /// <summary>The text of a key: in the language, else in English, else the key.</summary>
    public string this[string key] => _texts.TryGetValue(key, out var t) && t.Length > 0 ? t : _fallback.TryGetValue(key, out var f) && f.Length > 0 ? f : key;

    public bool Has(string key) => _texts.ContainsKey(key) || _fallback.ContainsKey(key);

    /// <summary>The text of a key, or the given text when no dictionary has the key (texts from older data).</summary>
    public string Text(string key, string otherwise) => Has(key) ? this[key] : otherwise;

    /// <summary>A text with values ({0}, {1}, ...), formatted with the culture of the language.</summary>
    public string Format(string key, params object?[] values) => string.Format(Culture, this[key], values);

    /// <summary>The language for a user: the own choice wins, else the system language, else English.</summary>
    public static string Resolve(string? chosen, CultureInfo system)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && Languages.Any(l => l.Code.Equals(chosen, StringComparison.OrdinalIgnoreCase)))
        {
            return Languages.First(l => l.Code.Equals(chosen, StringComparison.OrdinalIgnoreCase)).Code;
        }

        for (var c = system; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Languages.FirstOrDefault(l => l.Code.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return exact.Code;
            }

            // pt-PT and pt-BR: Brazilian Portuguese is the Portuguese we have
            if (c.TwoLetterISOLanguageName == "pt")
            {
                return "pt-BR";
            }
        }

        return Fallback;
    }

    /// <summary>The embedded dictionaries, by language code.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Dictionaries => Embedded.Value;

    /// <summary>Keys of the English dictionary that a language does not have (the report of missing translations).</summary>
    public static IReadOnlyList<string> Missing(string language)
    {
        var all = Embedded.Value;
        var own = all.GetValueOrDefault(language) ?? new Dictionary<string, string>();
        return (all.GetValueOrDefault(Fallback) ?? new Dictionary<string, string>()).Keys.Where(k => !own.TryGetValue(k, out var v) || v.Length == 0).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyDictionary<string, string> Parse(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
           ?? new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadEmbedded()
    {
        var output = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(Translator).Assembly;
        foreach (var l in Languages)
        {
            using var stream = assembly.GetManifestResourceStream($"Moss365.Texts.{l.Code}.json");
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            output[l.Code] = Parse(reader.ReadToEnd());
        }

        return output;
    }
}
