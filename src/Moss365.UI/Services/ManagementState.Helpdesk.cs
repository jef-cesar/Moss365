using Moss365.Core.Localization;
using Moss365.Core.Helpdesk;

namespace Moss365.UI.Services;

/// <summary>Helpdesk (R10, 0.33): BitLocker and LAPS lookups and the message trace, each written to the access log.</summary>
public sealed partial class ManagementState
{
    public async Task<IReadOnlyList<DeviceInfo>> FindDevicesAsync(string name, CancellationToken cancel = default)
        => Graph() is { } graph ? await graph.FindDevicesAsync(name, cancel) : Array.Empty<DeviceInfo>();

    /// <summary>BitLocker keys from Entra (by device or by key id) and from AD (by computer name); written to the access log.</summary>
    public async Task<(IReadOnlyList<BitLockerKey> Keys, string? Note)> BitLockerAsync(DeviceInfo? device, string? keyId, string? computerName, CancellationToken cancel = default)
    {
        var keys = new List<BitLockerKey>();
        string? note = null;
        if (Graph() is { } graph && (device is not null || !string.IsNullOrWhiteSpace(keyId)))
        {
            keys.AddRange(await graph.BitLockerKeysAsync(device?.DeviceId, keyId, cancel));
        }

        if (HasAd && !string.IsNullOrWhiteSpace(computerName) && _factory?.Ad(EffectiveSettings, AdPassword) is { } ad
            && await ad.ComputerSecretsAsync(computerName, cancel) is { } secrets)
        {
            keys.AddRange(secrets.BitLocker);
            note = secrets.Note;
        }

        Access("BitLocker-herstelsleutel bekeken", device?.Name ?? computerName ?? ("sleutel " + keyId));
        return (keys, note);
    }

    /// <summary>The LAPS password of a device (Entra first, then AD); written to the access log.</summary>
    public async Task<(LapsPassword? Password, string? Note)> LapsAsync(DeviceInfo? device, string? computerName, CancellationToken cancel = default)
    {
        LapsPassword? found = null;
        string? note = null;
        if (device is not null && Graph() is { } graph)
        {
            found = await graph.LapsAsync(device.DeviceId, cancel);
        }

        if (found is null && HasAd && !string.IsNullOrWhiteSpace(computerName ?? device?.Name) && _factory?.Ad(EffectiveSettings, AdPassword) is { } ad
            && await ad.ComputerSecretsAsync(computerName ?? device!.Name, cancel) is { } secrets)
        {
            found = secrets.Laps;
            note = secrets.Note;
        }

        Access("LAPS-wachtwoord bekeken", device?.Name ?? computerName ?? string.Empty);
        return (found, note);
    }

    /// <summary>Message trace for an address (received, sent or both); written to the access log.</summary>
    public async Task<IReadOnlyList<MessageTraceRow>> MessageTraceAsync(string address, bool received, bool sent, int days, CancellationToken cancel = default)
    {
        if (!HasMicrosoft365 || _factory?.Exchange(EffectiveSettings) is not { } exchange)
        {
            throw new InvalidOperationException(Texts.T["code.management-state-helpdesk.exchange-online-is-not-available"]);
        }

        var rows = new List<MessageTraceRow>();
        if (received)
        {
            rows.AddRange(await exchange.MessageTraceAsync(null, address, days, cancel));
        }

        if (sent)
        {
            rows.AddRange(await exchange.MessageTraceAsync(address, null, days, cancel));
        }

        Access("Berichtspoor bekeken", $"{address}, {days} dagen");
        return rows.DistinctBy(r => (r.Id, r.Recipient)).OrderByDescending(r => r.Received).ToList();
    }
}
