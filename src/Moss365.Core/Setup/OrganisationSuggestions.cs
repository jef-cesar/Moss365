using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Setup;

public enum SuggestionKind
{
    AdDomain,
    SynchronisedOu,
    Department,
    Office,
    GroupPrefix,
    EmergencyAccount,
    Company,
    NewUsersOu,
    OffboardingOu,
    GroupsOu,
    LocalPart,
    DisplayName,
    LicenceName,
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
            output.Add(new(SuggestionKind.AdDomain, domain.Key, domain.Count(), string.IsNullOrWhiteSpace(c.AdDomain) ? Texts.T["code.organisation-suggestions.not-filled-in-yet"] : Texts.T.Format("code.organisation-suggestions.filled-in-is", c.AdDomain)));
        }

        // The OUs whose accounts are synchronised to Entra (the highest OU per branch)
        var synced = staff.Where(p => p.Ad is not null && p.Entra?.OnPremisesSyncEnabled == true).Select(p => p.Ad!.OuPath).Where(o => o.Length > 0).ToList();
        foreach (var top in synced.GroupBy(o => o.Split('/')[0], StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()))
        {
            if (!c.SynchronisedOus.Any(s => DnHelp.FallsUnder(top.Key, new[] { s }) || DnHelp.FallsUnder(s, new[] { top.Key })))
            {
                output.Add(new(SuggestionKind.SynchronisedOu, top.Key, top.Count(), Texts.T["code.organisation-suggestions.accounts-from-this-ou-are"]));
            }
        }

        // Departments and offices that accounts use but the settings do not know
        foreach (var g in staff.Select(p => p.Department).Where(x => x.Length > 0).GroupBy(OrganisationConfig.Normalise).OrderByDescending(g => g.Count()))
        {
            if (c.SearchDepartment(g.Key) is null)
            {
                output.Add(new(SuggestionKind.Department, MostUsed(g), g.Count(), Texts.T["code.organisation-suggestions.set-on-accounts-not-in"]));
            }
        }

        var offices = c.Offices.Select(OrganisationConfig.Normalise).ToHashSet();
        foreach (var g in staff.Select(p => (p.Ad?.Office ?? p.Entra?.OfficeLocation ?? string.Empty).Trim()).Where(x => x.Length > 0)
                     .GroupBy(OrganisationConfig.Normalise).OrderByDescending(g => g.Count()))
        {
            if (!offices.Contains(g.Key))
            {
                output.Add(new(SuggestionKind.Office, MostUsed(g), g.Count(), Texts.T["code.organisation-suggestions.set-on-accounts-not-in-2"]));
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
                output.Add(new(SuggestionKind.GroupPrefix, g.Key, g.Count(), Texts.T["code.organisation-suggestions.start-of-group-names"]));
            }
        }

        output.AddRange(MoreFor(d, c, staff));
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
                Texts.T.Format(named ? "suggest.emergency.named" : "suggest.emergency", under switch
                {
                    0 => Texts.T["suggest.emergency.none"],
                    null => Texts.T["suggest.emergency.no-ca"],
                    _ => Texts.T.Format("suggest.emergency.under", under),
                }));
        }
    }

    /// <summary>R9: the company, the OUs, the naming and the names of the licences.</summary>
    private static IEnumerable<Suggestion> MoreFor(InventoryData d, OrganisationConfig c, IReadOnlyList<Person> staff)
    {
        var company = staff.Select(p => (p.Ad?.Company is { Length: > 0 } a ? a : p.Entra?.CompanyName ?? string.Empty).Trim()).Where(x => x.Length > 0)
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (company is not null && !string.Equals(company.Key, c.Company, StringComparison.OrdinalIgnoreCase))
        {
            yield return new(SuggestionKind.Company, company.Key, company.Count(), Texts.T.Format("code.organisation-suggestions.set-on-most-accounts-filled", c.Company));
        }

        // The OU with the most enabled accounts (new users), with the most disabled accounts (offboarding), with the most groups
        var enabledOu = d.AdUsers.Where(u => u.Enabled && u.OuPath.Length > 0).GroupBy(u => u.OuPath, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (enabledOu is not null && !c.Populations.Any(p => string.Equals(p.Ou, enabledOu.Key, StringComparison.OrdinalIgnoreCase)))
        {
            var domain = enabledOu.Select(u => u.UserPrincipalName.Contains('@') ? u.UserPrincipalName[(u.UserPrincipalName.IndexOf('@') + 1)..] : string.Empty)
                .Where(x => x.Length > 0).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? string.Empty;
            yield return new(SuggestionKind.NewUsersOu, enabledOu.Key, enabledOu.Count(),
                Texts.T.Format("suggest.new-users-ou", domain.Length > 0 ? $" (UPN @{domain})" : string.Empty));
        }

        var disabledOu = d.AdUsers.Where(u => !u.Enabled && u.OuPath.Length > 0).GroupBy(u => u.OuPath, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (disabledOu is not null && disabledOu.Count() >= 2 && string.IsNullOrWhiteSpace(c.Offboarding?.Ou))
        {
            yield return new(SuggestionKind.OffboardingOu, disabledOu.Key, disabledOu.Count(), Texts.T["code.organisation-suggestions.the-ou-with-the-most"]);
        }

        var groupsOu = d.AdGroupsByDn.Values.Where(g => g.OuPath.Length > 0 && !g.OuPath.Equals("Builtin", StringComparison.OrdinalIgnoreCase) && !g.OuPath.Equals("Users", StringComparison.OrdinalIgnoreCase))
            .GroupBy(g => g.OuPath, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (groupsOu is not null && string.IsNullOrWhiteSpace(c.GroupsOu))
        {
            yield return new(SuggestionKind.GroupsOu, groupsOu.Key, groupsOu.Count(), Texts.T["code.organisation-suggestions.the-ou-with-the-most-2"]);
        }

        // The naming most accounts follow (only when a clear majority does)
        if (NamingSettings.Detect(d) is { } n)
        {
            if (n.Local != c.Naming.LocalPart && n.LocalCount * 10 >= n.Accounts * 6)
            {
                yield return new(SuggestionKind.LocalPart, n.Local.ToString(), n.LocalCount,
                    Texts.T.Format("code.organisation-suggestions.account-names-like-of-accounts", NamingSettings.Example(n.Local), n.LocalCount, n.Accounts, NamingSettings.Example(c.Naming.LocalPart)));
            }

            if (n.Display != c.Naming.DisplayName && n.DisplayCount * 10 >= n.Accounts * 6)
            {
                yield return new(SuggestionKind.DisplayName, n.Display.ToString(), n.DisplayCount,
                    Texts.T.Format("code.organisation-suggestions.display-names-like-of-accounts", NamingSettings.Example(n.Display), n.DisplayCount, n.Accounts, NamingSettings.Example(c.Naming.DisplayName)));
            }
        }

        // Readable names for the subscriptions that have none yet
        foreach (var l in d.Licences.Where(l => l.SkuPartNumber.Length > 0 && !c.Licences.Any(x => string.Equals(x.Sku, l.SkuPartNumber, StringComparison.OrdinalIgnoreCase))))
        {
            yield return new(SuggestionKind.LicenceName, l.SkuPartNumber, l.Assigned, $"Leesbare naam: {LicenceNames.For(l.SkuPartNumber)}.");
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
                case SuggestionKind.Company:
                    c.Company = s.Value;
                    break;
                case SuggestionKind.NewUsersOu when !c.Populations.Any(p => string.Equals(p.Ou, s.Value, StringComparison.OrdinalIgnoreCase)):
                    c.Populations.Add(new Population { Name = s.Value.Split('/')[0] is { Length: > 0 } first ? first : Texts.T["suggest.employees"], Ou = s.Value, UpnDomain = UpnDomainOf(s.Explanation) });
                    break;
                case SuggestionKind.OffboardingOu:
                    c.Offboarding ??= new OffboardingRule();
                    c.Offboarding.Ou = s.Value;
                    break;
                case SuggestionKind.GroupsOu:
                    c.GroupsOu = s.Value;
                    break;
                case SuggestionKind.LocalPart when Enum.TryParse<LocalPartPattern>(s.Value, out var local):
                    c.Naming.LocalPart = local;
                    break;
                case SuggestionKind.DisplayName when Enum.TryParse<DisplayNamePattern>(s.Value, out var display):
                    c.Naming.DisplayName = display;
                    break;
                case SuggestionKind.LicenceName when !c.Licences.Any(x => string.Equals(x.Sku, s.Value, StringComparison.OrdinalIgnoreCase)):
                    c.Licences.Add(new LicenceChoice { Sku = s.Value, Name = LicenceNames.For(s.Value) });
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

    private static string UpnDomainOf(string explanation)
    {
        var m = System.Text.RegularExpressions.Regex.Match(explanation, @"UPN @([^\s)]+)");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }
}

/// <summary>Readable names of the usual Microsoft 365 subscriptions (the SKU part number otherwise).</summary>
public static class LicenceNames
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPB"] = "Microsoft 365 Business Premium",
        ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
        ["O365_BUSINESS"] = "Microsoft 365 Apps for business",
        ["OFFICESUBSCRIPTION"] = "Microsoft 365 Apps for enterprise",
        ["ENTERPRISEPACK"] = "Office 365 E3",
        ["ENTERPRISEPREMIUM"] = "Office 365 E5",
        ["STANDARDPACK"] = "Office 365 E1",
        ["SPE_E3"] = "Microsoft 365 E3",
        ["SPE_E5"] = "Microsoft 365 E5",
        ["SPE_F1"] = "Microsoft 365 F3",
        ["DESKLESSPACK"] = "Office 365 F3",
        ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)",
        ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)",
        ["EXCHANGEDESKLESS"] = "Exchange Online Kiosk",
        ["AAD_PREMIUM"] = "Microsoft Entra ID P1",
        ["AAD_PREMIUM_P2"] = "Microsoft Entra ID P2",
        ["EMS"] = "Enterprise Mobility + Security E3",
        ["EMSPREMIUM"] = "Enterprise Mobility + Security E5",
        ["INTUNE_A"] = "Microsoft Intune Plan 1",
        ["POWER_BI_STANDARD"] = "Power BI (free)",
        ["POWER_BI_PRO"] = "Power BI Pro",
        ["PROJECTPROFESSIONAL"] = "Project Plan 3",
        ["VISIOCLIENT"] = "Visio Plan 2",
        ["TEAMS_EXPLORATORY"] = "Microsoft Teams Exploratory",
        ["FLOW_FREE"] = "Power Automate (free)",
        ["STANDARDWOFFPACK_FACULTY"] = "Office 365 A1 for faculty",
        ["STANDARDWOFFPACK_STUDENT"] = "Office 365 A1 for students",
        ["M365EDU_A3_FACULTY"] = "Microsoft 365 A3 for faculty",
        ["M365EDU_A3_STUDENT"] = "Microsoft 365 A3 for students",
        ["M365EDU_A5_FACULTY"] = "Microsoft 365 A5 for faculty",
    };

    public static string For(string skuPartNumber)
        => Known.TryGetValue(skuPartNumber, out var name) ? name : skuPartNumber.Replace('_', ' ');
}
