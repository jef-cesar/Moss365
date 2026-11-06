using Bunit;
using Moss365.Core.Inventory;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for 0.8: Entra ID, profiles, editing, new group, tasks for a selection.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void EachObjectShowsItsEntraId()
    {
        var person = State.Data.SearchPerson("ad:cas.vos")!;
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        Assert.Contains($"<code>{person.Entra!.Id}</code>", cut.Find(".kb-entra-id").InnerHtml);

        // A group that exists only in AD has no Entra ID
        var adGroup = State.Data.Index.Groups.First(g => g.Cloud is null);
        Assert.Null(State.Data.Index.EntraIdOf(adGroup.Ref));
        var cloud = State.Data.Index.Groups.First(g => g.Cloud is not null);
        Assert.Equal(cloud.Cloud!.Id, State.Data.Index.EntraIdOf(cloud.Ref));

        // In the list as a column you turn on yourself
        var list = RenderComponent<Users>();
        list.FindAll("button").First(b => b.TextContent == "Kolommen").Click();
        list.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("Entra-ID")).QuerySelector("input")!.Change(true);
        Assert.Contains(person.Entra.Id, list.Markup);
    }

    [Fact]
    public void AReaderSeesEverythingButChangesNothing()
    {
        State.Config.ProfilesRequired = true;
        _factory.AppRoles = new[] { "Moss365.Lezer" };

        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        Assert.DoesNotContain("Wachtwoord opnieuw instellen", cut.Markup);
        Assert.DoesNotContain(">uitstroom<", cut.Markup);

        var list = RenderComponent<Users>();
        Assert.True(list.FindAll("button").First(b => b.TextContent == "Exporteren").HasAttribute("hidden"));

        // A plan that still reaches the screen: nothing to tick, with the reason
        var plan = new List<Moss365.Core.Changes.Change>
        {
            new() { TargetSystem = Moss365.Core.Changes.TargetSystem.ActiveDirectory, ChangeAction = Moss365.Core.Changes.ChangeAction.AdDisableAccount, Target = "CN=x", Description = "uitschakelen" },
        };
        var view = RenderComponent<Moss365.UI.Components.PlanView>(p => p.Add(x => x.Plan, plan).Add(x => x.Context, "test"));
        Assert.Contains("Niet voor jouw profiel", view.Markup);
        Assert.False(plan[0].Selected);

        // Organisation: no access; menu without Actions
        var layout = RenderWithLayout<Organisation>("organisatie");
        Assert.Contains("Geen toegang", layout.Markup);
        Assert.Contains("Profiel: Reader", layout.Markup);
        Assert.DoesNotContain(layout.FindAll("nav.kb-nav a"), a => a.TextContent.Trim().StartsWith("Uitstroom", StringComparison.Ordinal));
    }

    [Fact]
    public void HelpdeskSeesOnlyOwnChangesInTheChangeLog()
    {
        State.ChangeLog.Write(new Moss365.Core.Changes.LogEntry(DateTimeOffset.Now, "beheerder@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "eigen", "o", new Dictionary<string, string>(), "Gelukt", ""));
        State.ChangeLog.Write(new Moss365.Core.Changes.LogEntry(DateTimeOffset.Now, "collega@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "andermans", "o", new Dictionary<string, string>(), "Gelukt", ""));
        _factory.AppRoles = new[] { "Moss365.Helpdesk" };

        var cut = RenderComponent<ChangeLog>();
        Assert.Contains("eigen", cut.Markup);
        Assert.DoesNotContain("andermans", cut.Markup);
        Assert.DoesNotContain("Gekozen wijzigingen ongedaan maken", cut.Markup);

        _factory.AppRoles = new[] { "Moss365.Auditor" };
        Assert.Contains("andermans", RenderComponent<ChangeLog>().Markup);
    }

    [Fact]
    public void WithoutProfilesEveryoneMayDoEverythingWithAMessage()
    {
        var layout = RenderWithLayout<Users>("gebruikers");
        Assert.Contains("Profielen nog niet ingesteld", layout.Markup);
        Assert.True(State.Can(Moss365.Core.Permissions.Permission.Manage));
    }

    [Fact]
    public void EditTheDataOfAUser()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        cut.FindAll("button").First(b => b.TextContent == "Bewerken").Click();
        cut.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Functie", StringComparison.Ordinal)).QuerySelector("input")!.Change("Maatschappelijk werker");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Functie: Maatschappelijk werker", cut.Find("section.kb-plan").TextContent);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == Moss365.Core.Changes.ChangeAction.AdSetAttributes && w.P("title") == "Maatschappelijk werker"));
    }

    [Fact]
    public void CreateNewGroupFromTheList()
    {
        State.Config.GroupsOu = "GEMEENTEBESTUUR/GROEPEN";
        Assert.Contains("groepen/nieuw", RenderComponent<Groups>().Markup);

        var cut = RenderComponent<NewGroup>();
        cut.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith("Naam", StringComparison.Ordinal)).QuerySelector("input")!.Change("gg_scherm_test");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("AD-beveiligingsgroep gg_scherm_test aanmaken", cut.Find("section.kb-plan").TextContent);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == Moss365.Core.Changes.ChangeAction.AdCreateGroup));
    }

    [Fact]
    public void TasksForTheSelectedUsersAndAnExtraConfirmationForALargePlan()
    {
        var cut = RenderComponent<Users>();
        Assert.DoesNotContain("Met de", cut.Markup);
        cut.FindAll("tbody td.kb-kies input")[0].Click();
        cut.FindAll("tbody td.kb-kies input")[1].Click();
        Assert.Contains("Met de 2 gekozen gebruikers", cut.Markup);

        cut.Find(".kb-bulk input[list]").Change("bibliotheek");
        cut.FindAll(".kb-bulk button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Kantoor wordt bibliotheek", cut.Markup);

        // A large plan (more than 50 changes) asks for an extra tick
        var plan = Enumerable.Range(0, 51).Select(i => new Moss365.Core.Changes.Change
        {
            TargetSystem = Moss365.Core.Changes.TargetSystem.ActiveDirectory, ChangeAction = Moss365.Core.Changes.ChangeAction.AdUnlockAccount,
            Target = $"CN=x{i}", Description = "ontgrendelen",
        }).ToList();
        var view = RenderComponent<Moss365.UI.Components.PlanView>(p => p.Add(x => x.Plan, plan).Add(x => x.Context, "groot"));
        view.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        Assert.True(view.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).HasAttribute("disabled"));
        view.Find(".kb-groot-plan input").Change(true);
        Assert.False(view.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).HasAttribute("disabled"));
    }

    // ---------------------------------------------------------------- logs, retention, automatic refresh (#17)

    private string TechnicalLog()
    {
        // The technical log is in the tenant database (table TechnicalLog)
        var text = new System.Text.StringBuilder();
        foreach (var (time, level, message) in State.Database.ReadTechnicalLog(10000))
        {
            text.Append(time).Append(' ').Append(level).Append(' ').AppendLine(message);
        }

        return text.ToString();
    }

    [Fact]
    public void TheTechnicalLogContainsNoSecrets()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        cut.FindAll("button").First(b => b.TextContent == "Wachtwoord opnieuw instellen").Click();
        var password = cut.Find(".kb-kopieer input").GetAttribute("value")!;
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        State.AdPassword = "AD-Geheim-987!";
        WaitOnTask(); // refreshing the object after the plan still runs as a background task
        Assert.True(State.StartLoading(Moss365.UI.Services.LoadKind.AdAndEntra));
        WaitOnTask();

        var log = TechnicalLog();
        Assert.Contains("gestart", log);
        Assert.Contains("Plan uitgevoerd", log);
        Assert.Contains("Live laden (AD en Entra) klaar", log);
        Assert.DoesNotContain(password, log);
        Assert.DoesNotContain("AD-Geheim-987!", log);
        // No names or accounts, only what happened
        Assert.DoesNotContain("janssens", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportsAndSignInsGoToTheAccessLog()
    {
        RenderComponent<Users>().FindAll("button").First(b => b.TextContent == "Exporteren").Click();
        var export = Assert.Single(State.Database.ReadAccess(10));
        Assert.Equal("Export", export.What);
        Assert.StartsWith("gebruikers: ", export.Details);

        var security = RenderComponent<Security>();
        Assert.Contains(State.Database.ReadAccess(10), r => r.What == "Beveiliging bekeken");

        var changeLog = RenderComponent<ChangeLog>();
        Assert.Contains("Toegangslog", changeLog.Markup);
        Assert.Contains("Beveiliging bekeken", changeLog.Markup);
    }

    [Fact]
    public void OldChangeLogLinesArchiveFirstThenRemove()
    {
        State.ChangeLog.Write(new Moss365.Core.Changes.LogEntry(DateTimeOffset.Now.AddYears(-3), "oud@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "heel oud", "o", new Dictionary<string, string>(), "Gelukt", ""));
        State.ChangeLog.Write(new Moss365.Core.Changes.LogEntry(DateTimeOffset.Now, "nu@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "recent", "o", new Dictionary<string, string>(), "Gelukt", ""));

        var cut = RenderComponent<ChangeLog>();
        Assert.Contains("<strong>1</strong> regels ouder dan", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Uit de databank halen"); // archive first
        cut.FindAll("button").First(b => b.TextContent == "Archiveren naar CSV").Click();
        var csv = Assert.Single(Directory.GetFiles(State.ExportFolder, "Logboek-archief-*.csv"));
        Assert.Contains("heel oud", File.ReadAllText(csv));
        Assert.DoesNotContain("recent", File.ReadAllText(csv));

        cut.FindAll("button").First(b => b.TextContent == "Uit de databank halen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, weghalen").Click();
        Assert.Equal(1, State.Database.CountLog());
        Assert.Equal("recent", State.ChangeLog.Recent(5).Single().TargetName);
    }

    [Fact]
    public async Task AutoRefreshOnlyWhenAllowedAndWithoutSignInLink()
    {
        var now = DateTime.Now;
        Assert.False(await State.RefreshAutoAsync(now)); // is off

        State.Settings.RefreshAuto = true;
        Assert.False(await State.RefreshAutoAsync(now)); // not loaded live yet

        Assert.True(State.StartLoading(Moss365.UI.Services.LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.False(await State.RefreshAutoAsync(DateTime.Now.AddMinutes(5))); // too recently
        Assert.True(await State.RefreshAutoAsync(DateTime.Now.AddMinutes(16)));
        WaitOnTask();
        Assert.Equal(2, _factory.AdBases.Count);
    }
}
