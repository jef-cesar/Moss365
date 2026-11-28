using Moss365.Core.Localization;
using System.Globalization;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// Snapshots and the organisation over time (R4): every read becomes a snapshot in the tenant database, the screens can
/// show the organisation as it was on a date (read only), and a KPI can be computed for every snapshot.
/// </summary>
public sealed partial class ManagementState
{
    private const string SnapshottedFolders = "momentopnamen-mappen";

    private InventoryData? _presentData;

    /// <summary>The snapshot the screens show, or null for now.</summary>
    public SnapshotInfo? AsOf { get; private set; }

    /// <summary>Showing the past: everything is read only, no plan runs and nothing is loaded.</summary>
    public bool IsHistoric => AsOf is not null;

    public IReadOnlyList<SnapshotInfo> SnapshotList() => Database.SnapshotList();

    /// <summary>A snapshot of what was just read live (the parts that were read in full).</summary>
    private void SaveLiveSnapshot(InventoryComponent components, string folder)
    {
        try
        {
            var tables = Snapshots.Tables(InventoryWriter.Files(Data, components));
            var parts = string.Join(", ", new[] { (InventoryComponent.Ad, "AD"), (InventoryComponent.Entra, "Entra"), (InventoryComponent.Rooms, "rooms"), (InventoryComponent.Mailboxes, "postvakken"), (InventoryComponent.Google, "Google") }
                .Where(x => components.HasFlag(x.Item1)).Select(x => x.Item2));
            var id = Database.SaveSnapshot(DateTimeOffset.Now, "Live: " + parts, tables);
            MarkSnapshotted(folder);
            Log.Information("Momentopname {Id} bewaard ({Tabellen} tabellen)", id, tables.Count);
        }
        catch (Exception ex)
        {
            Log.Warning("Momentopname bewaren mislukt: {Fout}", ex.Message);
        }
    }

