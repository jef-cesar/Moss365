using System.Text.Json;
using Moss365.Core.Localization;
using Moss365.Core.Scheduling;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// Reading without a window (R18, U19): the settings per tenant, the read itself (AD and Entra into a snapshot, and the
/// logs kept locally; never a write) and how the last one went. The task in the Windows Task Scheduler starts it.
/// </summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions UnattendedJson = new JsonSerializerOptions().WithLegacyNames();

    /// <summary>A read without a window is running: AD with a password and Microsoft 365 without "stay signed in"
    /// are left out.</summary>
    private bool _readingWithoutWindow;

    /// <summary>This state belongs to a read without a window (no automatic refresh, no search for updates).</summary>
    public bool WithoutWindow { get; }

    /// <summary>AD is read with a separate account, whose password is only typed in the app.</summary>
    public bool AdNeedsPassword => !string.IsNullOrWhiteSpace(EffectiveSettings.AdUser);

    public UnattendedSettings UnattendedSettings => ReadJson<UnattendedSettings>(Unattended.SettingsDocument) ?? new UnattendedSettings(false, Unattended.DefaultHour);

    /// <summary>How the last read without a window went, or null.</summary>
    public UnattendedRun? UnattendedLast => ReadJson<UnattendedRun>(Unattended.RunDocument);

    public void SaveUnattended(UnattendedSettings settings)
        => Database.SaveDocument(Unattended.SettingsDocument, JsonSerializer.Serialize(settings with { Hour = Math.Clamp(settings.Hour, 0, 23) }, UnattendedJson));

    public UnattendedCheck CheckUnattended(EncryptionMethod encryption)
        => Unattended.Check(HasAd, EffectiveSettings.AdFilled, AdNeedsPassword, HasMicrosoft365, EffectiveSettings.GraphFilled, EffectiveSettings.RememberSignIn, encryption);

    /// <summary>
    /// Reads AD and Entra as "AD en Entra live laden" does, waits until it is done (at most <see cref="Unattended.Limit"/>)
    /// and records how it went. Nothing is written to AD, Entra or Exchange, and no plan or scheduled action runs.
    /// </summary>
    public async Task<UnattendedRun> RunUnattendedAsync(EncryptionMethod encryption, CancellationToken cancel = default)
    {
        var start = DateTimeOffset.Now;
        var check = CheckUnattended(encryption);
        UnattendedRun run;
        if (!check.CanRun)
        {
            run = new UnattendedRun(start, DateTimeOffset.Now, UnattendedResult.Skipped, check.Notes);
        }
        else
        {
            _readingWithoutWindow = true;
            try
            {
                if (!StartLoading(LoadKind.AdAndEntra))
                {
                    run = new UnattendedRun(start, DateTimeOffset.Now, UnattendedResult.Failed, check.Notes.Append(Error ?? Texts.T["unattended.not-started"]).ToList());
                }
                else
                {
                    var until = DateTime.Now + Unattended.Limit;
                    while (Job is { Busy: true } && DateTime.Now < until)
                    {
                        await Task.Delay(200, cancel);
                    }

                    var steps = Job?.Steps ?? Array.Empty<TaskStep>();
                    var failed = steps.Where(s => s.Status is StepStatus.Error or StepStatus.Cancelled or StepStatus.Busy or StepStatus.Wait).ToList();
                    run = new UnattendedRun(start, DateTimeOffset.Now, failed.Count == 0 ? UnattendedResult.Done : UnattendedResult.DoneWithErrors,
                        check.Notes.Concat(failed.Select(s => $"{s.Name}: {s.Message}")).ToList());
                }
            }
            finally
            {
                _readingWithoutWindow = false;
            }
        }

        Database.SaveDocument(Unattended.RunDocument, JsonSerializer.Serialize(run, UnattendedJson));
        Log.Information("Lezen zonder venster: {Resultaat} in {Seconden:0} s", run.Result, (run.End - run.Start).TotalSeconds);
        return run;
    }

    private T? ReadJson<T>(string document) where T : class
    {
        try
        {
            return Database.ReadDocument(document) is { } json ? JsonSerializer.Deserialize<T>(json, UnattendedJson) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
