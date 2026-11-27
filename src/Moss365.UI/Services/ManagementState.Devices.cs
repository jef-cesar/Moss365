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

    private ComplianceReading? _compliance;

    /// <summary>Compliance per policy (R17, U26), as last read in this session. Not kept in the snapshots: on a date in the
    /// past there is none.</summary>
    public ComplianceReading? Compliance => IsHistoric ? null : _compliance;

    /// <summary>The part "clean up old devices in Entra" is on for this tenant (U25).</summary>
    public bool EntraCleanupOn => Core.Setup.PermissionParts.IsOn(Config.PermissionParts, Core.Setup.PermissionParts.EntraDeviceCleanup);

    /// <summary>The part "baseline compliance policies" is on for this tenant (U26).</summary>
    public bool CompliancePoliciesOn => Core.Setup.PermissionParts.IsOn(Config.PermissionParts, Core.Setup.PermissionParts.CompliancePolicies);

    /// <summary>The devices in Entra that may be deleted: disabled by Moss365 at least 90 days ago (the change log), or
    /// disabled elsewhere and not seen for 180 days.</summary>
    public IReadOnlyList<EntraDevice> EntraDevicesToDelete => DeviceCleanup.ToDelete(EntraDevices ?? Array.Empty<EntraDevice>(), ChangeLog.Recent(20_000), DateTimeOffset.Now);

    /// <summary>How many BitLocker keys each device has (the list of keys, never a key), for the warning in the plan that
    /// deletes them; a device whose keys cannot be counted counts as one, so the warning stays.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountBitLockerKeysAsync(IEnumerable<EntraDevice> devices, CancellationToken cancel = default)
    {
        var output = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (Graph() is not { } graph)
        {
            return output;
        }

        foreach (var d in devices.Where(d => d.DeviceId.Length > 0))
        {
            output[d.DeviceId] = await graph.CountBitLockerKeysAsync(d.DeviceId, cancel) ?? 1;
        }

        return output;
    }

    /// <summary>The compliance policies of one Intune device with the settings it fails (R17, the panel of a device), read
    /// live; null with a note when that cannot be read.</summary>
    public async Task<(IReadOnlyList<DeviceComplianceState>? States, string? Note)> ReadDeviceComplianceAsync(IntuneDevice device, CancellationToken cancel = default)
    {
        if (IsHistoric || !HasMicrosoft365 || Graph() is not { } graph)
        {
            return (null, Texts.T["device.compliance.not-now"]);
        }

        try
        {
            var states = await graph.ReadDeviceComplianceAsync(device.Id, cancel);
            return (states, states is null ? Texts.T["device.compliance.no-permission"] : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, Texts.T.Format("compliance.not-read", ex.Message));
        }
    }

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

            try
            {
                _compliance = await graph.ReadComplianceAsync(cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add(Texts.T.Format("compliance.not-read", ex.Message));
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
