using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Scripts;
using Moss365.Core.Storage;
using Moss365.UI.Components;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.12 (R2 and R3): one database per tenant with everything in it, and several tenants that never mix.</summary>
public sealed partial class PlanScreenTests
{
    /// <summary>Like the Windows app: a data folder with tenants, no inventory folder imported by itself.</summary>
    private sealed record HubEnvironment(string Root, string ConfigDefaultPath) : IAppEnvironment
    {
        public string ExportFolder => Path.Combine(Root, "export");

        public string TempFolder => Path.Combine(Root, "temp");
    }

    private (TenantHub Hub, Dictionary<string, FakeFactory> Factories, HubEnvironment Environment) Hub(string name)
    {
        var environment = new HubEnvironment(Path.Combine(_base, name), Path.Combine(_base, "standaard.json"));
        var factories = new Dictionary<string, FakeFactory>();
        var hub = new TenantHub(environment, t =>
        {
            var f = factories[t.Id] = new FakeFactory();
            var s = new ManagementState(environment, f, null, t);
            s.Settings.AdServer = "dc.test.internal";
            return s;
        });
        return (hub, factories, environment);
    }

    private static Change Unlock(string target) => new()
    {
        TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdUnlockAccount, Target = target, Description = "ontgrendelen",
    };

    [Fact]
    public async Task TwoTenantsSideBySideNeverMix()
    {
        var (hub, factories, environment) = Hub("twee");
        using var _ = hub;
        var a = hub.Current;
        var bInfo = hub.Add("Fabrikam");
        var b = hub.Open(bInfo);
        Assert.Equal(2, hub.Tenants.Count);
        Assert.NotEqual(a.Tenant.Colour, b.Tenant.Colour);

        // Each its own database in its own folder
        Assert.NotEqual(a.Database.FilePath, b.Database.FilePath);
        Assert.StartsWith(Path.Combine(environment.Root, "tenants"), a.Database.FilePath, StringComparison.Ordinal);
        Assert.Equal(TenantFolders.DatabaseName, Path.GetFileName(b.Database.FilePath));

        // Lists: the inventory read into A is not in B
        var inventory = Path.Combine(_base, "export-inventaris");
        TestData.WriteInventory(Path.Combine(inventory, "Inventaris-20261016-1343"));
        Assert.Contains("1 inventarismap", a.ImportInventory(inventory));
        Assert.NotEmpty(a.Data.Persons);
        b.LoadInventory();
        Assert.Empty(b.Data.Persons);
        Assert.Empty(InventoryLoaderFolders(b));

        // Plans: run in A, with the connections of A; the change log of B stays empty
        var results = await a.ExecuteAsync(new[] { Unlock("CN=an,DC=test,DC=internal") }, "test", null);
        Assert.Equal(ResultStatus.Succeeded, Assert.Single(results).Status);
        Assert.Single(factories[a.Tenant.Id].Executed);
        Assert.Empty(factories[b.Tenant.Id].Executed);
        Assert.Single(a.ChangeLog.Recent(10));
        Assert.Empty(b.ChangeLog.Recent(10));

        // Log lines: the technical log of B has nothing of the plan in A
        Assert.Contains(a.Database.ReadTechnicalLog(100), l => l.Message.Contains("Plan uitgevoerd", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Database.ReadTechnicalLog(100), l => l.Message.Contains("Plan uitgevoerd", StringComparison.Ordinal));

        // Exports: a folder per tenant
        Assert.NotEqual(a.ExportFolder, b.ExportFolder);
        Assert.EndsWith("Fabrikam", b.ExportFolder, StringComparison.Ordinal);

        // Organisation, scripts, scheduled actions: only in the tenant where they were made
        a.Config.Offices.Add("Kantoor A");
        a.SaveConfig();
        b.LoadConfig();
        Assert.True(a.ConfigIsModified);
        Assert.False(b.ConfigIsModified);
        Assert.DoesNotContain("Kantoor A", b.Config.Offices);
        ScriptLibrary.Store(a.ScriptFolders, ScriptLibrary.NewScript("Alleen in A"));
        Assert.Contains(ScriptLibrary.Load(a.ScriptFolders), s => s.Header.Name == "Alleen in A");
        Assert.DoesNotContain(ScriptLibrary.Load(b.ScriptFolders), s => s.Header.Name == "Alleen in A");

        // Switching: the other tenant is open, also after a restart
        hub.Switch(bInfo.Id);
        Assert.Same(b, hub.Current);
        hub.Dispose();
        var (again, _, _) = Hub("twee");
        using var __ = again;
        Assert.Equal("Fabrikam", again.CurrentTenant.Name);
        Assert.Equal(2, again.Tenants.Count);
        Assert.Single(again.Open(again.Tenants.First(t => t.Id != bInfo.Id)).ChangeLog.Recent(10));
    }

    private static IReadOnlyList<string> InventoryLoaderFolders(ManagementState s) => Moss365.Core.Inventory.InventoryLoader.SearchFolders(s.Files);

    [Fact]
    public void TenantsCanBeAddedRenamedAndRemovedWithoutLosingData()
    {
        var (hub, _, environment) = Hub("beheer");
        using var _ = hub;
        Assert.Equal("Mijn organisatie", Assert.Single(hub.Tenants).Name);
        Assert.Throws<ArgumentException>(() => hub.Add("mijn organisatie"));
        Assert.Throws<ArgumentException>(() => hub.Add(" "));

        var other = hub.Add("Fabrikam");
        hub.Open(other).ChangeLog.Write(new LogEntry(DateTimeOffset.Now, "x", "c", "AD", "AdUnlockAccount", "t", "t", "o", new Dictionary<string, string>(), "Succeeded", "ok"));
        hub.Update(other with { Name = "Fabrikam BV", Colour = TenantColours.All[3] });
        Assert.Equal("Fabrikam BV", hub.Open(other).Tenant.Name);

        // The open tenant cannot be removed; another one moves to tenants\removed with its database
        Assert.Throws<InvalidOperationException>(() => hub.Remove(hub.CurrentTenant.Id));
        var folder = hub.Remove(other.Id);
        Assert.Single(hub.Tenants);
        Assert.NotNull(folder);
        Assert.StartsWith(Path.Combine(environment.Root, "tenants", "removed"), folder, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder!, TenantFolders.DatabaseName)));
    }

    [Fact]
    public void BackupAndRestoreOfATenant()
    {
        var (hub, _, environment) = Hub("backup");
        using var _ = hub;
        var a = hub.Current;
        a.ChangeLog.Write(new LogEntry(DateTimeOffset.Now, "x", "c", "AD", "AdUnlockAccount", "t", "t", "o", new Dictionary<string, string>(), "Succeeded", "ok"));
        a.Files.WriteText("ca-backup/ca-20261101-100000.json", """{"tijd":"2026-11-01T10:00:00","beleiden":[],"locaties":[]}""");

        var file = hub.Backup(a.Tenant, Path.Combine(environment.Root, "export", "Back-ups"));
        Assert.True(File.Exists(file));

        // Always as a new tenant, with everything in it
        var restored = hub.Restore(file, "Kopie");
        Assert.Equal(2, hub.Tenants.Count);
        var b = hub.Open(restored);
        Assert.Single(b.ChangeLog.Recent(10));
        Assert.Single(b.CaBackups());
        Assert.Throws<ArgumentException>(() => hub.Restore(file, "Kopie"));

        // Not a database: refused, and no tenant left behind
        var broken = Path.Combine(environment.Root, "kapot.db");
        File.WriteAllText(broken, "geen databank");
        Assert.Throws<InvalidDataException>(() => hub.Restore(broken, "Kapot"));
        Assert.Equal(2, hub.Tenants.Count);
    }

    [Fact]
    public void AnInstallationOf011BecomesTheFirstTenant()
    {
        // The layout of 0.11: one database with the change log, and loose files next to it
        var root = Path.Combine(_base, "oud");
        var old = new Database(Path.Combine(root, "moss365.db"));
        old.WriteLog(new[] { new LogEntry(DateTimeOffset.Now, "admin@contoso.com", "c", "AD", "AdGroepLidToevoegen", "t", "t", "o", new Dictionary<string, string>(), "Gelukt", "ok") });
        var config = TestData.Config();
        config.Company = "Contoso";
        ConfigStorage.Save(config, Path.Combine(root, "organisatie.json"));
        Directory.CreateDirectory(Path.Combine(root, "ca-backup"));
        File.WriteAllText(Path.Combine(root, "ca-backup", "ca-20261001-100000.json"), """{"tijd":"2026-10-01T10:00:00","beleiden":[],"locaties":[]}""");
        Directory.CreateDirectory(Path.Combine(root, "scripts", "eigen"));
        File.WriteAllText(Path.Combine(root, "scripts", "eigen", "Rapport.ps1"), ScriptLibrary.NewScript("Rapport"));
        File.WriteAllText(Path.Combine(root, "scripts", "eigen", "goedgekeurd.json"), """{"Rapport.ps1":"abc"}""");
        Directory.CreateDirectory(Path.Combine(root, "scripts", "runs", "Rapport", "20261001-100000"));
        File.WriteAllText(Path.Combine(root, "scripts", "runs", "Rapport", "20261001-100000", "run.json"), """{"Id":"20261001-100000","Key":"Rapport","Name":"Rapport","Resultaat":"Gelukt"}""");
        TestData.WriteInventory(Path.Combine(root, "Inventaris", "Inventaris-20261001-1000"));

        var environment = new HubEnvironment(root, Path.Combine(_base, "standaard.json"));
        using var hub = new TenantHub(environment, t => new ManagementState(environment, null, null, t));
        var tenant = Assert.Single(hub.Tenants);
        Assert.Equal("Contoso", tenant.Name);
        Assert.Contains(hub.Messages, m => m.Contains("Contoso", StringComparison.Ordinal));

        var s = hub.Current;
        Assert.Equal("AdGroepLidToevoegen", Assert.Single(s.ChangeLog.Recent(10)).ChangeAction);
        Assert.True(s.ConfigIsModified);
        Assert.Equal("Contoso", s.Config.Company);
        Assert.Single(s.CaBackups());
        Assert.Contains(ScriptLibrary.Load(s.ScriptFolders), x => x.Header.Name == "Rapport");
        Assert.Equal(RunResult.Succeeded, Assert.Single(ScriptRuns.Items(s.ScriptFolders)).Result);
        Assert.NotEmpty(s.Data.Persons);

        // Nothing is deleted: the old files are in old-0.11; next to the databases there are no loose files
        Assert.True(File.Exists(Path.Combine(root, "old-0.11", "moss365.db")));
        Assert.True(Directory.Exists(Path.Combine(root, "old-0.11", "scripts")));
        Assert.False(File.Exists(Path.Combine(root, "organisatie.json")));
        Assert.True(AppDatabaseOnly(root));
        Assert.All(Directory.GetFiles(s.TenantFolder), f => Assert.StartsWith(TenantFolders.DatabaseName, Path.GetFileName(f), StringComparison.Ordinal));
    }

    private static bool AppDatabaseOnly(string root)
        => Directory.GetFiles(root).All(f => Path.GetFileName(f).StartsWith(AppDatabase.FileName, StringComparison.Ordinal));

    [Fact]
    public void TheBarAndThePlanNameTheTenant()
    {
        var bar = RenderComponent<TenantBar>();
        Assert.Contains("Tenant", bar.Find(".kb-tenant-naam").TextContent);
        Assert.Contains("--kb-tenant", bar.Find(".kb-tenant").GetAttribute("style"));

        var plan = RenderComponent<PlanView>(p => p.Add(x => x.Plan, new[] { Unlock("CN=x") }).Add(x => x.Context, "test"));
        plan.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        Assert.Equal("Ja, uitvoeren in Tenant", plan.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).TextContent);
    }
}