    /// <summary>
    /// Inventory folders in the database that are not a snapshot yet (imported from Export-Inventaris.ps1, or from an
    /// older version) become one, oldest first. A folder older than the last snapshot is history that came too late:
    /// it stays as an inventory but does not become a snapshot (snapshots follow each other in time).
    /// </summary>
    private void SnapshotNewFolders()
    {
        try
        {
            var done = (Database.ReadDocument(SnapshottedFolders) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var last = Database.SnapshotList().LastOrDefault()?.Time;
            foreach (var folder in InventoryLoader.SearchFolders(Files).Reverse().Where(f => !done.Contains(f)))
            {
                var time = FolderTime(folder);
                if (time is null || (last is { } l && time <= l))
                {
                    MarkSnapshotted(folder);
                    continue;
                }

                var files = Files.List(folder + "/").Where(f => !f.Name.StartsWith("snel-", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(f => f.Name, f => Files.ReadText(f.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                Database.SaveSnapshot(time.Value, "Inventaris " + folder[(folder.LastIndexOf('/') + 1)..], Snapshots.Tables(files));
                MarkSnapshotted(folder);
                last = time;
            }
        }
        catch (Exception ex)
        {
            Log.Warning("Inventaris als momentopname bewaren mislukt: {Fout}", ex.Message);
        }
    }

    private void MarkSnapshotted(string folder)
    {
        var done = (Database.ReadDocument(SnapshottedFolders) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!done.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            done.Add(folder);
            Database.SaveDocument(SnapshottedFolders, string.Join('\n', done));
        }
    }

    /// <summary>The time in the name of an inventory folder (Inventaris-yyyyMMdd-HHmm), as local time.</summary>
    private static DateTimeOffset? FolderTime(string folder)
    {
        var name = folder[(folder.LastIndexOf('/') + 1)..];
        return name.Length >= 24 && DateTime.TryParseExact(name.Substring(11, 13), "yyyyMMdd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? new DateTimeOffset(d) : null;
    }

    /// <summary>The organisation as it was at a moment (the last snapshot before it); null when there is none.</summary>
    // The organisation at the last snapshot asked, kept (issue 40): the alerts and the past ask it more than once, and a
    // snapshot that exists never changes (thinning out keeps every remaining date exact)
    private (long Snapshot, InventoryData Data)? _dataAt;

    public InventoryData? DataAt(DateTimeOffset moment)
    {
        if (Database.SnapshotAt(moment) is not { } s)
        {
            return null;
        }

        if (_dataAt is { } kept && kept.Snapshot == s.Id)
        {
            return kept.Data;
        }

        var data = Snapshots.Data(Database.TablesAt(s.Id), Texts.T.Format("code.management-state-history.state-of", s.Time.LocalDateTime));
        _dataAt = (s.Id, data);
        return data;
    }

    /// <summary>Shows the organisation as it was at a moment, read only. Returns an error, or null.</summary>
    public string? ShowAsOf(DateTimeOffset moment)
    {
        if (TaskBusy || ScriptBusy)
        {
            return Texts.T["code.management-state-history.wait-until-the-task-is"];
        }

        if (Database.SnapshotAt(moment) is not { } s)
        {
            return Texts.T["code.management-state-history.there-is-no-snapshot-from"];
        }

        _presentData ??= _data;
        Data = Snapshots.Data(Database.TablesAt(s.Id), Texts.T.Format("code.management-state-history.state-of", s.Time.LocalDateTime));
        AsOf = s;
        Access("Stand uit het verleden bekeken", s.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Changed?.Invoke();
        return null;
    }

    /// <summary>Back to the present.</summary>
    public void BackToNow()
    {
        if (_presentData is { } now)
        {
            Data = now;
        }

        _presentData = null;
        AsOf = null;
        Changed?.Invoke();
    }

    /// <summary>What changed between two moments (the snapshots at those moments).</summary>
    public IReadOnlyList<SnapshotChange>? Differences(DateTimeOffset from, DateTimeOffset to)
        => Database.SnapshotAt(from) is { } a && Database.SnapshotAt(to) is { } b ? Database.Differences(a.Id, b.Id) : null;

    /// <summary>
    /// Computes the KPIs again from the snapshots: the last snapshot of every day becomes the KPI value of that day
    /// (also for a KPI that was added later). Returns the number of days.
    /// </summary>
    public int RecomputeKpis()
    {
        var days = 0;
        foreach (var day in Database.SnapshotList().GroupBy(s => DateOnly.FromDateTime(s.Time.LocalDateTime)))
        {
            var s = day.OrderBy(x => x.Id).Last();
            var data = Snapshots.Data(Database.TablesAt(s.Id), "momentopname");
            Database.SaveKpiSnapshot(day.Key, BiStatistics.Snapshot(new KpiSource(data, Config, s.Time.LocalDateTime)));
            days++;
        }

        Log.Information("KPI's herberekend uit de momentopnamen: {Dagen} dagen", days);
        return days;
    }

    /// <summary>The timeline of a user: the versions of its AD and Entra account and its memberships, with the change log.</summary>
    public IReadOnlyList<TimelineEntry> Timeline(Person p)
    {
        var output = new List<TimelineEntry>();
        void Add(string table, string key, string what)
        {
            IReadOnlyDictionary<string, string>? previous = null;
            foreach (var v in Database.Timeline(table, key))
            {
                // R21: only columns the older version had count (a field read for the first time is no change)
                var changed = previous is null
                    ? Array.Empty<string>()
                    : previous.Keys.Where(k => (v.Content.GetValueOrDefault(k) ?? string.Empty) != (previous.GetValueOrDefault(k) ?? string.Empty)).OrderBy(k => k).ToArray();
                if (previous is not null && changed.Length == 0)
                {
                    previous = v.Content;
                    continue;
                }

                output.Add(new TimelineEntry(v.FromTime, what, previous is null ? "eerste momentopname" : string.Join(", ", changed.Select(k => $"{k}: {previous.GetValueOrDefault(k)} → {v.Content.GetValueOrDefault(k)}")), v)
                {
                    Fields = changed,
                });
                previous = v.Content;
            }
        }

        if (p.Ad is { } ad)
        {
            Add(InventoryLoader.AdUsers, ad.SamAccountName, "AD-account");
        }

        if (p.Entra is { } e)
        {
            Add(InventoryLoader.EntraUsers, e.Id, "Entra-account");
        }

        var targets = new[] { p.Ad?.DistinguishedName, p.Ad?.SamAccountName, p.Entra?.Id, p.Entra?.UserPrincipalName }.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ChangeLog.Recent(2000).Where(r => targets.Contains(r.Target)))
        {
            output.Add(new TimelineEntry(r.Time, "Moss365", $"{r.Description} ({r.User}, {(r.Succeeded ? "gelukt" : "mislukt")})", null)
            {
                Fields = r.Parameters.Keys.ToList(),
            });
        }

        return output.OrderByDescending(x => x.Time).ToList();
    }

    /// <summary>
    /// A plan that puts the attributes and AD groups of a user back as they were in a version of its AD account. Only
    /// what differs; privileged and dynamic groups follow the usual rules of the plans.
    /// </summary>
    public PlanOutcome RestorePlan(Person p, ObjectVersion version)
    {
        if (IsHistoric)
        {
            return PlanOutcome.Error(Texts.T["code.management-state-history.go-back-to-now-first"]);
        }

        var row = version.Content;
        string? V(string column) => row.TryGetValue(column, out var x) ? x : null;

        // Only what really differs (a value that is the same apart from spaces stays as it is now)
        var current = UserDesired.Of(p, Data);
        string? Then(string column, string? now) => V(column) is { } then && !string.Equals(then.Trim(), (now ?? string.Empty).Trim(), StringComparison.Ordinal) ? then : null;
        var desired = new UserDesired
        {
            DisplayName = Then("DisplayName", current.DisplayName), GivenName = Then("GivenName", current.GivenName), Surname = Then("Surname", current.Surname),
            JobTitle = Then("Title", current.JobTitle), Department = Then("Department", current.Department), Office = Then("Office", current.Office),
            City = Then("City", current.City), Description = Then("Description", current.Description), LogonScript = Then("ScriptPath", current.LogonScript),
            Phone = Then("OfficePhone", current.Phone), Mobile = Then("MobilePhone", current.Mobile),
        };
        var attributes = AttributePlans.User(Data, Config, p, desired);
        var plan = attributes.Plan.ToList();
        var errors = attributes.Errors.Where(e => !e.Contains("niets", StringComparison.OrdinalIgnoreCase)).ToList();

        if (p.Ad is { } ad && V("MemberOf") is { } memberOf)
        {
            var then = memberOf.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = ad.MemberOr.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var who = new ObjectRef(ObjectKind.User, p.Key, p.Name);
            foreach (var dn in then.Except(now).Concat(now.Except(then)))
            {
                if (Data.Index.Find(dn) is not { Kind: ObjectKind.Group } r || Data.Index.Group(r.Key) is not { } g)
                {
                    errors.Add(Texts.T.Format("code.management-state-history.group-no-longer-exists", DnHelp.Name(dn)));
                    continue;
                }

                var u = then.Contains(dn) ? GroupPlans.AddMember(Data, g, who) : GroupPlans.RemoveMember(Data, g, who);
                plan.AddRange(u.Plan);
                errors.AddRange(u.Errors);
            }
        }

        return plan.Count == 0 && errors.Count == 0 ? PlanOutcome.Error(Texts.T["code.management-state-history.there-is-nothing-to-put"]) : new PlanOutcome(plan, errors);
    }
}

/// <summary>One line of the timeline of an object: a version from a snapshot, or a change made with Moss365.</summary>
public sealed record TimelineEntry(DateTimeOffset Time, string What, string Details, ObjectVersion? Version)
{
    /// <summary>R21: the fields this entry changed (columns of a snapshot, or parameters of a change in Moss365), to show
    /// the history of one field.</summary>
    public IReadOnlyList<string> Fields { get; init; } = Array.Empty<string>();
}
