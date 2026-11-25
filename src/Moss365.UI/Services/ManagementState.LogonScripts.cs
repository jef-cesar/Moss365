using Moss365.Core.Changes;
using Moss365.Core.LogonScripts;

namespace Moss365.UI.Services;

/// <summary>Logon scripts (R9, 0.32): the NETLOGON share of the domain, and the versions from the change log.</summary>
public sealed partial class ManagementState
{
    /// <summary>The share, or null (no AD, not on Windows, no domain controller filled in).</summary>
    public ILogonScriptShare? LogonScriptShare() => HasAd ? _factory?.LogonScripts(EffectiveSettings) : null;

    /// <summary>The scripts in the share, or the reason why they cannot be read.</summary>
    public (IReadOnlyList<LogonScriptFile> Files, string? Error) ReadLogonScripts()
    {
        if (LogonScriptShare() is not { } share)
        {
            return (Array.Empty<LogonScriptFile>(), "NETLOGON is niet bereikbaar: vul bij Verbindingen de domeincontroller in (enkel in de Windows-app).");
        }

        try
        {
            return (share.List(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (Array.Empty<LogonScriptFile>(), $"{share.Location} is niet leesbaar: {ex.Message}");
        }
    }

    /// <summary>The saved versions of a script in this app (newest first), from the change log.</summary>
    public IReadOnlyList<LogEntry> LogonScriptVersions(string name)
        => ChangeLog.Recent(5000)
            .Where(r => r.Succeeded && r.ChangeAction == nameof(ChangeAction.AdWriteLogonScript)
                        && string.Equals(LogonScriptAnalysis.Normalise(r.Target), LogonScriptAnalysis.Normalise(name), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Time)
            .ToList();
}
