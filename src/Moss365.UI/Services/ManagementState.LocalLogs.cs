using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Security;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// The sign-in and audit logs kept locally (R18, U19): after every read of Entra (also without a window), the failed,
/// risky, administrator and emergency account sign-ins and the directory audit log go to the tenant database, for
/// <see cref="RetentionSettings.LocalLogDays"/> days. The technical log only gets the counts.
/// </summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions LocalLogsJson = new JsonSerializerOptions().WithLegacyNames();

    /// <summary>The last collection, or null when there was none yet.</summary>
    public LocalLogsRun? LocalLogsLast
    {
        get
        {
            try
            {
                return Database.ReadDocument(LocalLogs.Document) is { } json ? JsonSerializer.Deserialize<LocalLogsRun>(json, LocalLogsJson) : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Reads what is new since the last collection and keeps it. A part that cannot be read (licence,
    /// permission) gives a message; the other part is still kept.</summary>
    internal async Task<LocalLogsRun> CollectLocalLogsAsync(IGraphConnection graph, IProgress<string>? progress, CancellationToken cancel)
    {
        var now = DateTimeOffset.Now;
        var messages = new List<string>();
        var signIns = 0;
        var audits = 0;
        try
        {
            var admins = LocalLogs.Admins(_data.Security);
            var emergency = Config.EmergencyAccounts.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var emergencyIds = _data.EntraUsers.Where(u => emergency.Contains(u.UserPrincipalName)).Select(u => u.Id);
            var read = await graph.ReadSignInsToKeepAsync(LocalLogs.Since(Database.LastLocal(LocalLogs.SignInKind), now), admins.Concat(emergencyIds).ToList(), LocalLogs.MaxPerRun, cancel);
            signIns = Database.KeepSignIns(LocalLogs.ToKeep(read, admins, emergency));
        }
        catch (NoPermissionError ex)
        {
            messages.Add(Texts.T.Format("local-logs.no-sign-ins", ex.Message));
        }

        try
        {
            var read = await graph.ReadDirectoryAuditsAsync(LocalLogs.Since(Database.LastLocal(LocalLogs.AuditKind), now), LocalLogs.MaxPerRun, cancel);
            audits = Database.KeepAudits(read);
        }
        catch (NoPermissionError ex)
        {
            messages.Add(Texts.T.Format("local-logs.no-audit", ex.Message));
        }

        var run = new LocalLogsRun(now, signIns, audits, messages.Count == 0 ? null : string.Join(" ", messages));
        Database.SaveDocument(LocalLogs.Document, JsonSerializer.Serialize(run, LocalLogsJson));
        Log.Information("Lokale logboeken: {Aanmeldingen} aanmeldingen en {Audit} auditregels bewaard", signIns, audits);
        progress?.Report(run.Message ?? Texts.T.Format("local-logs.kept", signIns, audits));
        return run;
    }

    /// <summary>The sign-ins kept between two times, newest first (the screen writes the access log).</summary>
    public IReadOnlyList<KeptSignIn> KeptSignIns(DateTimeOffset from, DateTimeOffset until) => Database.ReadSignIns(from, until);

    /// <summary>The audit lines kept between two times, newest first.</summary>
    public IReadOnlyList<AuditEvent> KeptAudits(DateTimeOffset from, DateTimeOffset until) => Database.ReadAudits(from, until);
}
