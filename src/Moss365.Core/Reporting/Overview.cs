using Moss365.Core.Localization;
using Moss365.Core.Permissions;

namespace Moss365.Core.Reporting;

/// <summary>The four blocks of the overview (R19).</summary>
public enum OverviewBlock
{
    Security,
    Devices,
    Hygiene,
    Work,
}

/// <summary>
/// One tile of the overview (R19): a stable key (for the trend and for All tenants), its block, the number (null: not
/// known yet, such as devices that were not read), what it counts, the list behind it, whether it is a point of
/// attention, and the number of a period ago for the trend.
/// </summary>
public sealed record OverviewTile(string Key, OverviewBlock Block, int? Value, string Label, string Link, bool Let, int? Before = null, string Unit = "",
    bool HigherIsBetter = false);

/// <summary>The order of the blocks and the hidden ones (per person; the default follows the role).</summary>
public sealed record OverviewLayout(IReadOnlyList<OverviewBlock> Order, IReadOnlyList<OverviewBlock> Hidden);

/// <summary>
/// The overview (R19): the start screen of a tenant in four blocks: security, devices, hygiene and costs, and work.
/// Every tile clicks through to the list behind it and shows its trend against a period ago, from the counts kept per day
/// (only counts, never names; the KPIs and the own tiles with the prefix <see cref="CountPrefix"/>).
/// </summary>
public static class Overview
{
    /// <summary>The own tiles in the counts per day (table KpiStand), next to the KPIs.</summary>
    public const string CountPrefix = "overzicht:";

    public const string LayoutDocument = "overzicht-indeling";

    public static IReadOnlyList<int> Periods { get; } = new[] { 7, 30, 90 };

    /// <summary>What a role sees first: the helpdesk the work, the auditor the security and hygiene.</summary>
    public static OverviewLayout DefaultLayout(Role roles)
    {
        IReadOnlyList<OverviewBlock> order = roles.HasFlag(Role.Administrator) ? new[] { OverviewBlock.Security, OverviewBlock.Work, OverviewBlock.Devices, OverviewBlock.Hygiene }
            : roles.HasFlag(Role.Auditor) ? new[] { OverviewBlock.Security, OverviewBlock.Hygiene, OverviewBlock.Devices, OverviewBlock.Work }
            : roles.HasFlag(Role.Helpdesk) ? new[] { OverviewBlock.Work, OverviewBlock.Devices, OverviewBlock.Security, OverviewBlock.Hygiene }
            : new[] { OverviewBlock.Hygiene, OverviewBlock.Security, OverviewBlock.Devices, OverviewBlock.Work };
        return new OverviewLayout(order, Array.Empty<OverviewBlock>());
    }

    /// <summary>A layout that was kept, made whole: every block once, unknown blocks left out.</summary>
    public static OverviewLayout Complete(OverviewLayout layout, OverviewLayout fallback)
        => new(layout.Order.Where(Enum.IsDefined).Distinct().Concat(fallback.Order).Distinct().ToList(), layout.Hidden.Where(Enum.IsDefined).Distinct().ToList());

    /// <summary>A block one place up (-1) or down (+1).</summary>
    public static OverviewLayout Move(OverviewLayout layout, OverviewBlock block, int step)
    {
        var order = layout.Order.ToList();
        var i = order.IndexOf(block);
        var j = i + step;
        if (i < 0 || j < 0 || j >= order.Count)
        {
            return layout;
        }

        (order[i], order[j]) = (order[j], order[i]);
        return layout with { Order = order };
    }

    public static OverviewLayout Toggle(OverviewLayout layout, OverviewBlock block)
        => layout with { Hidden = layout.Hidden.Contains(block) ? layout.Hidden.Where(b => b != block).ToList() : layout.Hidden.Append(block).ToList() };

    /// <summary>The value on or before a date from the counts kept per day, or null.</summary>
    public static int? ValueAt(IReadOnlyDictionary<string, IReadOnlyList<(DateOnly Date, int Value)>> kept, string key, DateOnly date)
        => kept.TryGetValue(key, out var points) ? points.Where(p => p.Date <= date).Select(p => (int?)p.Value).LastOrDefault() : null;

    /// <summary>The trend in words ("+3 in 30 days, worse"), or null when the number of then is not known.</summary>
    public static string? Trend(OverviewTile t, int days)
    {
        if (t.Value is not { } now || t.Before is not { } then)
        {
            return null;
        }

        var difference = now - then;
        if (difference == 0)
        {
            return Texts.T.Format("overview.trend.same", days);
        }

        var better = difference > 0 == t.HigherIsBetter;
        return Texts.T.Format(better ? "overview.trend.better" : "overview.trend.worse", difference > 0 ? $"+{difference}" : $"−{-difference}", days);
    }

    /// <summary>A number as a tile shows it: a percentage, an amount in euros, or a count; "?" when not known.</summary>
    public static string Format(int? value, string unit) => value is not { } v ? "?" : unit switch
    {
        "%" => $"{v}%",
        "€" => Texts.T.Format("overview.euro", v.ToString("N0", Texts.T.Culture)),
        _ => v.ToString(Texts.T.Culture),
    };

    public static string Number(OverviewTile t) => Format(t.Value, t.Unit);

    /// <summary>The own tiles with a number, as counts to keep per day (with <see cref="CountPrefix"/>).</summary>
    public static IReadOnlyDictionary<string, int> CountsToKeep(IEnumerable<OverviewTile> tiles, IReadOnlySet<string> own)
        => tiles.Where(t => t.Value is not null && own.Contains(t.Key)).ToDictionary(t => CountPrefix + t.Key, t => t.Value!.Value, StringComparer.Ordinal);
}
