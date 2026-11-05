using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>
/// Loads live data from AD and Entra into the same model as the inventory. What is not loaded live (e.g. rooms, or AD
/// when no domain controller is configured) stays from the current data.
/// </summary>
public static class LiveLoader
{
    public static async Task<InventoryData> LoadAsync(
        IAdConnection? ad,
        IGraphConnection? graph,
        InventoryData current,
        IProgress<string>? progress,
        CancellationToken cancel)
        => (await ReadAsync(ad, graph, current, progress, cancel)).Data;

    /// <summary>Like LoadAsync, with a previous read to ask only for the changes, and what was read.</summary>
    public static async Task<LiveReading> ReadAsync(
        IAdConnection? ad,
        IGraphConnection? graph,
        InventoryData current,
        IProgress<string>? progress,
        CancellationToken cancel,
        AdBase? adBase = null,
        GraphBase? graphBase = null)
    {
        var adData = ad is null ? null : await ad.ReadAsync(adBase, progress, cancel);
        var graphData = graph is null ? null : await graph.ReadAsync(graphBase, progress, cancel);
        return new LiveReading(Merge(current, adData, graphData), adData, graphData);
    }

    /// <summary>
    /// Puts what was read from AD and/or Entra into the current data; the rest stays. Separate from reading, so that
    /// AD, Entra and Exchange can be read at the same time and merged one by one afterwards.
    /// </summary>
    public static InventoryData Merge(InventoryData current, AdData? adData, GraphData? graphData)
    {
        var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var sources = new List<SourceFile>();
        if (adData is not null)
        {
            sources.Add(new SourceFile("AD-gebruikers en -groepen", $"live {time}" + (adData.Quick ? " (enkel wijzigingen)" : string.Empty), adData.Users.Count + adData.Groups.Count));
        }

        if (graphData is not null)
        {
            sources.Add(new SourceFile("Entra-gebruikers, -groepen en licenties", $"live {time}" + (graphData.Quick ? " (groepen: enkel wijzigingen)" : string.Empty), graphData.Users.Count + graphData.Groups.Count));
        }

        sources.AddRange(current.Sources.Where(b => (adData is null || !b.File.StartsWith("ad-", StringComparison.Ordinal))
                                                   && (graphData is null || !b.File.StartsWith("entra-", StringComparison.Ordinal))
                                                   && !b.File.Contains("live", StringComparison.Ordinal)));

        var membership = graphData?.Membership
                           ?? current.CloudMembership.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var data = InventoryLoader.Compose(
            adData?.Users ?? current.AdUsers,
            adData?.Groups ?? current.AdGroupsByDn.Values.ToList(),
            graphData?.Users ?? current.EntraUsers,
            graphData?.Groups ?? current.CloudGroups.Values.ToList(),
            membership,
            current.Rooms,
            graphData?.Licences ?? current.Licences,
            sources,
            adData is null && graphData is null ? current.Origin : "Live",
            adData?.DomainDn ?? current.DomainDn) with
        {
            // What was not read live stays from the current data
            CloudMemberNames = current.CloudMemberNames,
            ExoGroups = current.ExoGroups,
            Mailboxes = current.Mailboxes,
            MailboxPermissions = current.MailboxPermissions,
            Security = current.Security,
            ConditionalAccess = current.ConditionalAccess,
        };
        return data;
    }

    /// <summary>Replaces the security data (MFA and admin roles) after a live read from Entra.</summary>
    public static InventoryData WithSecurity(InventoryData current, SecurityData security)
    {
        var replace = new[] { InventoryLoader.EntraMfa, InventoryLoader.EntraRoles };
        var sources = current.Sources.Where(b => !replace.Contains(b.File) && !b.File.StartsWith("Beveiliging", StringComparison.Ordinal)).ToList();
        sources.Add(new SourceFile("Beveiliging (MFA en beheerrollen)", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", security.Mfa.Count + security.Roles.Count));
        return current with { Security = security, Sources = sources };
    }

    /// <summary>Replaces the Conditional Access data after a live read from Entra.</summary>
    public static InventoryData WithConditionalAccess(InventoryData current, CaData ca)
    {
        var sources = current.Sources.Where(b => b.File != InventoryLoader.EntraCa && !b.File.StartsWith("Conditional Access", StringComparison.Ordinal)).ToList();
        sources.Add(new SourceFile("Conditional Access", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", ca.Policies.Count));
        return current with { ConditionalAccess = ca, Sources = sources };
    }

    /// <summary>Replaces the rooms (after a live read from Exchange) and keeps the rest.</summary>
    public static InventoryData WithRooms(InventoryData current, IReadOnlyList<Room> rooms)
    {
        var sources = current.Sources.Where(b => b.File != InventoryLoader.ExoRooms && !b.File.StartsWith("Rooms", StringComparison.Ordinal)).ToList();
        sources.Add(new SourceFile("Rooms (Exchange)", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", rooms.Count));
        return current with
        {
            Rooms = rooms.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Sources = sources,
        };
    }

    /// <summary>Replaces the Exchange groups, shared mailboxes and their permissions (after a live read from
    /// Exchange).</summary>
    public static InventoryData WithExchange(InventoryData current, ExchangeData exchange)
    {
        var replace = new[] { InventoryLoader.ExoGroups, InventoryLoader.ExoGroupMembers, InventoryLoader.ExoMailboxes, InventoryLoader.ExoMailboxPermissions };
        var sources = current.Sources.Where(b => !replace.Contains(b.File) && !b.File.StartsWith("Postvakken", StringComparison.Ordinal)).ToList();
        sources.Add(new SourceFile("Postvakken en groepen (Exchange)", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", exchange.Mailboxes.Count + exchange.Groups.Count));
        return current with
        {
            ExoGroups = exchange.Groups,
            Mailboxes = exchange.Mailboxes.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            MailboxPermissions = exchange.Permissions,
            Sources = sources,
        };
    }
}

/// <summary>The result of a live read, with what AD and Graph returned (state and deltaLink for the next
/// time).</summary>
public sealed record LiveReading(InventoryData Data, AdData? Ad, GraphData? Graph);
