using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>R6: maintenance of a tenant database. Thinning snapshots keeps every date that stays exact.</summary>
public sealed class MaintenanceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moss365-maint-" + Guid.NewGuid().ToString("N"));

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

    private static string Fingerprint(Dictionary<string, List<Dictionary<string, string>>> tables)
        => string.Join("\n", tables.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).Select(t => t.Key + ":" + string.Join("|",
            t.Value.Select(r => System.Text.Json.JsonSerializer.Serialize(new SortedDictionary<string, string>(r, StringComparer.Ordinal))).OrderBy(x => x, StringComparer.Ordinal))));

    [Fact]
    public void ThinningKeepsEveryDateThatStaysExact()
    {
        var db = Db();
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        var random = new Random(42);
        var files = TestData.Files();
        var users = files[InventoryLoader.AdUsers];

        // Two years of snapshots, about every four days, with changes, accounts that come and go, and partial reads
        var time = now.AddDays(-730);
        var step = 0;
        while (time <= now)
        {
            var u = users[random.Next(users.Count)];
            u["Department"] = "dienst-" + random.Next(5);
            if (step % 7 == 3)
            {
                users.Add(TestData.AdUser($"tijdelijk.{step}", "OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=test,DC=internal", $"tijdelijk.{step}@test.be"));
            }

            if (step % 11 == 5 && users.Count > 8)
            {
                users.RemoveAt(users.Count - 1);
            }

            var tables = Snapshots.Tables(InventoryWriter.Files(InventoryLoader.Build(files), All));
            if (step % 9 == 4)
            {
                // Only the rooms were read this time
                tables = new Dictionary<string, List<Dictionary<string, string>>> { [InventoryLoader.ExoRooms] = tables[InventoryLoader.ExoRooms].Skip(step % 2).ToList() };
            }

            db.SaveSnapshot(time, "test", tables);
            time = time.AddDays(4).AddHours(random.Next(-5, 5));
            step++;
        }

        var before = db.SnapshotList().ToDictionary(s => s.Id, s => Fingerprint(db.TablesAt(s.Id)));
        var versionsBefore = Count(db, "Version");

        var thin = db.SnapshotsToThin(now, 365);
        Assert.NotEmpty(thin);
        Assert.All(thin, s => Assert.True(s.Time < now.AddDays(-365)));
        Assert.Equal(thin.Count, db.RemoveSnapshots(thin.Select(s => s.Id).ToList()));

        // Older than a year: one per month (the last); the last year: all
        var kept = db.SnapshotList();
        var old = kept.Where(s => s.Time < now.AddDays(-365)).ToList();
        Assert.Equal(old.Count, old.Select(s => (s.Time.LocalDateTime.Year, s.Time.LocalDateTime.Month)).Distinct().Count());
        Assert.Equal(before.Count - thin.Count, kept.Count);

        // Every date that stays shows exactly what it showed before
        foreach (var s in kept)
        {
            Assert.Equal(before[s.Id], Fingerprint(db.TablesAt(s.Id)));
        }

        // Fewer versions, none that points to a removed snapshot, no object without a version; the timeline still works
        Assert.True(Count(db, "Version") < versionsBefore);
        Assert.Equal(0, Count(db, "Version", "WHERE FromSnapshot NOT IN (SELECT Id FROM Snapshot) OR (UntilSnapshot IS NOT NULL AND UntilSnapshot NOT IN (SELECT Id FROM Snapshot))"));
        Assert.Equal(0, Count(db, "Object", "WHERE Id NOT IN (SELECT ObjectId FROM Version)"));
        Assert.NotEmpty(db.Timeline(InventoryLoader.AdUsers, "bo.janssens"));

        // Thinning again removes nothing
        Assert.Empty(db.SnapshotsToThin(now, 365));
    }

    [Fact]
    public void TheNewestSnapshotAlwaysStays()
    {
        var db = Db();
        var tables = Snapshots.Tables(InventoryWriter.Files(InventoryLoader.Build(TestData.Files()), All));
        var now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        db.SaveSnapshot(now.AddDays(-800), "a", tables);
        db.SaveSnapshot(now.AddDays(-799), "b", tables);
        var thin = db.SnapshotsToThin(now, 365);
        Assert.Empty(thin.Where(s => s.Source == "b"));
        Assert.Throws<InvalidOperationException>(() => db.RemoveSnapshots(db.SnapshotList().Select(s => s.Id).ToList()));
    }

    [Fact]
    public void SizesAccessLogAndCopies()
    {
        var db = Db();
        db.WriteAccess(new AccessEntry(DateTimeOffset.Now.AddYears(-6), "a", "Export", "10 rijen"));
        db.WriteAccess(new AccessEntry(DateTimeOffset.Now, "a", "Export", "2 rijen"));
        db.WriteTechnicalLog(DateTimeOffset.Now.AddDays(-100), "Information", "oud");
        Assert.Equal(1, db.CountAccess(DateTimeOffset.Now.AddYears(-5)));
        Assert.Single(db.ReadAccessOlder(DateTimeOffset.Now.AddYears(-5)));
        Assert.Equal(1, db.RemoveAccess(DateTimeOffset.Now.AddYears(-5)));
        Assert.Single(db.ReadAccess(10));
        Assert.Equal(1, db.CountTechnicalLog(DateTimeOffset.Now.AddDays(-90)));

        var sizes = db.Sizes();
        Assert.Equal(1, sizes.Single(p => p.Part == "Toegangslog").Rows);
        Assert.Contains(sizes, p => p.Part == "Momentopnamen");
        db.Compact();
        Assert.Equal("ok", db.IntegrityCheck());

        // Copies before a migration: the newest always stays
        foreach (var (name, days) in new[] { ("20250101-000000", 400), ("20250601-000000", 200), ("20261101-000000", 5) })
        {
            var f = db.FilePath + ".bak-" + name;
            File.WriteAllText(f, "x");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-days));
        }

        Assert.Equal(3, db.MigrationCopies().Count);
        Assert.Equal(2, db.OldMigrationCopies(DateTimeOffset.Now.AddDays(-30)).Count);
        Assert.Single(db.OldMigrationCopies(DateTimeOffset.Now.AddDays(-300)));
        File.SetLastWriteTimeUtc(db.FilePath + ".bak-20261101-000000", DateTime.UtcNow.AddDays(-500));
        Assert.Equal(2, db.OldMigrationCopies(DateTimeOffset.Now.AddDays(-30)).Count);
    }

    private static int Count(Database db, string table, string where = "")
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.FilePath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table} {where};";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
