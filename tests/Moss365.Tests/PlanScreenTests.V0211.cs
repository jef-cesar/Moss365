using Bunit;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.21.1: a tenant with only Microsoft 365 shows nothing of Active Directory; the choice is easy to find.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void WithoutAdLeavesEveryAdObjectOut()
    {
        var d = State.Data;
        Assert.NotEmpty(d.AdUsers);
        var without = d.WithoutAd();

        Assert.Empty(without.AdUsers);
        Assert.Empty(without.AdGroupsByDn);
        Assert.All(without.Persons, p => Assert.Null(p.Ad));
        Assert.Equal(d.EntraUsers.Count, without.Persons.Count);
        Assert.DoesNotContain(without.Persons, p => p.Source == "Alleen AD");
        Assert.DoesNotContain(without.Sources, f => f.File.StartsWith("ad-", StringComparison.Ordinal));
        Assert.DoesNotContain(without.Index.Groups, g => g.Ad is not null);
        Assert.Same(without, without.WithoutAd());
    }

    [Fact]
    public void ATenantWithOnlyMicrosoft365ShowsNothingOfAd()
    {
        var (hub, _, _) = Hub("enkel-m365");
        using var __ = hub;
        var s = hub.Current;
        s.Settings.TenantId = "tenant";
        s.Settings.ClientId = "client";
        Assert.True(s.StartLoading(LoadKind.Everything));
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        Thread.Sleep(100);
        Assert.NotEmpty(s.Data.AdUsers);

        // Switching hides what was read from AD at once, and switching back brings it back
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Microsoft365 });
        Assert.Empty(s.Data.AdUsers);
        Assert.All(s.Data.Persons, p => Assert.Null(p.Ad));
        Assert.True(Access.FitsTenant("nieuw", s.HasAd, s.HasMicrosoft365)); // in the cloud since 0.31

        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(hub);
        ctx.Services.AddSingleton(s);
        ctx.Services.AddSingleton(s.AppEnvironment);

        var users = ctx.RenderComponent<Users>().Markup;
        Assert.DoesNotContain("sAMAccountName", users, StringComparison.Ordinal);
        Assert.DoesNotContain("Alleen AD", users, StringComparison.Ordinal);
        Assert.DoesNotContain(">OU<", users, StringComparison.Ordinal);
        Assert.Contains("Nieuwe gebruiker", users, StringComparison.Ordinal);

        var groups = ctx.RenderComponent<Groups>().Markup;
        Assert.DoesNotContain("Alleen AD", groups, StringComparison.Ordinal);
        Assert.DoesNotContain("uit AD", groups, StringComparison.Ordinal);

        var offboarding = ctx.RenderComponent<Offboarding>().Markup;
        Assert.DoesNotContain("OU", offboarding, StringComparison.Ordinal);
        Assert.Contains("nooit verwijderd", offboarding, StringComparison.Ordinal);

        var newGroup = ctx.RenderComponent<NewGroup>().Markup;
        Assert.DoesNotContain("AD-beveiligingsgroep", newGroup, StringComparison.Ordinal);

        // On Verbindingen the choice is right there
        var connections = ctx.RenderComponent<Moss365.UI.Pages.Connections>();
        Assert.Contains("Wat heeft deze tenant?", connections.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("On-prem AD", connections.Markup, StringComparison.Ordinal);
        connections.FindAll("label.kb-keuze").First(l => l.TextContent.Contains("AD en Microsoft 365", StringComparison.Ordinal)).QuerySelector("input")!.Change(true);
        connections.FindAll("button").First(b => b.TextContent == "Bewaren").Click();
        Assert.Equal(TenantSystems.Both, hub.CurrentTenant.Systems);
        Assert.NotEmpty(s.Data.AdUsers);
    }

    [Fact]
    public void ANewTenantGetsItsSystemsWhenItIsAdded()
    {
        var (hub, _, _) = Hub("nieuw-m365");
        using var __ = hub;
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(hub);
        ctx.Services.AddSingleton(hub.Current);
        var cut = ctx.RenderComponent<Tenants>();
        Assert.Contains("<th>Systemen</th>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Aanpassen");

        cut.Find("input[placeholder='bv. Contoso']").Change("Fabrikam");
        cut.FindAll("label.kb-keuze").First(l => l.TextContent.Contains("Enkel Microsoft 365", StringComparison.Ordinal)).QuerySelector("input")!.Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Toevoegen").Click();
        Assert.Equal(TenantSystems.Microsoft365, hub.Tenants.Single(t => t.Name == "Fabrikam").Systems);
    }
}
