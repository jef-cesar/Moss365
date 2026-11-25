using System.Globalization;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// The language of the interface (R15, 0.38), for the whole app: screens use <c>T[...]</c> with a key (via <c>@using static</c>
/// in _Imports). Until the app chooses (and in tests) the interface is Dutch, as before. The Windows app resolves the
/// language at the start: the choice in Settings (per Windows user, in moss365.db), else the Windows display language,
/// else English. Dates and numbers follow the language.
/// </summary>
public static class UiText
{
    public const string SettingKey = "language";

    private static readonly AsyncLocal<Translator?> Local = new();
    private static Translator _global = new("nl");

    public static Translator T => Local.Value ?? _global;

    /// <summary>Another language for this flow only (tests), without touching the rest of the app.</summary>
    public static IDisposable Scope(string language)
    {
        var before = Local.Value;
        Local.Value = new Translator(language);
        return new Restore(() => Local.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>The language now and whether it was chosen (or follows the system).</summary>
    public static string? Chosen { get; private set; }

    public static void Use(string language)
    {
        _global = new Translator(language);
        var culture = _global.Culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>At the start of the app: the choice of the user, else the system language.</summary>
    public static void Start(AppDatabase app, CultureInfo system)
    {
        Chosen = app.Setting(SettingKey);
        Use(Translator.Resolve(Chosen, system));
    }

    /// <summary>The choice in Settings (null = follow the system language); effective after reloading the screens.</summary>
    public static void Choose(AppDatabase app, string? language, CultureInfo system)
    {
        app.SetSetting(SettingKey, string.IsNullOrWhiteSpace(language) ? null : language);
        Chosen = string.IsNullOrWhiteSpace(language) ? null : language;
        Use(Translator.Resolve(Chosen, system));
    }

    /// <summary>The text of a menu item, by its address.</summary>
    public static string Menu(MenuItem item) => T.Text("menu." + (item.Href.Length == 0 ? "start" : item.Href.Replace('/', '.')), item.Text);

    /// <summary>A part of the organisation settings (its first part has the same address as Organisatie).</summary>
    public static string Part(MenuItem item) => item.Href == "organisatie" ? T.Text("menu.organisatie.bedrijf", item.Text) : Menu(item);

    /// <summary>The name of a menu group (the Dutch name stays the id, e.g. for groups you closed).</summary>
    public static string Group(string name) => T.Text("group." + name.ToLowerInvariant().Replace(' ', '-'), name);
}
