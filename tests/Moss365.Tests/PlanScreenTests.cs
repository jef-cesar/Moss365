using AngleSharp.Dom;
using Bunit;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.UI.Components;
using Microsoft.JSInterop;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>
/// Screen tests for the write actions: create a plan, confirm, run via fake connections, change log and follow-up.
/// Nothing connects for real.
/// </summary>
public sealed partial class PlanScreenTests : TestContext, IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "kb-plan-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFactory _factory = new();

    public PlanScreenTests()
    {
        TestData.WriteInventory(Path.Combine(_base, "Inventaris", "Inventaris-20261016-1343"));
        var defaultValue = Path.Combine(_base, "standaard.json");
        ConfigStorage.Save(TestData.Config(), defaultValue);
        Services.AddSingleton<IAppEnvironment>(new FixedEnvironment(Path.Combine(_base, "Inventaris"), defaultValue, Path.Combine(_base, "eigen", "organisatie.json"), Scripts: Path.Combine(_base, "scripts-ingebouwd")));
        Services.AddSingleton<IConnectionFactory>(_factory);
        Services.AddSingleton<ManagementState>();

        var state = Services.GetRequiredService<ManagementState>();
        state.Settings.AdServer = "dc.test.internal";
        state.Settings.TenantId = "tenant";
        state.Settings.ClientId = "client";
        state.SaveSettings(); // otherwise the connection settings show as "not saved"
    }

    private ManagementState State => Services.GetRequiredService<ManagementState>();

    void IDisposable.Dispose()
    {
        base.Dispose();

        // A background task (refreshing after a plan) may still write something: try again shortly
        for (var attempt = 0; attempt < 10 && Directory.Exists(_base); attempt++)
        {
            try
            {
                Directory.Delete(_base, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void Confirm(IRenderedFragment cut)
    {
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).Click();
    }

    private static IElement Field(IRenderedFragment cut, string label, string kind = "input")
        => cut.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith(label, StringComparison.Ordinal)).QuerySelector(kind)!;

    [Fact]
    public void ApplyMirrorAfterConfirmation()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        var cut = RenderComponent<Mirror>();

        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("gg_rww_foxbeleid", cut.Find("section.kb-plan").TextContent);
        Assert.DoesNotContain("Domain Admins", cut.Find("section.kb-plan").TextContent);

        // Run first, then confirm: only then does something happen
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        Assert.Empty(_factory.Executed);
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        // The synchronised group via AD (once), the mail groups via Exchange
        Assert.Equal(
            new[] { "AdAddGroupMember gg_rww_foxbeleid", "ExchangeAddGroupMember gemeentehuis@test.be", "ExchangeAddGroupMember sg-sm-info-full@test.be" },
            _factory.Executed.Select(w => $"{w.ChangeAction} {w.P("groep")}").OrderBy(t => t, StringComparer.Ordinal));

        var log = State.ChangeLog.Recent(10);
        Assert.Equal(3, log.Count);
        Assert.All(log, r => Assert.Equal(nameof(ResultStatus.Succeeded), r.Status));
        Assert.All(log, r => Assert.Contains("Spiegel", r.Context));
    }

    [Fact]
    public void PlanWithoutConnectionWarns()
    {
        State.Settings.AdServer = string.Empty;
        Services.GetRequiredService<NavigationManager>().NavigateTo("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        var cut = RenderComponent<Mirror>();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Geen verbinding ingesteld voor: AD", cut.Markup);
    }

    [Fact]
    public void UncheckedIsNotRun()
    {
        var plan = new List<Change>
        {
            new() { TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdSetAttributes, Target = "CN=a", Description = "een" },
            new() { TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdSetAttributes, Target = "CN=b", Description = "twee" },
        };
        var cut = RenderComponent<PlanView>(p => p.Add(x => x.Plan, plan).Add(x => x.Context, "test"));
        cut.Find("input[aria-label=twee]").Change(false);
        Assert.Contains("Uitvoeren (1)", cut.Markup);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.Equal("CN=a", Assert.Single(_factory.Executed).Target);
    }

    [Fact]
    public void CreateNewUserShowsPasswordAndSavesFollowUp()
    {
        var cut = RenderComponent<NewUser>();
        Field(cut, "Soort", "select").Change("Gemeente");
        Field(cut, "Voornaam").Change("Eva");
        Field(cut, "Achternaam").Change("De Smet");
        Field(cut, "Dienst", "select").Change("cultuur");
        Field(cut, "Kantoor", "select").Change("gemeentehuis");
        Field(cut, "Licentie", "select").Change("DESKLESSPACK");
        cut.FindAll("button").First(b => b.TextContent == "Voorbeeld").Click();

        Assert.Contains("eva.desmet@test.be", cut.Markup);
        Assert.Contains("cultuur.bat", cut.Markup);
        Assert.Contains("Licentie DESKLESSPACK toekennen", cut.Markup);
        Assert.Contains("Standaardgroep bestaat_niet niet gevonden", cut.Markup);
        Assert.DoesNotContain("Startwachtwoord", cut.Markup);

        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("Startwachtwoord", cut.Markup));

        var create = _factory.Executed.First(w => w.ChangeAction == ChangeAction.AdCreateUser);
        Assert.Equal(create.Secret, cut.Find("code.kb-wachtwoord").TextContent);
        Assert.Equal("cultuur", create.P("department"));
        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdAddGroupMember && w.P("groep") == "gg_wifiIntern");

        // The password is nowhere in the change log or the follow-up
        var files = Directory.GetFiles(Path.Combine(_base, "eigen"), "*", SearchOption.AllDirectories);
        Assert.All(files, f => Assert.DoesNotContain(create.Secret!, ReadShared(f)));

        var task = Assert.Single(State.FollowUp.Read());
        Assert.Equal("eva.desmet@test.be", task.Upn);
        Assert.Contains(task.Changes, w => w.ChangeAction == ChangeAction.GraphAssignLicence);
        Assert.Contains(task.Changes, w => w.ChangeAction == ChangeAction.GraphAddGroupMember && w.P("groep") == "AFD_VRIJETIJD");
    }

    [Fact]
    public void FollowUpWaitsUntilTheUserExistsInEntra()
    {
        State.FollowUp.Add(new FollowUpTask
        {
            Upn = "eva.desmet@test.be",
            Description = "Nieuwe gebruiker Eva De Smet",
            Changes = new() { new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphAssignLicence, Target = "eva.desmet@test.be", Description = "Licentie" } },
        });

        var cut = RenderComponent<FollowUp>();
        cut.FindAll("button").First(b => b.TextContent == "Afwerken").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, afwerken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Nog niet in Entra", cut.Markup));
        Assert.Empty(_factory.Executed);

        _factory.GraphId = "e-eva";
        cut.FindAll("button").First(b => b.TextContent == "Afwerken").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, afwerken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Alles afgewerkt", cut.Markup));
        Assert.Equal(ChangeAction.GraphAssignLicence, Assert.Single(_factory.Executed).ChangeAction);
        Assert.Empty(State.FollowUp.Read());
    }

    [Fact]
    public void OffboardingMovesAndRemovesPrivilegedGroups()
    {
        var cut = RenderComponent<Offboarding>(p => p.Add(x => x.Key, "ad:an.peeters"));
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("Verplaatsen naar ARCHIVE/disabled_users", plan);
        Assert.Contains("Uit AD-groep Domain Admins halen (bevoorrecht)", plan);
        Assert.DoesNotContain("verwijderen", plan, StringComparison.OrdinalIgnoreCase);

        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.Equal(ChangeAction.AdMove, _factory.Executed.Last(w => w.TargetSystem == TargetSystem.ActiveDirectory).ChangeAction);
        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdRemoveGroupMember && w.P("groep") == "Domain Admins");
        Assert.DoesNotContain(_factory.Executed, w => w.ChangeAction == ChangeAction.AdRemoveGroupMember && w.P("groep") == "gg_wifiIntern");
    }

    [Fact]
    public void BringRoomsToTheDefault()
    {
        var cut = RenderComponent<Rooms>();
        Assert.Contains("Boekingsvenster: 600 (standaard 180)", cut.Markup);
        cut.Find("thead th.kb-kies input").Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Standaard invullen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();

        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("BookingWindowInDays=180", plan);
        Assert.DoesNotContain("Vergaderzaal B", plan);

        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.All(_factory.Executed, w => Assert.Equal(TargetSystem.Exchange, w.TargetSystem));
        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.ExchangeCalendarPermission && w.P("AccessRights") == "Reviewer");
    }

    [Fact]
    public void LiveLoadReplacesTheData()
    {
        var cut = RenderComponent<UI.Pages.Connections>();
        Assert.DoesNotContain("enkel beschikbaar in de Windows-app", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Live laden (AD en Entra)").Click();
        WaitOnTask();
        cut.WaitForAssertion(() => Assert.Contains("Bewaren als inventaris", cut.Markup));
        Assert.Equal("Live", State.Data.Origin);
        Assert.Equal("DC=test,DC=internal", State.Data.DomainDn);

        // The password is never saved
        State.AdPassword = "Geheim123!";
        State.SaveSettings();
        Assert.Contains("dc.test.internal", State.Database.ReadDocument("verbinding"));
        Assert.DoesNotContain("Geheim123!", DatabaseOnDisk());
    }

    [Fact]
    public void ChangeLogShowsExecutedChanges()
    {
        State.ChangeLog.Write(new LogEntry(DateTimeOffset.Now, "beheerder", "test", "AD", "AdKenmerkenZetten", "CN=x", "Xavier",
            "Omschrijving aanpassen", new Dictionary<string, string>(), "Mislukt", "geen rechten"));
        var cut = RenderComponent<ChangeLog>();
        Assert.Contains("geen rechten", cut.Markup);
        Assert.Contains("kb-ontbreekt", cut.Markup);
    }

    private static void Choose(IRenderedFragment cut, string label, string search, string result)
    {
        var picker = cut.FindAll(".kb-kiezer").First(k => k.QuerySelector("label")?.TextContent.Trim() == label);
        picker.QuerySelector("input")!.Input(search);
        cut.FindAll(".kb-kiezer").First(k => k.QuerySelector("label")?.TextContent.Trim() == label)
            .QuerySelectorAll("button.kb-link").First(b => b.TextContent.Contains(result, StringComparison.OrdinalIgnoreCase)).Click();
    }

    [Fact]
    public void GroupsListLinksToTheGroup()
    {
        var cut = RenderComponent<Groups>();
        Assert.Contains("href=\"groep/ad%3Agg_eigen\"", cut.Markup);
        cut.Find("input[aria-label=Zoeken]").Input("SG-SM");
        Assert.Single(cut.FindAll("tbody tr"));
    }

    [Fact]
    public void GroupShowsRelationsAndAddsMember()
    {
        var cut = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, "ad:gg_eigen"));
        Assert.Contains("href=\"groep/ad%3Agg_rww_foxbeleid\"", cut.Markup);   // nested group as a member
        Assert.Contains("href=\"gebruiker/ad%3Abo.janssens\"", cut.Markup);

        Choose(cut, "Lid toevoegen", "cas", "cas vos");
        Assert.Contains("Lid maken van AD-groep gg_eigen", cut.Find("section.kb-plan").TextContent);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        var w = Assert.Single(_factory.Executed);
        Assert.Equal(ChangeAction.AdAddGroupMember, w.ChangeAction);
        Assert.Equal(TestData.Dn("cas.vos", TestData.SocialWelfare), w.Target);
    }

    [Fact]
    public void GroupMemberOfAndMailboxPermissions()
    {
        var fox = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, "ad:gg_rww_foxbeleid"));
        Assert.Contains("gg_eigen", fox.FindAll("section.kb-kaart").First(s => s.TextContent.Contains("Lid van")).TextContent);

        var sg = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, "cloud:g-sm"));
        Assert.Contains("href=\"postvak/info%40test.be\"", sg.Markup);
        sg.FindAll("button").First(b => b.TextContent == "Gekozen leden verwijderen (0)");
    }

    [Fact]
    public void LoadCloudOwnersLive()
    {
        var cut = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, "cloud:g-team"));
        cut.FindAll("button").First(b => b.TextContent == "Eigenaars live laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("eigenaar in Entra", cut.Markup));
    }

    [Fact]
    public void MailboxCleanupDirectPermissions()
    {
        var cut = RenderComponent<MailboxDetail>(p => p.Add(x => x.Smtp, "info@test.be"));
        Assert.Contains("SG-SM-info-Full", cut.Markup);
        Assert.Contains("rechtstreeks", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent == "Rechtstreekse rechten opruimen").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.ExchangeAddGroupMember && w.Target == "bo.janssens@test.be");
        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.ExchangeRemoveMailboxPermission && w.P("Trustee") == "bo.janssens@test.be");
        Assert.DoesNotContain(_factory.Executed, w => w.P("Trustee").StartsWith("S-1-5", StringComparison.Ordinal));
    }

    [Fact]
    public void MailboxWithoutGroupAndGrantAccess()
    {
        var cut = RenderComponent<MailboxDetail>(p => p.Add(x => x.Smtp, "boekhouding@test.be"));
        Assert.Contains("SG-SM-boekhouding-Full", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Toegangsgroep aanmaken").Click();
        Assert.Contains("Mail-beveiligingsgroep SG-SM-boekhouding-Full aanmaken", cut.Find("section.kb-plan").TextContent);

        var info = RenderComponent<MailboxDetail>(p => p.Add(x => x.Smtp, "info@test.be"));
        Choose(info, "Toegang geven aan", "cas", "cas vos");
        Assert.Contains("SG-SM-info-Full", info.Find("section.kb-plan").TextContent);
    }

    [Fact]
    public void NewSharedMailbox()
    {
        var cut = RenderComponent<NewMailbox>();
        Field(cut, "Weergavenaam").Change("Jeugddienst");
        Field(cut, "Adres").Change("jeugddienst");
        Choose(cut, "Wie krijgt toegang?", "cas", "cas vos");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Equal(5, cut.FindAll("section.kb-plan tbody tr").Count);
        Assert.Contains("jeugddienst@test.be", cut.Markup);
    }

    [Fact]
    public void RoomPageWithRelations()
    {
        var cut = RenderComponent<RoomDetail>(p => p.Add(x => x.Smtp, "rm-vz-a@test.be"));
        Assert.Contains("Wijkt af van de standaard", cut.Markup);
        Assert.Contains("rl-gh", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Standaard invullen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("BookingWindowInDays=180", cut.Find("section.kb-plan").TextContent);
    }

    [Fact]
    public void UserShowsMailboxesAndLinks()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:an.peeters"));
        Assert.Contains("href=\"postvak/boekhouding%40test.be\"", cut.Markup);
        Assert.Contains("href=\"postvak/info%40test.be\"", cut.Markup);
        Assert.Contains("href=\"groep/ad%3Agg_wifiintern\"", cut.Markup);
    }

    [Fact]
    public void SignInBarShowsLinkToCopy()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<SignInBar>();
        Assert.Empty(cut.Markup.Trim());

        using var cts = new CancellationTokenSource();
        State.SignInEvent.Show(new Uri("https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize?client_id=x"), cts);
        cut.WaitForAssertion(() => Assert.Contains("login.microsoftonline.com", cut.Find("input").GetAttribute("value")));

        cut.FindAll("button").First(b => b.TextContent == "Kopiëren").Click();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "moss365.kopieer");

        cut.FindAll("button").First(b => b.TextContent == "Annuleren").Click();
        Assert.True(cts.IsCancellationRequested);

        State.SignInEvent.Done();
        cut.WaitForAssertion(() => Assert.Empty(cut.Markup.Trim()));
    }

    [Fact]
    public void LoadMailboxesLive()
    {
        var cut = RenderComponent<UI.Pages.Connections>();
        cut.FindAll("button").First(b => b.TextContent == "Postvakken en groepen laden uit Exchange").Click();
        WaitOnTask();
        Assert.Single(State.Data.Mailboxes);
        Assert.Contains(State.Data.Sources, b => b.File.StartsWith("Postvakken", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 0.4: tables, back, background, refresh

    /// <summary>The view choices as saved in the database (since 0.8).</summary>
    private string View() => State.Database.ReadDocument("weergave") ?? string.Empty;

    /// <summary>Read a file the app still has open (database, technical log): otherwise Windows refuses.</summary>
    private static string ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, System.Text.Encoding.Latin1);
        return r.ReadToEnd();
    }

    /// <summary>Everything the database has on disk (also the WAL file), as text: to prove that a secret is not in
    /// it.</summary>
    private string DatabaseOnDisk()
    {
        var text = new System.Text.StringBuilder();
        foreach (var path in new[] { State.Database.FilePath, State.Database.FilePath + "-wal" }.Where(File.Exists))
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f, System.Text.Encoding.Latin1);
            text.Append(r.ReadToEnd());
        }

        return text.ToString();
    }

    [Fact]
    public void TableSortHideAndMove()
    {
        var cut = RenderComponent<Groups>();
        string First() => cut.Find("tbody tr td:not(.kb-kies)").TextContent.Trim();
        var ascendingValue = First();

        cut.FindAll("th").First(t => t.TextContent.Trim().StartsWith("Groep", StringComparison.Ordinal)).Click();
        Assert.NotEqual(ascendingValue, First());
        Assert.Contains("descending", cut.FindAll("th").First(t => t.TextContent.Contains("Groep")).GetAttribute("aria-sort"));

        cut.FindAll("button").First(b => b.TextContent == "Kolommen").Click();
        cut.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("Soort")).QuerySelector("input")!.Change(false);
        Assert.DoesNotContain(cut.FindAll("thead th"), t => t.TextContent.Trim() == "Soort");

        // Show a column that is hidden by default and move it forward
        cut.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("sAMAccountName")).QuerySelector("input")!.Change(true);
        Assert.Contains(cut.FindAll("thead th"), t => t.TextContent.Trim() == "sAMAccountName");
        cut.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("Waar")).QuerySelectorAll("button")[0].Click();
        Assert.Equal("Waar", cut.FindAll("thead th:not(.kb-kies)")[0].TextContent.Trim());

        // Saved for the next time
        var json = View();
        Assert.Contains("\"groepen\"", json);
        Assert.Contains("soort", json);
        var again = RenderComponent<Groups>();
        Assert.Equal("Waar", again.FindAll("thead th:not(.kb-kies)")[0].TextContent.Trim());

        cut.FindAll("button").First(b => b.TextContent == "Standaard").Click();
        Assert.Equal("Groep ▲", cut.FindAll("thead th:not(.kb-kies)")[0].TextContent.Trim());
    }

    [Fact]
    public void BackAndForwardInTheApp()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderComponent<Navigation>();
        IElement Button(string text) => cut.FindAll("button").First(b => b.TextContent.Contains(text));
        Assert.True(Button("Terug").HasAttribute("disabled"));
        Assert.True(Button("Vooruit").HasAttribute("disabled"));

        nav.NavigateTo("groepen");
        nav.NavigateTo("rooms");
        cut.WaitForAssertion(() => Assert.False(Button("Terug").HasAttribute("disabled")));
        Button("Terug").Click();
        Assert.EndsWith("/groepen", nav.Uri);
        Assert.False(Button("Vooruit").HasAttribute("disabled"));
        Button("Vooruit").Click();
        Assert.EndsWith("/rooms", nav.Uri);
        Assert.True(Button("Vooruit").HasAttribute("disabled"));

        // A new page after Back: the steps after it lapse
        Button("Terug").Click();
        nav.NavigateTo("postvakken");
        cut.WaitForAssertion(() => Assert.True(Button("Vooruit").HasAttribute("disabled")));
        Button("Terug").Click();
        Assert.EndsWith("/groepen", nav.Uri);
        Button("Terug").Click();
        Assert.True(Button("Terug").HasAttribute("disabled"));

        // Mouse buttons and Alt+arrow go through the script to the same buttons
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "moss365.navigatie");
    }

    [Fact]
    public void LoadAllInTheBackgroundAndSave()
    {
        var layout = RenderComponent<Moss365.UI.Layout.MainLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<p>inhoud</p>")));
        Assert.Contains("Niet live", layout.Markup);

        var cut = RenderComponent<UI.Pages.Connections>();
        cut.FindAll("button").First(b => b.TextContent == "Alles live laden").Click();
        WaitOnTask();

        var task = State.Job!;
        Assert.Equal(new[] { "AD lezen", "Entra lezen", "Beveiliging lezen (MFA en beheerrollen)", "Conditional Access lezen", "Rooms, postvakken en groepen lezen (Exchange)", "Bewaren als inventaris" },
            task.Steps.Select(s => s.Name));
        Assert.All(task.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Contains(task.Log, r => r.Contains("AD gelezen"));
        Assert.Equal(new[] { "AD", "Entra", "Rooms", "Postvakken" }, State.LiveOn.Keys.OrderBy(k => k switch { "AD" => 0, "Entra" => 1, "Rooms" => 2, _ => 3 }));

        // Saved as an inventory, and the app finds that folder
        Assert.NotNull(State.SavedInventory);
        Assert.True(State.Files.Exists($"{State.SavedInventory}/{InventoryLoader.AdUsers}"));
        Assert.Contains(State.Folders, m => m.EndsWith("-live", StringComparison.Ordinal));

        // Away and back to the screen: the progress is still there; the menu shows the status
        var back = RenderComponent<UI.Pages.Connections>();
        Assert.Contains("Bewaren als inventaris", back.Markup);
        Assert.Contains("Tekstlog", back.Markup);
        layout.WaitForAssertion(() => Assert.Contains("AD: live", layout.Markup));
        Assert.DoesNotContain("Niet live", layout.Markup);
        Assert.Contains("Alles live laden: klaar", layout.Markup);
    }

    [Fact]
    public void RefreshObjectWithTheButton()
    {
        _factory.AdReadAfter = u => u with { Enabled = false };
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        Assert.Contains("uit de inventaris", cut.Find(".kb-objectstatus").TextContent);

        cut.FindAll("button").First(b => b.TextContent.Contains("Vernieuwen")).Click();
        cut.WaitForAssertion(() => Assert.Contains("live gelezen om", cut.Find(".kb-objectstatus").TextContent));

        // The status draws itself; the page recalculates a moment later (AD and Entra run at the same time)
        cut.WaitForAssertion(() => Assert.Contains("uitgeschakeld", cut.Markup));
        Assert.Contains("ad:cas.vos", _factory.WasRead);
        Assert.Contains("entra:e-cas", _factory.WasRead);
    }

    [Fact]
    public void AfterOffboardingTheUserIsRefreshed()
    {
        _factory.AdReadAfter = u => u.SamAccountName == "an.peeters" ? u with { Enabled = false } : u;
        var cut = RenderComponent<Offboarding>(p => p.Add(x => x.Key, "ad:an.peeters"));
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        WaitOnTask();
        Assert.StartsWith("Vernieuwen na wijziging", State.Job!.Name);
        Assert.False(State.Data.SearchPerson("ad:an.peeters")!.Enabled);
        Assert.Equal(0, State.CountChanged);
        Assert.Contains("live gelezen om", State.OriginOf(State.Data.Index.Ref(State.Data.SearchPerson("ad:an.peeters")!)));
    }

    [Fact]
    public void WithoutConnectionTheObjectStaysMarked()
    {
        _factory.ReadFailed = true;
        var cut = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, "cloud:g-team"));
        Choose(cut, "Lid toevoegen", "cas", "cas vos");
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        WaitOnTask();

        // Reading Entra fails: refreshing fails and the group stays "changed, not refreshed yet"
        Assert.Contains(State.Job!.Steps, s => s.Status == StepStatus.Error);
        Assert.True(State.IsChanged(State.Data.Index.Group("cloud:g-team")!.Ref));
        Assert.Contains("gewijzigd, nog niet vernieuwd", cut.Markup);
    }

    [Fact]
    public void InviteAndRemoveGuestUsers()
    {
        var cut = RenderComponent<Guests>();
        Assert.Contains("1 gasten", cut.Markup);
        Field(cut, "E-mailadres").Change("jan@example.net");
        Field(cut, "Naam").Change("Jan (Leverancier)");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Gast uitnodigen", cut.Find("section.kb-plan").TextContent);

        cut.Find("tbody td.kb-kies input").Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Gekozen gasten verwijderen", StringComparison.Ordinal)).Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.Equal(ChangeAction.GraphRemoveGuest, Assert.Single(_factory.Executed).ChangeAction);
    }

    [Fact]
    public void ShowStatistics()
    {
        var cut = RenderComponent<Statistics>();
        Assert.Contains("bijna vol", cut.Markup);
        Assert.Contains("10 / 10", cut.Markup);
        Assert.Contains($"href=\"{Moss365.Core.Analysis.Kpis.Guests.Link}\"", cut.Markup);
    }

    [Fact]
    public void CheckOfThePcWithInstall()
    {
        var fake = new FakeCheck();
        _factory.Check = fake;
        var cut = RenderComponent<ThisComputer>();
        cut.FindAll("button").First(b => b.TextContent == "Controleren").Click();
        cut.WaitForAssertion(() => Assert.Contains("ExchangeOnlineManagement", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent == "Installeren").Click();
        Assert.Empty(fake.Installed);   // confirm first
        cut.FindAll("button").First(b => b.TextContent == "Ja, installeren").Click();
        cut.WaitForAssertion(() => Assert.Equal("ExchangeOnlineManagement", Assert.Single(fake.Installed)));
    }

    private sealed class FakeCheck : ISystemCheck
    {
        public List<string> Installed { get; } = new();

        public Task<IReadOnlyList<CheckRule>> CheckAsync(ConnectionSettings settings, IProgress<string>? progress, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<CheckRule>>(new[]
            {
                new CheckRule("PowerShell 7", "ExchangeOnlineManagement", Installed.Count == 0 ? CheckStatus.Error : CheckStatus.Good,
                    Installed.Count == 0 ? "niet geïnstalleerd" : "3.7.2", "Nodig voor Exchange.", "Install-Module ExchangeOnlineManagement", "ExchangeOnlineManagement", "pwsh.exe"),
                new CheckRule("Netwerk", "graph.microsoft.com", CheckStatus.Good, "bereikbaar", "Entra."),
            });

        public Task<string> InstallAsync(string powerShell, string module, CancellationToken cancel)
        {
            Installed.Add(module);
            return Task.FromResult("Gelukt");
        }
    }

    /// <summary>Fake connections: remember what was run and always report success.</summary>
    private sealed class FakeFactory : IConnectionFactory
    {
        public List<Change> Executed { get; } = new();
        public string? GraphId { get; set; }

        /// <summary>What an AD user looks like when read again (e.g. disabled after offboarding).</summary>
        public Func<AdUser, AdUser?> AdReadAfter { get; set; } = u => u;

        public List<string> WasRead { get; } = new();

        public ISystemCheck? Check { get; set; }

        /// <summary>Reading again fails (e.g. the connection drops after the change).</summary>
        public bool ReadFailed { get; set; }

        /// <summary>How long each full read takes (AD, Entra, rooms, mailboxes), to test loading in parallel.</summary>
        public TimeSpan Delay { get; set; }

        /// <summary>The app roles in the sign-in (profiles, 0.8); null = not signed in.</summary>
        public IReadOnlyList<string>? AppRoles { get; set; }
        public bool AdReadFailed { get; set; }

        // AD, Entra and Exchange read at the same time: update the list of read objects under a lock
        public void Read(string key)
        {
            lock (WasRead)
            {
                WasRead.Add(key);
            }
        }

        public void ReadAll(IEnumerable<string> keys)
        {
            lock (WasRead)
            {
                WasRead.AddRange(keys);
            }
        }

        /// <summary>The previous read the app passed along (quick load), per time.</summary>
        public List<AdBase?> AdBases { get; } = new();
        public List<GraphBase?> GraphBases { get; } = new();

        public SecurityData? Security { get; set; }

        /// <summary>The calendar of a room (#20); with CalendarNoPermission reading fails as with a 403.</summary>
        public List<Appointment> Calendar { get; } = new();
        public bool CalendarNoPermission { get; set; }
        public List<(string Address, DateTime Of, DateTime Until)> CalendarRead { get; } = new();
        public List<SignInEvent> SignIns { get; } = new();
        public CaData? Ca { get; set; }
        public FakeScripts FakeScripts { get; } = new();

        public Core.Scripts.IScriptExecutor? Scripts(ConnectionSettings settings, string? adPassword) => FakeScripts;

        public IAdConnection? Ad(ConnectionSettings settings, string? password)
            => settings.AdFilled ? new FakeAd(this) : null;

        public IGraphConnection? Graph(ConnectionSettings settings)
            => settings.GraphFilled ? new FakeGraphConnection(this) : null;

        public IExchangeConnection? Exchange(ConnectionSettings settings) => new FakeExchange(this);

        public IReadOnlyList<IExecutor> Executors(ConnectionSettings settings, string? adPassword)
            => new IExecutor[] { new FakeAd(this), new FakeGraphConnection(this), new FakeExchange(this) };

        public Task<IReadOnlyList<ChangeResult>> Run(IReadOnlyList<Change> changes)
        {
            Executed.AddRange(changes);
            return Task.FromResult<IReadOnlyList<ChangeResult>>(changes.Select(w => ChangeResult.Ok(w)).ToList());
        }
    }

    private sealed class FakeAd(FakeFactory f) : IAdConnection, IExecutor
    {
        public TargetSystem TargetSystem => TargetSystem.ActiveDirectory;

        public Task<AdData> ReadAsync(IProgress<string>? progress, CancellationToken cancel) => ReadAsync(null, progress, cancel);

        public async Task<AdData> ReadAsync(AdBase? baseline, IProgress<string>? progress, CancellationToken cancel)
        {
            f.AdBases.Add(baseline);
            await Task.Delay(f.Delay, cancel);
            if (f.AdReadFailed)
            {
                throw new InvalidOperationException("De domeincontroller antwoordt niet.");
            }

            var data = TestData.AppEnvironment();
            progress?.Report("AD gelezen");
            return (new AdData(data.AdUsers, data.AdGroupsByDn.Values.ToList(), "DC=test,DC=internal",
                new AdSnapshot("CN=NTDS Settings,CN=DC1", 1000 + f.AdBases.Count), Quick: baseline is not null));
        }

        public Task<AdUser?> ReadUserAsync(string samAccountName, CancellationToken cancel)
        {
            f.Read("ad:" + samAccountName);
            var u = TestData.AppEnvironment().AdUsers.FirstOrDefault(x => x.SamAccountName == samAccountName);
            return Task.FromResult(u is null ? null : f.AdReadAfter(u));
        }

        public Task<AdGroup?> ReadGroupAsync(string samAccountName, CancellationToken cancel)
        {
            f.Read("adgroep:" + samAccountName);
            return Task.FromResult(TestData.AppEnvironment().AdGroupsByDn.Values.FirstOrDefault(g => g.SamAccountName == samAccountName));
        }

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel) => f.Run(changes);
    }

    private sealed class FakeGraphConnection(FakeFactory f) : IGraphConnection, IExecutor
    {
        public TargetSystem TargetSystem => TargetSystem.Graph;

        public string? SignedInAs => "beheerder@test.be";

        public IReadOnlyList<string>? AppRoles => f.AppRoles;

        public Task<GraphData> ReadAsync(IProgress<string>? progress, CancellationToken cancel) => ReadAsync(null, progress, cancel);

        public async Task<GraphData> ReadAsync(GraphBase? baseline, IProgress<string>? progress, CancellationToken cancel)
        {
            f.GraphBases.Add(baseline);
            await Task.Delay(f.Delay, cancel);
            var data = TestData.AppEnvironment();
            return (new GraphData(data.EntraUsers, data.CloudGroups.Values.ToList(),
                data.CloudMembership.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value), StringComparer.OrdinalIgnoreCase), data.Licences,
                $"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=t{f.GraphBases.Count}", Quick: baseline is not null));
        }

        public Task<SecurityData?> ReadSecurityAsync(IProgress<string>? progress, CancellationToken cancel) => Task.FromResult(f.Security);

        public Task<IReadOnlyList<SignInEvent>> ReadSignInsAsync(string? userId, int days, bool onlyFailed, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<SignInEvent>>(f.SignIns.Where(a => (userId is null || a.UserId == userId) && (!onlyFailed || !a.Succeeded)).ToList());

        public Task<IReadOnlyList<SignInEvent>> ReadSignInsAsync(int days, int max, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<SignInEvent>>(f.SignIns.Take(max).ToList());

        public Task<CaData?> ReadConditionalAccessAsync(IProgress<string>? progress, CancellationToken cancel) => Task.FromResult(f.Ca);

        public Task<IReadOnlyList<Appointment>> ReadCalendarAsync(string address, DateTime start, DateTime until, CancellationToken cancel)
        {
            f.CalendarRead.Add((address, start, until));
            return f.CalendarNoPermission
                ? throw new NoPermissionError("Access is denied. Check credentials and try again.")
                : Task.FromResult<IReadOnlyList<Appointment>>(f.Calendar.Where(a => a.Begin < until && a.End > start).ToList());
        }

        public Task<string?> FindUserIdAsync(string upn, CancellationToken cancel) => Task.FromResult(f.GraphId);

        public Task<GraphUserReading?> ReadUserAsync(string idOrUpn, CancellationToken cancel)
        {
            f.Read("entra:" + idOrUpn);
            var d = TestData.AppEnvironment();
            var e = d.EntraUsers.FirstOrDefault(x => x.Id == idOrUpn || x.UserPrincipalName == idOrUpn);
            return Task.FromResult(e is null ? null : new GraphUserReading(e, d.CloudMembership.GetValueOrDefault(e.Id)?.ToList() ?? new List<string>()));
        }

        public Task<GraphGroupReading?> ReadGroupAsync(string id, CancellationToken cancel)
        {
            if (f.ReadFailed)
            {
                throw new HttpRequestException("Graph is niet bereikbaar.");
            }

            f.Read("cloud:" + id);
            var d = TestData.AppEnvironment();
            return Task.FromResult(d.CloudGroups.TryGetValue(id, out var g)
                ? new GraphGroupReading(g, d.CloudMembership.Where(kv => kv.Value.Contains(id)).Select(kv => kv.Key).ToList())
                : null);
        }

        public Task<IReadOnlyList<GraphOwner>> ReadOwnersAsync(string groupId, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<GraphOwner>>(new[] { new GraphOwner("e-bo", "bo janssens", "bo.janssens@test.be") });

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel) => f.Run(changes);
    }

    private sealed class FakeExchange(FakeFactory f) : IExchangeConnection, IExecutor
    {
        public TargetSystem TargetSystem => TargetSystem.Exchange;

        public async Task<IReadOnlyList<Room>> ReadRoomsAsync(IProgress<string>? progress, CancellationToken cancel)
        {
            await Task.Delay(f.Delay, cancel);
            return TestData.AppEnvironment().Rooms;
        }

        public async Task<ExchangeData> ReadMailboxesAsync(IProgress<string>? progress, CancellationToken cancel)
        {
            await Task.Delay(f.Delay, cancel);
            var d = TestData.AppEnvironment();
            return (new ExchangeData(d.ExoGroups, d.Mailboxes.Take(1).ToList(), d.MailboxPermissions.Take(1).ToList()));
        }

        public Task<ExchangeObjects> ReadObjectsAsync(IReadOnlyList<string> addresses, CancellationToken cancel)
        {
            f.ReadAll(addresses.Select(a => "exo:" + a));
            var d = TestData.AppEnvironment();
            var set = addresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mailboxes = d.Mailboxes.Where(p => set.Contains(p.Smtp)).ToList();
            var groups = d.ExoGroups.Where(g => set.Contains(g.Smtp)).ToList();
            var rooms = d.Rooms.Where(r => set.Contains(r.Smtp)).ToList();
            var found = mailboxes.Select(p => p.Smtp).Concat(groups.Select(g => g.Smtp)).Concat(rooms.Select(r => r.Smtp)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ExchangeObjects(rooms,
                new ExchangeData(groups, mailboxes, d.MailboxPermissions.Where(r => set.Contains(r.Mailbox)).ToList()),
                addresses.Where(a => !found.Contains(a)).ToList()));
        }

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel) => f.Run(changes);
    }
}
