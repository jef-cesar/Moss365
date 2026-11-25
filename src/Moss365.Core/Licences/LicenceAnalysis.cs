using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Licences;

public enum WasteKind
{
    /// <summary>A disabled account that still has a licence.</summary>
    Disabled,

    /// <summary>A shared mailbox with a licence (up to 50 GB it needs none).</summary>
    SharedMailbox,

    /// <summary>A licence that is already part of another licence of the same account.</summary>
    Duplicate,

    /// <summary>An enabled account that has not signed in for a long time (or never).</summary>
    Unused,
}

/// <summary>A licence that brings nothing: the person, why, and which licences can go.</summary>
public sealed record WastedLicence(Person Person, WasteKind Kind, IReadOnlyList<string> Skus, string Reason)
{
    public string KindText => Kind switch
    {
        WasteKind.Disabled => "uitgeschakeld account",
        WasteKind.SharedMailbox => "gedeeld postvak",
        WasteKind.Duplicate => "dubbel plan",
        _ => "niet gebruikt",
    };
}

/// <summary>The cost of a group of people (a department or division).</summary>
public sealed record CostRow(string Name, int People, decimal Cost);

/// <summary>Licences of a person compared with the job profile.</summary>
public sealed record ProfileLicenceRow(Person Person, JobProfile Profile, IReadOnlyList<string> Missing, IReadOnlyList<string> Extra);

