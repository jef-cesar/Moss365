using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.Core.Changes;

namespace Moss365.UI.Services;

/// <summary>Managing Conditional Access (0.9): backups, the audit log and the data for the safety check.</summary>
public sealed partial class ManagementState
{
    /// <summary>Where the Conditional Access backups are (local; they contain ids of people and groups).</summary>
    public string CaBackupFolder => Path.Combine(_environment.DataFolder, CaBackup.FolderName);

    /// <summary>After the live read: a backup when something changed since the previous one. A failure does not stop
    /// the loading.</summary>
    private void SaveCaBackup(CaData ca, IProgress<string> p)
    {
        try
        {
            if (CaBackup.Save(ca, CaBackupFolder, DateTime.Now) is { } path)
            {
                p.Report($"Back-up van Conditional Access bewaard ({Path.GetFileName(path)}).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            p.Report("Back-up van Conditional Access niet bewaard: " + ex.Message);
            Log.Warning("Back-up van Conditional Access mislukt: {Fout}", ex.Message);
        }
    }

    public IReadOnlyList<CaBackupFile> CaBackups() => CaBackup.Items(CaBackupFolder);

    /// <summary>Who changed Conditional Access (Entra audit log); null when Graph is not configured.</summary>
    public async Task<IReadOnlyList<CaAuditEntry>?> ReadCaAuditAsync(int days, CancellationToken cancel = default)
    {
        if (Graph() is not { } graph)
        {
            return null;
        }

        Access("Auditlogboek Conditional Access bekeken", $"{days} dagen");
        return await graph.ReadCaAuditAsync(days, cancel);
    }

    /// <summary>The user who makes the change (UPN), for the check "do not lock yourself out".</summary>
    public string OwnUpn => User;
}
