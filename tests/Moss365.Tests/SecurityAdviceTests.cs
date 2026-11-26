using Moss365.Core.Localization;
using Moss365.Core.Security;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16: security advice with its explanation, accepted risks and the hardening score.</summary>
public class SecurityAdviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(1));

    private static SecurityCheck Check(string key, CheckStatus status, params CheckRow[] rows) => new(key, key, status, "samenvatting", rows);

    [Fact]
    public void EveryAdviceHasItsExplanationLinksAndAWeight()
    {
        var en = new Translator("en");
        foreach (var a in SecurityAdvice.All)
        {
            foreach (var field in new[] { "what", "why", "fix", "risk" })
            {
                Assert.True(en.Has($"advice.{a.Key}.{field}"), $"advice.{a.Key}.{field}");
            }

            Assert.All(a.Links, l => Assert.StartsWith("https://learn.microsoft.com/", l, StringComparison.Ordinal));
        }

        Assert.Equal("https://attack.mitre.org/techniques/T1558/003/", new AdviceInfo("x", 1, "T1558.003", Array.Empty<string>()).TechniqueLink);
        Assert.Equal(0, SecurityAdvice.Info("meldingen")!.Weight); // monitoring, not hardening
    }

    [Fact]
    public void AnAcceptedLineNoLongerCountsUntilItsEndDate()
    {
        var check = Check("ad-beveiliging", CheckStatus.Problem,
            new CheckRow("svc-scan", "SPN", CheckStatus.Attention),
            new CheckRow("svc-oud", "wachtwoord niet vereist", CheckStatus.Problem));
        var accepted = SecurityAdvice.Accept("ad-beveiliging", "svc-oud", "afgesproken met de leverancier", "beheerder", Now, new DateOnly(2027, 1, 31));

        var shown = SecurityAdvice.Apply(check, new[] { accepted }, Now);
        Assert.Equal(CheckStatus.Attention, shown.Status);
        Assert.Equal(new DateOnly(2027, 1, 31), shown.Rows.Single(r => r.Object == "svc-oud").AcceptedUntil);
        Assert.Null(shown.AcceptedUntil);

        // After the end date the advice is back
        Assert.Equal(CheckStatus.Problem, SecurityAdvice.Apply(check, new[] { accepted }, Now.AddDays(70)).Status);
    }

    [Fact]
    public void TheScoreWeighsTheChecksAndAnAcceptedCheckCountsAsHandled()
    {
        var roles = Check("rollen", CheckStatus.Attention);
        var mfa = Check("zwakke-mfa", CheckStatus.Problem);
        var gpo = Check("gpo", CheckStatus.Ok);
        var alerts = Check("meldingen", CheckStatus.Problem); // weight 0: does not count
        var risky = Check("risico", CheckStatus.Unknown);     // unknown: does not count

        // roles 3 x 0.5 + weak MFA 3 x 0 + GPO 1 x 1 = 2.5 of 7
        Assert.Equal(36, SecurityAdvice.Score(new[] { roles, mfa, gpo, alerts, risky }));

        var accepted = SecurityAdvice.Accept("zwakke-mfa", string.Empty, "uitrol passkeys loopt tot januari", "beheerder", Now, new DateOnly(2027, 1, 15));
        var shown = SecurityAdvice.Apply(mfa, new[] { accepted }, Now);
        Assert.Equal(new DateOnly(2027, 1, 15), shown.AcceptedUntil);
        Assert.Contains("uitrol passkeys", shown.AcceptedNote, StringComparison.Ordinal);

        // roles 1.5 + weak MFA 3 + GPO 1 = 5.5 of 7
        Assert.Equal(79, SecurityAdvice.Score(new[] { roles, shown, gpo, alerts, risky }));
        Assert.Null(SecurityAdvice.Score(new[] { alerts, risky }));
    }

    [Fact]
    public void AnAcceptedRiskNeedsAReasonAndEndsWithinAYear()
    {
        Assert.Throws<ArgumentException>(() => SecurityAdvice.Accept("gpo", string.Empty, " ", "b", Now, new DateOnly(2027, 1, 1)));
        Assert.Throws<ArgumentException>(() => SecurityAdvice.Accept("gpo", string.Empty, "reden", "b", Now, new DateOnly(2028, 1, 1)));
        Assert.Throws<ArgumentException>(() => SecurityAdvice.Accept("gpo", string.Empty, "reden", "b", Now, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void ResultsAcceptedRisksAndTheScoreAreKept()
    {
        var many = Enumerable.Range(1, 600).Select(i => new CheckRow($"account{i}", "geen MFA-methode", CheckStatus.Attention)).ToArray();
        var results = SecurityAdvice.WithResult(new SecurityResults(), Check("zwakke-mfa", CheckStatus.Attention, many), Now);
        results = SecurityAdvice.WithResult(results, Check("gpo", CheckStatus.Attention, new CheckRow("Oud beleid", "nergens gekoppeld", CheckStatus.Attention)), Now);
        results = SecurityAdvice.WithResult(results, Check("rollen", CheckStatus.Ok), Now);
        results = SecurityAdvice.WithException(results, SecurityAdvice.Accept("gpo", "Oud beleid", "bewaard voor de test", "beheerder", Now, new DateOnly(2027, 2, 1)), add: true, Now);

        var again = SecurityAdvice.Read(SecurityAdvice.Write(results));
        Assert.Equal(3, again.Last.Count);
        Assert.Equal(SecurityAdvice.MaxStoredRows, again.Last.Single(c => c.Key == "zwakke-mfa").Rows.Count);
        Assert.Equal(Now, again.Last.Single(c => c.Key == "gpo").Time);
        Assert.Equal("Oud beleid", Assert.Single(again.Exceptions).Object);

        // weak MFA 3 x 0.5 + GPO 1 x 1 (its only line accepted) + roles 3 x 1 = 5.5 of 7
        Assert.Equal(79, Assert.Single(again.Scores).Score);

        // Lifting it counts again, on the same day
        var lifted = SecurityAdvice.WithException(again, again.Exceptions[0], add: false, Now);
        Assert.Empty(lifted.Exceptions);
        Assert.Equal(71, Assert.Single(lifted.Scores).Score);

        Assert.Empty(SecurityAdvice.Read("{kapot").Last);
    }
}
