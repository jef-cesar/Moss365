using Bunit;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for 0.5: export, helpdesk, undo, scheduling, cleanup, security and quick load.</summary>
public sealed partial class PlanScreenTests
{
    private string ExportFolder => State.ExportFolder;

    [Fact]
    public void ExportListToCsv()
    {
        var cut = RenderComponent<Users>();
        cut.FindAll("button").First(b => b.TextContent == "Exporteren").Click();

        var file = Assert.Single(Directory.GetFiles(ExportFolder, "Gebruikers-*.csv"));
        var rules = File.ReadAllLines(file);
        Assert.Equal("\"Naam\";\"UPN\";\"Bron\";\"OU\";\"Dienst\";\"Afdeling\";\"Status\"", rules[0].TrimStart('﻿'));
        Assert.Equal(7, rules.Length); // header + the 6 active users
        Assert.Contains("Tonen in Verkenner", cut.Markup);
    }

    [Fact]
    public void ResetPasswordWithoutLoggingIt()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        cut.FindAll("button").First(b => b.TextContent == "Wachtwoord opnieuw instellen").Click();
        var password = cut.Find(".kb-kopieer input").GetAttribute("value")!;
        Assert.Equal(16, password.Length);

        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Nieuw wachtwoord instellen", cut.Find("section.kb-plan").TextContent);
        Assert.Contains("Account ontgrendelen", cut.Find("section.kb-plan").TextContent);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        var configure = Assert.Single(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetPassword);
        Assert.Equal("True", configure.P("wijzigenBijAanmelding"));
        Assert.DoesNotContain(configure.Parameters.Values, v => v == password);

