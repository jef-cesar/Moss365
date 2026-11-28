using Moss365.Connections.Google;
using Moss365.Core.Changes;
using Moss365.Core.Google;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>U41: the wizard for Google Workspace with gcloud on this PC.</summary>
public sealed class GoogleSetupTests
{
    private static readonly string[] Gmail = { GoogleParts.GmailSettings };

    private static GoogleCheck SignIn(bool ok) => new(Moss365.Core.Localization.Texts.T["google.check.sign-in"], ok, ok ? "beheer@example.com" : "geweigerd");

    [Fact]
    public void ANewProjectIsAPlanAndTheStepsByHandStayOpenUntilChecked()
    {
        var gcloud = new GcloudState(true, "beheer@example.com", false, null, null);
        var a = GoogleSetup.Assess(new GoogleSetupInput("moss365-contoso", gcloud, false, false, Gmail, null, null));
        Assert.Equal(new[] { ChangeAction.GcloudCreateProject, ChangeAction.GcloudEnableServices, ChangeAction.GcloudCreateServiceAccount, ChangeAction.GcloudCreateServiceAccountKey },
            a.Plan.Select(w => w.ChangeAction));
        var apis = a.Plan[1];
        Assert.Equal("admin.googleapis.com gmail.googleapis.com", apis.P("apis"));
        Assert.Equal(a.Plan[0].Id, apis.DependentOf);
        Assert.Equal(a.Plan[2].Id, a.Plan[3].DependentOf);
        Assert.NotNull(a.Plan[3].Warning);
        Assert.All(a.Plan, w => Assert.Equal(("moss365-contoso", Moss365.Core.Permissions.Permission.Settings), (w.Target, Moss365.Core.Permissions.PermissionTable.ForAction(w.ChangeAction))));
        Assert.Contains(a.Items, i => i.Status == SetupStatus.Missing && i.Explanation.Contains("Intern", StringComparison.Ordinal));
        Assert.All(a.Plan, w => Assert.NotNull(Undo.WhyNot(new LogEntry(DateTimeOffset.Now, "b", "c", "Google", w.ChangeAction.ToString(), w.Target, "", "x", w.Parameters, "Succeeded", ""))));
        Assert.Equal(Platform.GoogleWorkspace, Platforms.Of(ChangeAction.GcloudCreateProject));
    }

    [Fact]
    public void WhatIsThereIsInOrderAndOnlyTheDifferenceIsPlanned()
    {
        var gcloud = new GcloudState(true, "beheer@example.com", true, new[] { "admin.googleapis.com" }, "1234567890");
        var key = new GmailKeyInfo(GoogleSetup.ServiceAccountEmail("moss365-contoso"), "1234567890", "kid-1", "moss365-contoso");
        var checks = new[] { SignIn(true), new GoogleCheck(Moss365.Core.Localization.Texts.T["gmail.check"], false, "unauthorized_client") };
        var a = GoogleSetup.Assess(new GoogleSetupInput("moss365-contoso", gcloud, true, true, Gmail, key, checks));
        var step = Assert.Single(a.Plan);
        Assert.Equal((ChangeAction.GcloudEnableServices, "gmail.googleapis.com"), (step.ChangeAction, step.P("apis")));
        Assert.Contains(a.Items, i => i.Status == SetupStatus.Missing && i.Explanation.Contains("1234567890", StringComparison.Ordinal) && i.Explanation.Contains("gmail.settings.sharing", StringComparison.Ordinal));
        Assert.Equal(2, a.Items.Count(i => i.Status == SetupStatus.InOrder && i.Step == Moss365.Core.Localization.Texts.T["google-setup.step.client"]));

        // Without gcloud signed in nothing is planned; without gcloud the wizard says how to install it
        Assert.Empty(GoogleSetup.Assess(new GoogleSetupInput("moss365-contoso", gcloud with { Account = null }, true, true, Gmail, null, null)).Plan);
        Assert.Contains("winget", GoogleSetup.Assess(new GoogleSetupInput("moss365-contoso", new GcloudState(false, null, null, null, null), false, false, Gmail, null, null)).Items[0].Explanation, StringComparison.Ordinal);
        Assert.Contains(GoogleSetup.Assess(new GoogleSetupInput("Moss_365", gcloud, false, false, Gmail, null, null)).Items, i => i.Status == SetupStatus.Missing);
        // Without the part Gmail settings: no service account
        Assert.DoesNotContain(GoogleSetup.Assess(new GoogleSetupInput("moss365-contoso", gcloud with { ServiceAccountClientId = null }, true, true, Array.Empty<string>(), null, null)).Plan,
            w => w.ChangeAction == ChangeAction.GcloudCreateServiceAccount);
    }

