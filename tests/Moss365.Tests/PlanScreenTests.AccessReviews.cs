using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Reporting;
using Moss365.Core.Security;
using Moss365.UI.Pages;
using CheckStatus = Moss365.Core.Security.CheckStatus;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R20: light access reviews; every decision in the change log, asking and removing as plans.</summary>
public sealed partial class PlanScreenTests
{
    private InventoryData WithRoles() => State.Data with
    {
        Security = new SecurityData(Array.Empty<MfaRegistration>(), new[]
        {
            new RoleMember("Global Administrator", "e-an", "an peeters", "an.peeters@test.be", "user"),
            new RoleMember("Global Administrator", "e-an", "an peeters", "an.peeters@test.be", "user"), // twice: once
            new RoleMember("Exchange Administrator", "e-bo", "bo janssens", "bo.janssens@test.be", "user"),
        }),
    };

    [Fact]
    public void WhatIsReviewedAndWhenItIsDue()
    {
        var subjects = AccessReviews.Subjects(WithRoles());

        var admins = Assert.Single(subjects, s => s.Kind == ReviewKind.AdGroup && s.Name == "Domain Admins");
        Assert.Contains(admins.Members, m => m.Detail == "an.peeters@test.be");
        Assert.DoesNotContain(subjects, s => s.Kind == ReviewKind.AdGroup && s.Name == "gg_wifiIntern"); // not privileged
        Assert.Single(Assert.Single(subjects, s => s.Kind == ReviewKind.AdminRole && s.Name == "Global Administrator").Members);
        var info = Assert.Single(subjects, s => s.Kind == ReviewKind.Mailbox && s.Key == "info@test.be");
        Assert.Equal("Info", info.Name);
        Assert.Equal(3, info.Members.Count);
        Assert.Equal(2, Assert.Single(subjects, s => s.Key == "boekhouding@test.be").Members.Count); // FullAccess and SendAs

        // Never reviewed: due now; reviewed a month ago every quarter: not yet; four months ago: overdue
        var now = DateTimeOffset.Now;
        var settings = new[]
        {
            new ReviewSetting(info.Id, "bo.janssens@test.be", 3, now.AddMonths(-1), "beheerder@test.be"),
            new ReviewSetting(admins.Id, "an.peeters@test.be", 3, now.AddMonths(-4), "beheerder@test.be"),
        };
        var due = AccessReviews.Due(subjects, settings, now);
        Assert.False(due.Single(d => d.Subject.Id == info.Id).Overdue);
        Assert.True(due.Single(d => d.Subject.Id == admins.Id).Overdue);
        Assert.All(due.Where(d => d.Subject.Kind == ReviewKind.AdminRole), d => Assert.True(d.Overdue && d.Due is null));

        // An interval that is not offered becomes the default
        Assert.Equal(AccessReviews.DefaultMonths, Assert.Single(AccessReviews.With(settings, new ReviewSetting(info.Id, " x@test.be ", 7)), s => s.Subject == info.Id).Months);
        Assert.Equal("x@test.be", AccessReviews.With(settings, new ReviewSetting(info.Id, " x@test.be ", 12)).Single(s => s.Subject == info.Id).Reviewer);
    }