        // The password is ready to pass on, but not in the change log
        Assert.Contains($"Nieuw wachtwoord voor", cut.Markup);
        Assert.Equal(password, cut.Find(".kb-geheim .kb-kopieer input").GetAttribute("value"));
        var log = DatabaseOnDisk();
        Assert.Contains(nameof(ChangeAction.AdSetPassword), log);
        Assert.DoesNotContain(password, log);
    }

    [Fact]
    public void UndoChangeFromTheChangeLog()
    {
        var groupValue = RenderComponent<GroupDetail>(p => p.Add(x => x.Key, "cloud:g-team"));
        Choose(groupValue, "Lid toevoegen", "cas", "cas vos");
        Confirm(groupValue);
        groupValue.WaitForAssertion(() => Assert.Contains("gelukt,", groupValue.Markup));
        WaitOnTask();
        _factory.Executed.Clear();

        var cut = RenderComponent<ChangeLog>();

        // The background refresh may just redraw the page: tick until the button counts it
        cut.WaitForAssertion(() =>
        {
            var box = cut.Find("tbody td.kb-kies input:not([disabled])");
            if (!box.HasAttribute("checked"))
            {
                box.Click();
            }

            Assert.Contains(cut.FindAll("button"), b => b.TextContent.StartsWith("Gekozen wijzigingen ongedaan maken (1)", StringComparison.Ordinal));
        });
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Gekozen wijzigingen ongedaan maken (1)", StringComparison.Ordinal)).Click();
        Assert.Contains("Terug uit DNST_CULTUUR halen", cut.Find("section.kb-plan").TextContent);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        var back = Assert.Single(_factory.Executed);
        Assert.Equal(ChangeAction.GraphRemoveGroupMember, back.ChangeAction);
        Assert.Equal("g-team", back.P("groepId"));
        Assert.Contains(State.ChangeLog.Recent(10), r => r.Context.StartsWith("Ongedaan maken:", StringComparison.Ordinal));
    }

    [Fact]
    public void ScheduleOffboardingAndRunLater()
    {
        var output = RenderComponent<Offboarding>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        output.FindAll("button").First(b => b.TextContent == "Inplannen").Click();
        Assert.Contains("Ingepland op", output.Markup);
        var scheduled = Assert.Single(State.Scheduled.Read());
        Assert.Equal(ScheduledKind.Offboarding, scheduled.Kind);
        Assert.Empty(_factory.Executed);

        // The moment has passed: the menu says so, the page shows the plan only on request
        scheduled.When = DateTimeOffset.Now.AddMinutes(-5);
        State.Scheduled.Save(new[] { scheduled });
        var layout = RenderComponent<Moss365.UI.Layout.MainLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<p>x</p>")));
        Assert.Contains("1 geplande actie(s) klaar om uit te voeren", layout.Markup);

        var cut = RenderComponent<Scheduled>();
        Assert.Contains("klaar om uit te voeren", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Plan tonen").Click();
        Assert.Contains("AD-account uitschakelen", cut.Find("section.kb-plan").TextContent);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("Uitgevoerd en uit Gepland gehaald", cut.Markup));
        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdDisableAccount);
        Assert.Empty(State.Scheduled.Read());
    }

    [Fact]
    public void ScheduledOnboardingCreatesThePasswordOnlyWhenRun()
    {
        var proposal = PlanBuilder.NewUser(State.Data, State.Config, new NewUserRequest
        {
            Population = "Vrijwilliger", GivenName = "Piet", Surname = "Goossens",
        });
        Assert.True(proposal.CanBeExecuted, string.Join("; ", proposal.Errors));
        State.Schedule(ScheduledPlans.Onboarding(proposal, DateTimeOffset.Now.AddMinutes(-1), "test"));
        var saved = State.Database.ReadDocument("gepland")!;
        Assert.DoesNotContain(proposal.Password, DatabaseOnDisk());
        Assert.DoesNotContain(proposal.Password, saved);

        var cut = RenderComponent<Scheduled>();
        Assert.True(cut.FindAll("button").Any(b => b.TextContent == "Plan tonen"), cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Plan tonen").Click();
        Assert.True(cut.FindAll("button").Any(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)), cut.Markup);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("Startwachtwoord", cut.Markup));
        var create = Assert.Single(_factory.Executed, w => w.ChangeAction == ChangeAction.AdCreateUser);
        Assert.Equal("piet.goossens", create.P("sAMAccountName"));
        Assert.NotEqual(proposal.Password, cut.Find(".kb-geheim .kb-kopieer input").GetAttribute("value"));
    }

    [Fact]
    public void CleanupRoundWithReport()
    {
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("opkuis?dagen=1");
        var cut = RenderComponent<Cleanup>();
        Assert.Contains("cas vos", cut.Markup);
        Assert.DoesNotContain("oud account", cut.Markup); // already disabled

        var row = cut.FindAll("tbody tr").First(r => r.TextContent.Contains("cas vos"));
        row.QuerySelector("td.kb-kies input")!.Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Plan: uitschakelen (1)", StringComparison.Ordinal)).Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("Rapport bewaard", cut.Markup));

        Assert.Equal(new[] { ChangeAction.AdDisableAccount, ChangeAction.AdSetAttributes }, _factory.Executed.Select(w => w.ChangeAction));
        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(ExportFolder, "Opkuisronde-Staff-*.csv")));
        Assert.Contains("\"cas vos\"", report);
        Assert.Contains("\"uitschakelen\";\"gelukt\"", report);
        Assert.Contains("\"behouden\"", report);
    }

    [Fact]
    public void SecurityWithMfaRolesAndSignIns()
    {
        _factory.Security = new SecurityData(
            new[]
            {
                new MfaRegistration("e-an", "an.peeters@test.be", true, true, new[] { "microsoftAuthenticatorPush" }, "microsoftAuthenticatorPush", true),
                new MfaRegistration("e-bo", "bo.janssens@test.be", false, false, new[] { "email" }, "", true),
            },
            new[] { new RoleMember("Global Administrator", "e-bo", "BO JANSSENS", "bo.janssens@test.be", "user") });
        _factory.SignIns.Add(new SignInEvent(DateTimeOffset.Now.AddHours(-1), "e-bo", "bo.janssens@test.be", "BO JANSSENS", "Office 365",
            "203.0.113.5", "Gent, BE", 50126, "Invalid username or password", "Browser"));

        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.All(State.Job!.Steps, s => Assert.Equal(StepStatus.Done, s.Status));

        var cut = RenderComponent<Security>();
        Assert.Contains("actieve medewerkers zonder MFA (1)", cut.Markup.Replace("Actieve", "actieve"));
        Assert.Contains("Global Administrator", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Live laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("Invalid username or password", cut.Markup));

        var detail = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        Assert.Contains("geen MFA", detail.Markup);
        Assert.Contains("Global Administrator", detail.Markup);

        var stat = RenderComponent<Statistics>();
        Assert.Contains("Global Administrators", stat.Markup);
        Assert.Contains("beheerders zonder MFA", stat.Markup);

        // Saved in the live inventory and read back
        var again = InventoryLoader.Load(State.Files, new[] { State.SavedInventory! });
        Assert.Equal(2, again.Security!.Mfa.Count);
        Assert.Equal("Global Administrator", Assert.Single(again.Security.Roles).Role);
    }

    [Fact]
    public void QuickLoadAfterAFullReadAlsoAfterRestart()
    {
        Assert.False(State.AdCanQuick);
        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.True(State.AdCanQuick);
        Assert.True(State.EntraCanQuick);

        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.Null(_factory.AdBases[0]);
        Assert.Equal(1001, _factory.AdBases[1]!.Snapshot.HighestUsn);
        Assert.Equal("https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=t1", _factory.GraphBases[1]!.DeltaLink);
        Assert.Contains(State.Data.Sources, b => b.Folder?.Contains("enkel wijzigingen", StringComparison.Ordinal) == true);

        // After a restart: the inventory saved live carries the starting point
        var restart = new ManagementState(Services.GetRequiredService<IAppEnvironment>(), _factory);
        Assert.True(restart.AdCanQuick);
        Assert.True(restart.EntraCanQuick);

        // Another (older) inventory does not match that starting point
        restart.ChooseFolder(restart.Folders.Single(m => m.EndsWith("20261016-1343", StringComparison.Ordinal)));
        Assert.False(restart.AdCanQuick);
        Assert.False(restart.EntraCanQuick);

        // Turned off: always read everything
        State.Settings.QuickLoad = false;
        Assert.False(State.AdCanQuick);
    }

    [Fact]
    public void CheckShowsTheVersion()
    {
        var cut = RenderComponent<ThisComputer>();
        Assert.Contains("Over Moss365", cut.Markup);
        Assert.Contains($"Versie <strong>{ManagementState.AppVersion}</strong>", cut.Markup);
        Assert.Contains("github.com/jef-cesar/Moss365/releases", cut.Markup);
    }

    [Fact]
    public void BrokenOwnOrganisationGivesAVisibleWarning()
    {
        State.Database.SaveDocument(Moss365.Core.Storage.Documents.Organisation, "{ kapot");
        State.LoadConfig();
        State.LoadInventory(); // clears Error, but not the warning about the organisation

        Assert.NotEmpty(State.Config.Populations);
        var layout = RenderComponent<Moss365.UI.Layout.MainLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<p>x</p>")));
        Assert.Contains("is niet leesbaar", layout.Markup);
        Assert.Contains("Gemeente", RenderComponent<NewUser>().Markup);
    }

    private void WaitOnTask()
    {
        var until = DateTime.Now.AddSeconds(10);
        while (State.TaskBusy && DateTime.Now < until)
        {
            Thread.Sleep(20);
        }

        Assert.False(State.TaskBusy);

        // The task reports its end to the screens just after it stops being busy: let that notification pass
        Thread.Sleep(100);
    }

    /// <summary>
    /// After a background task: let the screen finish the re-render it was told to do (the notification goes through the
    /// renderer's queue), so that the next click lands on the screen with the new data.
    /// </summary>
    private void Settle(IRenderedFragment cut)
    {
        WaitOnTask();
        Thread.Sleep(100);
        cut.InvokeAsync(() => { }).GetAwaiter().GetResult();
    }
}
