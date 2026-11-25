using Bunit;
using Moss365.UI.Components;
using Moss365.UI.Layout;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.38 (R15): the shell of the app in the language of the interface.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheShellFollowsTheLanguage()
    {
        using (UiText.Scope("en"))
        {
            var cut = RenderComponent<MainLayout>(p => p.Add(x => x.Body, b => b.AddContent(0, "body")));
            Assert.Contains("Settings", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("People", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Users", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain(">Instellingen<", cut.Markup, StringComparison.Ordinal);
            var bar = RenderComponent<StatusBar>();
            Assert.Contains("offline", bar.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("niet live", bar.Markup, StringComparison.Ordinal);
        }

        using (UiText.Scope("fr"))
        {
            var settings = RenderComponent<SettingsLayout>(p => p.Add(x => x.Body, b => b.AddContent(0, "body")));
            Assert.Contains("Paramètres", settings.Markup, StringComparison.Ordinal);
            Assert.Contains("Langue", settings.Markup, StringComparison.Ordinal);
        }

        // Without a choice the tests (and the app until it starts) stay Dutch
        Assert.Contains("Instellingen", RenderComponent<MainLayout>(p => p.Add(x => x.Body, b => b.AddContent(0, "body"))).Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLanguageScreenShowsTheFiveLanguages()
    {
        using var _ = UiText.Scope("de");
        var cut = RenderComponent<LanguagePage>();
        foreach (var name in new[] { "English", "Nederlands", "Português (Brasil)", "Français", "Deutsch" })
        {
            Assert.Contains(name, cut.Markup, StringComparison.Ordinal);
        }

        Assert.Contains("Systemsprache", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Jetzt: Deutsch", cut.Markup, StringComparison.Ordinal);
    }
}
