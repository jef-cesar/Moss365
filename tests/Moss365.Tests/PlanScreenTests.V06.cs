using Bunit;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for 0.6: Conditional Access.</summary>
public sealed partial class PlanScreenTests
{
    private void LoadWithCa()
    {
        _factory.Ca = TestData.Ca(DateTimeOffset.Now);
        _factory.SignIns.Add(new SignInEvent(DateTimeOffset.Now.AddHours(-1), "e-an", "an.peeters@test.be", "an peeters", "Office 365",
            "203.0.113.5", "Gent, BE", 0, "", "Browser",
            new[] { new CaApplication("ca-mfa", "MFA voor iedereen", "success"), new CaApplication("ca-beheer", "Beheerders phishingbestendig", "reportOnlyFailure") }));
        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.All(State.Job!.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
    }

    [Fact]
    public void ConditionalAccessWithoutDataExplainsHow()
    {
        var cut = RenderComponent<CaOverview>();
        Assert.Contains("Policy.Read.All", cut.Markup);
    }

    [Fact]
    public void ConditionalAccessCheckPoliciesAndEffect()
    {
        LoadWithCa();
        Assert.Equal(4, State.Data.ConditionalAccess!.Policies.Count);

        var cut = RenderComponent<CaOverview>();
        Assert.Contains("Medewerkers zonder MFA-beleid", cut.Markup);
        Assert.Contains("Noodaccounts", cut.Markup);
        Assert.Contains("aandacht", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent == "Beleiden").Click();
        Assert.Contains("Buitenland blokkeren", cut.Markup);
        Assert.Contains("Alleen rapport", cut.Markup);
        Assert.Contains("Niet vanaf België", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.StartsWith("Effect laden")).Click();
        cut.WaitForAssertion(() => Assert.Contains("1 aanmeldingen van de laatste 7 dagen", cut.Markup));
        Assert.Equal(1, State.CaEffect!.Single(e => e.PolicyId == "ca-beheer").WouldBlock);

        cut.FindAll("button").First(b => b.TextContent == "Locaties en sterktes").Click();
        Assert.Contains("203.0.113.0/24", cut.Markup);
        Assert.Contains("Phishingbestendige MFA", cut.Markup);
    }

    [Fact]
    public void ConditionalAccessWhatIfFromTheUser()
    {
        LoadWithCa();

        var detail = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        Assert.Contains("uitgezonderd via de groep WKG_Eigen", detail.Markup);

        Services.GetRequiredService<NavigationManager>().NavigateTo("conditional-access?persoon=ad%3Abo.janssens");
        var cut = RenderComponent<CaOverview>();
        Assert.Contains("Geen enkel beleid is zeker van toepassing", cut.Markup);

        // Abroad: blocked
        cut.FindAll("select").Single(s => s.InnerHtml.Contains("buitenland")).Change(nameof(Core.Analysis.CaPlace.Abroad));
        Assert.Contains("Geblokkeerd door Buitenland blokkeren", cut.Markup);
    }

    [Fact]
    public void ConditionalAccessPolicyDetail()
    {
        LoadWithCa();
        var cut = RenderComponent<CaPolicyDetail>(p => p.Add(x => x.Id, "ca-mfa"));
        Assert.Contains("Behalve leden van: WKG_Eigen", cut.Markup);
        Assert.Contains("Toegang mits MFA", cut.Markup);
        Assert.Contains("an peeters", cut.Markup);
        Assert.DoesNotContain("BO JANSSENS", cut.Markup);

        var signIns = RenderComponent<Moss365.UI.Components.SignInList>(p => p.Add(x => x.SignIns, _factory.SignIns));
        Assert.Contains("rapport: zou tegenhouden", signIns.Markup);

        var stat = RenderComponent<Statistics>();
        Assert.Contains("medewerkers buiten elk MFA-beleid", stat.Markup);
    }
}
