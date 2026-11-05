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
    public string KindText => Kind switch
    {
        DifferenceKind.MissingInEntra => "Ontbreekt in Entra",
        DifferenceKind.StatusDiffers => "Status verschilt",
        _ => "Kenmerk verschilt",
    };
}

/// <summary>Compares on-premises AD with Entra ID for the synchronised populations.</summary>
public static class AdEntraComparison
{
    /// <summary>The attributes Entra Connect takes over from AD to Entra (AD name, Entra name).</summary>
    public static readonly IReadOnlyList<(string Attribute, Func<AdUser, string> Ad, Func<EntraUser, string> Entra)> Attributes = new[]
    {
        ("Weergavenaam", (Func<AdUser, string>)(a => a.DisplayName), (Func<EntraUser, string>)(e => e.DisplayName)),
        ("UPN", a => a.UserPrincipalName, e => e.UserPrincipalName),
        ("E-mail", a => a.EmailAddress, e => e.Mail),
        ("Dienst (Department)", a => a.Department, e => e.Department),
        ("Functie (Title)", a => a.Title, e => e.JobTitle),
        ("Bedrijf (Company)", a => a.Company, e => e.CompanyName),
        ("Kantoor (Office)", a => a.Office, e => e.OfficeLocation),
    };

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
                output.Add(new Difference(p, DifferenceKind.StatusDiffers, "Ingeschakeld", "uitgeschakeld", "ingeschakeld",
                    "Uitgeschakeld in AD maar nog actief in Entra (wacht op de volgende synchronisatie?)"));
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
                    ? "Alleen hoofdletters verschillen"
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
            reasons.Add("geen UPN");
        }
        else if (domain.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add($"UPN-domein {domain} is niet routeerbaar");
        }

        if (string.IsNullOrWhiteSpace(ad.LastLogonDate))
        {
            reasons.Add("nooit aangemeld");
        }

        return reasons.Count == 0 ? "Oorzaak onbekend (synchronisatiefilter, recent aangemaakt?)" : string.Join("; ", reasons);
    }
}
