using Moss365.Core.Localization;
using Moss365.Core.Changes;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// Updating the app (R7): the channel and source live in moss365.db (they belong to this installation, not to a tenant).
/// Never while a plan, task or script runs or something is still open; before installing, a copy of every database.
/// </summary>
public sealed class AppUpdates(IAppUpdater? updater, TenantHub hub)
{
    private const string ChannelKey = "update.channel";
    private const string SourceKey = "update.source";

    /// <summary>The updater exists (the Windows app) and this copy was installed with Setup.exe.</summary>
    public bool CanUpdate => updater?.Installed == true;

    public UpdateSettings Settings
    {
        get
        {
            var channel = hub.App.Setting(ChannelKey) switch
            {
                "stable" => UpdateChannel.Stable,
                "preview" => UpdateChannel.Preview,
                _ => UpdateSettings.DefaultChannel(ManagementState.AppVersion),
            };
            return new UpdateSettings(channel, hub.App.Setting(SourceKey));
        }
    }

    /// <summary>Saves the channel and the source; the open tenants follow the channel for the "new version" notice.</summary>
    public void Save(UpdateSettings settings)
    {
        hub.App.SetSetting(ChannelKey, settings.ChannelName);
        hub.App.SetSetting(SourceKey, string.IsNullOrWhiteSpace(settings.Source) ? null : settings.Source.Trim());
        foreach (var s in hub.Opened)
        {
            s.UpdateChannel = settings.Channel;
        }

        Found = null;
    }

    /// <summary>The version found by the last search (or chosen to go back to), or null.</summary>
    public AvailableVersion? Found { get; private set; }

    /// <summary>Why the last search or update did not work, or null.</summary>
    public string? Message { get; private set; }

    /// <summary>Why the app cannot update right now (empty: it can).</summary>
    public IReadOnlyList<string> Blockers()
    {
        var output = new List<string>();
        foreach (var s in hub.Opened)
        {
            if (s.TaskBusy)
            {
                output.Add(Texts.T.Format("code.app-updates.a-task-is-running", s.Tenant.Name));
            }

            if (s.ScriptBusy)
            {
                output.Add(Texts.T.Format("code.app-updates.a-script-is-running", s.Tenant.Name));
            }

            if (s.Pending.Everything.Count > 0)
            {
                output.Add(Texts.T.Format("code.app-updates.something-is-still-open-a", s.Tenant.Name, s.Pending.Everything.Count));
            }
        }

        return output;
    }

    public async Task<AvailableVersion?> CheckAsync(CancellationToken cancel = default)
    {
        Message = null;
        Found = null;
        if (!CanUpdate)
        {
            Message = Texts.T["code.app-updates.this-copy-was-not-installed"];
            return null;
        }

        try
        {
            Found = await updater!.CheckAsync(Settings, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = (Texts.T["code.app-updates.search-failed"] + " ") + ex.Message;
        }

        return Found;
    }

    public async Task<IReadOnlyList<string>> OlderVersionsAsync(CancellationToken cancel = default)
    {
        try
        {
            return CanUpdate ? await updater!.OlderVersionsAsync(Settings, ManagementState.AppVersion, cancel) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = (Texts.T["code.app-updates.reading-the-previous-versions-failed"] + " ") + ex.Message;
            return Array.Empty<string>();
        }
    }

    /// <summary>Chooses an older version to go back to.</summary>
    public async Task<AvailableVersion?> ChooseAsync(string version, CancellationToken cancel = default)
    {
        Message = null;
        try
        {
            Found = CanUpdate ? await updater!.FindAsync(Settings, version, cancel) : null;
            Message = Found is null ? Texts.T.Format("code.app-updates.version-is-not-in-this", version) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Mislukt: " + ex.Message;
        }

        return Found;
    }

    /// <summary>
    /// Downloads the version found (its fingerprint is checked), copies every database, writes it to the change log of
    /// the open tenants and installs; the app closes and starts again. Returns why not, when it cannot.
    /// </summary>
    public async Task<string?> InstallAsync(IProgress<int>? progress = null, CancellationToken cancel = default)
    {
        if (Found is not { } version || updater is null)
        {
            return Texts.T["code.app-updates.search-for-a-version-first"];
        }

        if (Blockers() is { Count: > 0 } blockers)
        {
            return string.Join(" ", blockers);
        }

        try
        {
            await updater.DownloadAsync(version, progress, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (Texts.T["code.app-updates.download-failed-nothing-was-changed"] + " ") + ex.Message;
        }

        // Checked once more: something may have started during the download
        if (Blockers() is { Count: > 0 } late)
        {
            return string.Join(" ", late);
        }

        var copies = hub.CopyBeforeUpdate();
        foreach (var s in hub.Opened)
        {
            s.ChangeLog.Write(new LogEntry(DateTimeOffset.Now, s.User, "Moss365 bijwerken", "Deze pc", "UpdateApp", version.Version, "Moss365",
                $"Moss365 {(version.Downgrade ? "terugzetten" : "bijwerken")} van {ManagementState.AppVersion} naar {version.Version}",
                new Dictionary<string, string> { ["sha256"] = version.Sha256 ?? string.Empty, ["kopieën"] = copies.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                nameof(ResultStatus.Succeeded), string.Empty));
            s.Log.Information("Moss365 bijwerken naar {Versie}", version.Version);
        }

        updater.ApplyAndRestart(version);
        return null;
    }
}
