using Moss365.Core.Localization;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

public enum DifferenceKind
{
    MissingInEntra,
    StatusDiffers,
    AttributeDiffers,
}

public sealed record Difference(Person Person, DifferenceKind Kind, string Attribute, string AdValue, string EntraValue, string Explanation)
{
    /// <summary>The attribute in the language of the interface (<see cref="Attribute"/> is a fixed name).</summary>
    public string AttributeText => AdEntraComparison.AttributeText(Attribute);

    public string KindText => Kind switch
    {
        DifferenceKind.MissingInEntra => Texts.T["code.ad-entra-comparison.missing-in-entra"],
        DifferenceKind.StatusDiffers => Texts.T["code.ad-entra-comparison.status-differs"],
        _ => Texts.T["code.ad-entra-comparison.attribute-differs"],
    };
}

/// <summary>Compares on-premises AD with Entra ID for the synchronised populations.</summary>
public static class AdEntraComparison
{
    /// <summary>The attributes Entra Connect takes over from AD to Entra: a fixed name (the same in every language,
    /// the field of <see cref="Changes.UserDesired"/> where there is one), the AD value and the Entra value.</summary>
    public static readonly IReadOnlyList<(string Attribute, Func<AdUser, string> Ad, Func<EntraUser, string> Entra)> Attributes = new[]
    {
        ("DisplayName", (Func<AdUser, string>)(a => a.DisplayName), (Func<EntraUser, string>)(e => e.DisplayName)),
        ("UPN", a => a.UserPrincipalName, e => e.UserPrincipalName),
        ("Mail", a => a.EmailAddress, e => e.Mail),
        ("Department", a => a.Department, e => e.Department),
        ("JobTitle", a => a.Title, e => e.JobTitle),
        ("Company", a => a.Company, e => e.CompanyName),
        ("Office", a => a.Office, e => e.OfficeLocation),
    };

    /// <summary>The name of an attribute of a difference in the language of the interface.</summary>
    public static string AttributeText(string attribute) => Texts.T.Text("compare.attribute." + attribute.ToLowerInvariant(), attribute);

    public static IReadOnlyList<Difference> Compare(InventoryData data, OrganisationConfig config)
    {
        var output = new List<Difference>();
        if (!data.HasAd || !data.HasEntra)
        {
            return output;
        }

        foreach (var p in data.Persons)
        {
            if (p.Ad is null)
            {
                continue;
            }

            var inSync = config.SynchronisedOus.Count == 0 || DnHelp.FallsUnder(p.Ad.OuPath, config.SynchronisedOus);
            if (p.Entra is null)
            {
                if (inSync && p.Ad.Enabled)
                {
                    output.Add(new Difference(p, DifferenceKind.MissingInEntra, "Account", p.Ad.UserPrincipalName, string.Empty, ReasonMissing(p.Ad)));
                }

                continue;
            }

            if (!p.Ad.Enabled && p.Entra.AccountEnabled)
            {
                output.Add(new Difference(p, DifferenceKind.StatusDiffers, "Enabled", Texts.T["device.disabled"], Texts.T["device.enabled"],
                    Texts.T["code.ad-entra-comparison.disabled-in-ad-but-still"]));
            }

            foreach (var (attribute, adValue, entraValue) in Attributes)
            {
                var a = adValue(p.Ad);
                var e = entraValue(p.Entra);
                // Entra strips leading and trailing spaces during synchronisation: that is not a real difference
                if (string.Equals(a.Trim(), e.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                var explanation = string.Equals(a.Trim(), e.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? Texts.T["code.ad-entra-comparison.only-upper-and-lower-case"]
                    : string.Empty;
                output.Add(new Difference(p, DifferenceKind.AttributeDiffers, attribute, a, e, explanation));
            }
        }

        return output
            .OrderBy(v => v.Kind)
            .ThenBy(v => v.Person.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ReasonMissing(AdUser ad)
    {
        var reasons = new List<string>();
        var domain = ad.UserPrincipalName.Contains('@') ? ad.UserPrincipalName[(ad.UserPrincipalName.IndexOf('@') + 1)..] : string.Empty;
        if (domain.Length == 0)
        {
            reasons.Add(Texts.T["code.ad-entra-comparison.no-upn"]);
        }
        else if (domain.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(Texts.T.Format("code.ad-entra-comparison.upn-domain-is-not-routable", domain));
        }

        if (string.IsNullOrWhiteSpace(ad.LastLogonDate))
        {
            reasons.Add(Texts.T["code.ad-entra-comparison.never-signed-in"]);
        }

        return reasons.Count == 0 ? Texts.T["code.ad-entra-comparison.cause-unknown-synchronisation-filter-created"] : string.Join("; ", reasons);
    }
}