/// <summary>
/// Licences and costs (R12, 0.35): licences that bring nothing (disabled accounts, shared mailboxes, duplicate plans,
/// licences not used), the cost per month with a price per licence, per department and division, and licences
/// against the job profile. Removing is a plan as always.
/// </summary>
public static class LicenceAnalysis
{
    /// <summary>Which licences already contain which other licences (Microsoft's bundles).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Includes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["SPE_E5"] = new[] { "SPE_E3", "ENTERPRISEPREMIUM", "ENTERPRISEPACK", "STANDARDPACK", "EMSPREMIUM", "EMS", "AAD_PREMIUM", "AAD_PREMIUM_P2", "EXCHANGESTANDARD", "EXCHANGEENTERPRISE" },
        ["SPE_E3"] = new[] { "ENTERPRISEPACK", "STANDARDPACK", "EMS", "AAD_PREMIUM", "EXCHANGESTANDARD", "EXCHANGEENTERPRISE" },
        ["ENTERPRISEPREMIUM"] = new[] { "ENTERPRISEPACK", "STANDARDPACK", "EXCHANGESTANDARD", "EXCHANGEENTERPRISE" },
        ["ENTERPRISEPACK"] = new[] { "STANDARDPACK", "EXCHANGESTANDARD", "EXCHANGEENTERPRISE" },
        ["SPB"] = new[] { "O365_BUSINESS_PREMIUM", "O365_BUSINESS_ESSENTIALS", "SMB_BUSINESS_ESSENTIALS", "EXCHANGESTANDARD", "AAD_PREMIUM" },
        ["O365_BUSINESS_PREMIUM"] = new[] { "O365_BUSINESS_ESSENTIALS", "SMB_BUSINESS_ESSENTIALS", "EXCHANGESTANDARD" },
        ["SPE_F1"] = new[] { "DESKLESSPACK", "EMS" },
        ["EMSPREMIUM"] = new[] { "EMS", "AAD_PREMIUM", "AAD_PREMIUM_P2" },
        ["EMS"] = new[] { "AAD_PREMIUM" },
    };

    /// <summary>Licences that bring nothing; an account appears once per kind.</summary>
    public static IReadOnlyList<WastedLicence> Wasted(InventoryData d, DateTime now, int unusedDays = 90)
    {
        var output = new List<WastedLicence>();
        var shared = d.Mailboxes.Where(m => m.Type.Equals("SharedMailbox", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Smtp).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var p in d.Persons.Where(p => p.Entra is { Licences.Count: > 0 } && !p.IsGuest))
        {
            var licences = p.Entra!.Licences;
            if (shared.Contains(p.Entra.Mail) || shared.Contains(p.Entra.UserPrincipalName))
            {
                output.Add(new WastedLicence(p, WasteKind.SharedMailbox, licences, "Een gedeeld postvak tot 50 GB heeft geen licentie nodig (behalve voor een archief of bewaring)."));
                continue;
            }

            if (!p.Enabled)
            {
                output.Add(new WastedLicence(p, WasteKind.Disabled, licences, "Uitgeschakeld, maar de licentie loopt door. Zet het postvak eerst om naar een gedeeld postvak als het moet blijven."));
                continue;
            }

            var duplicates = licences.Where(l => licences.Any(o => !string.Equals(o, l, StringComparison.OrdinalIgnoreCase)
                && Includes.TryGetValue(o, out var inner) && inner.Contains(l, StringComparer.OrdinalIgnoreCase))).ToList();
            if (duplicates.Count > 0)
            {
                output.Add(new WastedLicence(p, WasteKind.Duplicate, duplicates,
                    $"{string.Join(", ", duplicates)} zit al in {string.Join(", ", licences.Except(duplicates, StringComparer.OrdinalIgnoreCase))}."));
            }

            var last = Statistic.LastSignIn(p);
            if (last is null || last < now.AddDays(-unusedDays))
            {
                output.Add(new WastedLicence(p, WasteKind.Unused, licences, last is null ? "Nooit aangemeld (of onbekend)." : $"Laatst aangemeld op {last:dd/MM/yyyy}."));
            }
        }

        return output.OrderBy(w => w.Kind).ThenBy(w => w.Person.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static decimal PriceOf(OrganisationConfig c, string sku) => c.LicencePrices.TryGetValue(sku, out var price) ? price : 0m;

    public static decimal MonthlyCost(Person p, OrganisationConfig c) => (p.Entra?.Licences ?? Array.Empty<string>()).Sum(l => PriceOf(c, l));

    /// <summary>The cost per month of a group of people (department, division), most expensive first.</summary>
    public static IReadOnlyList<CostRow> CostPer(InventoryData d, OrganisationConfig c, Func<Person, string> key)
        => d.Persons.Where(p => p.Entra is { Licences.Count: > 0 } && !p.IsGuest)
            .GroupBy(p => key(p) is { Length: > 0 } k ? k : "(geen)")
            .Select(g => new CostRow(g.Key, g.Count(), g.Sum(p => MonthlyCost(p, c))))
            .OrderByDescending(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>What removing these licences saves per month.</summary>
    public static decimal Savings(IEnumerable<WastedLicence> list, OrganisationConfig c)
        => list.GroupBy(w => w.Person.Key).Sum(g => g.SelectMany(w => w.Skus).Distinct(StringComparer.OrdinalIgnoreCase).Sum(s => PriceOf(c, s)));

    /// <summary>Who has more or fewer licences than the job profile they hold.</summary>
    public static IReadOnlyList<ProfileLicenceRow> ByProfile(InventoryData d, OrganisationConfig c, IReadOnlyDictionary<string, ProfileAssignment> assignments)
    {
        var output = new List<ProfileLicenceRow>();
        foreach (var profile in c.JobProfiles.Where(p => p.Licences.Count > 0))
        {
            foreach (var p in JobProfilePlans.Holders(d, profile, assignments))
            {
                var has = p.Entra?.Licences ?? Array.Empty<string>();
                var missing = profile.Licences.Where(l => !has.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
                var extra = has.Where(l => !profile.Licences.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count + extra.Count > 0)
                {
                    output.Add(new ProfileLicenceRow(p, profile, missing, extra));
                }
            }
        }

        return output;
    }

    /// <summary>
    /// The plan that removes the chosen wasted licences: for a duplicate plan only the licence that is already
    /// included, otherwise all licences of the account.
    /// </summary>
    public static PlanOutcome Remove(InventoryData d, IEnumerable<WastedLicence> chosen)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var g in chosen.GroupBy(w => w.Person.Key))
        {
            var p = g.First().Person;
            if (p.Entra is not { } e)
            {
                continue;
            }

            var onlyDuplicates = g.All(w => w.Kind == WasteKind.Duplicate);
            var skus = onlyDuplicates ? g.SelectMany(w => w.Skus).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : e.Licences.ToList();
            var ids = skus.Select(s => d.Licences.FirstOrDefault(l => string.Equals(l.SkuPartNumber, s, StringComparison.OrdinalIgnoreCase))?.SkuId).ToList();
            if (onlyDuplicates && ids.Any(i => string.IsNullOrEmpty(i)))
            {
                errors.Add($"{p.Name}: de id van {string.Join(", ", skus)} is niet geladen; laad Entra live.");
                continue;
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphRemoveLicences,
                Target = e.Id,
                TargetName = p.Name,
                Parameters = onlyDuplicates ? new(StringComparer.OrdinalIgnoreCase) { ["skuIds"] = string.Join(',', ids) } : new(StringComparer.OrdinalIgnoreCase),
                Description = onlyDuplicates ? $"Dubbele licentie verwijderen: {string.Join(", ", skus)}" : "Alle licenties verwijderen: " + string.Join(", ", skus),
                CurrentValue = string.Join(", ", e.Licences),
                Warning = !onlyDuplicates && e.Mail.Length > 0 && g.Any(w => w.Kind == WasteKind.Disabled)
                    ? "Zonder licentie verdwijnt het postvak na 30 dagen, tenzij het eerst naar een gedeeld postvak wordt omgezet." : null,
            });
        }

        return PlanOutcome.Of(plan, errors);
    }
}
