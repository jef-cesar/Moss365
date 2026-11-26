using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Inventory;
using Moss365.Core.Security;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.34 (R11): the security checks.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void ForwardingOutsideTheOwnDomainsIsFound()
    {
        var rules = new[]
        {
            new ForwardingRule("info@test.be", "postvak", "", "smtp:collega@test.be"),
            new ForwardingRule("a@test.be", "postvak", "", "privé@example.com"),
            new ForwardingRule("b@test.be", "regel", "Alles", "aanvaller@example.com"),
        };
        var c = SecurityChecks.Forwarding(rules, new[] { "test.be" });
        Assert.Equal(CheckStatus.Problem, c.Status);
        Assert.Equal(2, c.Rows.Count);
        Assert.Contains(c.Rows, r => r.Status == CheckStatus.Problem && r.Detail.Contains("inboxregel", StringComparison.Ordinal));
        Assert.Equal(CheckStatus.Ok, SecurityChecks.Forwarding(rules.Take(1).ToList(), new[] { "test.be" }).Status);
        Assert.Equal(CheckStatus.Unknown, SecurityChecks.Forwarding(null, new[] { "test.be" }).Status);
    }

    [Fact]
    public void AppSecretsAndBroadPermissions()
    {
        var now = DateTimeOffset.Now;
        var r = new AppsReading(
            new[] { new AppCredential("A", "1", "geheim", now.AddDays(-1)), new AppCredential("B", "2", "certificaat", now.AddDays(10)), new AppCredential("C", "3", "geheim", now.AddDays(200)) },
            new[] { new AppPermission("A", "1", "Directory.ReadWrite.All", "Application", "AllPrincipals", null), new AppPermission("D", "4", "User.Read", "Delegated", "Principal", "u1") });
        var credentials = SecurityChecks.AppCredentials(r, now);
        Assert.Equal(CheckStatus.Problem, credentials.Status);
        Assert.Equal(2, credentials.Rows.Count);
        var permissions = SecurityChecks.AppPermissions(r);
        Assert.Contains(permissions.Rows, x => x.Object == "A" && x.Status == CheckStatus.Attention);
        Assert.Contains(permissions.Rows, x => x.Object == "D" && x.Status == CheckStatus.Attention);
    }

    [Fact]
    public void TheLightAdCheckAndGroupPolicy()
    {
        var ad = SecurityChecks.AdSecurity(new AdSecurityReading(new[] { "svc" }, new[] { "srv1" }, Array.Empty<string>(), Array.Empty<string>(),
            DateTimeOffset.Now.AddDays(-30), Array.Empty<string>(), Array.Empty<(string, string)>()), DateTimeOffset.Now);
        Assert.Equal(CheckStatus.Problem, ad.Status);
        Assert.Contains(ad.Rows, r => r.Object == "krbtgt" && r.Status == CheckStatus.Ok);

        var gpo = SecurityChecks.Gpo(new[]
        {
            new GpoInfo("{1}", "Gekoppeld", false, false, 3, new[] { "OU=x" }, null),
            new GpoInfo("{2}", "Leeg en los", false, false, 0, Array.Empty<string>(), null),
            new GpoInfo("{3}", "Uit", true, true, 5, new[] { "OU=y" }, null),
        });
        Assert.Equal(3, gpo.Rows.Count);
        Assert.Equal(CheckStatus.Attention, gpo.Status);
    }

    [Fact]
    public void AlertsComeFromTheSnapshots()
    {
        var now = State.Data;
        var groups = now.AdGroupsByDn.Values.Select(g => g.Name == "Domain Admins" ? g with { Members = Array.Empty<string>(), MembersKnown = true } : g).ToList();
        var ad = now.AdUsers.Select(a => a.SamAccountName == "bo.janssens" ? a with { Enabled = false } : a).ToList();
        var before = now with
        {
            AdUsers = ad,
            AdGroupsByDn = groups.ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase),
            Persons = InventoryLoader.LinkPersons(ad, now.EntraUsers),
        };
        var alerts = SecurityChecks.Alerts(before, now, DateTimeOffset.Now.AddDays(-7));
        Assert.Contains(alerts.Rows, r => r.Object == "Domain Admins" && r.Detail.Contains("nieuw lid", StringComparison.Ordinal));
        Assert.Contains(alerts.Rows, r => r.Object == "bo.janssens");
        Assert.Equal(CheckStatus.Unknown, SecurityChecks.Alerts(null, now, null).Status);
    }

    [Fact]
    public void AdviceExplainsAcceptsARiskAndGivesAScore()
    {
        EntraFilled();
        Assert.All(State.SecurityCheckList(), c => Assert.NotNull(SecurityAdvice.Info(c.Key)));
        var cut = RenderComponent<SecurityChecksPage>();
        Assert.Contains("Nog niet berekend", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("button").First(b => b.TextContent == "Alles nakijken").Click();
        cut.WaitForAssertion(() => Assert.Equal(State.SecurityCheckList().Count, State.SecurityCheckResults.Count), TimeSpan.FromSeconds(5));
        var before = State.HardeningScore;
        Assert.NotNull(before);
        cut.WaitForAssertion(() => Assert.Contains(" / 100", cut.Markup, StringComparison.Ordinal));

        // The explanation (i) of a check, with the attack technique
        const string apps = "Geheimen en certificaten van apps";
        AngleSharp.Dom.IElement Row() => cut.FindAll("tr").First(tr => tr.TextContent.Contains(apps, StringComparison.Ordinal) && tr.QuerySelector("button[aria-label='Uitleg']") is not null);
        Row().QuerySelector("button[aria-label='Uitleg']")!.Click();
        Assert.Contains("Wat is het?", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("https://attack.mitre.org/techniques/T1098/001/", cut.Markup, StringComparison.Ordinal);

        // Accepting the risk: with a reason, in the change log, the score goes up
        Assert.Equal(CheckStatus.Problem, State.SecurityCheckResults["app-geheimen"].Status);
        Row().QuerySelectorAll("button").First(b => b.TextContent == "Risico aanvaarden").Click();
        cut.Find("input[aria-label='Reden']").Input("vernieuwd bij de leverancier op 1 december");
        cut.FindAll("button").First(b => b.TextContent == "Aanvaarden").Click();
        Assert.NotNull(State.SecurityCheckResults["app-geheimen"].AcceptedUntil);
        Assert.Contains("Aanvaard tot", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(State.ChangeLog.Recent(10), l => l.ChangeAction == "AcceptRisk" && l.Message.Contains("leverancier", StringComparison.Ordinal));
        Assert.True(State.HardeningScore > before);

        // Lifting it: the advice counts again
        cut.FindAll("button").First(b => b.TextContent == "Opheffen").Click();
        Assert.Empty(State.AcceptedRisks);
        Assert.Equal(before, State.HardeningScore);
        Assert.Equal(2, State.ChangeLog.Recent(10).Count(l => l.ChangeAction == "AcceptRisk"));

        // The results are kept in the tenant database: after a restart the last result is still there
        using var again = ActivatorUtilities.CreateInstance<ManagementState>(Services);
        Assert.Equal(CheckStatus.Problem, again.SecurityCheckResults["app-geheimen"].Status);
        Assert.NotNull(again.SecurityCheckResults["app-geheimen"].Time);
        Assert.Equal(before, again.HardeningScore);
    }

    [Fact]
    public void AlertsSeeAccountsEnabledAgainInAdAndEntra()
    {
        var now = State.Data;
        var cloud = now.EntraUsers.First() with { AccountEnabled = true, OnPremisesSyncEnabled = false };
        var current = now with { EntraUsers = now.EntraUsers.Select(e => e.Id == cloud.Id ? cloud : e).ToList() };
        var before = current with
        {
            EntraUsers = current.EntraUsers.Select(e => e.Id == cloud.Id ? e with { AccountEnabled = false } : e).ToList(),
            AdUsers = current.AdUsers.Select(a => a.SamAccountName == "bo.janssens" ? a with { Enabled = false, SamAccountName = "BO.JANSSENS" } : a).ToList(),
        };

        var alerts = SecurityChecks.Alerts(before, current, DateTimeOffset.Now.AddDays(-7));
        Assert.Contains(alerts.Rows, r => r.Object == cloud.UserPrincipalName);
        Assert.Contains(alerts.Rows, r => r.Object == "bo.janssens"); // names are compared without case
    }

    [Fact]
    public void SecureScoreLinksAreOnlyWebAddresses()
    {
        var score = SecurityChecks.SecureScore(new SecureScoreReading(new[] { (DateTimeOffset.Now, 50.0, 100.0) },
            new[] { ("MFA", 10.0, "https://security.microsoft.com/securescore"), ("Script", 9.0, "javascript:alert(1)"), ("Share", 8.0, "file://server/share/x.exe"), ("Empty", 7.0, "") }));
        Assert.Equal("https://security.microsoft.com/securescore", score.Rows.Single(r => r.Object == "MFA").Href);
        Assert.Null(score.Rows.Single(r => r.Object == "Script").Href);
        Assert.Null(score.Rows.Single(r => r.Object == "Share").Href);
        Assert.Null(score.Rows.Single(r => r.Object == "Empty").Href);
    }

    [Fact]
    public void RolesSecureScoreAndRisk()
    {
        var roles = SecurityChecks.Roles(Enumerable.Range(1, 6).Select(i => new RoleAssignmentInfo("Global Administrator", $"admin{i}", "permanent", null)).ToList());
        Assert.Equal(CheckStatus.Problem, roles.Status);
        var score = SecurityChecks.SecureScore(new SecureScoreReading(new[] { (DateTimeOffset.Now, 80.0, 100.0) }, Array.Empty<(string, double, string)>()));
        Assert.Equal(CheckStatus.Ok, score.Status);
        Assert.Equal(CheckStatus.Unknown, SecurityChecks.Risky(null).Status);
        Assert.Equal(CheckStatus.Problem, SecurityChecks.Risky(new[] { new RiskyUser("x@test.be", "high", "atRisk", null) }).Status);
    }

    [Fact]
    public void TheScreenRunsEveryCheck()
    {
        EntraFilled();
        var cut = RenderComponent<SecurityChecksPage>();
        cut.Find("input[type=checkbox]").Change(true); // with inbox rules
        cut.FindAll("button").First(b => b.TextContent == "Alles nakijken").Click();
        cut.WaitForAssertion(() => Assert.Equal(State.SecurityCheckList().Count, State.SecurityCheckResults.Count), TimeSpan.FromSeconds(5));
        Assert.Single(_factory.WasRead, w => w == "apps"); // one round reads the app registrations once, for two checks
        Assert.Equal(CheckStatus.Problem, State.SecurityCheckResults["doorsturen"].Status);
        Assert.Equal(CheckStatus.Problem, State.SecurityCheckResults["app-geheimen"].Status);
        Assert.Equal(CheckStatus.Problem, State.SecurityCheckResults["ad-beveiliging"].Status);
        Assert.Equal(CheckStatus.Attention, State.SecurityCheckResults["secure-score"].Status);
        Assert.Contains("probleem", cut.Markup, StringComparison.Ordinal);
    }
}
