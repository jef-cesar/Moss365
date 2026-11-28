using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Google;

/// <summary>What an edition of Google Workspace has of what matters for management and security (G1).</summary>
public sealed record GoogleEdition(string Key, string Name, bool Vault, bool ContextAwareAccess, bool AdvancedEndpoints, bool SecurityCenter)
{
    /// <summary>The features this edition lacks, in the language of the screen.</summary>
    public IReadOnlyList<string> Lacks => new[]
    {
        (Vault, "vault"), (ContextAwareAccess, "context-aware-access"), (AdvancedEndpoints, "advanced-endpoints"), (SecurityCenter, "security-center"),
    }.Where(x => !x.Item1).Select(x => Texts.T[$"google.feature.{x.Item2}"]).ToList();
}

/// <summary>
/// The edition of Google Workspace from its licences (G1). The editions and what they have are data: Vault from Business
/// Plus on, context-aware access and the security center only in Enterprise, advanced endpoint management from Business
/// Plus on. Unknown SKUs are shown by their name, without a judgement.
/// </summary>
public static class GoogleEditions
{
    /// <summary>The product of the Workspace editions in the Enterprise License Manager.</summary>
    public const string WorkspaceProduct = "Google-Apps";

    /// <summary>The products the licences are read for: Workspace, Vault, Cloud Identity Premium and archived users.</summary>
    public static IReadOnlyList<string> Products { get; } = new[] { WorkspaceProduct, "Google-Vault", "101031", "101034" };

    private static readonly GoogleEdition[] Known =
    {
        new("business-starter", "Business Starter", false, false, false, false),
        new("business-standard", "Business Standard", false, false, false, false),
        new("business-plus", "Business Plus", true, false, true, false),
        new("enterprise-essentials", "Enterprise Essentials", false, true, false, false),
        new("enterprise-standard", "Enterprise Standard", true, true, true, true),
        new("enterprise-plus", "Enterprise Plus", true, true, true, true),
        new("education-fundamentals", "Education Fundamentals", true, false, false, false),
        new("education-standard", "Education Standard", true, false, true, true),
        new("education-plus", "Education Plus", true, true, true, true),
        new("frontline", "Frontline", false, false, false, false),
    };

    /// <summary>The known edition of a SKU name ("Google Workspace Business Plus"), or null.</summary>
    public static GoogleEdition? Of(string skuName)
        => Known.OrderByDescending(e => e.Name.Length).FirstOrDefault(e => skuName.Contains(e.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The edition of the tenant: the Workspace SKU most users have, and how many have each SKU.</summary>
    public static (GoogleEdition? Edition, string SkuName, IReadOnlyList<(string Sku, int Count)> Skus) Of(IReadOnlyList<GoogleLicence> licences)
    {
        var skus = licences.GroupBy(l => l.SkuName.Length > 0 ? l.SkuName : l.SkuId).Select(g => (Sku: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Sku, StringComparer.CurrentCultureIgnoreCase).ToList();
        var main = licences.Where(l => l.ProductId == WorkspaceProduct).GroupBy(l => l.SkuName).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? string.Empty;
        return (main.Length > 0 ? Of(main) : null, main, skus);
    }

    /// <summary>Accounts that are not suspended or archived and have no Workspace licence.</summary>
    public static IReadOnlyList<GoogleUser> WithoutLicence(IReadOnlyList<GoogleUser> users, IReadOnlyList<GoogleLicence> licences)
    {
        var licensed = licences.Where(l => l.ProductId == WorkspaceProduct).Select(l => l.UserEmail).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return users.Where(u => !u.Suspended && !u.Archived && !licensed.Contains(u.PrimaryEmail)).ToList();
    }
}
