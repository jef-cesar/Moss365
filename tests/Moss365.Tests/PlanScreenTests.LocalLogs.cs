using Bunit;
using Moss365.Core.Inventory;
using Moss365.Core.Security;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R18 (U19): the sign-in and audit logs kept in the tenant database.</summary>
public sealed partial class PlanScreenTests
{
    private static SignInEvent KeptSignIn(string id, string userId, string upn, int error = 0, string risk = "", double hoursAgo = 1)
        => new(DateTimeOffset.Now.AddHours(-hoursAgo), userId, upn, upn, "Office 365", "192.0.2.10", "Gent, BE", error, error == 0 ? string.Empty : "Invalid password",
            "Browser", null, id, risk);

    private static readonly HashSet<string> NoOne = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void OnlyFailedRiskyAdminAndEmergencySignInsAreKept()
    {
        var admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "u-admin" };
        var emergency = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nood@test.be" };
        var kept = LocalLogs.ToKeep(new[]
        {
            KeptSignIn("1", "u-1", "an@test.be"),                   // an ordinary sign-in: not kept
            KeptSignIn("2", "u-1", "an@test.be", error: 50126),     // failed
            KeptSignIn("3", "u-admin", "admin@test.be"),            // an administrator
            KeptSignIn("4", "u-n", "NOOD@test.be"),                 // an emergency account
            KeptSignIn("5", "u-1", "an@test.be", risk: "medium"),   // risky
            KeptSignIn("5", "u-1", "an@test.be", risk: "medium"),   // the same sign-in twice
            KeptSignIn("", "u-1", "an@test.be", error: 1),          // without an id it cannot be told apart
            KeptSignIn("6", "u-1", "an@test.be", risk: "hidden"),   // the risk is not shown without Entra ID P2
        }, admins, emergency);

        Assert.Equal(new[] { "2", "3", "4", "5" }, kept.Select(k => k.SignIn.Id));
        Assert.Equal(new[] { "failed", "admin", "emergency", "risky" }, kept.Select(k => Assert.Single(k.Reasons)));
        Assert.Equal(new[] { "u-admin" }, LocalLogs.Admins(new SecurityData(Array.Empty<MfaRegistration>(), new[]
        {
            new RoleMember("Global Administrator", "u-admin", "Admin", "admin@test.be", "user"),
            new RoleMember("User Administrator", "g-1", "Helpdesk", string.Empty, "group"),
        })));

