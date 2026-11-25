using Moss365.Core.Devices;

namespace Moss365.UI.Services;

/// <summary>Devices, Teams and SharePoint (R13, 0.36): read on demand, kept while the app is open.</summary>
public sealed partial class ManagementState
{
    public IReadOnlyList<AdComputer>? AdComputers { get; private set; }

    public IReadOnlyList<IntuneDevice>? IntuneDevices { get; private set; }

    public CollaborationReading? Collaboration { get; private set; }

    /// <summary>What did not work in the last read of devices or collaboration (shown on the screen).</summary>
    public string? DevicesNote { get; private set; }

    public async Task ReadDevicesAsync(CancellationToken cancel = default)
    {
        var notes = new List<string>();
        if (HasAd && _factory?.Ad(EffectiveSettings, AdPassword) is { } ad)
        {
            try
            {
                AdComputers = await ad.ReadComputersAsync(cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add("Computers in AD niet gelezen: " + ex.Message);
            }
        }

        if (HasMicrosoft365 && Graph() is { } graph)
        {
            try
            {
                IntuneDevices = await graph.ReadIntuneDevicesAsync(cancel);
                if (IntuneDevices is null)
                {
                    notes.Add("Intune is niet leesbaar (licentie of recht DeviceManagementManagedDevices.Read.All; voer Tenant inrichten opnieuw uit).");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add("Intune niet gelezen: " + ex.Message);
            }
        }

        DevicesNote = notes.Count == 0 ? null : string.Join(" ", notes);
        Changed?.Invoke();
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
                DevicesNote = "Teams en SharePoint niet gelezen: " + ex.Message;
            }
        }

        Changed?.Invoke();
    }
}
