using Moss365.Core.Analysis;

namespace Moss365.UI.Services;

/// <summary>KPIs: the number on a tile and the list behind it come from the same computation
/// (<see cref="Kpis"/>).</summary>
public sealed partial class ManagementState
{
    private KpiSource? _kpiSource;

    /// <summary>The source for the KPIs, built again when the data or the configuration changes (or after 5
    /// minutes).</summary>
    public KpiSource KpiSource
    {
        get
        {
            var b = _kpiSource;
            if (b is null || !ReferenceEquals(b.Data, Data) || !ReferenceEquals(b.Config, Config) || DateTime.Now - b.Now > TimeSpan.FromMinutes(5))
            {
                b = _kpiSource = new KpiSource(Data, Config, DateTime.Now);
            }

            return b;
        }
    }

    /// <summary>The change log the change log KPIs compute from (the last 2000 entries).</summary>
    public IReadOnlyList<Moss365.Core.Changes.LogEntry> ChangeLogForKpi() => ChangeLog.Recent(2000);

    /// <summary>
    /// After loading live: today's KPI snapshot in the database (counts only, no names), for the trend. An old
    /// inventory gives no snapshot: it would be on the wrong day.
    /// </summary>
    public void SaveKpiSnapshot()
    {
        try
        {
            Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Now), BiStatistics.Snapshot(new KpiSource(Data, Config, DateTime.Now)));
        }
        catch (Exception ex)
        {
            Log.Warning("KPI-stand bewaren mislukt: {Fout}", ex.Message);
        }
    }

    /// <summary>The trend of the KPIs with a history, the last days.</summary>
    public IReadOnlyList<KpiTrend> KpiTrend(int days = 90) => KpiTrend(TrendPeriod.Day, days);

    /// <summary>The trend of every KPI, one point per day, week, month or year.</summary>
    public IReadOnlyList<KpiTrend> KpiTrend(TrendPeriod period, int? days = null)
    {
        var reference = AsOf?.Time.LocalDateTime ?? DateTime.Now;
        var snapshot = Database.ReadKpiSnapshot(DateOnly.FromDateTime(reference.AddDays(-(days ?? Moss365.Core.Analysis.KpiTrend.DaysFor(period)))));
        var until = DateOnly.FromDateTime(reference);
        return BiStatistics.WithTrend
            .Select(k => new KpiTrend(k, snapshot.TryGetValue(k.Key, out var points) ? points.Where(p => p.Date <= until).ToList() : Array.Empty<(DateOnly, int)>()).Per(period))
            .ToList();
    }

    // The counts of the tiles, kept as long as the KPI source (issue 40): a tile no longer counts its list again at every
    // render, and the change log tiles no longer read 2,000 lines each time. A new source (other data or configuration,
    // or after 5 minutes) or an executed plan starts over.
    private (KpiSource Source, System.Collections.Concurrent.ConcurrentDictionary<string, int> Counts)? _kpiCounts;

    public int KpiCount(Kpi k)
    {
        var source = KpiSource;
        if (_kpiCounts is not { } kept || !ReferenceEquals(kept.Source, source))
        {
            kept = (source, new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal));
            _kpiCounts = kept;
        }

        return kept.Counts.GetOrAdd(k.Key, _ => Kpis.Count(k, source, k.Items == KpiList.ChangeLog ? ChangeLogForKpi() : null));
    }

    /// <summary>The kept KPI counts no longer hold (after a plan: the change log and often the data changed).</summary>
    private void ForgetKpiCounts() => _kpiCounts = null;

    /// <summary>
    /// A page opens with <c>?kpi=...</c>. When you came via a tile (not with Back or Forward), the filters of the page
    /// and the column filters of its lists start over, so that you see exactly the rows behind the number. Returns true
    /// when the page has to reset its filters.
    /// </summary>
    public bool KpiArrival(string? kpi, string uri, string pageKey, params string[] tables)
    {
        if (Kpis.Find(kpi) is null || History.IsJumpTo(uri))
        {
            return false;
        }

        PageState.Forget(pageKey);
        foreach (var t in tables)
        {
            var session = TableSession(t);
            session.Filters.Clear();
            session.Page = 0;
        }

        return true;
    }
}

/// <summary>The active KPI filter of a page without its own search fields (Rooms, Conditional Access), in the page
/// state.</summary>
public sealed class KpiChoice
{
    public string? Kpi { get; set; }
}
