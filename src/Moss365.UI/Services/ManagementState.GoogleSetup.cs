using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Permissions;
using Moss365.Core.Setup;
using Serilog;

namespace Moss365.UI.Services;

/// <summary>
/// The wizard for Google Workspace (U41), on the page Inrichten next to that of Microsoft 365: what gcloud on this PC and
/// the check of the project find, the plan of what gcloud can do, and the steps by hand that are checked afterwards.
/// </summary>
public sealed partial class ManagementState
{
    /// <summary>What gcloud said the last time (null: not read in this session).</summary>
    public GcloudState? GoogleCloud { get; private set; }

    /// <summary>The last check of the project in the wizard (null: not checked in this session).</summary>
    public IReadOnlyList<GoogleCheck>? GoogleSetupChecks { get; private set; }

    /// <summary>gcloud can be used from this app (not without a window).</summary>
    public bool GoogleSetupAvailable => HasGoogle && _factory?.GoogleSetup() is not null;

    public GoogleSetupInput GoogleSetupInput => new(
        Settings.GoogleProjectId, GoogleCloud, Settings.GoogleDomain.Trim().Length > 0,
        Settings.GoogleClientId.Trim().Length > 0 && Settings.GoogleClientSecret.Trim().Length > 0,
        GoogleParts, GmailKey, GoogleSetupChecks);

    public SetupAssessment? GoogleSetupAssessment => HasGoogle ? GoogleSetup.Assess(GoogleSetupInput) : null;

    /// <summary>Reads the project with gcloud and, with the OAuth client filled in, checks the project (signing in at
    /// Google); returns what went wrong, or null.</summary>
    public async Task<string?> ReadGoogleSetupAsync(IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (IsHistoric || _factory?.GoogleSetup() is not { } gcloud)
        {
            return Texts.T["google-setup.not-available"];
        }

        try
        {
            progress?.Report(Texts.T["google-setup.reading"]);
            GoogleCloud = await gcloud.ReadAsync(Settings.GoogleProjectId.Trim(), cancel);
            if (EffectiveSettings.GoogleFilled)
            {
                progress?.Report(Texts.T["google-setup.checking"]);
                GoogleSetupChecks = await CheckGoogleAsync(cancel);
            }

            Log.Information("Wizard Google: gelezen ({Items} punten)", GoogleSetupAssessment?.Items.Count ?? 0);
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning("Wizard Google: lezen mislukt ({Soort})", ex.GetType().Name);
            return ex.Message;
        }
    }

    /// <summary>Signs in to gcloud in the browser; returns what went wrong, or null.</summary>
    public async Task<string?> SignInGcloudAsync(CancellationToken cancel = default)
    {
        if (IsHistoric || _factory?.GoogleSetup() is not { } gcloud)
        {
            return Texts.T["google-setup.not-available"];
        }

        try
        {
            await gcloud.SignInAsync(cancel);
            return await ReadGoogleSetupAsync(null, cancel);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ex.Message;
        }
    }

    /// <summary>Keeps the project id and the OAuth client of the wizard with the connection settings.</summary>
    public void SaveGoogleSetupSettings(string projectId, string domain, string clientId, string? clientSecret)
    {
        Settings.GoogleProjectId = projectId.Trim();
        Settings.GoogleDomain = domain.Trim();
        Settings.GoogleClientId = clientId.Trim();
        if (clientSecret is { Length: > 0 })
        {
            Settings.GoogleClientSecret = clientSecret.Trim();
        }

        SaveSettings();
        GoogleSetupChecks = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Runs the plan of the Google wizard with gcloud (the role Instellingen, the change log), keeps a new key of the
    /// service account encrypted (and forgets it in the plan), then reads again.
    /// </summary>
    public async Task<IReadOnlyList<ChangeResult>> ExecuteGoogleSetupAsync(IReadOnlyList<Change> plan, string context, IProgress<string>? progress, CancellationToken cancel = default)
    {
        if (_factory?.GoogleSetup() is not { } gcloud)
        {
            throw new InvalidOperationException(Texts.T["google-setup.not-available"]);
        }

        if (IsHistoric)
        {
            throw new InvalidOperationException(Texts.T["code.management-state-library.you-are-looking-at-a"]);
        }

        var roles = Roles;
        var results = await new PlanExecutor(new IExecutor[] { gcloud }, ChangeLog, () => GoogleCloud?.Account ?? User, w => PermissionTable.Refusal(roles, w))
            .ExecuteAsync(plan, context, progress, cancel);
        Log.Information("Wizard Google: plan uitgevoerd: {Gelukt} gelukt, {Mislukt} mislukt", results.Count(r => r.Status == ResultStatus.Succeeded), results.Count(r => r.Status == ResultStatus.Failed));

        foreach (var w in plan.Where(w => w.ChangeAction == ChangeAction.GcloudCreateServiceAccountKey))
        {
            if (w.Secret is { Length: > 0 } key && results.Any(r => r.ChangeId == w.Id && r.Status == ResultStatus.Succeeded))
            {
                SetPermissionPart(Core.Setup.GoogleParts.GmailSettings, true);
                if (SaveGmailKey(key) is { } error)
                {
                    Log.Warning("Wizard Google: sleutel niet bewaard");
                    progress?.Report(error);
                }
            }

            w.Secret = null;
        }

        await ReadGoogleSetupAsync(progress, cancel);
        return results;
    }
}
