using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Linking;

/// <summary>The kinds of objects that have a source of truth (G0, U33).</summary>
public enum TruthKind
{
    Users,
    Groups,
}

/// <summary>Where Google Workspace gets its users and groups, which Moss365 cannot see (G0, U33).</summary>
public enum GoogleFeed
{
    /// <summary>Not chosen yet: asked on the page.</summary>
    Unknown,

    /// <summary>Managed in Google itself: Google is a source of its own.</summary>
    Itself,

    /// <summary>From Entra ID, by the provisioning of the enterprise app for Google.</summary>
    FromEntra,

    /// <summary>From AD, by Google Cloud Directory Sync.</summary>
    FromAd,
}

/// <summary>The choices of a tenant (document <c>bron</c>): what Moss365 cannot see.</summary>
public sealed record SourceSettings
{
    /// <summary>Where Google gets its users.</summary>
    public GoogleFeed GoogleUsers { get; init; } = GoogleFeed.Unknown;

    /// <summary>Where Google gets its groups.</summary>
    public GoogleFeed GoogleGroups { get; init; } = GoogleFeed.Unknown;

    /// <summary>Directory Sync also carries the passwords to Google (Password Sync).</summary>
    public bool? GooglePasswordSync { get; init; }
}

/// <summary>
/// The source of truth per tenant (G0, U33): per kind of object a chain from the source to what follows it. Moss365
/// proposes the chain from what it sees (Entra Connect marks what it synchronises) and asks what it cannot see (whether
/// Google gets its users from Entra, from AD or keeps them itself). A change goes to the source; the other platforms show
/// "follows from".
/// </summary>
public static class SourceOfTruth
{
    public const string Document = "bron";

    /// <summary>The chain of a kind: the source first, then the platforms that follow it; a platform on its own is a
    /// chain of one.</summary>
    public static IReadOnlyList<IReadOnlyList<Platform>> Chains(TruthKind kind, TenantSystems systems, InventoryData data, SourceSettings settings)
    {
        var chains = new List<List<Platform>>();
        var adToEntra = systems.HasAd() && systems.HasMicrosoft365() && Synchronised(kind, data);
        if (adToEntra)
        {
            chains.Add(new List<Platform> { Platform.ActiveDirectory, Platform.EntraId });
        }
        else
        {
            if (systems.HasAd()) chains.Add(new List<Platform> { Platform.ActiveDirectory });
            if (systems.HasMicrosoft365()) chains.Add(new List<Platform> { Platform.EntraId });
        }

        if (systems.HasGoogle())
        {
            var feed = kind == TruthKind.Users ? settings.GoogleUsers : settings.GoogleGroups;
            var follows = feed switch
            {
                GoogleFeed.FromEntra when systems.HasMicrosoft365() => Platform.EntraId,
                GoogleFeed.FromAd when systems.HasAd() => Platform.ActiveDirectory,
                _ => (Platform?)null,
            };
            if (follows is { } after && chains.FirstOrDefault(c => c.Contains(after)) is { } chain)
            {
                chain.Add(Platform.GoogleWorkspace);
            }
            else
            {
                chains.Add(new List<Platform> { Platform.GoogleWorkspace });
            }
        }

        return chains;
    }

    /// <summary>The platform a platform follows for a kind of object, or null when it is a source itself.</summary>
    public static Platform? FollowsFrom(Platform platform, TruthKind kind, TenantSystems systems, InventoryData data, SourceSettings settings)
        => Chains(kind, systems, data, settings).FirstOrDefault(c => c.Contains(platform)) is { } chain && chain[0] != platform
            ? chain[0]
            : null;

    /// <summary>What Moss365 cannot see and asks (the questions on the page that are still open).</summary>
    public static IReadOnlyList<TruthKind> OpenQuestions(TenantSystems systems, SourceSettings settings)
        => !systems.HasGoogle() || (!systems.HasAd() && !systems.HasMicrosoft365())
            ? Array.Empty<TruthKind>()
            : new[] { TruthKind.Users, TruthKind.Groups }.Where(k => (k == TruthKind.Users ? settings.GoogleUsers : settings.GoogleGroups) == GoogleFeed.Unknown).ToList();

    /// <summary>The choices Google can get for a tenant: from the systems it has.</summary>
    public static IReadOnlyList<GoogleFeed> Feeds(TenantSystems systems)
    {
        var list = new List<GoogleFeed> { GoogleFeed.Itself };
        if (systems.HasMicrosoft365()) list.Add(GoogleFeed.FromEntra);
        if (systems.HasAd()) list.Add(GoogleFeed.FromAd);
        return list;
    }

    public static string Name(GoogleFeed feed) => Texts.T["source.feed." + feed.ToString().ToLowerInvariant()];

    /// <summary>
    /// Entra Connect synchronises this kind: most accounts (or groups) in Entra that also exist in AD are marked as
    /// synchronised. Without data the answer is no (two sources, until a read says otherwise).
    /// </summary>
    public static bool Synchronised(TruthKind kind, InventoryData data)
        => kind == TruthKind.Users
            ? data.EntraUsers.Any(u => u.OnPremisesSyncEnabled)
            : data.CloudGroups.Values.Any(g => g.FromAd);
}
