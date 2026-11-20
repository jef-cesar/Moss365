using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Storage;
using Moss365.UI.Components;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R7: updating never happens while something is open, and always after a copy of every database.</summary>
public sealed partial class PlanScreenTests
{
    private sealed class FakeUpdater : IAppUpdater
    {
        public bool Installed { get; set; } = true;
        public AvailableVersion? Newest { get; set; } = new("0.18.0", "## 0.18.0\n- iets nieuws", false, "abcdef0123456789abcdef");
        public List<string> Events { get; } = new();
        public bool DownloadFails { get; set; }
        public UpdateSettings? AskedWith { get; private set; }

        public Task<AvailableVersion?> CheckAsync(UpdateSettings settings, CancellationToken cancel)
        {
            AskedWith = settings;
            return Task.FromResult(Newest);
        }

        public Task<IReadOnlyList<string>> OlderVersionsAsync(UpdateSettings settings, string current, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "0.15.0", "0.14.0" });

        public Task<AvailableVersion?> FindAsync(UpdateSettings settings, string version, CancellationToken cancel)
            => Task.FromResult<AvailableVersion?>(new AvailableVersion(version, string.Empty, true));

        public Task DownloadAsync(AvailableVersion version, IProgress<int>? progress, CancellationToken cancel)
        {
            Events.Add("download " + version.Version);
            return DownloadFails ? Task.FromException(new InvalidOperationException("checksum klopt niet")) : Task.CompletedTask;
        }

        public void ApplyAndRestart(AvailableVersion version) => Events.Add("apply " + version.Version);
    }

    [Fact]
    public async Task AnUpdateWaitsForOpenWorkAndCopiesEveryDatabaseFirst()
    {
        var (hub, _, environment) = EncryptedHub("bijwerken");
        using var _ = hub;
        var second = hub.Add("Fabrikam"); // never opened in this session: copied as a file
        new Database(TenantFolders.DatabasePath(environment.Root, second.Id)).SaveDocument("x", "1");
        var state = hub.Current;
        var updater = new FakeUpdater();
        var updates = new AppUpdates(updater, hub);

        // The channel lives in moss365.db, for all tenants
        Assert.Equal(UpdateChannel.Preview, updates.Settings.Channel);
        updates.Save(new UpdateSettings(UpdateChannel.Stable, @"\\server\share\Moss365"));
        Assert.Equal(new UpdateSettings(UpdateChannel.Stable, @"\\server\share\Moss365"), updates.Settings);
        Assert.Equal(UpdateChannel.Stable, state.UpdateChannel);

        Assert.Equal("0.18.0", (await updates.CheckAsync())!.Version);
        Assert.Equal(UpdateChannel.Stable, updater.AskedWith!.Channel);

        // Something open: no update
        state.Pending.Set(new OpenItem("nieuw", OpenKind.Plan, "plan klaar"));
        Assert.Contains("open", Assert.Single(updates.Blockers()), StringComparison.Ordinal);
        Assert.NotNull(await updates.InstallAsync());
        Assert.Empty(updater.Events);
        state.Pending.Route("nieuw", OpenKind.Plan);
        Assert.Empty(updates.Blockers());

        // A download that fails the fingerprint check changes nothing
        updater.DownloadFails = true;
        Assert.Contains("checksum", await updates.InstallAsync() ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(state.Database.MigrationCopies());

        updater.DownloadFails = false;
        Assert.Null(await updates.InstallAsync());
        Assert.Equal(new[] { "download 0.18.0", "download 0.18.0", "apply 0.18.0" }, updater.Events);
        Assert.Single(state.Database.MigrationCopies());
        Assert.True(File.Exists(TenantFolders.DatabasePath(environment.Root, second.Id) + ".bak-" + Path.GetFileName(state.Database.MigrationCopies()[0].FullName).Split(".bak-")[1]));
        Assert.Single(Directory.GetFiles(environment.Root, AppDatabase.FileName + ".bak-*"));
        Assert.Contains(state.ChangeLog.Recent(5), r => r.ChangeAction == "UpdateApp" && r.Target == "0.18.0" && r.Parameters["sha256"].StartsWith("abcdef", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePanelOffersTheUpdateAndAnOlderVersion()
    {
        var (hub, _, _) = EncryptedHub("bijwerken-scherm");
        using var __ = hub;
        var updater = new FakeUpdater();
        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(hub);
        ctx.Services.AddSingleton(hub.Current);
        ctx.Services.AddSingleton(new AppUpdates(updater, hub));
        var cut = ctx.RenderComponent<UpdatePanel>();

        cut.FindAll("button").First(b => b.TextContent == "Nu zoeken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Versie 0.18.0", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("iets nieuws", cut.Find("pre.kb-nota").TextContent, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Nu bijwerken").Click();
        cut.WaitForAssertion(() => Assert.Contains("apply 0.18.0", updater.Events));

        // Back to an older version
        cut.FindAll("button").First(b => b.TextContent == "Naar een vorige versie").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("select[aria-label='Vorige versie']")));
        cut.Find("select[aria-label='Vorige versie']").Change("0.15.0");
        cut.FindAll("button").First(b => b.TextContent == "Deze versie nemen").Click();
        cut.WaitForAssertion(() => Assert.Contains("(een oudere versie: terugzetten)", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Nu terugzetten");

        // Not installed with Setup.exe: only the explanation
        updater.Installed = false;
        var other = ctx.RenderComponent<UpdatePanel>();
        Assert.Contains("niet geïnstalleerd", other.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(other.FindAll("button"), b => b.TextContent == "Nu zoeken");
    }
}
