using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>G1: Google Workspace is read in its own lane, becomes a snapshot and is linked to the persons.</summary>
public sealed partial class PlanScreenTests
{
    private sealed class FakeGoogleConnection(GoogleData data) : IGoogleConnection, IExecutor
    {
        public int Reads { get; private set; }

        /// <summary>G2: the factory that records what is written in Google.</summary>
        public FakeFactory? Writer { get; set; }

        public TargetSystem TargetSystem => TargetSystem.Google;

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel) => Writer!.Run(changes);

        /// <summary>G2: the members of every group, as Google gives them.</summary>
        public List<GoogleMember> Members { get; } = new() { new("cas.vos@test.be", "OWNER", "USER"), new("nieuw@test.be", "MEMBER", "USER") };

        public Task<IReadOnlyList<GoogleMember>?> ReadMembersAsync(string group, CancellationToken cancel) => Task.FromResult<IReadOnlyList<GoogleMember>?>(Members.ToList());

        public Task<GoogleData> ReadAsync(IProgress<string>? progress, CancellationToken cancel)
        {
            Reads++;
            return Task.FromResult(data);
        }

        public Task<GoogleLogs> ReadLogsAsync(DateTimeOffset loginsSince, DateTimeOffset adminSince, int max, CancellationToken cancel)
            => Task.FromResult(new GoogleLogs(
                new[]
                {
                    new SignInEvent(DateTimeOffset.Now.AddHours(-1), "g-cas", "cas.vos@test.be", "", "Google", "192.0.2.1", "", 1, "login_failure", "", Id: "q1:0"),
                    new SignInEvent(DateTimeOffset.Now.AddHours(-1), "g-nieuw", "nieuw@test.be", "", "Google", "192.0.2.2", "", 0, "", "", Id: "q2:0"),
                },
                new[] { new Moss365.Core.Security.AuditEvent("a1:0", DateTimeOffset.Now.AddHours(-1), "CHANGE_PASSWORD", "USER_SETTINGS", "Google Admin", "admin@test.be", "", "cas.vos@test.be", "") }));
    }

    private sealed partial class FakeFactory
    {
        public FakeGoogleConnection? GoogleFake { get; set; }

        public IGoogleConnection? Google(ConnectionSettings settings) => settings.GoogleFilled ? GoogleFake : null;
    }

    private static GoogleData GoogleSample() => new(
        new[]
        {
            new GoogleUser { Id = "g-cas", PrimaryEmail = "cas.vos@test.be", Name = "Cas Vos", OrgUnitPath = "/Zorg", TwoStepEnrolled = true },
            new GoogleUser { Id = "g-nieuw", PrimaryEmail = "nieuw@test.be", Name = "Nieuw Persoon" },
        },
        new[] { new GoogleGroup { Id = "gg-1", Email = "team@test.be", Name = "Team", CountMembers = 2 } },
        new[] { new GoogleOrgUnit { Path = "/Zorg", Name = "Zorg", ParentPath = "/" } });

    [Fact]
    public void GoogleIsReadOnlyWithItsSettingsAndBecomesASnapshot()
    {
        var (hub, factories, _) = Hub("g1-google");
        using var _ = hub;
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Both | TenantSystems.GoogleWorkspace });
        var s = hub.Current;
        var fake = factories[hub.CurrentTenant.Id].GoogleFake = new FakeGoogleConnection(GoogleSample());

        // Without the domain and the client: nothing to read, and the screen says what to fill in
        Assert.False(s.StartLoading(LoadKind.Google));
        Assert.NotNull(s.Error);

        // Everything at once: Google in its own lane next to AD and Entra
        s.Settings.TenantId = "tenant";
        s.Settings.ClientId = "client";
        s.Settings.GoogleDomain = "test.be";
        s.Settings.GoogleClientId = "client-1.apps.googleusercontent.com";
        Assert.True(s.StartLoading(LoadKind.Everything));
        Assert.True(SpinWait.SpinUntil(() => !s.TaskBusy, TimeSpan.FromSeconds(20)));
        Assert.Equal(1, fake.Reads);
        Assert.Equal(2, s.Data.GoogleUsers.Count);
        Assert.Single(s.Data.GoogleGroups);
        Assert.Single(s.Data.GoogleOrgUnits);
        Assert.Contains(s.SnapshotList().Last().Tables, t => t == InventoryLoader.GoogleUsersFile);

        // The logs of Google kept locally, apart from those of Entra: the failure, not the normal sign-in
        var kept = s.KeptSignIns(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1), google: true);
        Assert.Equal("q1:0", Assert.Single(kept).SignIn.Id);
        Assert.Single(s.KeptAudits(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1), google: true));
        Assert.DoesNotContain(s.KeptSignIns(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)), k => k.SignIn.Id == "q1:0");
        Assert.Equal(1, s.GoogleLogsLast!.SignIns);

        // Linked to the person with the same address
        var cas = s.Data.Persons.Single(p => p.Ad?.SamAccountName == "cas.vos");
        Assert.Equal("g-cas", cas.Google?.Id);

        // Shown on the page of the person and in Statistics, with this state
        using (var ctx = new Bunit.TestContext())
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddSingleton(s);
            ctx.Services.AddSingleton<IConnectionFactory>(factories[hub.CurrentTenant.Id]);
            ctx.Services.AddSingleton<IAppEnvironment>(Services.GetRequiredService<IAppEnvironment>());
            var page = ctx.RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key)).Markup;
            Assert.Contains("/Zorg", page, StringComparison.Ordinal);
            Assert.Contains("gekoppeld via het adres", page, StringComparison.Ordinal);
            var statistics = ctx.RenderComponent<Statistics>().Markup;
            Assert.Contains("Google-accounts", statistics, StringComparison.Ordinal);
            Assert.Contains("Zonder verificatie in twee stappen", statistics, StringComparison.Ordinal);
        }

        // A tenant without Google never reads it, also not with its settings
        hub.Update(hub.CurrentTenant with { Systems = TenantSystems.Both });
        s = hub.Current;
        Assert.False(s.EffectiveSettings.GoogleFilled);
        Assert.Empty(s.Data.GoogleUsers);
    }

    [Fact]
    public void ConnectionsAsksForTheGoogleProjectOnlyWithGoogle()
    {
        Assert.DoesNotContain("OAuth-client-ID", RenderComponent<Moss365.UI.Pages.Connections>().Markup, StringComparison.Ordinal);
    }
}
