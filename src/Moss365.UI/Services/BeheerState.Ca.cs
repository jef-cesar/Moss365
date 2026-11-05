using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Opslag;
using Moss365.Core.Wijzigingen;

namespace Moss365.UI.Services;

/// <summary>Conditional Access beheren (0.9): back-ups, het auditlogboek en de gegevens voor de veiligheidscontrole.</summary>
public sealed partial class BeheerState
{
    /// <summary>Waar de back-ups van Conditional Access staan (lokaal; ze bevatten id's van personen en groepen).</summary>
    public string CaBackupMap => Path.Combine(_omgeving.GegevensMap, CaBackup.MapNaam);

    /// <summary>Na het live lezen: een back-up als er iets veranderde sinds de vorige. Mislukken stopt het laden niet.</summary>
    private void BewaarCaBackup(CaGegevens ca, IProgress<string> p)
    {
        try
        {
            if (CaBackup.Bewaar(ca, CaBackupMap, DateTime.Now) is { } pad)
            {
                p.Report($"Back-up van Conditional Access bewaard ({Path.GetFileName(pad)}).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            p.Report("Back-up van Conditional Access niet bewaard: " + ex.Message);
            Log.Warning("Back-up van Conditional Access mislukt: {Fout}", ex.Message);
        }
    }

    public IReadOnlyList<CaBackupBestand> CaBackups() => CaBackup.Lijst(CaBackupMap);

    /// <summary>Wie wijzigde Conditional Access (auditlogboek van Entra); null als Graph niet ingesteld is.</summary>
    public async Task<IReadOnlyList<CaAuditRegel>?> LeesCaAuditAsync(int dagen, CancellationToken annuleren = default)
    {
        if (Graph() is not { } graph)
        {
            return null;
        }

        Toegang("Auditlogboek Conditional Access bekeken", $"{dagen} dagen");
        return await graph.LeesCaAuditAsync(dagen, annuleren);
    }

    /// <summary>De gebruiker die de wijziging doet (UPN), voor de controle "jezelf niet buitensluiten".</summary>
    public string EigenUpn => Gebruiker;
}
