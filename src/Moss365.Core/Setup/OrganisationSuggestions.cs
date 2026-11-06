using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Setup;

public enum SuggestionKind
{
    AdDomain,
    SynchronisedOu,
    Department,
    Office,
    GroupPrefix,
    EmergencyAccount,
}

/// <summary>A value the wizard found in the data; Count = how many accounts or groups use it.</summary>
public sealed record Suggestion(SuggestionKind Kind, string Value, int Count, string Explanation);

/// <summary>
/// Suggestions for the organisation settings from what was read (R5, step 8 and 9 of the wizard): only what is not in
/// the settings yet. You confirm or adjust; nothing is taken over by itself.
/// </summary>
public static class OrganisationSuggestions
{
    private static readonly string[] EmergencyWords = { "nood", "breakglass", "break-glass", "break.glass", "emergency", "glass" };

    public static IReadOnlyList<Suggestion> For(InventoryData d, OrganisationConfig c)
    {
        var output = new List<Suggestion>();
        var staff = d.Persons.Where(p => !p.IsGuest && p.Enabled && !d.Index.IsMailboxAccount(p)).ToList();

        // The AD domain, from the DNs of the accounts
        var domain = d.AdUsers.Select(u => DomainOf(u.DistinguishedName)).Where(x => x.Length > 0)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (domain is not null && !string.Equals(domain.Key, c.AdDomain?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            output.Add(new(SuggestionKind.AdDomain, domain.Key, domain.Count(), string.IsNullOrWhiteSpace(c.AdDomain) ? "Nog niet ingevuld." : $"Ingevuld is {c.AdDomain}."));
        }

        // The OUs whose accounts are synchronised to Entra (the highest OU per branch)
        var synced = staff.Where(p => p.Ad is not null && p.Entra?.OnPremisesSyncEnabled == true).Select(p => p.Ad!.OuPath).Where(o => o.Length > 0).ToList();
        foreach (var top in synced.GroupBy(o => o.Split('/')[0], StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
        {
            if (!c.SynchronisedOus.Any(s => DnHelp.FallsUnder(top.Key, new[] { s }) || DnHelp.FallsUnder(s, new[] { top.Key })))
            {
                output.Add(new(SuggestionKind.SynchronisedOu, top.Key, top.Count(), "Accounts uit deze OU staan ook in Entra."));
            }
        }

        // Departments and offices that accounts use but the settings do not know
        foreach (var g in staff.Select(p => p.Department).Where(x => x.Length > 0).GroupBy(OrganisationConfig.Normalise).OrderByDescending(g => g.Count()))
        {
            if (c.SearchDepartment(g.Key) is null)
            {
                output.Add(new(SuggestionKind.Department, MostUsed(g), g.Count(), "Staat bij accounts, niet in de lijst van diensten."));
            }
        }

        var offices = c.Offices.Select(OrganisationConfig.Normalise).ToHashSet();
        foreach (var g in staff.Select(p => (p.Ad?.Office ?? p.Entra?.OfficeLocation ?? string.Empty).Trim()).Where(x => x.Length > 0)
                     .GroupBy(OrganisationConfig.Normalise).OrderByDescending(g => g.Count()))
        {
            if (!offices.Contains(g.Key))
            {
                output.Add(new(SuggestionKind.Office, MostUsed(g), g.Count(), "Staat bij accounts, niet in de lijst van kantoren."));
            }
        }

        // Prefixes of group names (gg_, AFD_, SG-): at least three groups
        var known = c.GroupPrefixes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = d.AdGroupsByDn.Values.Select(g => g.Name).Concat(d.CloudGroups.Values.Select(g => g.DisplayName));
        foreach (var g in names.Select(Prefix).Where(x => x is not null).GroupBy(x => x!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()))
        {
            if (!known.Contains(g.Key))
            {
                output.Add(new(SuggestionKind.GroupPrefix, g.Key, g.Count(), "Begin van groepsnamen."));
            }
        }

        output.AddRange(EmergencyCandidates(d, c));
        return output;
    }

    /// <summary>
    /// Candidates for emergency accounts (break glass): enabled, cloud-only Global Administrators. The best ones are
    /// excluded from every Conditional Access policy that is on, or have a name that says so.
    /// </summary>
    public static IEnumerable<Suggestion> EmergencyCandidates(InventoryData d, OrganisationConfig c)
    {
        if (d.Security is not { RolesKnown: true } security)
        {
            yield break;
        }

        var already = c.EmergencyAccounts.Select(u => u.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ca = d.ConditionalAccess is { Message: null } x ? x : null;
        var names = ca is null ? null : CaReader.Names(d);
        var on = ca?.Policies.Where(b => b.Status == CaStatus.On).ToList() ?? new List<CaPolicy>();
        foreach (var p in d.Persons.Where(p => p.Entra is { AccountEnabled: true, OnPremisesSyncEnabled: false } && !p.IsGuest))
        {
            var e = p.Entra!;
            if (already.Contains(e.UserPrincipalName)
                || !security.RolesOf(e.Id).Any(r => r.Role == "Global Administrator" || r.RoleTemplateId == CaData.RoleGlobalAdmin))
            {
                continue;
            }

            var named = EmergencyWords.Any(w => e.UserPrincipalName.Contains(w, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(w, StringComparison.OrdinalIgnoreCase));
            var g = CaUser.Of(p, d);
            var under = ca is null || g is null ? (int?)null : on.Count(b => CaReader.Scope(b, g, names!, ca).Applies != CaApplies.No);
            if (!named && under is not 0)
            {
                continue;
            }

            yield return new(SuggestionKind.EmergencyAccount, e.UserPrincipalName, 1,
                "Cloud-only Global Administrator" + (under switch
                {
                    0 => ", valt onder geen enkel beleid dat aan staat",
                    null => " (Conditional Access niet geladen)",
                    _ => $", valt nog onder {under} beleid(en)",
                }) + (named ? "; de naam wijst op een noodaccount." : "."));
        }
    }

    /// <summary>Takes the chosen suggestions over in the settings (the caller saves them).</summary>
    public static void Apply(OrganisationConfig c, IEnumerable<Suggestion> chosen)
    {
        foreach (var s in chosen)
        {
            switch (s.Kind)
            {
                case SuggestionKind.AdDomain:
                    c.AdDomain = s.Value;
                    break;
                case SuggestionKind.SynchronisedOu when !c.SynchronisedOus.Contains(s.Value, StringComparer.OrdinalIgnoreCase):
                    c.SynchronisedOus.Add(s.Value);
                    break;
                case SuggestionKind.Department when c.SearchDepartment(s.Value) is null:
                    c.Departments.Add(new DepartmentMapping { Department = s.Value });
                    break;
                case SuggestionKind.Office when !c.Offices.Contains(s.Value, StringComparer.OrdinalIgnoreCase):
                    c.Offices.Add(s.Value);
                    break;
                case SuggestionKind.GroupPrefix when !c.GroupPrefixes.Contains(s.Value, StringComparer.OrdinalIgnoreCase):
                    c.GroupPrefixes.Add(s.Value);
                    break;
                case SuggestionKind.EmergencyAccount when !c.EmergencyAccounts.Contains(s.Value, StringComparer.OrdinalIgnoreCase):
                    c.EmergencyAccounts.Add(s.Value);
                    break;
            }
        }
    }

    /// <summary>The DNS name of the domain of a DN (DC=contoso,DC=local becomes contoso.local).</summary>
    public static string DomainOf(string? dn) => string.Join('.', (dn ?? string.Empty).Split(',')
        .Select(p => p.Trim()).Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p[3..].ToLowerInvariant()));

    /// <summary>The prefix of a group name: up to and including the first _ or - within the first six characters.</summary>
    public static string? Prefix(string name)
    {
        var i = name.IndexOfAny(new[] { '_', '-' });
        return i is > 0 and < 6 ? name[..(i + 1)] : null;
    }

    private static string MostUsed(IEnumerable<string> spellings) => spellings.GroupBy(x => x, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First().Key;
}