    [Fact]
    public void DecisionsGoToTheChangeLogAndRemovingIsAPlan()
    {
        var subjects = AccessReviews.Subjects(WithRoles());
        var admins = subjects.Single(s => s.Kind == ReviewKind.AdGroup && s.Name == "Domain Admins");
        var an = admins.Members.Single(m => m.Detail == "an.peeters@test.be");
        var decisions = admins.Members.Select(m => new ReviewDecision(m.Key, m.Key != an.Key)).ToList();

        var lines = AccessReviews.LogLines(admins, decisions, "beheerder@test.be", "an.peeters@test.be", DateTimeOffset.Now);
        Assert.Equal(admins.Members.Count, lines.Count);
        var line = lines.Single(l => l.Parameters["lid"] == an.Key);
        Assert.Equal(("Review", "remove", "AdGroup"), (line.ChangeAction, line.Parameters["besluit"], line.Parameters["soort"]));
        Assert.Contains("verliest toegang tot Domain Admins", line.Description, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => AccessReviews.LogLines(admins, Array.Empty<ReviewDecision>(), "x", "y", DateTimeOffset.Now));

        var removal = Assert.Single(AccessReviews.Removals(admins, decisions).Plan);
        Assert.Equal((ChangeAction.AdRemoveGroupMember, an.Key, admins.Key), (removal.ChangeAction, removal.Target, removal.Parameters["groepDn"]));

        var info = subjects.Single(s => s.Key == "info@test.be");
        var sendAs = info.Members.Single(m => m.Detail == "SendAs");
        var mailbox = Assert.Single(AccessReviews.Removals(info, new[] { new ReviewDecision(sendAs.Key, false) }).Plan);
        Assert.Equal((ChangeAction.ExchangeRemoveMailboxPermission, "info@test.be", "SendAs", "bo.janssens@test.be"),
            (mailbox.ChangeAction, mailbox.Target, mailbox.Parameters["Recht"], mailbox.Parameters["Trustee"]));

        // Admin roles: Moss365 has no permission to change them; the way in Entra instead
        var role = subjects.Single(s => s.Kind == ReviewKind.AdminRole && s.Name == "Exchange Administrator");
        var roleOutcome = AccessReviews.Removals(role, new[] { new ReviewDecision("e-bo", false) });
        Assert.Empty(roleOutcome.Plan);
        Assert.Contains("Moss365 wijzigt geen beheerrollen", Assert.Single(roleOutcome.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void EntraGroupsWithARoleAndAppPermissionsAreReviewedButNeverChangedHere()
    {
        var data = State.Data with
        {
            Security = new SecurityData(Array.Empty<MfaRegistration>(), new[] { new RoleMember("Security Administrator", "g-eigen", "WKG_Eigen", string.Empty, "group") }),
        };
        var checks = new Dictionary<string, SecurityCheck>(StringComparer.OrdinalIgnoreCase)
        {
            ["app-rechten"] = new SecurityCheck("app-rechten", "App-rechten", CheckStatus.Problem, "x", new[]
            {
                new CheckRow("Oude app", "Mail.ReadWrite zonder gebruiker", CheckStatus.Problem),
                new CheckRow("Oude app", "Files.Read.All", CheckStatus.Attention),
                new CheckRow("Goede app", "User.Read", CheckStatus.Ok),
            }),
        };

        var subjects = AccessReviews.Subjects(data, checks);

        var group = Assert.Single(subjects, s => s.Kind == ReviewKind.EntraGroup);
        Assert.Equal("WKG_Eigen (Security Administrator)", group.Name);
        Assert.Equal("e-bo", Assert.Single(group.Members).Key);
        Assert.DoesNotContain(subjects, s => s.Kind == ReviewKind.AdminRole); // the group is the subject, not a role with a group in it
        var app = Assert.Single(subjects, s => s.Kind == ReviewKind.AppPermission);
        Assert.Equal(("Oude app", 2), (app.Name, app.Members.Count));

        // Removing is the way in Entra, never a plan: Moss365 has no permission to change roles or apps
        var groupOutcome = AccessReviews.Removals(group, new[] { new ReviewDecision("e-bo", false) });
        Assert.Empty(groupOutcome.Plan);
        Assert.Contains("Moss365 wijzigt geen beheerrollen", Assert.Single(groupOutcome.Errors), StringComparison.Ordinal);
        var appOutcome = AccessReviews.Removals(app, app.Members.Select(m => new ReviewDecision(m.Key, false)).ToList());
        Assert.Empty(appOutcome.Plan);
        Assert.Equal(2, appOutcome.Errors.Count);
    }

    [Fact]
    public void TheReviewsAreInThePeriodReportAndWithTheStandards()
    {
        var subject = AccessReviews.Subjects(State.Data).Single(s => s.Name == "Domain Admins");
        var an = subject.Members.Single(m => m.Detail == "an.peeters@test.be");
        State.FinishReview(subject, subject.Members.Select(m => new ReviewDecision(m.Key, m.Key != an.Key)).ToList());

        var today = DateOnly.FromDateTime(DateTime.Today);
        var report = State.BuildReport(new ReportPeriod(today.AddDays(-7), today, "week"));

        Assert.Equal((1, subject.Members.Count - 1, 1), (report.Reviews!.Reviewed, report.Reviews.Kept, report.Reviews.Removed));
        Assert.True(report.Reviews.Due > 0); // the mailboxes are still due
        Assert.Contains("gereviewd in deze periode", report.ToHtml(), StringComparison.Ordinal);
        Assert.Contains("Toegangsreviews", report.ToCsv(), StringComparison.Ordinal);
        foreach (var id in new[] { "21.2.i", "6" })
        {
            var row = report.Standards.Single(s => s.Control.Id == id);
            Assert.Contains(row.Results, r => r.Check == AccessReviews.CheckKey && r.Status == CheckStatus.Attention);
        }
    }

    [Fact]
    public void AskingTheReviewersIsAPlanOfMails()
    {
        var due = AccessReviews.Due(AccessReviews.Subjects(State.Data), new[] { new ReviewSetting(AccessReviews.SubjectId(ReviewKind.Mailbox, "info@test.be"), "bo.janssens@test.be") }, DateTimeOffset.Now);
        var outcome = AccessReviews.AskReviewers(due.Where(d => d.Subject.Kind == ReviewKind.Mailbox), AccessReviews.DefaultQuestion);

        var mail = Assert.Single(outcome.Plan);
        Assert.Equal((ChangeAction.GraphSendMail, "bo.janssens@test.be"), (mail.ChangeAction, mail.Target));
        Assert.Contains("Info:\n- ", mail.Parameters["tekst"], StringComparison.Ordinal);
        Assert.Contains("(SendAs)", mail.Parameters["tekst"], StringComparison.Ordinal);
        Assert.Contains(outcome.Errors, e => e.StartsWith("Boekhouding heeft geen beoordelaar", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReviewScreenRecordsDecisionsAndMakesThePlan()
    {
        var cut = RenderComponent<AccessReviewsPage>();
        var row = cut.FindAll("#reviews tbody tr").First(r => r.TextContent.Contains("Domain Admins", StringComparison.Ordinal));
        Assert.Contains("te doen", row.TextContent, StringComparison.Ordinal);
        row.QuerySelector("input[aria-label='Beoordelaar van Domain Admins']")!.Change("an.peeters@test.be");
        cut.FindAll("#reviews tbody tr").First(r => r.TextContent.Contains("Domain Admins", StringComparison.Ordinal))
            .QuerySelectorAll("button").First(b => b.TextContent == "Reviewen").Click();

        var subject = AccessReviews.Subjects(State.Data).Single(s => s.Name == "Domain Admins");
        var an = subject.Members.Single(m => m.Detail == "an.peeters@test.be");
        cut.Find($"#review input[aria-label='{an.Name} verwijderen']").Change(true);
        cut.FindAll("#review button").First(b => b.TextContent == "Review afronden").Click();

        Assert.Contains("Review afgerond", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Uit AD-groep Domain Admins halen", cut.Markup, StringComparison.Ordinal); // the plan, still to confirm
        Assert.Empty(_factory.Executed);
        var log = State.ChangeLog.Recent(20).Where(l => l.ChangeAction == AccessReviews.LogAction).ToList();
        Assert.Equal(subject.Members.Count, log.Count);
        Assert.Contains(log, l => l.Parameters["besluit"] == "remove" && l.Parameters["beoordelaar"] == "an.peeters@test.be");
        var setting = State.ReviewSettings().Single(s => s.Subject == subject.Id);
        Assert.NotNull(setting.Reviewed);
        Assert.False(State.AccessReviewsDue().Single(d => d.Subject.Id == subject.Id).Overdue);
        Assert.Contains("Toegangsreview", log[0].Context, StringComparison.Ordinal);
        Assert.NotNull(Undo.WhyNot(log[0])); // a decision is a record, not something to undo
    }

    [Fact]
    public void OnlyAnAdministratorRecordsAReview()
    {
        State.Config.ProfilesRequired = true;
        _factory.AppRoles = new[] { "Moss365.Auditor" };
        var cut = RenderComponent<AccessReviewsPage>();

        Assert.Contains("Domain Admins", cut.Markup, StringComparison.Ordinal); // the Auditor sees everything
        Assert.DoesNotContain(cut.FindAll("#reviews button"), b => b.TextContent == "Reviewen");
        var subject = AccessReviews.Subjects(State.Data).First();
        Assert.Throws<InvalidOperationException>(() => State.FinishReview(subject, subject.Members.Select(m => new ReviewDecision(m.Key, true)).ToList()));
        Assert.Equal(Moss365.Core.Permissions.Permission.Security, Access.ForPath("toegangsreviews"));
    }
}
