using System.Globalization;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.UI.Services;

/// <summary>
/// Quick load: the app remembers from where AD (state of the domain controller) and Entra (deltaLink of the groups)
/// were at the previous read, and asks only for the changes the next time. That is possible only when the loaded data
/// comes from that read (live, or the inventory saved live with the same starting point). After 7 days the app reads
/// everything again, as a safety net.
/// </summary>
public sealed partial class ManagementState
{
    public const string AdSnapshotFile = "ad-stand.txt";
    public const string EntraDeltaFile = "entra-delta.txt";

    /// <summary>How long the app may continue with changes before it reads everything again.</summary>
    public static readonly TimeSpan FullReadAfter = TimeSpan.FromDays(7);

    private (AdSnapshot Snapshot, DateTime Full)? _adSnapshot;
    private (string Link, DateTime Full)? _graphDelta;

    /// <summary>Whether the next AD read can ask for the changes only.</summary>
    public bool AdCanQuick => Settings.QuickLoad && _adSnapshot is { } s && DateTime.Now - s.Full < FullReadAfter && Data.HasAd;

    /// <summary>Whether the next Entra read can read the groups via their changes.</summary>
    public bool EntraCanQuick => Settings.QuickLoad && _graphDelta is { } d && DateTime.Now - d.Full < FullReadAfter && Data.HasEntra;

    private AdBase? AdBaseForQuick()
        => AdCanQuick ? new AdBase(_adSnapshot!.Value.Snapshot, Data.AdUsers, Data.AdGroupsByDn.Values.ToList()) : null;

    private GraphBase? GraphBaseForQuick()
        => EntraCanQuick ? new GraphBase(_graphDelta!.Value.Link, Data.CloudGroups, Data.CloudMembership) : null;

    private void RememberSnapshot(AdData? read)
    {
        if (read?.Snapshot is not { } snapshot)
        {
            _adSnapshot = null;
            return;
        }

        // A quick read keeps the moment of the last full read
        var full = read.Quick && _adSnapshot is { } previous ? previous.Full : DateTime.Now;
        _adSnapshot = (snapshot, full);
    }

    private void RememberDelta(GraphData? read)
    {
        if (read?.DeltaLink is not { } link)
        {
            _graphDelta = null;
            return;
        }

        var full = read.Quick && _graphDelta is { } previous ? previous.Full : DateTime.Now;
        _graphDelta = (link, full);
    }

    /// <summary>Saves the starting point in the live inventory too, so that the app can also load quickly after a
    /// restart.</summary>
    private void SaveQuickSnapshot(string folder, InventoryComponent components)
    {
        try
        {
            if (components.HasFlag(InventoryComponent.Ad) && _adSnapshot is { } a)
            {
                File.WriteAllLines(Path.Combine(folder, AdSnapshotFile), new[]
                {
                    a.Snapshot.DomainController, a.Snapshot.HighestUsn.ToString(CultureInfo.InvariantCulture), a.Full.ToString("o", CultureInfo.InvariantCulture),
                });
            }

            if (components.HasFlag(InventoryComponent.Entra) && _graphDelta is { } g)
            {
                File.WriteAllLines(Path.Combine(folder, EntraDeltaFile), new[] { g.Link, g.Full.ToString("o", CultureInfo.InvariantCulture) });
            }
        }
        catch (Exception)
        {
            // No problem: the next time the app reads everything
        }
    }

    /// <summary>
    /// After loading an inventory from disk: take over the starting point only when all files involved come from the
    /// same folder saved live as the starting point; otherwise it does not match the data and the app reads everything
    /// the next time.
    /// </summary>
    private void RestoreQuickSnapshot()
    {
        _adSnapshot = null;
        _graphDelta = null;
        try
        {
            if (OnlyFolder(InventoryLoader.AdUsers, InventoryLoader.AdGroups, InventoryLoader.AdGroupMembers) is { } adFolder
                && File.Exists(Path.Combine(adFolder, AdSnapshotFile)))
            {
                var r = File.ReadAllLines(Path.Combine(adFolder, AdSnapshotFile));
                if (r.Length >= 3 && long.TryParse(r[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var usn)
                    && DateTime.TryParse(r[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var full))
                {
                    _adSnapshot = (new AdSnapshot(r[0], usn), full);
                }
            }

            if (OnlyFolder(InventoryLoader.EntraGroups, InventoryLoader.EntraGroupMembers) is { } entraFolder
                && File.Exists(Path.Combine(entraFolder, EntraDeltaFile)))
            {
                var r = File.ReadAllLines(Path.Combine(entraFolder, EntraDeltaFile));
                if (r.Length >= 2 && r[0].StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase)
                    && DateTime.TryParse(r[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var full))
                {
                    _graphDelta = (r[0], full);
                }
            }
        }
        catch (Exception)
        {
            _adSnapshot = null;
            _graphDelta = null;
        }
    }

    /// <summary>The folder all these files were loaded from, when that is one and the same folder.</summary>
    private string? OnlyFolder(params string[] files)
    {
        var folders = files.Select(b => Data.Sources.FirstOrDefault(x => x.File == b)?.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return folders.Count == 1 && folders[0] is { } m && Directory.Exists(m) ? m : null;
    }
}
