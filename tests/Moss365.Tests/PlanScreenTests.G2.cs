using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Setup;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>G2: writing in Google Workspace from the screens, always as a plan.</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>A tenant with AD, Microsoft 365 and Google, read once, with a test context on its state.</summary>
    private (TenantHub Hub, FakeFactory Factory, ManagementState State, Bunit.TestContext Context) GoogleTenant(string name, GoogleData? data = null, params string[] parts)
    {
        var (hub, factories, _) = Hub(name);
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Both | TenantSystems.GoogleWorkspace });
        var s = hub.Current;
        var factory = factories[hub.CurrentTenant.Id];
        factory.GoogleFake = new FakeGoogleConnection(data ?? GoogleSample()) { Writer = factory };
        s.Config.PermissionParts.AddRange(parts);
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

    /// <summary>After a plan the objects are read again in the background: wait for that, then a fresh page.</summary>
    private static IRenderedComponent<TPage> Settled<TPage>(Bunit.TestContext ctx, ManagementState s, Func<IRenderedComponent<TPage>> render)
        where TPage : Microsoft.AspNetCore.Components.IComponent
    {
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        return render();
    }

    /// <summary>
    /// A find and an action on the dispatcher of the renderer, waiting for its redraw: the background task of the hub
    /// still redraws the page just after it counts as done, and a plain click would then return before its redraw.
    /// </summary>
    private static void Do(IRenderedFragment cut, Action action) => cut.InvokeAsync(action).GetAwaiter().GetResult();

    /// <summary>
    /// Opens a plan and runs it. These tests use a hub of their own whose background work redraws the page now and then;
    /// a click on an element found before such a redraw is lost. So every find and click runs on the dispatcher of the
    /// renderer (as bUnit advises), and a plan that a redraw closed is opened again.
    /// </summary>
    private static void ConfirmSteady(IRenderedFragment cut, Action open)
    {
        bool Click(string start) => cut.InvokeAsync(() =>
        {
            if (cut.FindAll("button").FirstOrDefault(b => b.TextContent.StartsWith(start, StringComparison.Ordinal)) is not { } button)
            {
                return false;
            }

            button.Click();
            return true;
        }).GetAwaiter().GetResult();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cut.InvokeAsync(open).GetAwaiter().GetResult();
            if (Click("Uitvoeren (") && Click("Ja, uitvoeren"))
            {
                return;
            }
        }

        Assert.Fail("The plan did not stay open");
    }

    [Fact]
    public void AGoogleUserIsEditedOnThePageOfThePersonAndWhatFollowsIsShownOnly()
    {
        var (hub, f, s, ctx) = GoogleTenant("g2-gebruiker");
        using var _ = hub;
        using var __ = ctx;
        var cas = s.Data.Persons.Single(p => p.Ad?.SamAccountName == "cas.vos");
        var page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Do(page, () => page.Find("#google-velden").QuerySelectorAll("button").First(b => b.TextContent == "Bewerken").Click());
        Do(page, () => page.Find("input[aria-label='Afdeling']").Input("Zorg"));
        Do(page, () => page.Find("#google-velden").QuerySelectorAll("button").First(b => b.TextContent == "Plan maken").Click());
        Assert.Contains("Google Workspace", page.Find("section.kb-plan").TextContent, StringComparison.Ordinal);
        Confirm(page);
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleUpdateUser));
        var set = f.Executed.Single(w => w.ChangeAction == ChangeAction.GoogleUpdateUser);
        Assert.Equal(("g-cas", "Zorg", ""), (set.Target, set.P("department"), set.Previous!["department"]));
        Assert.Contains(s.ChangeLog.Recent(5), r => r.ChangeAction == nameof(ChangeAction.GoogleUpdateUser) && r.TargetSystem == "Google");

        // An alias, and signing out everywhere
        page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Do(page, () => page.Find("input[aria-label='Nieuwe alias']").Input("cas@test.be"));
        ConfirmSteady(page, () => page.FindAll("button").First(b => b.TextContent == "Alias toevoegen").Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleAddAlias && w.P("alias") == "cas@test.be"));
        page = Settled(ctx, s, () => ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key)));
        ConfirmSteady(page, () => page.FindAll("button").First(b => b.TextContent == "Overal afmelden").Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleSignOut && w.Target == "g-cas"));

        // Google gets its users from AD with the passwords: the fields and the password are changed there
        s.SaveSource(new Moss365.Core.Linking.SourceSettings { GoogleUsers = Moss365.Core.Linking.GoogleFeed.FromAd, GoogleGroups = Moss365.Core.Linking.GoogleFeed.Itself, GooglePasswordSync = true });
        page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Assert.Contains("volgen daaruit", page.Find("#google-velden").TextContent, StringComparison.Ordinal);
        Do(page, () => page.Find("#google-velden").QuerySelectorAll("button").First(b => b.TextContent == "Bewerken").Click());
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
        Do(page, () => page.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Voornaam", StringComparison.Ordinal)).QuerySelector("input")!.Input("Lien"));
        Do(page, () => page.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Achternaam", StringComparison.Ordinal)).QuerySelector("input")!.Input("Maes"));
        Do(page, () => page.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Organisatie-eenheid", StringComparison.Ordinal)).QuerySelector("select")!.Change("/Zorg"));
        Do(page, () => page.FindAll("button").First(b => b.TextContent == "Plan maken").Click());
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

    [Fact]
    public void AGoogleGroupGetsItsMembersWhenAskedAndEveryChangeIsAPlan()
    {
        var sample = GoogleSample();
        var data = sample with
        {
            Groups = new[] { sample.Groups[0] with { WhoCanJoin = "INVITED_CAN_JOIN", WhoCanPost = "ALL_MEMBERS_CAN_POST", WhoCanViewMembership = "ALL_MEMBERS_CAN_VIEW", AllowExternalMembers = false } },
            Resources = new[] { new GoogleResource { Id = "r-1", Email = "zaal1@test.be", Name = "Zaal 1", Capacity = 8 } },
        };
        var (hub, f, s, ctx) = GoogleTenant("g2-groep", data, Moss365.Core.Setup.GoogleParts.GroupSettings);
        using var _ = hub;
        using var __ = ctx;
        Assert.True(Access.FitsTenant("google-groep/gg-1", s.Systems));
        Assert.Contains("Team", ctx.RenderComponent<GoogleGroups>().Markup, StringComparison.Ordinal);

        var page = ctx.RenderComponent<GoogleGroupDetail>(p => p.Add(x => x.Id, "gg-1"));
        Assert.Empty(page.FindAll("#google-groep-leden table"));
        Do(page, () => page.FindAll("button").First(b => b.TextContent == "Leden lezen").Click());
        page.WaitForAssertion(() => Assert.Contains("nieuw@test.be", page.Find("#google-groep-leden").TextContent, StringComparison.Ordinal));

        Do(page, () => page.Find("input[aria-label='Adres van het nieuwe lid']").Input("lien@test.be"));
        ConfirmSteady(page, () => page.FindAll("button").First(b => b.TextContent == "Lid toevoegen").Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleAddGroupMember && w.Target == "lien@test.be" && w.P("rol") == "MEMBER"));
        page = Settled(ctx, s, () => ctx.RenderComponent<GoogleGroupDetail>(p => p.Add(x => x.Id, "gg-1")));
        Do(page, () => page.FindAll("button").First(b => b.TextContent == "Leden lezen").Click());
        page.WaitForAssertion(() => Assert.Contains("nieuw@test.be", page.Find("#google-groep-leden").TextContent, StringComparison.Ordinal));

        ConfirmSteady(page, () => page.FindAll("#google-groep-leden tr").First(r => r.TextContent.Contains("nieuw@test.be", StringComparison.Ordinal)).QuerySelector("button")!.Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleRemoveGroupMember && w.Target == "nieuw@test.be"));

        // The settings with their part
        page = Settled(ctx, s, () => ctx.RenderComponent<GoogleGroupDetail>(p => p.Add(x => x.Id, "gg-1")));
        Do(page, () => page.Find("select[aria-label='Wie mag lid worden']").Change("ALL_IN_DOMAIN_CAN_JOIN"));
        ConfirmSteady(page, () => page.Find("#google-groep-instellingen").QuerySelectorAll("button").First(b => b.TextContent == "Plan maken").Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleUpdateGroupSettings && w.P("whoCanJoin") == "ALL_IN_DOMAIN_CAN_JOIN"));

        // A calendar resource, edited in its row
        var resources = Settled(ctx, s, () => ctx.RenderComponent<GoogleResources>());
        Do(resources, () => resources.Find("#bron-r-1 button").Click());
        Do(resources, () => resources.Find("input[aria-label='Capaciteit']").Input("10"));
        ConfirmSteady(resources, () => resources.FindAll("button").First(b => b.TextContent == "Plan maken").Click());
        resources.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleUpdateResource && w.P("capacity") == "10" && w.Previous!["capacity"] == "8"));
    }

    [Fact]
    public void LicencesOfGoogleAreChangedOnThePageOfThePersonWithTheirPart()
    {
        var data = GoogleSample() with { Licences = new[] { new GoogleLicence("cas.vos@test.be", "Google-Apps", "1010020028", "Business Standard") } };
        var (hub, f, s, ctx) = GoogleTenant("g2-licentie", data, Moss365.Core.Setup.GoogleParts.Licences);
        using var _ = hub;
        using var __ = ctx;
        var cas = s.Data.Persons.Single(p => p.Ad?.SamAccountName == "cas.vos");
        var page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        ConfirmSteady(page, () => page.Find("#google-licenties").QuerySelectorAll("button").First(b => b.TextContent == "Verwijderen").Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GoogleRemoveLicence && w.Target == "cas.vos@test.be" && w.P("skuId") == "1010020028"));

        // Without the part: no licences on the page
        s.Config.PermissionParts.Clear();
        page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Assert.Empty(page.FindAll("#google-licenties"));
    }

    [Fact]
    public void TheKeyOfTheServiceAccountIsKeptOutOfSightAndGmailIsChangedAsAPlan()
    {
        var (hub, f, s, ctx) = GoogleTenant("g2-gmail", null, GoogleParts.GmailSettings);
        using var _ = hub;
        using var __ = ctx;
        Assert.Contains("Nog geen serviceaccount", ctx.RenderComponent<Moss365.UI.Pages.Connections>().Markup, StringComparison.Ordinal);

        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var file = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account", ["project_id"] = "moss365-test", ["private_key_id"] = "kid-1", ["private_key"] = pem,
            ["client_email"] = "moss365-gmail@example.com", ["client_id"] = "1234567890", ["token_uri"] = Moss365.Core.Google.GmailKey.GoogleTokenUri,
        });
        Assert.NotNull(s.SaveGmailKey("{}"));
        Assert.Null(s.SaveGmailKey(file));
        Assert.Equal("1234567890", s.GmailKey?.ClientId);
        var connections = ctx.RenderComponent<Moss365.UI.Pages.Connections>().Markup;
        Assert.Contains("1234567890", connections, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", connections, StringComparison.Ordinal);
        Assert.Contains(s.Database.ReadAccess(10), a => a.What == "Gmail-serviceaccount ingesteld");
        var middle = pem.Split('\n')[5];
        Assert.DoesNotContain(s.Database.ReadTechnicalLog(500), l => l.Message.Contains(middle, StringComparison.Ordinal));

        var cas = s.Data.Persons.Single(p => p.Ad?.SamAccountName == "cas.vos");
        var page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        page.InvokeAsync(() => page.FindAll("button").First(b => b.TextContent == "Gmail-instellingen lezen").Click()).GetAwaiter().GetResult();
        page.WaitForAssertion(() => Assert.Contains("Stuurt niet door.", page.Find("#gmail").TextContent, StringComparison.Ordinal));
        Assert.Contains(s.Database.ReadAccess(10), a => a.What == "Gmail-instellingen bekeken" && a.Details == "cas.vos@test.be");
        page.InvokeAsync(() => page.Find("#gmail input[aria-label='Doorsturen naar']").Input("lien@test.be")).GetAwaiter().GetResult();
        ConfirmSteady(page, () => page.Find("#gmail").QuerySelectorAll("button").First(b => b.TextContent == "Plan maken").Click());
        page.WaitForAssertion(() => Assert.Contains(f.Executed, w => w.ChangeAction == ChangeAction.GmailSetForwarding && w.P("adres") == "lien@test.be" && w.Target == "cas.vos@test.be"));

        // Removed: no service account any more, and without the part no Gmail on the page
        s.RemoveGmailKey();
        Assert.Null(s.GmailKey);
        s.Config.PermissionParts.Clear();
        Assert.Empty(ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key)).FindAll("#gmail"));
    }
}
