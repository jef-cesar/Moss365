using System.Globalization;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Core.Analysis;

/// <summary>
/// A part of a distribution (stacked bar or list): name, count and the KPI with exactly those rows. Colour is the role
/// of the colour, not the colour itself: "reeks", "schaal-1".."schaal-4", "goed", "kritiek" or "neutraal".
/// </summary>
public sealed record ChartPart(string Name, int Count, Kpi? Kpi, string Colour = "reeks");

/// <summary>One column in a column chart (e.g. a month), with a value per series.</summary>
public sealed record ChartColumn(string Label, string Tip, IReadOnlyList<int> Values, Kpi? Kpi = null)
{
    public int Total => Values.Sum();
}

/// <summary>The trend of one KPI: the saved snapshots per day.</summary>
/// <summary>The steps of a trend: one point per day, week, month or year (the last value in that period).</summary>
public enum TrendPeriod
{
    Day,
    Week,
    Month,
    Year,
}

public sealed record KpiTrend(Kpi Kpi, IReadOnlyList<(DateOnly Date, int Value)> Points, TrendPeriod Period = TrendPeriod.Day)
{
    /// <summary>How far back a trend of this period goes, in days.</summary>
    public static int DaysFor(TrendPeriod period) => period switch
    {
        TrendPeriod.Day => 90,
        TrendPeriod.Week => 7 * 52,
        TrendPeriod.Month => 2 * 365,
        _ => 10 * 365,
    };

    /// <summary>The same trend with one point per period: the last value of every week, month or year.</summary>
    public KpiTrend Per(TrendPeriod period)
    {
        if (period == TrendPeriod.Day)
        {
            return this with { Period = period };
        }

        DateOnly Start(DateOnly d) => period switch
        {
            TrendPeriod.Week => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
            TrendPeriod.Month => new DateOnly(d.Year, d.Month, 1),
            _ => new DateOnly(d.Year, 1, 1),
        };
        var points = Points.GroupBy(p => Start(p.Date)).OrderBy(g => g.Key).Select(g => g.OrderBy(p => p.Date).Last()).ToList();
        return new KpiTrend(Kpi, points, period);
    }

    /// <summary>The difference with the previous period (week, month, year), or null.</summary>
    public int? DifferenceWithPrevious(int now) => Points.Count >= 2 ? now - Points[^2].Value : null;

    /// <summary>The difference with the snapshot of about this many days ago (the oldest snapshot that is at least that
    /// old), or null.</summary>
    public int? Difference(int days, int now)
    {
        if (Points.Count == 0)
        {
            return null;
        }

        var limit = Points[^1].Date.AddDays(-days);
        var old = Points.LastOrDefault(p => p.Date <= limit);
        return old == default ? null : now - old.Value;
    }
}

/// <summary>
/// The charts of Statistics (BI): distributions and series from the loaded data. Every part has its KPI, so a click
/// opens the list with exactly those rows. With a division only that division counts (the filter above the charts).
/// </summary>
public static class BiStatistics
{
    /// <summary>What the division filter filters with: the KPI key with the division added, or the KPI
    /// itself.</summary>
    public static Kpi WithDivision(Kpi k, string? division)
        => string.IsNullOrEmpty(division) ? k : Kpis.Find($"{k.Key}|afdeling-alle:{division}")!;

    private static ChartPart Part(Kpi k, KpiSource b, string? division, string colour)
    {
        var kpi = WithDivision(k, division);
        return new ChartPart(k.Label, Kpis.Count(kpi, b), kpi, colour);
    }

    /// <summary>Active employees by their last sign-in (a scale from recent to long ago).</summary>
    public static IReadOnlyList<ChartPart> SignInRecency(KpiSource b, string? division = null) => new[]
    {
        Part(Kpis.SignInEvent("0-30"), b, division, "schaal-1"),
        Part(Kpis.SignInEvent("30-90"), b, division, "schaal-2"),
        Part(Kpis.SignInEvent("90-180"), b, division, "schaal-3"),
        Part(Kpis.SignInEvent("180+"), b, division, "schaal-4"),
        Part(Kpis.SignInEvent("nooit"), b, division, "neutraal"),
    };

    /// <summary>Active AD accounts by the age of their password.</summary>
    public static IReadOnlyList<ChartPart> PasswordAge(KpiSource b, string? division = null) => new[]
    {
        Part(Kpis.Password("0-90"), b, division, "schaal-1"),
        Part(Kpis.Password("90-365"), b, division, "schaal-2"),
        Part(Kpis.Password("365-730"), b, division, "schaal-3"),
        Part(Kpis.Password("730+"), b, division, "schaal-4"),
        Part(Kpis.Password("onbekend"), b, division, "neutraal"),
    };

