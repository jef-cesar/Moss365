using Moss365.Core.Localization;
using Moss365.Core.Devices;

namespace Moss365.UI.Services;

/// <summary>Devices, Teams and SharePoint (R13, 0.36): read on demand, kept while the app is open.</summary>
public sealed partial class ManagementState
{
    private IReadOnlyList<AdComputer>? _adComputers;
    private IReadOnlyList<IntuneDevice>? _intuneDevices;
    private IReadOnlyList<EntraDevice>? _entraDevices;

    // The devices on a date in the past (R17): from the snapshot shown, kept while that one is shown
    private (long Snapshot, (IReadOnlyList<AdComputer>? Ad, IReadOnlyList<EntraDevice>? Entra, IReadOnlyList<IntuneDevice>? Intune) Devices)? _devicesAt;

    private (IReadOnlyList<AdComputer>? Ad, IReadOnlyList<EntraDevice>? Entra, IReadOnlyList<IntuneDevice>? Intune) HistoricDevices()
    {
        if (AsOf is not { } s)
        {
            return (null, null, null);
        }

        if (_devicesAt is not { } kept || kept.Snapshot != s.Id)
        {
            kept = (s.Id, DeviceSnapshot.Read(Database.TablesAt(s.Id), s.Time));
            _devicesAt = kept;
        }

        return kept.Devices;
    }

    /// <summary>The computers in AD as last read in this session, or on the date shown in the past.</summary>
    public IReadOnlyList<AdComputer>? AdComputers => IsHistoric ? HistoricDevices().Ad : _adComputers;

    public IReadOnlyList<IntuneDevice>? IntuneDevices => IsHistoric ? HistoricDevices().Intune : _intuneDevices;

    public IReadOnlyList<EntraDevice>? EntraDevices => IsHistoric ? HistoricDevices().Entra : _entraDevices;

    /// <summary>One list over AD, Entra and Intune (R17), from what was read (on the date shown, in the past).</summary>
    public IReadOnlyList<DeviceRecord> DeviceList => DeviceAnalysis.Combine(AdComputers, EntraDevices, IntuneDevices, AsOf?.Time ?? DateTimeOffset.Now);

    public CollaborationReading? Collaboration { get; private set; }

    /// <summary>What did not work in the last read of devices or collaboration (shown on the screen).</summary>
    public string? DevicesNote { get; private set; }

    public async Task ReadDevicesAsync(CancellationToken cancel = default)
    {
        if (IsHistoric)
        {
            return;
        }

        var notes = new List<string>();
        if (HasAd && _factory?.Ad(EffectiveSettings, AdPassword) is { } ad)
        {
            try
            {
                _adComputers = await ad.ReadComputersAsync(cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add((Texts.T["code.management-state-devices.computers-in-ad-not-read"] + " ") + ex.Message);
            }
        }

        if (HasMicrosoft365 && Graph() is { } graph)
        {
            try
            {
                _intuneDevices = await graph.ReadIntuneDevicesAsync(cancel);
                if (_intuneDevices is null)
                {
                    notes.Add(Texts.T["code.management-state-devices.intune-cannot-be-read-licence"]);
                }

                _entraDevices = await graph.ReadEntraDevicesAsync(cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add((Texts.T["code.management-state-devices.intune-not-read"] + " ") + ex.Message);
            }
        }

        DevicesNote = notes.Count == 0 ? null : string.Join(" ", notes);
        KeepDevices();
        Changed?.Invoke();
    }

    /// <summary>The devices read become a snapshot (only what changed is stored), and their counts are kept per day for
    /// the trend on the overview.</summary>
    private void KeepDevices()
    {
        var tables = DeviceSnapshot.Tables(_adComputers, _entraDevices, _intuneDevices, DateTimeOffset.Now);
        if (tables.Count == 0)
        {
            return;
        }

        try
        {
            Database.SaveSnapshot(DateTimeOffset.Now, Texts.T["menu.toestellen"], tables);
            SaveOverviewCounts();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            Log.Warning("Toestellen bewaren in een momentopname mislukt: {Fout}", ex.Message);
        }
    }

    public async Task ReadCollaborationAsync(CancellationToken cancel = default)
    {
        DevicesNote = null;
        if (HasMicrosoft365 && Graph() is { } graph)
        {
            try
            {
                Collaboration = await graph.ReadCollaborationAsync(cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DevicesNote = (Texts.T["code.management-state-devices.teams-and-sharepoint-not-read"] + " ") + ex.Message;
            }
        }

        Changed?.Invoke();
    }
}
