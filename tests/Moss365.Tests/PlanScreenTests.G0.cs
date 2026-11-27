using Bunit;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.Core.Storage;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>G0: Google Workspace as a third system per tenant, in any combination with AD and Microsoft 365.</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>Pages about one system: the menu, the settings and Ctrl+K show them only for a tenant with it.</summary>
    private static readonly string[] AdPages = { "logonscripts", "wachtwoorden" };

    private static readonly string[] Microsoft365Pages =
        { "gasten", "postvakken", "rooms", "beveiliging", "conditional-access", "inrichten", "bewaarde-logboeken", "licenties", "samenwerking", "gastenverlenging" };

    [Fact]
    public void EveryCombinationOfSystemsShowsOnlyItsPages()
    {
        Assert.Equal(7, TenantSystemsInfo.Combinations.Count);
        foreach (var systems in TenantSystemsInfo.Combinations)
        {
            bool Fits(string path) => Access.FitsTenant(path, systems);
            var hasAd = systems.HasAd();
            var hasMicrosoft365 = systems.HasMicrosoft365();

            Assert.All(AdPages, p => Assert.Equal(hasAd, Fits(p)));
            Assert.All(Microsoft365Pages, p => Assert.Equal(hasMicrosoft365, Fits(p)));
            Assert.Equal(hasAd && hasMicrosoft365, Fits("vergelijk"));
            Assert.Equal(hasAd || hasMicrosoft365, Fits("nieuw"));
            Assert.Equal(systems.Count() >= 2, Fits("koppelingen"));
            Assert.Equal(systems.Count() >= 2, Fits("bron"));

            // Every page of the menu either fits or belongs to a system the tenant does not have
            foreach (var page in MenuModel.AllPages.Select(i => i.Href.Split('?')[0]))
            {
                var first = page.Split('/')[0];
                var belongsElsewhere = (AdPages.Contains(first) && !hasAd) || (Microsoft365Pages.Contains(first) && !hasMicrosoft365)
                                       || (first == "vergelijk" && !(hasAd && hasMicrosoft365)) || (first == "nieuw" && !(hasAd || hasMicrosoft365))
                                       || (first is "koppelingen" or "bron" && systems.Count() < 2);
                Assert.True(Fits(page) || belongsElsewhere, $"{systems}: {page}");
            }
        }
    }

    [Fact]
    public void NothingChosenCountsAsAdAndMicrosoft365()
    {
        Assert.Equal(TenantSystems.Both, TenantSystems.None.Effective());
        Assert.Equal(TenantSystems.GoogleWorkspace, TenantSystems.GoogleWorkspace.Effective());
        Assert.Equal(TenantSystems.Both, ((TenantSystems)64).Effective());
        Assert.Equal(3, (TenantSystems.Both | TenantSystems.GoogleWorkspace).Count());
        Assert.True(TenantSystems.None.HasAd());
        Assert.False(TenantSystems.GoogleWorkspace.HasMicrosoft365());
    }

    [Fact]
    public void ThreeSystemsAreChosenWithCheckboxesAndOneStaysOn()
    {
        var chosen = TenantSystems.Both;
        var cut = RenderComponent<SystemsChoice>(p => p
            .Add(x => x.Value, chosen)
            .Add(x => x.ValueChanged, v => chosen = v));

        var boxes = cut.FindAll("input[type=checkbox]");
        Assert.Equal(3, boxes.Count);
        Assert.Contains("Google Workspace", cut.Markup, StringComparison.Ordinal);

        boxes[2].Change(true);
        Assert.Equal(TenantSystems.Both | TenantSystems.GoogleWorkspace, chosen);

        // Only Google Workspace on: that box cannot be turned off, and the screen says what Google can do today
        cut.SetParametersAndRender(p => p.Add(x => x.Value, TenantSystems.GoogleWorkspace));
        boxes = cut.FindAll("input[type=checkbox]");
        Assert.True(boxes[2].HasAttribute("disabled"));
        Assert.False(boxes[0].HasAttribute("disabled"));
        Assert.Contains("G1", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("Active Directory, Microsoft 365 en Google Workspace", SystemsChoice.Text(TenantSystems.Both | TenantSystems.GoogleWorkspace));
        Assert.Equal("Google Workspace", SystemsChoice.Text(TenantSystems.GoogleWorkspace));
    }

    [Fact]
    public void ATenantWithoutMicrosoft365ShowsNothingOfIt()
    {
        var (hub, _, _) = Hub("g0-systemen");
        using var _ = hub;
        var s = hub.Current;
        s.Settings.TenantId = "tenant";
        s.Settings.ClientId = "client";
        Assert.True(s.StartLoading(LoadKind.Everything));
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        Assert.NotEmpty(s.Data.EntraUsers);
        Assert.NotEmpty(s.Data.AdUsers);

        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.ActiveDirectory | TenantSystems.GoogleWorkspace });
        s = hub.Current;
        Assert.True(s.HasGoogle);
        Assert.False(s.HasMicrosoft365);
        Assert.Empty(s.Data.EntraUsers);
        Assert.Empty(s.Data.CloudGroups);
        Assert.Empty(s.Data.Mailboxes);
        Assert.Null(s.Data.ConditionalAccess);
        Assert.NotEmpty(s.Data.AdUsers);
        Assert.All(s.Data.Persons, p => Assert.Null(p.Entra));

        // Only Google Workspace: nothing of AD or Microsoft 365 until G1 reads Google
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.GoogleWorkspace });
        s = hub.Current;
        Assert.Empty(s.Data.AdUsers);
        Assert.Empty(s.Data.EntraUsers);
        Assert.Empty(s.Data.Persons);
        Assert.False(s.EffectiveSettings.AdFilled);
        Assert.False(s.EffectiveSettings.GraphFilled);

        // Back to all three: what was read is all there again
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Both | TenantSystems.GoogleWorkspace });
        Assert.NotEmpty(hub.Current.Data.EntraUsers);
        Assert.Equal(TenantSystems.Both | TenantSystems.GoogleWorkspace, hub.Tenants.Single(x => x.Id == hub.CurrentTenant.Id).Systems);
    }

    [Fact]
    public void WithoutMicrosoft365LeavesOutEveryCloudFile()
    {
        var data = new InventoryData
        {
            Sources = new[] { new SourceFile("ad-gebruikers.csv", "x", 1), new SourceFile("entra-gebruikers.csv", "x", 1), new SourceFile("exo-groepen.csv", "x", 1) },
        };

        var without = data.WithoutMicrosoft365();

        Assert.Equal("ad-gebruikers.csv", Assert.Single(without.Sources).File);
        Assert.Same(without, without.WithoutMicrosoft365());
    }
}
