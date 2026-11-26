using System.Globalization;
using System.Text.RegularExpressions;
using Moss365.Core.Localization;
using Moss365.Tests.Localization;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R15 (0.38): the interface in five languages, from dictionaries.</summary>
public sealed class LanguageTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static string Ui => Path.Combine(Repo(), "src", "Moss365.UI");

    [Fact]
    public void FiveLanguagesWithEveryKey()
    {
        Assert.Equal(new[] { "en", "nl", "pt-BR", "fr", "de" }, Translator.Languages.Select(l => l.Code));
        var english = Translator.Dictionaries["en"];
        Assert.NotEmpty(english);
        foreach (var l in Translator.Languages)
        {
            Assert.True(Translator.Dictionaries.ContainsKey(l.Code), $"No dictionary for {l.Code}");
            Assert.True(Translator.Missing(l.Code).Count == 0, $"Missing in {l.Code}: {string.Join(", ", Translator.Missing(l.Code))}");
            var extra = Translator.Dictionaries[l.Code].Keys.Except(english.Keys).ToList();
            Assert.True(extra.Count == 0, $"Keys in {l.Code} that English does not have: {string.Join(", ", extra)}");
        }
    }

    [Fact]
    public void PlaceholdersAreTheSameInEveryLanguage()
    {
        static string Holes(string text) => string.Join(",", Regex.Matches(text, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
        foreach (var (key, english) in Translator.Dictionaries["en"])
        {
            foreach (var l in Translator.Languages)
            {
                var text = Translator.Dictionaries[l.Code][key];
                Assert.True(Holes(english) == Holes(text), $"{l.Code} {key}: {text}");
                _ = string.Format(CultureInfo.InvariantCulture, text, Enumerable.Repeat<object>(1.5, 10).ToArray()); // valid format
            }
        }
    }

    [Fact]
    public void AttributesTakeTheirTextAsAnExpression()
    {
        // title=T["x"] (without @ and quotes) shows the code instead of the text
        var wrong = Directory.GetFiles(Ui, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"[\w-]+=Tx?(\[|\.Format)").Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();
        Assert.True(wrong.Count == 0, string.Join(", ", wrong));
    }

    [Fact]
    public void TranslationsKeepTheSameLinksAndMarkup()
    {
        static string Shape(string text) => string.Join(" ", Regex.Matches(text, @"<(/?)(\w+)([^>]*)>").Select(m => m.Groups[1].Value + m.Groups[2].Value + Regex.Match(m.Groups[3].Value, @"href=""[^""]*""").Value).Order(StringComparer.Ordinal));
        foreach (var (key, english) in Translator.Dictionaries["en"])
        {
            foreach (var l in Translator.Languages)
            {
                var text = Translator.Dictionaries[l.Code][key];
                Assert.True(Shape(english) == Shape(text), $"{l.Code} {key}: other tags or links than English: {text}");
            }
        }
    }

    [Fact]
    public void EveryKeyUsedInTheScreensExists()
    {
        var used = new Regex(@"\bTx?(?:\.Format|\.Text)?\(?\[?""([a-z][a-z0-9\.\-]+)""");
        var english = Translator.Dictionaries["en"];
        var missing = Directory.GetFiles(Ui, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => used.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
            .Where(x => !english.ContainsKey(x.Key) && !x.Key.EndsWith('.'))
            .ToList();
        Assert.True(missing.Count == 0, "Keys without a text: " + string.Join(", ", missing.Select(m => $"{m.File}: {m.Key}")));
    }

    [Fact]
    public void EveryMenuItemAndGroupIsTranslated()
    {
        var t = new Translator("en");
        foreach (var g in MenuModel.Main.Concat(MenuModel.Settings))
        {
            Assert.True(t.Has("group." + g.Name.ToLowerInvariant().Replace(' ', '-')), g.Name);
            foreach (var i in g.Items)
            {
                Assert.True(t.Has("menu." + i.Href.Replace('/', '.')), $"menu.{i.Href}");
            }
        }

        Assert.All(MenuModel.OrganisationParts, p => Assert.True(t.Has(p.Href == "organisatie" ? "menu.organisatie.bedrijf" : "menu." + p.Href.Replace('/', '.')), p.Href));
        using var _ = UiText.Scope("de");
        Assert.Equal("Einstellungen", UiText.T["nav.settings"]);
        Assert.Equal("Personen", UiText.Group("Mensen"));
        Assert.Equal("Unternehmen und Benennung", UiText.Part(MenuModel.OrganisationParts[0]));
    }

    [Theory]
    [InlineData(null, "nl-BE", "nl")]
    [InlineData(null, "nl-NL", "nl")]
    [InlineData(null, "pt-PT", "pt-BR")]
    [InlineData(null, "fr-CA", "fr")]
    [InlineData(null, "de-AT", "de")]
    [InlineData(null, "es-ES", "en")]
    [InlineData(null, "", "en")]
    [InlineData("de", "nl-BE", "de")]
    [InlineData("xx", "fr-BE", "fr")]
    public void TheOwnChoiceWinsThenTheSystemThenEnglish(string? chosen, string system, string expected)
        => Assert.Equal(expected, Translator.Resolve(chosen, string.IsNullOrEmpty(system) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void AMissingTextFallsBackToEnglishThenTheKey()
    {
        var dictionaries = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = new Dictionary<string, string> { ["a"] = "Save", ["b"] = "Back" },
            ["fr"] = new Dictionary<string, string> { ["a"] = "Enregistrer", ["b"] = "" },
        };
        var t = new Translator("fr", dictionaries);
        Assert.Equal("Enregistrer", t["a"]);
        Assert.Equal("Back", t["b"]);
        Assert.Equal("c", t["c"]);
        Assert.Equal("fr-BE", t.Culture.Name);
        Assert.Equal("1 234,5", new Translator("fr").Format("{0}", 1234.5m.ToString("N1", new Translator("fr").Culture)).Replace('\u202f', ' ').Replace('\u00a0', ' '));
    }

    [Fact]
    public void TheGlossaryKeepsFixedTerms()
    {
        foreach (var term in new[] { "term.plan", "term.snapshot", "term.tenant", "term.role", "term.changelog" })
        {
            foreach (var l in Translator.Languages)
            {
                Assert.False(string.IsNullOrWhiteSpace(Translator.Dictionaries[l.Code][term]), $"{l.Code} {term}");
            }
        }
    }

    /// <summary>
    /// No hard-coded text in the screens: a screen that is translated stays at zero, and a screen that is not yet
    /// translated may only get fewer texts (tests/Moss365.Tests/Localization/untranslated.txt; lower the number, or
    /// remove the line, when a screen moves to the dictionaries). The test reports what it finds.
    /// </summary>
    [Fact]
    public void NoNewHardCodedTextInTheScreens()
    {
        var baseline = File.ReadAllLines(Path.Combine(Repo(), "tests", "Moss365.Tests", "Localization", "untranslated.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 2))
            .ToDictionary(p => p[1], p => int.Parse(p[0], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var problems = new List<string>();
        var total = 0;
        foreach (var file in Directory.GetFiles(Ui, "*.razor", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(Ui, file).Replace('\\', '/');
            var texts = TextScanner.Texts(File.ReadAllText(file));
            total += texts.Count;
            var allowed = baseline.GetValueOrDefault(name);
            if (texts.Count > allowed)
            {
                problems.Add($"{name}: {texts.Count} texts (allowed {allowed}), e.g. \"{texts[0]}\"");
            }
        }

        Assert.True(problems.Count == 0, "Hard-coded text in the screens; put it in the dictionaries (Localization/Texts):\n" + string.Join("\n", problems));
        Assert.True(total <= baseline.Values.Sum());
    }

    [Fact]
    public void TheTranslatedShellHasNoHardCodedText()
    {
        foreach (var file in new[] { "Layout/MainLayout.razor", "Layout/SettingsLayout.razor", "Components/StatusBar.razor", "Components/TenantBar.razor",
                     "Components/Navigation.razor", "Components/YesNo.razor", "Components/LeaveConfirm.razor", "Pages/LanguagePage.razor" })
        {
            var texts = TextScanner.Texts(File.ReadAllText(Path.Combine(Ui, file)));
            Assert.True(texts.Count == 0, $"{file}: {string.Join(" | ", texts)}");
        }
    }
}
