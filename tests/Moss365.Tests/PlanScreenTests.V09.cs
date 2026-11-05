using System.Text.Json.Nodes;
using Bunit;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for 0.9: managing Conditional Access.</summary>
public sealed partial class PlanScreenTests
{
    private IRenderedComponent<CaOverview> Manage()
    {
        LoadWithCa();
        var cut = RenderComponent<CaOverview>();
        cut.FindAll("button").First(b => b.TextContent == "Beheren").Click();
        return cut;
    }

    [Fact]
    public void WithoutEmergencyAccountsManageSaysWhatComesFirst()
    {
        var cut = Manage();
        Assert.Contains("geen noodaccounts", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Nieuw beleid").Click();
        cut.Find("select").Change("mfa-iedereen");
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));
    }

    [Fact]
    public void EnablingAsksExtraConfirmationAndGoesToGraph()
    {
        State.Config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        var cut = Manage();
        Assert.Contains("Noodaccounts: Info", cut.Markup);

        // "Administrators phishing-resistant" is report-only; enabling is possible only after the tick
        var row = cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Beheerders phishingbestendig"));
        Assert.True(row.QuerySelectorAll("button").First(b => b.TextContent == "Aanzetten").HasAttribute("disabled"));
        cut.FindAll("input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("effect in alleen rapport bekeken")).Change(true);
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Beheerders phishingbestendig"))
            .QuerySelectorAll("button").First(b => b.TextContent == "Aanzetten").Click();
        Assert.Contains("op aan zetten", cut.Markup);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        Settle(cut); // Conditional Access is read again after the plan

        var w = Assert.Single(_factory.Executed, x => x.ChangeAction == ChangeAction.GraphUpdateCaPolicy);
        Assert.Equal("ca-beheer", w.Target);
        Assert.Equal("enabled", JsonNode.Parse(w.P("json"))!["state"]!.GetValue<string>());

        // Turning off is always possible (a policy that is off locks nobody out)
        cut.FindAll("tbody tr").First(r => r.TextContent.Contains("Buitenland blokkeren"))
            .QuerySelectorAll("button").First(b => b.TextContent == "Uitzetten").Click();
        Assert.Contains("\"Buitenland blokkeren\" op uit zetten", cut.Markup);
    }

    [Fact]
    public void ExclusionWithEndDateIsThenReadyInScheduled()
    {
        State.Config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        var cut = Manage();
        cut.FindAll("button").First(b => b.TextContent == "Uitzonderingen").Click();
        cut.FindAll("select").First(s => s.QuerySelectorAll("option").Any(o => o.GetAttribute("value") == "ca-mfa")).Change("ca-mfa");
        Assert.Contains("Rechtstreeks uitgezonderd van \"MFA voor iedereen\" (1)", cut.Markup);
        Assert.Contains("noodaccount", cut.Find(".kb-lijst-plat").TextContent);
        Choose(cut, "Wie", "cas", "cas vos");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("cas vos uitzonderen van", cut.Markup);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        Settle(cut); // Conditional Access is read again after the plan

        var w = Assert.Single(_factory.Executed, x => x.ChangeAction == ChangeAction.GraphUpdateCaPolicy);
        Assert.Equal(new[] { "e-info", "e-cas" }, JsonNode.Parse(w.P("json"))!["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        var scheduled = Assert.Single(State.Scheduled.Read(), a => a.Kind == ScheduledKind.CaExclusionEnd);
        Assert.Equal("ca-mfa", scheduled.CaPolicyId);
        Assert.Equal(DateTime.Today.AddDays(14).Date, scheduled.When.Date);
        Assert.Contains("Einde uitzondering op \"MFA voor iedereen\"", scheduled.Title);
    }

    [Fact]
    public void NewPolicyFromATemplateAndANamedLocation()
    {
        State.Config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        var cut = Manage();
        cut.FindAll("button").First(b => b.TextContent == "Nieuw beleid").Click();
        cut.FindAll("select").First(s => s.QuerySelectorAll("option").Any(o => o.GetAttribute("value") == "toestelcode-blokkeren")).Change("toestelcode-blokkeren");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("in alleen rapport", cut.Markup);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        Settle(cut); // Conditional Access is read again after the plan
        var newValue = Assert.Single(_factory.Executed, x => x.ChangeAction == ChangeAction.GraphCreateCaPolicy);
        Assert.Equal("enabledForReportingButNotEnforced", JsonNode.Parse(newValue.P("json"))!["state"]!.GetValue<string>());

        cut.FindAll("button").First(b => b.TextContent == "Benoemde locaties").Click();
        Field(cut, "Naam").Change("Bibliotheek");
        Field(cut, "IP-bereiken", "textarea").Change("198.51.100.7/32\nfout");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("\"fout\" is geen IP-bereik", cut.Markup);
        Field(cut, "IP-bereiken", "textarea").Change("198.51.100.7/32");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Nieuwe benoemde locatie \"Bibliotheek\" (vertrouwd)", cut.Markup);
    }
}
