using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>G2: writing in Google Workspace from the screens, always as a plan.</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>A tenant with AD, Microsoft 365 and Google, read once, with a test context on its state.</summary>
    private (TenantHub Hub, FakeFactory Factory, ManagementState State, Bunit.TestContext Context) GoogleTenant(string name)
    {
        var (hub, factories, _) = Hub(name);
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Both | TenantSystems.GoogleWorkspace });
        var s = hub.Current;
        var factory = factories[hub.CurrentTenant.Id];
        factory.GoogleFake = new FakeGoogleConnection(GoogleSample()) { Writer = factory };
        s.Settings.TenantId = "tenant";
        s.Settings.ClientId = "client";
        s.Settings.GoogleDomain = "test.be";
        s.Settings.GoogleClientId = "client-1.apps.googleusercontent.com";
        Assert.True(s.StartLoading(LoadKind.Everything));
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(s);
        ctx.Services.AddSingleton<IConnectionFactory>(factory);
        ctx.Services.AddSingleton<IAppEnvironment>(Services.GetRequiredService<IAppEnvironment>());
        return (hub, factory, s, ctx);
    }

    [Fact]
    public void AGoogleUserIsEditedOnThePageOfThePersonAndWhatFollowsIsShownOnly()
    {
        var (hub, f, s, ctx) = GoogleTenant("g2-gebruiker");
        using var _ = hub;
        using var __ = ctx;
        var cas = s.Data.Persons.Single(p => p.Ad?.SamAccountName == "cas.vos");
        var page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        page.Find("#google-velden").QuerySelectorAll("button").First(b => b.TextContent == "Bewerken").Click();
        page.Find("input[aria-label='Afdeling']").Input("Zorg");
        page.Find("#google-velden").QuerySelectorAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Google Workspace", page.Find("section.kb-plan").TextContent, StringComparison.Ordinal);
        Confirm(page);
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleUpdateUser));
        var set = f.Executed.Single(w => w.ChangeAction == ChangeAction.GoogleUpdateUser);
        Assert.Equal(("g-cas", "Zorg", ""), (set.Target, set.P("department"), set.Previous!["department"]));
        Assert.Contains(s.ChangeLog.Recent(5), r => r.ChangeAction == nameof(ChangeAction.GoogleUpdateUser) && r.TargetSystem == "Google");

        // An alias, and signing out everywhere
        page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        page.Find("input[aria-label='Nieuwe alias']").Input("cas@test.be");
        page.FindAll("button").First(b => b.TextContent == "Alias toevoegen").Click();
        Confirm(page);
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleAddAlias && w.P("alias") == "cas@test.be"));
        page.FindAll("button").First(b => b.TextContent == "Overal afmelden").Click();
        Confirm(page);
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleSignOut && w.Target == "g-cas"));

        // Google gets its users from AD with the passwords: the fields and the password are changed there
        s.SaveSource(new Moss365.Core.Linking.SourceSettings { GoogleUsers = Moss365.Core.Linking.GoogleFeed.FromAd, GoogleGroups = Moss365.Core.Linking.GoogleFeed.Itself, GooglePasswordSync = true });
        page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Assert.Contains("volgen daaruit", page.Find("#google-velden").TextContent, StringComparison.Ordinal);
        page.Find("#google-velden").QuerySelectorAll("button").First(b => b.TextContent == "Bewerken").Click();
        Assert.Empty(page.FindAll("input[aria-label='Afdeling']"));
        Assert.DoesNotContain(page.Find("#google-acties").QuerySelectorAll("button"), b => b.TextContent == "Wachtwoord opnieuw instellen");
    }

    [Fact]
    public void ANewGoogleUserIsAPlanAndThePasswordIsNeverLogged()
    {
        var (hub, f, s, ctx) = GoogleTenant("g2-nieuw");
        using var _ = hub;
        using var __ = ctx;
        Assert.True(Access.FitsTenant("google-nieuw", s.Systems));
        Assert.False(Access.FitsTenant("google-nieuw", TenantSystems.Both));
        Assert.Equal(Moss365.Core.Permissions.Permission.Actions, Access.ForPath("google-nieuw"));

        var page = ctx.RenderComponent<GoogleNewUser>();
        page.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Voornaam", StringComparison.Ordinal)).QuerySelector("input")!.Input("Lien");
        page.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Achternaam", StringComparison.Ordinal)).QuerySelector("input")!.Input("Maes");
        page.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Organisatie-eenheid", StringComparison.Ordinal)).QuerySelector("select")!.Change("/Zorg");
        page.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("lien.maes@test.be", page.Markup, StringComparison.Ordinal);
        Confirm(page);
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleCreateUser));
        var create = f.Executed.Single(w => w.ChangeAction == ChangeAction.GoogleCreateUser);
        Assert.Equal(("lien.maes@test.be", "/Zorg"), (create.Target, create.P("orgUnitPath")));
        page.WaitForAssertion(() => Assert.Contains("Nieuw wachtwoord voor lien.maes@test.be", page.Markup, StringComparison.Ordinal));
        Assert.Null(create.Secret);
        var line = s.ChangeLog.Recent(5).Single(r => r.ChangeAction == nameof(ChangeAction.GoogleCreateUser));
        Assert.DoesNotContain(line.Parameters.Values, v => v.Length >= 16);
    }
}