    /// <summary>Active employees with and without MFA (unknown when the registration was not read).</summary>
    public static IReadOnlyList<ChartPart> Mfa(KpiSource b, string? division = null) => new[]
    {
        Part(Kpis.MfaStatus("ja"), b, division, "goed"),
        Part(Kpis.MfaStatus("nee"), b, division, "kritiek"),
        Part(Kpis.MfaStatus("onbekend"), b, division, "neutraal"),
    };

    /// <summary>Active employees per office: the largest seven, the rest together.</summary>
    public static IReadOnlyList<ChartPart> PerOffice(KpiSource b, string? division = null)
    {
        var names = b.Active.Select(p => Kpis.OfficeOf(p)).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var share = names.Select(n => Part(Kpis.Office(n), b, division, "reeks")).Where(d => d.Count > 0).OrderByDescending(d => d.Count).ToList();
        return share.Count <= 8 ? share : share.Take(7).Append(new ChartPart("Overige", share.Skip(7).Sum(d => d.Count), null)).ToList();
    }

    /// <summary>Guest users per domain of their own organisation: the largest seven, the rest together.</summary>
    public static IReadOnlyList<ChartPart> GuestsPerDomain(KpiSource b)
    {
        var share = b.Data.Persons.Where(p => p.IsGuest).GroupBy(Kpis.GuestDomain)
            .Select(g => new ChartPart(g.Key, g.Count(), Kpis.GuestDomainKpi(g.Key)))
            .OrderByDescending(d => d.Count).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
        return share.Count <= 8 ? share : share.Take(7).Append(new ChartPart("Overige", share.Skip(7).Sum(d => d.Count), null)).ToList();
    }

    /// <summary>New accounts (employees) per month, the last months up to and including the current one.</summary>
    public static IReadOnlyList<ChartColumn> CreatedPerMonth(KpiSource b, string? division = null, int months = 12)
    {
        var first = new DateTime(b.Now.Year, b.Now.Month, 1).AddMonths(-(months - 1));
        return Enumerable.Range(0, months).Select(i =>
        {
            var month = first.AddMonths(i);
            var kpi = WithDivision(Kpis.Created(month.ToString("yyyy-MM", CultureInfo.InvariantCulture)), division);
            var count = Kpis.Count(kpi, b);
            var name = month.ToString("MMM yyyy", CultureInfo.GetCultureInfo("nl-BE"));
            return new ChartColumn(month.ToString("MMM", CultureInfo.GetCultureInfo("nl-BE")).TrimEnd('.'), $"{name}: {count} nieuwe accounts", new[] { count }, kpi);
        }).ToList();
    }

    /// <summary>Executed changes per week (succeeded and failed), the last weeks up to and including this
    /// week.</summary>
    public static IReadOnlyList<ChartColumn> ChangeLogPerWeek(IEnumerable<LogEntry> rules, DateTime now, int weeks = 12)
    {
        var monday = CalendarPeriod.Monday(now).AddDays(-7 * (weeks - 1));
        var list = rules.ToList();
        return Enumerable.Range(0, weeks).Select(i =>
        {
            var start = monday.AddDays(7 * i);
            var until = start.AddDays(7);
            var inWeek = list.Where(r => r.Time.LocalDateTime >= start && r.Time.LocalDateTime < until).ToList();
            var succeeded = inWeek.Count(r => r.Succeeded);
            var failed = inWeek.Count(r => r.Failed);
            var week = ISOWeek.GetWeekOfYear(start);
            return new ChartColumn($"w{week}", $"Week {week} (vanaf {start:dd/MM}): {succeeded} gelukt, {failed} mislukt", new[] { succeeded, failed });
        }).ToList();
    }

    /// <summary>The KPIs of which the app saves the snapshot every day (counts only) and shows the trend.</summary>
    public static IReadOnlyList<Kpi> WithTrend { get; } = new[]
    {
        Kpis.Active, Kpis.Disabled, Kpis.Guests, Kpis.Inactive90, Kpis.NeverSignedIn, Kpis.WithoutMfa,
        Kpis.AdministratorsWithoutMfa, Kpis.PrivilegedAll, Kpis.SignInAllowed, Kpis.WithDirectPermissions, Kpis.EmptyGroups, Kpis.CaProblems,
    };

    /// <summary>Today's snapshot: every fixed KPI except those of the change log (that has its own series).</summary>
    public static IReadOnlyDictionary<string, int> Snapshot(KpiSource b)
        => Kpis.Fixed.Where(k => k.Items != KpiList.ChangeLog).ToDictionary(k => k.Key, k => Kpis.Count(k, b), StringComparer.Ordinal);
}
