using Bunit;
using Moss365.Core.Permissions;
using Moss365.Core.Scheduling;
using Moss365.Core.Storage;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R18 (U19): reading without a window only reads, and the settings page makes the task.</summary>
public sealed partial class PlanScreenTests
{
    private sealed class FakeScheduler : IUnattendedScheduler
    {
        public Dictionary<string, int> Tasks { get; } = new();
        public string? Fails { get; set; }

        public string? Plan(string tenantId, int hour)
        {
            if (Fails is not null)
            {
                return Fails;
            }

            Tasks[tenantId] = hour;
            return null;
        }

        public string? Remove(string tenantId)
        {
            Tasks.Remove(tenantId);
            return null;
        }

        public int? PlannedHour(string tenantId) => Tasks.TryGetValue(tenantId, out var h) ? h : null;
    }

    [Fact]
    public async Task AReadWithoutAWindowOnlyReadsAndRecordsHowItWent()
    {
        State.Settings.RememberSignIn = true;
        _factory.SignIns.Add(KeptSignIn("w1", "e-an", "an.peeters@test.be", error: 50126));

        var run = await State.RunUnattendedAsync(EncryptionMethod.WindowsAccount);

        Assert.Equal(UnattendedResult.Done, run.Result);
        Assert.Empty(run.Notes);
        Assert.Contains(State.Job!.Steps, s => s.Name == "AD lezen");
        Assert.Contains(State.Job.Steps, s => s.Name == "Aanmeldingen en auditlogboek lokaal bewaren");
        Assert.DoesNotContain(State.Job.Steps, s => s.Name.Contains("Exchange", StringComparison.Ordinal));
        Assert.Empty(_factory.Executed); // it never writes
        Assert.NotEmpty(State.Database.SnapshotList());
        Assert.Single(State.KeptSignIns(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1)));
        Assert.Equal(UnattendedResult.Done, State.UnattendedLast!.Result);

        // AD with its own account (the password is only typed in the app) waits for the app; Microsoft 365 is read
        State.Settings.AdUser = @"TEST\svc-moss";
        var cloud = await State.RunUnattendedAsync(EncryptionMethod.None);
        Assert.Equal(UnattendedResult.Done, cloud.Result);
        Assert.DoesNotContain(State.Job!.Steps, s => s.Name == "AD lezen");
        Assert.Contains(cloud.Notes, n => n.StartsWith("AD wordt gelezen met een apart account", StringComparison.Ordinal));

        // Without "stay signed in" nothing is left; with a password nothing at all
        State.Settings.RememberSignIn = false;
        Assert.Equal(UnattendedResult.Skipped, (await State.RunUnattendedAsync(EncryptionMethod.None)).Result);
        Assert.Equal(UnattendedResult.Skipped, (await State.RunUnattendedAsync(EncryptionMethod.Password)).Result);
        Assert.Equal(UnattendedResult.Skipped, State.UnattendedLast!.Result);
        Assert.Empty(_factory.Executed);
    }

    [Fact]
    public void ThePageMakesAndRemovesTheTask()
    {
        var scheduler = new FakeScheduler();
        ((FixedEnvironment)State.AppEnvironment).Scheduler = scheduler;
        State.Settings.RememberSignIn = true;
        var cut = RenderComponent<UnattendedPage>();

        Assert.Contains("AD en Microsoft 365", cut.Find("#voorwaarden").TextContent, StringComparison.Ordinal);
        Assert.Contains("Nog geen taak.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Nog niet gedraaid.", cut.Markup, StringComparison.Ordinal);
        cut.Find("#taak select").Change("5");
        cut.FindAll("#taak button").First(b => b.TextContent == "Taak maken").Click();
        Assert.Equal(5, scheduler.Tasks[State.Tenant.Id]);
        Assert.Equal(new UnattendedSettings(true, 5), State.UnattendedSettings);
        Assert.Contains("elke dag om 05:00", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("#taak button").First(b => b.TextContent == "Taak verwijderen").Click();
        Assert.Empty(scheduler.Tasks);
        Assert.False(State.UnattendedSettings.On);

        // Windows refuses: its message is shown
        scheduler.Fails = "Toegang geweigerd.";
        cut.FindAll("#taak button").First(b => b.TextContent == "Taak maken").Click();
        Assert.Contains("De taak maken is mislukt: Toegang geweigerd.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(scheduler.Tasks);

        Assert.Equal(Permission.Settings, Access.ForPath("lezen-zonder-venster"));
    }

    [Fact]
    public void WithoutTheWindowsAppThereIsNoTask()
    {
        var cut = RenderComponent<UnattendedPage>();
        Assert.Contains("Alleen in de Windows-app.", cut.Find("#taak").TextContent, StringComparison.Ordinal);
    }
}
