using Moss365.Core.Permissions;
using Moss365.Core.Reporting;
using Xunit;

namespace Moss365.Tests;

/// <summary>R19: the overview, its layout per role and its trends.</summary>
public class OverviewTests
{
    [Fact]
    public void EveryRoleSeesWhatItNeedsFirst()
    {
        Assert.Equal(OverviewBlock.Security, Overview.DefaultLayout(Role.Administrator).Order[0]);
        Assert.Equal(OverviewBlock.Security, Overview.DefaultLayout(Role.Everything).Order[0]); // roles off: the administrator
        Assert.Equal(OverviewBlock.Work, Overview.DefaultLayout(Role.Helpdesk).Order[0]);
        Assert.Equal(new[] { OverviewBlock.Security, OverviewBlock.Hygiene }, Overview.DefaultLayout(Role.Auditor).Order.Take(2));
        Assert.Equal(OverviewBlock.Hygiene, Overview.DefaultLayout(Role.Reader).Order[0]);
        Assert.All(new[] { Role.Administrator, Role.Helpdesk, Role.Auditor, Role.Reader }, r => Assert.Equal(4, Overview.DefaultLayout(r).Order.Distinct().Count()));
    }

    [Fact]
    public void ALayoutCanBeChangedAndStaysWhole()
    {
        var layout = Overview.DefaultLayout(Role.Administrator);
        var moved = Overview.Move(layout, OverviewBlock.Work, -1);
        Assert.Equal(OverviewBlock.Work, moved.Order[0]);
        Assert.Same(layout, Overview.Move(layout, OverviewBlock.Security, -1)); // already first
        var hidden = Overview.Toggle(moved, OverviewBlock.Devices);
        Assert.Equal(new[] { OverviewBlock.Devices }, hidden.Hidden);
        Assert.Empty(Overview.Toggle(hidden, OverviewBlock.Devices).Hidden);

        // A kept layout with a missing or unknown block gets the others back from the default
        var kept = new OverviewLayout(new[] { OverviewBlock.Hygiene, (OverviewBlock)42, OverviewBlock.Hygiene }, new[] { (OverviewBlock)42 });
        var whole = Overview.Complete(kept, layout);
        Assert.Equal(OverviewBlock.Hygiene, whole.Order[0]);
        Assert.Equal(4, whole.Order.Count);
        Assert.Empty(whole.Hidden);
    }

    [Fact]
    public void TheTrendSaysBetterOrWorse()
    {
        var mfa = new OverviewTile("zonder-mfa", OverviewBlock.Security, 5, "x", "y", true, Before: 8);
        Assert.Equal("−3 in 30 dagen, beter", Overview.Trend(mfa, 30));
        Assert.Equal("+3 in 7 dagen, slechter", Overview.Trend(mfa with { Value = 11 }, 7));
        Assert.Equal("evenveel als 30 dagen geleden", Overview.Trend(mfa with { Value = 8 }, 30));
        Assert.Null(Overview.Trend(mfa with { Before = null }, 30));
        Assert.Null(Overview.Trend(mfa with { Value = null }, 30));
        var score = new OverviewTile("hardening", OverviewBlock.Security, 80, "x", "y", false, Before: 72, Unit: "%", HigherIsBetter: true);
        Assert.Equal("+8 in 30 dagen, beter", Overview.Trend(score, 30));
        Assert.Equal("80%", Overview.Number(score));
        Assert.Equal("?", Overview.Number(score with { Value = null }));
        Assert.Equal("€ 1.250", Overview.Format(1250, "€"));

        // The value on or before a date from the counts kept per day
        var kept = new Dictionary<string, IReadOnlyList<(DateOnly Date, int Value)>>
        {
            ["overzicht:x"] = new[] { (new DateOnly(2026, 10, 1), 4), (new DateOnly(2026, 10, 20), 6) },
        };
        Assert.Equal(4, Overview.ValueAt(kept, "overzicht:x", new DateOnly(2026, 10, 19)));
        Assert.Equal(6, Overview.ValueAt(kept, "overzicht:x", new DateOnly(2026, 11, 26)));
        Assert.Null(Overview.ValueAt(kept, "overzicht:x", new DateOnly(2026, 9, 30)));
        Assert.Null(Overview.ValueAt(kept, "overzicht:y", new DateOnly(2026, 11, 26)));

        // Only the own tiles with a number are kept, under the prefix
        var own = new HashSet<string> { "hardening" };
        Assert.Equal(new Dictionary<string, int> { ["overzicht:hardening"] = 80 }, Overview.CountsToKeep(new[] { score, mfa, score with { Key = "other" } }, own));
    }
}
