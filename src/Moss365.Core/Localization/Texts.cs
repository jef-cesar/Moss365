namespace Moss365.Core.Localization;

/// <summary>
/// The language now, for Core and the screens alike (R15): texts that the app builds in code (plan descriptions, check
/// results, messages) take <see cref="T"/> too. Dutch until the app chooses (and in tests); a scope changes it for one
/// flow only.
/// </summary>
public static class Texts
{
    private static readonly AsyncLocal<Translator?> Local = new();
    private static Translator _global = new("nl");

    public static Translator T => Local.Value ?? _global;

    /// <summary>The language for the whole app (the Windows app at the start, or after a choice in Settings).</summary>
    public static void Use(Translator translator) => _global = translator;

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
}
