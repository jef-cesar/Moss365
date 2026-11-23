using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moss365.UI.Components;
using Moss365.UI.Layout;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.21: the menu by subject, favourites, the settings screen and searching with Ctrl+K.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheMenuIsGroupedBySubjectWithOneSettingsItem()
    {
        var cut = RenderWithLayout<Start>(string.Empty);
        var groups = cut.FindAll("button.kb-nav-groep").Select(b => b.TextContent.Trim().TrimStart('▸', '▾').Trim()).ToList();
        Assert.Equal(new[] { "Mensen", "Groepen en toegang", "Beveiliging", "Inzicht", "Werk" }, groups);
        Assert.Single(cut.FindAll("nav.kb-nav a.kb-nav-instellingen"));
        Assert.DoesNotContain(cut.FindAll("nav.kb-nav a"), a => a.TextContent.Trim() is "Organisatie" or "Verbindingen" or "Databank en versleuteling");
    }

    [Fact]
    public void TheGroupOfThePageStaysOpenAndOthersAreRemembered()
    {
        State.View.SetMenuGroup("Mensen", closed: true);
        State.View.SetMenuGroup("Werk", closed: true);
        var cut = RenderWithLayout<Users>("gebruikers");
        Assert.Contains(cut.FindAll("nav.kb-nav a"), a => a.TextContent.Trim().StartsWith("Gebruikers", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("nav.kb-nav a"), a => a.TextContent.Trim().StartsWith("Opkuisronde", StringComparison.Ordinal));
        Assert.Equal("false", cut.FindAll("button.kb-nav-groep").First(b => b.TextContent.Contains("Werk", StringComparison.Ordinal)).GetAttribute("aria-expanded"));
    }

    [Fact]
    public void AFavouriteComesToTheTop()
    {
        var cut = RenderWithLayout<Start>(string.Empty);
        Assert.Empty(cut.FindAll(".kb-nav-kop"));
        cut.Find("button[aria-label='Statistieken bij de favorieten']").Click();
        Assert.Contains("★ Favorieten", cut.Find(".kb-nav-kop").TextContent, StringComparison.Ordinal);
        Assert.Equal(new[] { "statistieken" }, State.View.Favourites);
        Assert.Equal(2, cut.FindAll("nav.kb-nav a").Count(a => a.TextContent.Trim().StartsWith("Statistieken", StringComparison.Ordinal)));
        cut.FindAll("button[aria-label='Statistieken uit de favorieten']").First().Click();
        Assert.Empty(State.View.Favourites);
    }

    [Fact]
    public void TheSettingsHaveTheirOwnSideBar()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("organisatie/kantoren");
        var cut = RenderComponent<SettingsLayout>(p => p.Add(x => x.Body, b => b.AddContent(0, "inhoud")));
        var links = cut.FindAll("nav.kb-instellingen-nav a").Select(a => a.TextContent.Trim()).ToList();
        Assert.Contains("Verbindingen", links);
        Assert.Contains("Tenants", links);
        Assert.Contains("Versie en updates", links);
        Assert.Contains("Kantoren", links); // the parts of the organisation, while you are there
        Assert.Contains("Tenant inrichten", links);
    }

    [Fact]
    public void SettingsGoToTheFirstPage()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("instellingen");
        RenderComponent<SettingsHome>();
        Assert.EndsWith("/organisatie", nav.Uri);
    }

    [Fact]
    public void CtrlKFindsPagesAndObjects()
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderWithLayout<Start>(string.Empty);
        cut.Find("button.kb-nav-zoek").Click();
        var field = cut.Find("input.kb-zoek-veld");
        field.Input("kant");
        Assert.Contains("Organisatie: Kantoren", cut.Find(".kb-zoek-lijst").TextContent, StringComparison.Ordinal);

        cut.Find("input.kb-zoek-veld").Input("peeters");
        Assert.Contains("an.peeters@test.be", cut.Find(".kb-zoek-lijst").TextContent, StringComparison.Ordinal);
        cut.Find("input.kb-zoek-veld").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Contains("/gebruiker/", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".kb-zoek"));

        // Escape closes
        cut.Find("button.kb-nav-zoek").Click();
        cut.Find("input.kb-zoek-veld").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll(".kb-zoek"));
    }
}
