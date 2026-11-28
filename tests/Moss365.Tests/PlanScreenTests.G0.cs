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

    private static readonly string[] GooglePages = { "google-nieuw", "google-groepen", "google-groep", "google-bronnen" };

    private static readonly string[] Microsoft365Pages =
        { "gasten", "postvakken", "rooms", "contacten", "beveiliging", "conditional-access", "inrichten", "licenties", "samenwerking", "gastenverlenging" };

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
            Assert.Equal(hasMicrosoft365 || systems.HasGoogle(), Fits("bewaarde-logboeken")); // G1: the Google logs too
            Assert.All(GooglePages, p => Assert.Equal(systems.HasGoogle(), Fits(p))); // G2

            // Every page of the menu either fits or belongs to a system the tenant does not have
            foreach (var page in MenuModel.AllPages.Select(i => i.Href.Split('?')[0]))
            {
                var first = page.Split('/')[0];
                var belongsElsewhere = (AdPages.Contains(first) && !hasAd) || (Microsoft365Pages.Contains(first) && !hasMicrosoft365)
                                       || (first == "vergelijk" && !(hasAd && hasMicrosoft365)) || (first == "nieuw" && !(hasAd || hasMicrosoft365))
                                       || (first is "koppelingen" or "bron" && systems.Count() < 2)
                                       || (first == "bewaarde-logboeken" && !(hasMicrosoft365 || systems.HasGoogle()))
                                       || (GooglePages.Contains(first) && !systems.HasGoogle());
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
    public void EveryPlanStepAndLineOfTheChangeLogHasItsPlatformLabel()
    {
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Assert.Contains("kb-platform", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">AD</span>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">Entra ID</span>", cut.Markup, StringComparison.Ordinal);

        var plan = new List<Moss365.Core.Changes.Change>
        {
            new() { TargetSystem = Moss365.Core.Changes.TargetSystem.ActiveDirectory, ChangeAction = Moss365.Core.Changes.ChangeAction.AdUnlockAccount, Target = "x", Description = "Ontgrendelen" },
            new() { TargetSystem = Moss365.Core.Changes.TargetSystem.Exchange, ChangeAction = Moss365.Core.Changes.ChangeAction.ExchangeAutoReply, Target = "y", Description = "Afwezig" },
        };
        var view = RenderComponent<PlanView>(p => p.Add(x => x.Plan, plan).Add(x => x.Title, "Plan"));
        var labels = view.FindAll("td .kb-platform").Select(l => l.TextContent).ToList();
        Assert.Equal(new[] { "AD", "Exchange Online" }, labels);
        Assert.All(view.FindAll("td .kb-platform"), l => Assert.Contains("--kb-platform-kleur: #", l.GetAttribute("style"), StringComparison.Ordinal));
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

/// <summary>G0, U30: every platform has a label with a name and a fixed colour; every change and line of the log gets one.</summary>
public class PlatformLabelTests
{
    [Fact]
    public void EveryChangeActionBelongsToThePlatformOfItsSystem()
    {
        foreach (var action in Enum.GetValues<Moss365.Core.Changes.ChangeAction>())
        {
            var platform = Moss365.Core.Changes.Platforms.Of(action);
            var name = action.ToString();
            Assert.True(name.StartsWith("Ad", StringComparison.Ordinal) || name.StartsWith("Graph", StringComparison.Ordinal)
                        || name.StartsWith("Exchange", StringComparison.Ordinal) || name.StartsWith("Intune", StringComparison.Ordinal)
                        || name.StartsWith("Google", StringComparison.Ordinal) || name.StartsWith("Gmail", StringComparison.Ordinal), name);
            var system = name.StartsWith("Ad", StringComparison.Ordinal) ? TenantSystems.ActiveDirectory
                : name.StartsWith("Google", StringComparison.Ordinal) || name.StartsWith("Gmail", StringComparison.Ordinal) ? TenantSystems.GoogleWorkspace
                : TenantSystems.Microsoft365;
            Assert.Equal(system, Moss365.Core.Changes.Platforms.System(platform));
        }

        Assert.Equal(Moss365.Core.Changes.Platform.Intune, Moss365.Core.Changes.Platforms.Of(Moss365.Core.Changes.ChangeAction.IntuneDeviceAction));
        Assert.Equal(Moss365.Core.Changes.Platform.EntraId, Moss365.Core.Changes.Platforms.Of(Moss365.Core.Changes.ChangeAction.GraphDeleteDevice));
        Assert.Equal(Moss365.Core.Changes.Platform.ExchangeOnline, Moss365.Core.Changes.Platforms.Of(Moss365.Core.Changes.ChangeAction.ExchangeAutoReply));
    }

    [Fact]
    public void EveryPlatformHasANameAndItsOwnColour()
    {
        var platforms = Moss365.Core.Changes.Platforms.All;
        Assert.Equal(platforms.Count, platforms.Select(Moss365.Core.Changes.Platforms.Colour).Distinct().Count());
        Assert.All(platforms, p => Assert.False(string.IsNullOrWhiteSpace(Moss365.Core.Changes.Platforms.Short(p))));
        Assert.All(platforms, p => Assert.Matches("^#[0-9a-f]{6}$", Moss365.Core.Changes.Platforms.Colour(p)));
        // White text stays readable on every colour (WCAG contrast 4.5:1)
        Assert.All(platforms, p => Assert.True(ContrastWithWhite(Moss365.Core.Changes.Platforms.Colour(p)) >= 4.5, p.ToString()));
    }

    [Fact]
    public void LinesOfTheLogAndTablesOfTheSnapshotsGetTheirPlatform()
    {
        Assert.Equal(Moss365.Core.Changes.Platform.ActiveDirectory, Moss365.Core.Changes.Platforms.OfLog("AdSetPassword"));
        Assert.Equal(Moss365.Core.Changes.Platform.EntraId, Moss365.Core.Changes.Platforms.OfLog("GraphRevokeSessions"));
        Assert.Null(Moss365.Core.Changes.Platforms.OfLog("AcceptRisk"));
        Assert.Null(Moss365.Core.Changes.Platforms.OfLog("UpdateApp"));
        Assert.Equal(Moss365.Core.Changes.Platform.ActiveDirectory, Moss365.Core.Changes.Platforms.OfTable("ad-gebruikers.csv"));
        Assert.Equal(Moss365.Core.Changes.Platform.ExchangeOnline, Moss365.Core.Changes.Platforms.OfTable("exo-postvakken-niet-gebruikers.csv"));
        Assert.Equal(Moss365.Core.Changes.Platform.Intune, Moss365.Core.Changes.Platforms.OfTable("toestellen-intune.csv"));
        Assert.Null(Moss365.Core.Changes.Platforms.OfTable("functieprofielen"));
    }

    private static double ContrastWithWhite(string hex)
    {
        static double Channel(int v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        var luminance = 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        return 1.05 / (luminance + 0.05);
    }
}

/// <summary>G0, U31: where the mail lives, on its page and on the user page.</summary>
public sealed partial class PlanScreenTests
{
    private sealed class FakeMxLookup : Moss365.Core.Mail.IMxLookup
    {
        public List<string> Asked { get; } = new();

        public Task<IReadOnlyList<Moss365.Core.Mail.MxRecord>> MxAsync(string domain, CancellationToken cancel = default)
        {
            Asked.Add(domain);
            var host = domain.Contains("fabrikam", StringComparison.Ordinal) ? "aspmx.l.google.com" : domain.Replace('.', '-') + ".mail.protection.outlook.com";
            return Task.FromResult<IReadOnlyList<Moss365.Core.Mail.MxRecord>>(new[] { new Moss365.Core.Mail.MxRecord(10, host) });
        }
    }

    private sealed partial class FakeFactory
    {
        public FakeMxLookup MxFake { get; } = new();

        public Moss365.Core.Mail.IMxLookup? Dns => MxFake;
    }

    [Fact]
    public void WhereTheMailLivesIsCheckedChosenAndLogged()
    {
        var cut = RenderComponent<MailLocationPage>();
        Assert.Contains("Waar de mail zit", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("het voorstel: Exchange Online", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("<option value=\"Gmail\"", cut.Markup, StringComparison.Ordinal); // this tenant has no Google Workspace

        cut.FindAll("button").First(b => b.TextContent == "Nu controleren").Click();
        cut.WaitForAssertion(() => Assert.NotNull(State.MailSettings.CheckedAt));
        Assert.NotEmpty(_factory.MxFake.Asked);
        Assert.All(State.MailSettings.Routes, r => Assert.Equal(Moss365.Core.Mail.MailSystem.ExchangeOnline, r.System));
        cut.WaitForAssertion(() => Assert.Contains("Laatste controle", cut.Markup, StringComparison.Ordinal));

        // The default chosen by hand, in the change log with what it was before
        cut.Find("section select").Change(nameof(Moss365.Core.Mail.MailSystem.ExchangeOnPremises));
        cut.FindAll("button").First(b => b.TextContent == "Bewaren").Click();
        Assert.Equal(Moss365.Core.Mail.MailSystem.ExchangeOnPremises, State.MailSettings.Default);
        var line = State.ChangeLog.Recent(5).First(r => r.ChangeAction == "MailLocation");
        Assert.Equal("ExchangeOnPremises", line.Parameters["standaard"]);
        Assert.Equal("Unknown", line.Previous!["standaard"]);
        // The domains of this tenant now arrive elsewhere than the default: a warning
        cut.WaitForAssertion(() => Assert.Contains("komt niet aan in de standaard", cut.Markup, StringComparison.Ordinal));
        // Only counts in the technical log, never a domain
        Assert.DoesNotContain(State.Database.ReadTechnicalLog(50), l => State.MailSettings.Routes.Any(r => l.Message.Contains(r.Domain, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ThePageOfAPersonSaysWhereTheMailIsAndLeavesOutWhatOnlyExchangeCanDo()
    {
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var page = RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Assert.Contains("mail in", page.Markup, StringComparison.Ordinal);
        Assert.Contains(">Exchange Online</span>", page.Markup, StringComparison.Ordinal);

        State.SetMailOf(cas, Moss365.Core.Mail.MailSystem.Other);
        page = RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        Assert.Contains("een ander systeem", page.Markup, StringComparison.Ordinal);
        Assert.Contains("enkel voor Exchange Online", page.Markup, StringComparison.Ordinal);

        var list = RenderComponent<MailLocationPage>();
        Assert.Contains("met de hand gekozen", list.Markup, StringComparison.Ordinal);
        list.FindAll("button").First(b => b.TextContent == "Terug naar de standaard").Click();
        Assert.False(State.MailSettings.Users.ContainsKey(cas.Key));
        Assert.Equal(2, State.ChangeLog.Recent(10).Count(r => r.ChangeAction == "MailLocation" && r.Target == cas.Key));
    }
}

/// <summary>G0, U32: the page Personen koppelen.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void ThePageOfLinksShowsTheRulesTheCountsAndTheDoubtfulCases()
    {
        var cut = RenderComponent<PersonLinksPage>();
        Assert.Contains("Personen koppelen", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Entra Connect", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("In AD en Entra ID (gesynchroniseerd)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Twijfelgevallen", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Google-accounts", cut.Markup, StringComparison.Ordinal); // this tenant has no Google Workspace
        Assert.True(Access.FitsTenant("koppelingen", TenantSystems.Both));
        Assert.False(Access.FitsTenant("koppelingen", TenantSystems.GoogleWorkspace));
    }
}

/// <summary>G0, U33: the page Bron van waarheid.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void ThePageOfTheSourceShowsTheChainAndAsksWhatMossCannotSee()
    {
        var cut = RenderComponent<SourcePage>();
        Assert.Contains("Bron van waarheid", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">AD</span>", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Entra Connect synchroniseert", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Waar Google Workspace", cut.Markup, StringComparison.Ordinal); // no Google in this tenant

        var (hub, _, _) = Hub("g0-bron");
        using var _ = hub;
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Both | TenantSystems.GoogleWorkspace });
        var s = hub.Current;
        Assert.Contains(s.SourceChains(Moss365.Core.Linking.TruthKind.Users), c => c.Count == 1 && c[0] == Moss365.Core.Changes.Platform.GoogleWorkspace);
        s.SaveSource(new Moss365.Core.Linking.SourceSettings { GoogleUsers = Moss365.Core.Linking.GoogleFeed.FromAd, GoogleGroups = Moss365.Core.Linking.GoogleFeed.Itself, GooglePasswordSync = true });
        Assert.Contains(s.SourceChains(Moss365.Core.Linking.TruthKind.Users), c => c[0] == Moss365.Core.Changes.Platform.ActiveDirectory && c[^1] == Moss365.Core.Changes.Platform.GoogleWorkspace);
        var line = s.ChangeLog.Recent(3).First(r => r.ChangeAction == "SourceOfTruth");
        Assert.Equal("FromAd", line.Parameters["gebruikers"]);
        Assert.Equal("Unknown", line.Previous!["gebruikers"]);
    }
}
