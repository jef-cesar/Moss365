using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Permissions;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.30: the Conditional Access editor on screen, the roles mode per tenant, the status bar per tenant.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheEditorChangesEveryConditionAsAPlan()
    {
        State.Config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        LoadWithCa();
        var detail = RenderComponent<CaPolicyDetail>(p => p.Add(x => x.Id, "ca-mfa"));
        Assert.Equal("conditional-access/ca-mfa/bewerken", detail.FindAll("a").First(a => a.TextContent == "Bewerken").GetAttribute("href"));
        Assert.Contains(detail.FindAll("a"), a => a.TextContent == "Kopie maken");

        var cut = RenderComponent<CaPolicyEditor>(p => p.Add(x => x.Id, "ca-mfa"));
        Assert.Contains("MFA voor iedereen bewerken", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nog niets.", cut.Markup, StringComparison.Ordinal);

        cut.Find("input[aria-label='Android']").Change(true);
        cut.Find("select[aria-label='Aanmeldfrequentie']").Change("days");
        Assert.Contains("platformen", cut.Find(".kb-ca-verschil").TextContent, StringComparison.OrdinalIgnoreCase);

        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphUpdateCaPolicy));
        var w = _factory.Executed.Single(x => x.ChangeAction == ChangeAction.GraphUpdateCaPolicy);
        Assert.Equal("ca-mfa", w.Target);
        var patch = JsonNode.Parse(w.P("json"))!;
        Assert.Equal("android", patch["conditions"]!["platforms"]!["includePlatforms"]![0]!.GetValue<string>());
        Assert.Equal("days", patch["sessionControls"]!["signInFrequency"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void TheEditorWarnsAndHelpsWithTheEmergencyAccounts()
    {
        State.Config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        LoadWithCa();
        var cut = RenderComponent<CaPolicyEditor>(p => p.Add(x => x.Id, "ca-mfa"));
        cut.Find("button[aria-label='Info weghalen']").Click();
        Assert.Contains("noodaccount(s) staan niet uitgezonderd", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));
        cut.FindAll("button").First(b => b.TextContent == "Noodaccounts uitsluiten").Click();
        Assert.DoesNotContain("noodaccount(s) staan niet uitgezonderd", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewPolicyAndACopyStartInReportOnly()
    {
        State.Config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        LoadWithCa();
        var cut = RenderComponent<CaPolicyEditor>();
        Assert.Contains("Nieuw beleid", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.Find("input[aria-label='Aan']").HasAttribute("disabled"));
        cut.Find("input[aria-label='Naam']").Change("Toestelcode blokkeren");
        cut.Find("input[aria-label='Aanmelden met een toestelcode']").Change(true);
        cut.Find("input[aria-label='Toegang blokkeren']").Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphCreateCaPolicy));
        var json = JsonNode.Parse(_factory.Executed.Single(x => x.ChangeAction == ChangeAction.GraphCreateCaPolicy).P("json"))!;
        Assert.Equal("enabledForReportingButNotEnforced", json["state"]!.GetValue<string>());
        Assert.Equal("deviceCodeFlow", json["conditions"]!["authenticationFlows"]!["transferMethods"]!.GetValue<string>());

        Services.GetRequiredService<NavigationManager>().NavigateTo("conditional-access/ca-legacy/kopie");
        var copy = RenderComponent<CaPolicyEditor>(p => p.Add(x => x.Id, "ca-legacy"));
        Assert.Equal("Kopie van Verouderd blokkeren", copy.Find("input[aria-label='Naam']").GetAttribute("value"));
    }

    [Fact]
    public void ATenantChoosesToWorkWithoutRoles()
    {
        var cut = RenderComponent<Organisation>(p => p.Add(x => x.Part, "rollen"));
        Assert.Contains("Zonder rollen", cut.Markup, StringComparison.Ordinal);
        cut.Find("input[aria-label='Zonder rollen']").Change(true);
        Assert.Equal(RolesMode.Off, State.Config.RolesModeEffective);
        Assert.Null(State.ProfileMessage);
        Assert.True(State.Can(Permission.Settings));

        // Requiring roles without a role yourself would lock you out: refused
        cut.Find("input[aria-label='Rollen verplicht']").Change(true);
        Assert.Contains("anders kan je dit niet meer terugzetten", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(RolesMode.Off, State.Config.RolesModeEffective);
    }

    [Fact]
    public void TheStatusBarSaysWhetherThisTenantIsLive()
    {
        var bar = RenderComponent<StatusBar>();
        var status = bar.Find("a.kb-offline, a.kb-online");
        Assert.Contains($"{State.Tenant.Name}: offline", status.TextContent, StringComparison.Ordinal);
        Assert.Equal("verbindingen", status.GetAttribute("href"));

        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        bar.WaitForAssertion(() => Assert.Contains($"{State.Tenant.Name}: online", bar.Find("a.kb-online").TextContent, StringComparison.Ordinal));
        Assert.Contains("live om", bar.Find("a.kb-online").GetAttribute("title"), StringComparison.Ordinal);
    }
}
