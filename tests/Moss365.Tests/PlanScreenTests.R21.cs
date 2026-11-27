using Bunit;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R21: editing a user in sections, with more fields, checked while typing, as one plan with undo.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void AUserIsEditedInSectionsWithItsCurrentValues()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        var sections = cut.FindAll(".kb-sectie").Select(s => s.QuerySelector("h3")!.TextContent).ToList();
        Assert.Equal(new[] { "Identiteit", "Organisatie", "Contact en adres", "Toegang" }, sections);
        Assert.Contains("Personeelsnummer", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Gebruikslocatie", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Entra ID volgt na de synchronisatie", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".kb-sectie input")); // nothing is open until you choose a section
    }

    [Fact]
    public void ValuesAreCheckedWhileTypingAndTheUsageLocationGoesToEntra()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Contact en adres").QuerySelector("button")!.Click();

        // A usage location of one letter: explained at once, and no plan
        cut.Find("input[aria-label='Gebruikslocatie']").Input("b");
        Assert.Contains("Twee hoofdletters", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));

        // Two letters (made capital) and a company: the company in AD, the usage location in Entra (not synchronised)
        cut.Find("input[aria-label='Gebruikslocatie']").Input("be");
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Organisatie").QuerySelector("button")!.Click();
        cut.Find("input[aria-label='Bedrijf']").Input("Contoso Zorg");
        Assert.Contains("2 gewijzigd", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("nu: ", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();

        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("Bedrijf: Contoso Zorg", plan, StringComparison.Ordinal);
        Assert.Contains("Gebruikslocatie: BE", plan, StringComparison.Ordinal);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphSetAttributes && w.P("usageLocation") == "BE"));
        var ad = Assert.Single(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes);
        Assert.Equal("Contoso Zorg", ad.P("company"));
        Assert.False(ad.Parameters.ContainsKey("usageLocation"));
        Assert.True(ad.Previous!.ContainsKey("company")); // can be undone
    }

    [Fact]
    public void ADuplicateEmployeeIdIsAWarningNotABlock()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.EmployeeId), "1234567890123456"));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.EmployeeId), "12345678901234567"));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.Phone), "+32 2 abc"));
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.Phone), "+32 (0)2 123.45.67"));
        Assert.Null(AttributePlans.SameEmployeeId(State.Data, an, "999"));

        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, an.Key));
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Identiteit").QuerySelector("button")!.Click();
        cut.Find("input[aria-label='Personeelsnummer']").Input("E-1001");
        Assert.False(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));
    }
}