    /// <summary>A gcloud that answers what a project with the Admin SDK on says, and records what it was asked.</summary>
    private sealed class FakeGcloud
    {
        public List<string> Asked { get; } = new();
        public bool Installed { get; set; } = true;
        public string? WrittenKey { get; private set; }

        public Task<GcloudRun> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancel)
        {
            if (!Installed)
            {
                throw new System.ComponentModel.Win32Exception("gcloud");
            }

            var line = string.Join(' ', args);
            Asked.Add(line);
            if (line.StartsWith("iam service-accounts keys create", StringComparison.Ordinal))
            {
                WrittenKey = args[4];
                File.WriteAllText(args[4], """{"type":"service_account","client_email":"x@example.com"}""");
            }

            return Task.FromResult(line switch
            {
                var l when l.StartsWith("auth list", StringComparison.Ordinal) => new GcloudRun(0, "beheer@example.com\n", ""),
                var l when l.StartsWith("projects describe", StringComparison.Ordinal) => new GcloudRun(0, "moss365-contoso\n", ""),
                var l when l.StartsWith("services list", StringComparison.Ordinal) => new GcloudRun(0, "admin.googleapis.com\ncompute.googleapis.com\n", ""),
                var l when l.StartsWith("iam service-accounts describe", StringComparison.Ordinal) => new GcloudRun(1, "", "ERROR: (gcloud.iam.service-accounts.describe) NOT_FOUND"),
                var l when l.StartsWith("projects create", StringComparison.Ordinal) => new GcloudRun(1, "", "ERROR: (gcloud.projects.create) Project already exists"),
                _ => new GcloudRun(0, "", ""),
            });
        }
    }

    [Fact]
    public async Task GcloudGetsOnlyFixedCommandsAndTheKeyNeverStaysOnDisk()
    {
        var fake = new FakeGcloud();
        var gcloud = new GcloudSetup(fake.RunAsync);
        var state = await gcloud.ReadAsync("moss365-contoso", CancellationToken.None);
        Assert.Equal((true, "beheer@example.com", (bool?)true, (string?)null), (state.Installed, state.Account, state.ProjectExists, state.ServiceAccountClientId));
        Assert.Contains("admin.googleapis.com", state.EnabledApis!);

        Change Step(ChangeAction a, params (string, string)[] p) => new()
        {
            TargetSystem = TargetSystem.Google, ChangeAction = a, Target = "moss365-contoso", Description = "x",
            Parameters = p.ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase),
        };
        var key = Step(ChangeAction.GcloudCreateServiceAccountKey);
        var results = await gcloud.ExecuteAsync(new[]
        {
            Step(ChangeAction.GcloudCreateProject, ("naam", "Moss365")),
            Step(ChangeAction.GcloudEnableServices, ("apis", "gmail.googleapis.com")),
            Step(ChangeAction.GcloudEnableServices, ("apis", "compute.googleapis.com")),
            Step(ChangeAction.GcloudCreateServiceAccount),
            key,
        }, CancellationToken.None);
        Assert.Equal(new[] { ResultStatus.Succeeded, ResultStatus.Succeeded, ResultStatus.Failed, ResultStatus.Succeeded, ResultStatus.Succeeded }, results.Select(r => r.Status));
        Assert.Contains("projects create moss365-contoso --name=Moss365", fake.Asked);
        Assert.Contains("services enable gmail.googleapis.com --project=moss365-contoso", fake.Asked);
        Assert.Contains("iam service-accounts create moss365-gmail --display-name=Moss365 Gmail settings --project=moss365-contoso", fake.Asked);
        Assert.Contains("service_account", key.Secret, StringComparison.Ordinal);
        Assert.False(File.Exists(fake.WrittenKey));
        Assert.False(Directory.Exists(Path.GetDirectoryName(fake.WrittenKey)));

        // A project id that is not one never reaches gcloud; neither does gcloud that is not installed
        Assert.Equal(ResultStatus.Failed, (await gcloud.ExecuteAsync(new[] { new Change { TargetSystem = TargetSystem.Google, ChangeAction = ChangeAction.GcloudCreateProject, Target = "x & del", Description = "x" } }, CancellationToken.None))[0].Status);
        Assert.DoesNotContain(fake.Asked, a => a.Contains('&'));
        Assert.False((await new GcloudSetup(new FakeGcloud { Installed = false }.RunAsync).ReadAsync("moss365-contoso", CancellationToken.None)).Installed);
    }
}
