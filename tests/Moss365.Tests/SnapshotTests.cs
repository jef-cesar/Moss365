using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>R4: snapshots of the organisation. A date is rebuilt exactly, and only what changed is stored.</summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moss365-snap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private Database Db() => new(Path.Combine(_folder, "tenant.db"));

    private static readonly InventoryComponent All = InventoryComponent.Ad | InventoryComponent.Entra | InventoryComponent.Rooms | InventoryComponent.Mailboxes;

    private static Dictionary<string, List<Dictionary<string, string>>> TablesOf(Dictionary<string, List<Dictionary<string, string>>> files)
        => Snapshots.Tables(InventoryWriter.Files(InventoryLoader.Build(files), All));

    /// <summary>A table as a sorted list of rows, to compare content without caring about order.</summary>
    private static Dictionary<string, List<string>> Normalised(Dictionary<string, List<Dictionary<string, string>>> tables)
        => tables.Where(t => t.Value.Count > 0).ToDictionary(t => t.Key,
            t => t.Value.Select(r => JsonSerializer.Serialize(new SortedDictionary<string, string>(r, StringComparer.Ordinal))).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ADateIsRebuiltExactly()
    {
        var db = Db();
        var first = TablesOf(TestData.Files());
        var s1 = db.SaveSnapshot(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), "Live: AD, Entra", first);

        // A month later: bo changed department, the old account is gone, cas joined a group
        var files = TestData.Files();
        var bo = files[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "bo.janssens");
        bo["Department"] = "bibliotheek";
        files[InventoryLoader.AdUsers].RemoveAll(r => r["SamAccountName"] == "oud.account");
        var cas = files[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "cas.vos");
        cas["MemberOf"] = "CN=gg_eigen,OU=GROEPEN,OU=GEMEENTEBESTUUR,DC=test,DC=internal";
        var second = TablesOf(files);
        var s2 = db.SaveSnapshot(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), "Live: AD, Entra", second);

        // Both dates come back exactly, as the data the app had then
        Assert.Equal(Normalised(first), Normalised(db.TablesAt(s1)));
        Assert.Equal(Normalised(second), Normalised(db.TablesAt(s2)));
        var then = Snapshots.Data(db.TablesAt(s1), "toen");
        Assert.Equal("cultuur ", then.AdUsers.Single(u => u.SamAccountName == "bo.janssens").Department);
        Assert.Contains(then.AdUsers, u => u.SamAccountName == "oud.account");

        // The date picks the last snapshot before it
        Assert.Equal(s1, db.SnapshotAt(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero))!.Id);
        Assert.Null(db.SnapshotAt(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));

        // What changed: exactly bo, cas (changed) and the old account (gone) among the AD users
        var changes = db.Differences(s1, s2).Where(c => c.Table == InventoryLoader.AdUsers).ToList();
        Assert.Equal(new[] { "bo.janssens", "cas.vos", "oud.account" }, changes.Select(c => c.Key).OrderBy(k => k));
        Assert.Equal("verdwenen", changes.Single(c => c.Key == "oud.account").Kind);
        Assert.Contains("Department", changes.Single(c => c.Key == "bo.janssens").Columns);

        // The timeline of bo: two versions
        var timeline = db.Timeline(InventoryLoader.AdUsers, "bo.janssens");
        Assert.Equal(2, timeline.Count);
        Assert.Equal(s2, timeline[0].Until);
        Assert.Equal("bibliotheek", timeline[1].Content["Department"]);
    }

    [Fact]
    public void OnlyWhatChangedIsStored()
    {
        var db = Db();
        var tables = TablesOf(TestData.Files());
        var rows = tables.Sum(t => t.Value.Count);
        db.SaveSnapshot(DateTimeOffset.Now.AddDays(-2), "a", tables);
        db.SaveSnapshot(DateTimeOffset.Now.AddDays(-1), "b", tables);
        db.SaveSnapshot(DateTimeOffset.Now, "c", tables);
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.FilePath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM Version;";
        Assert.Equal(rows, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void APartialReadKeepsTheOtherTables()
    {
        var db = Db();
        var all = TablesOf(TestData.Files());
        var s1 = db.SaveSnapshot(DateTimeOffset.Now.AddHours(-1), "Live: AD, Entra", all);

        // Only the rooms were read again (and one room is gone): the AD users stay as they were
        var rooms = all[InventoryLoader.ExoRooms].Skip(1).ToList();
        var s2 = db.SaveSnapshot(DateTimeOffset.Now, "Live: rooms", new Dictionary<string, List<Dictionary<string, string>>> { [InventoryLoader.ExoRooms] = rooms });
        var after = db.TablesAt(s2);
        Assert.Equal(all[InventoryLoader.AdUsers].Count, after[InventoryLoader.AdUsers].Count);
        Assert.Equal(rooms.Count, after[InventoryLoader.ExoRooms].Count);
        Assert.Single(db.Differences(s1, s2));
    }

    [Fact]
    public void ATrendPerWeekMonthAndYear()
    {
        var kpi = Kpis.Fixed.First();
        var points = Enumerable.Range(0, 70).Select(i => (new DateOnly(2026, 1, 1).AddDays(i), 100 + i)).ToList();
        var trend = new KpiTrend(kpi, points);
        var week = trend.Per(TrendPeriod.Week);
        Assert.All(week.Points, p => Assert.True(p.Date.DayOfWeek == DayOfWeek.Sunday || p == week.Points[^1]));
        var month = trend.Per(TrendPeriod.Month);
        Assert.Equal(new[] { 130, 158, 169 }, month.Points.Select(p => p.Value));
        Assert.Equal(11, month.DifferenceWithPrevious(169));
        Assert.Single(trend.Per(TrendPeriod.Year).Points);
    }
}