        // Never more than 30 days back, and a little overlap with the last line kept
        var now = DateTimeOffset.Now;
        Assert.Equal(now.AddDays(-30), LocalLogs.Since(null, now));
        Assert.Equal(now.AddDays(-30), LocalLogs.Since(now.AddDays(-90), now));
        Assert.Equal(now.AddHours(-5), LocalLogs.Since(now.AddHours(-2), now));
    }

    [Fact]
    public void TheDatabaseKeepsEveryLineOnceAndTheRetentionRemovesOldOnes()
    {
        var db = State.Database;
        var kept = LocalLogs.ToKeep(new[] { KeptSignIn("a", "u-admin", "admin@test.be", hoursAgo: 2), KeptSignIn("b", "u-1", "an@test.be", error: 50126, hoursAgo: 24 * 400) },
            new HashSet<string> { "u-admin" }, NoOne);
        Assert.Equal(2, db.KeepSignIns(kept));
        Assert.Equal(0, db.KeepSignIns(kept));
        var audit = new AuditEvent("x1", DateTimeOffset.Now.AddHours(-1), "Add member to role", "RoleManagement", "Core Directory", "beheerder@test.be", "u-1", "an", "success");
        Assert.Equal(1, db.KeepAudits(new[] { audit, audit, audit with { Id = string.Empty } }));

        var read = db.ReadSignIns(DateTimeOffset.Now.AddYears(-2), DateTimeOffset.Now.AddDays(1));
        Assert.Equal(new[] { "a", "b" }, read.Select(k => k.SignIn.Id));
        Assert.Equal(new[] { "admin" }, read[0].Reasons);
        Assert.Equal("192.0.2.10", read[0].SignIn.Ip);
        Assert.Equal("Add member to role", Assert.Single(db.ReadAudits(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1))).Activity);
        Assert.Contains(db.Sizes(), s => s.Part == "Aanmeld- en auditlogboeken" && s.Rows == 3);

        // The preview of the clean-up counts what is older than a year; cleaning up removes only that
        var item = Assert.Single(State.CleanupPreview(DateTimeOffset.Now), i => i.Part == CleanupPart.LocalLogs);
        Assert.Equal(1, item.Count);
        State.Cleanup(new[] { CleanupPart.LocalLogs }, DateTimeOffset.Now);
        Assert.Equal("a", Assert.Single(db.ReadSignIns(DateTimeOffset.Now.AddYears(-2), DateTimeOffset.Now.AddDays(1))).SignIn.Id);
    }

    [Fact]
    public void AReadOfEntraKeepsTheLogsLocallyOnlyWhenTurnedOn()
    {
        _factory.SignIns.AddRange(new[] { KeptSignIn("s1", "e-an", "an.peeters@test.be", error: 50126), KeptSignIn("s2", "e-bo", "bo.janssens@test.be") });
        _factory.Audits.Add(new AuditEvent("a1", DateTimeOffset.Now.AddHours(-1), "Update conditional access policy", "Policy", "Conditional Access", "beheerder@test.be", "p1", "Blokkeer oude protocollen", "success"));

        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.All(State.Job!.Steps, s => Assert.Equal(StepStatus.Done, s.Status));
        Assert.Equal("s1", Assert.Single(State.KeptSignIns(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1))).SignIn.Id);
        Assert.Single(State.KeptAudits(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));
        Assert.Equal((1, 1, (string?)null), (State.LocalLogsLast!.SignIns, State.LocalLogsLast.Audits, State.LocalLogsLast.Message));

        // A second read asks a little back again, but keeps nothing twice; the audit log may fail on its own
        _factory.AuditNoPermission = true;
        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.Equal((0, 0), (State.LocalLogsLast!.SignIns, State.LocalLogsLast.Audits));
        Assert.Contains("Het auditlogboek kon niet gelezen worden", State.LocalLogsLast.Message, StringComparison.Ordinal);

        // Turned off: not read at all
        State.Config.Retention.KeepLocalLogs = false;
        _factory.SignIns.Add(KeptSignIn("s3", "e-an", "an.peeters@test.be", error: 50126));
        Assert.True(State.StartLoading(LoadKind.AdAndEntra));
        WaitOnTask();
        Assert.DoesNotContain(State.Job!.Steps, s => s.Name == "Aanmeldingen en auditlogboek lokaal bewaren");
        Assert.Single(State.KeptSignIns(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));
    }

    [Fact]
    public void TheKeptLogsPageFiltersAndWritesTheAccessLog()
    {
        State.Database.KeepSignIns(LocalLogs.ToKeep(new[] { KeptSignIn("p1", "u-1", "an@test.be", error: 50126), KeptSignIn("p2", "u-admin", "admin@test.be") },
            new HashSet<string> { "u-admin" }, NoOne));
        var cut = RenderComponent<KeptLogsPage>();

        Assert.Contains("Aanmeldingen (2)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nog niets verzameld", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("#aanmeldingen button").First(b => b.TextContent.StartsWith("beheerder", StringComparison.Ordinal)).Click();
        var row = Assert.Single(cut.FindAll("#aanmeldingen tbody tr"));
        Assert.Contains("admin@test.be", row.TextContent, StringComparison.Ordinal);
        Assert.Contains(State.Database.ReadAccess(10), a => a.What == "Bewaarde logboeken bekeken" && a.Details.Contains("2 aanmeldingen", StringComparison.Ordinal));

        // Only for whoever may see sign-ins, and only for a tenant with Microsoft 365
        Assert.Equal(Moss365.Core.Permissions.Permission.SignIns, Access.ForPath("bewaarde-logboeken"));
        Assert.False(Access.FitsTenant("bewaarde-logboeken", hasAd: true, hasMicrosoft365: false));
    }
}
