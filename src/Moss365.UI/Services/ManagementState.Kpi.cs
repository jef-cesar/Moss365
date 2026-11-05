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
    public IReadOnlyList<KpiTrend> KpiTrend(int days = 90)
    {
        var snapshot = Database.ReadKpiSnapshot(DateOnly.FromDateTime(DateTime.Now.AddDays(-days)));
        return BiStatistics.WithTrend
            .Select(k => new KpiTrend(k, snapshot.TryGetValue(k.Key, out var points) ? points : Array.Empty<(DateOnly, int)>()))
            .ToList();
    }

    public int KpiCount(Kpi k) => Kpis.Count(k, KpiSource, k.Items == KpiList.ChangeLog ? ChangeLogForKpi() : null);

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
