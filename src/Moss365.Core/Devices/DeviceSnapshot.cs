using System.Globalization;

namespace Moss365.Core.Devices;

/// <summary>
/// Devices in the snapshots (R17): every read on Toestellen keeps the computers in AD and the devices in Entra and Intune
/// as tables of a snapshot, like the users, so that the devices on a date can be shown and only what changed is stored.
/// The moment a device was last seen changes every day, so a row only keeps whether it was seen in the last 90 days.
/// </summary>
public static class DeviceSnapshot
{
    public const string AdTable = "toestellen-ad.csv";
    public const string EntraTable = "toestellen-entra.csv";
    public const string IntuneTable = "toestellen-intune.csv";

    /// <summary>The column that identifies a row, per table.</summary>
    public static IReadOnlyDictionary<string, string[]> KeyColumns { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [AdTable] = new[] { "DistinguishedName" },
        [EntraTable] = new[] { "Id" },
        [IntuneTable] = new[] { "Id" },
    };

    private static string B(bool value) => value ? "True" : "False";

    private static bool Stale(DateTimeOffset? seen, DateTimeOffset now) => seen is not { } s || s < now.AddDays(-90);

    /// <summary>The tables of what was read (a part that was not read is left out, so it stays as it was).</summary>
    public static Dictionary<string, List<Dictionary<string, string>>> Tables(IReadOnlyList<AdComputer>? ad, IReadOnlyList<EntraDevice>? entra, IReadOnlyList<IntuneDevice>? intune, DateTimeOffset now)
    {
        var tables = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        if (ad is not null)
        {
            tables[AdTable] = ad.Select(c => new Dictionary<string, string>
            {
                ["Name"] = c.Name, ["DistinguishedName"] = c.DistinguishedName, ["OperatingSystem"] = c.OperatingSystem, ["Version"] = c.Version,
                ["Enabled"] = B(c.Enabled), ["Stale"] = B(Stale(c.LastLogon, now)),
                ["Created"] = c.Created?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                ["Description"] = c.Description, ["Location"] = c.Location, ["ManagedBy"] = c.ManagedBy,
            }).ToList();
        }

        if (entra is not null)
        {
            tables[EntraTable] = entra.Select(d => new Dictionary<string, string>
            {
                ["Id"] = d.Id, ["DeviceId"] = d.DeviceId, ["Name"] = d.Name, ["OperatingSystem"] = d.OperatingSystem, ["Version"] = d.Version,
                ["Enabled"] = B(d.Enabled), ["TrustType"] = d.TrustType, ["Stale"] = B(Stale(d.LastSignIn, now)),
            }).ToList();
        }

        if (intune is not null)
        {
            tables[IntuneTable] = intune.Select(d => new Dictionary<string, string>
            {
                ["Id"] = d.Id, ["Name"] = d.Name, ["Owner"] = d.Owner, ["OperatingSystem"] = d.OperatingSystem, ["Version"] = d.Version,
                ["Compliance"] = d.Compliance, ["Ownership"] = d.Ownership, ["Stale"] = B(Stale(d.LastSync, now)),
                ["Encrypted"] = d.Encrypted is { } e ? B(e) : string.Empty, ["EntraDeviceId"] = d.EntraDeviceId,
                ["Serial"] = d.Serial, ["Model"] = d.Model, ["Manufacturer"] = d.Manufacturer,
                ["Enrolled"] = d.Enrolled?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            }).ToList();
        }

        return tables;
    }

    /// <summary>The devices of a snapshot, as far as they were read before it; "seen" is the moment of the snapshot
    /// unless the row says the device was not seen for 90 days.</summary>
    public static (IReadOnlyList<AdComputer>? Ad, IReadOnlyList<EntraDevice>? Entra, IReadOnlyList<IntuneDevice>? Intune) Read(
        IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables, DateTimeOffset at)
    {
        static string V(Dictionary<string, string> r, string key) => r.TryGetValue(key, out var v) ? v : string.Empty;
        DateTimeOffset? Seen(Dictionary<string, string> r) => V(r, "Stale") == "True" ? null : at;
        static DateTimeOffset? Date(string text) => DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

        var ad = tables.TryGetValue(AdTable, out var a)
            ? a.Select(r => new AdComputer(V(r, "Name"), V(r, "DistinguishedName"), V(r, "OperatingSystem"), V(r, "Version"), Seen(r), V(r, "Enabled") == "True", Date(V(r, "Created")))
                { Description = V(r, "Description"), Location = V(r, "Location"), ManagedBy = V(r, "ManagedBy") }).ToList()
            : null;
        var entra = tables.TryGetValue(EntraTable, out var e)
            ? e.Select(r => new EntraDevice(V(r, "Id"), V(r, "DeviceId"), V(r, "Name"), V(r, "OperatingSystem"), V(r, "Version"), V(r, "Enabled") == "True", Seen(r), V(r, "TrustType"))).ToList()
            : null;
        var intune = tables.TryGetValue(IntuneTable, out var i)
            ? i.Select(r => new IntuneDevice(V(r, "Id"), V(r, "Name"), V(r, "Owner"), V(r, "OperatingSystem"), V(r, "Version"), V(r, "Compliance"), Seen(r), V(r, "Ownership"))
            {
                Encrypted = V(r, "Encrypted") switch { "True" => true, "False" => false, _ => null },
                EntraDeviceId = V(r, "EntraDeviceId"), Serial = V(r, "Serial"), Model = V(r, "Model"), Manufacturer = V(r, "Manufacturer"), Enrolled = Date(V(r, "Enrolled")),
            }).ToList()
            : null;
        return (ad, entra, intune);
    }
}
