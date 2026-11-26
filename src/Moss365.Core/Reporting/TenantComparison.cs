using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Reporting;

/// <summary>One difference between two tenants.</summary>
public sealed record TenantDifference(string Area, string Item, string This, string Other);

/// <summary>
/// Compare tenants (R14, 0.37): the Conditional Access baseline (by policy name: state and the main settings, without
/// ids of users or groups) and the settings of Moss365. Only the differences are shown; nothing is stored or mixed.
/// </summary>
public static class TenantComparison
{
    public static string Missing => Texts.T["compare.missing"];

    /// <summary>A policy in words, without tenant-specific ids.</summary>
    public static string Summary(CaPolicy p)
    {
        var parts = new List<string> { p.Status.ToString() };
        if (p.UsersIn.Contains("All", StringComparer.OrdinalIgnoreCase)) parts.Add(Texts.T["compare.all-users"]);
        if (p.RolesIn.Count > 0) parts.Add(Texts.T.Format("compare.roles", p.RolesIn.Count));
        if (p.GuestsIn) parts.Add(Texts.T["compare.guests"]);
        if (p.AppsIn.Count > 0) parts.Add("apps: " + string.Join(",", p.AppsIn.Select(a => a.Equals("All", StringComparison.OrdinalIgnoreCase) ? Texts.T["compare.all"] : Texts.T["compare.choice"]).Distinct()));
        if (p.ClientApps.Count > 0) parts.Add("clients: " + string.Join(",", p.ClientApps.OrderBy(c => c, StringComparer.Ordinal)));
        if (p.PlatformsIn.Count > 0) parts.Add(Texts.T["compare.platforms"] + string.Join(",", p.PlatformsIn.OrderBy(c => c, StringComparer.Ordinal)));
        if (p.LocationsIn.Count > 0 || p.LocationsOff.Count > 0) parts.Add(Texts.T["compare.locations"]);
        if (p.SignInRisk.Count > 0) parts.Add(Texts.T["compare.sign-in-risk"] + string.Join(",", p.SignInRisk));
        if (p.UsersRisk.Count > 0) parts.Add(Texts.T["compare.user-risk"] + string.Join(",", p.UsersRisk));
        if (p.Checks.Count > 0) parts.Add(Texts.T["compare.controls"] + string.Join($" {p.Operator} ", p.Checks.OrderBy(c => c, StringComparer.Ordinal)));
        if (p.StrengthName is { Length: > 0 } s) parts.Add(Texts.T["compare.strength"] + s);
        if (p.SignInFrequency is { Length: > 0 } f) parts.Add(Texts.T["compare.frequency"] + f);
        return string.Join("; ", parts);
    }

    public static IReadOnlyList<TenantDifference> Compare(InventoryData a, OrganisationConfig ca, InventoryData b, OrganisationConfig cb)
    {
        var output = new List<TenantDifference>();
        var pa = a.ConditionalAccess?.Policies ?? Array.Empty<CaPolicy>();
        var pb = b.ConditionalAccess?.Policies ?? Array.Empty<CaPolicy>();
        if (a.ConditionalAccess is null || b.ConditionalAccess is null)
        {
            output.Add(new TenantDifference("Conditional Access", Texts.T["compare.read"], a.ConditionalAccess is null ? Texts.T["compare.no"] : Texts.T["compare.yes"], b.ConditionalAccess is null ? Texts.T["compare.no"] : Texts.T["compare.yes"]));
        }
        else
        {
            var da = pa.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Summary(g.First()), StringComparer.OrdinalIgnoreCase);
            var db = pb.GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => Summary(g.First()), StringComparer.OrdinalIgnoreCase);
            foreach (var name in da.Keys.Union(db.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
            {
                var x = da.GetValueOrDefault(name) ?? Missing;
                var y = db.GetValueOrDefault(name) ?? Missing;
                if (!string.Equals(x, y, StringComparison.Ordinal))
                {
                    output.Add(new TenantDifference("Conditional Access", name, x, y));
                }
            }

            if (a.ConditionalAccess.SecurityDefaults != b.ConditionalAccess.SecurityDefaults)
            {
                output.Add(new TenantDifference("Conditional Access", "Security defaults", Text(a.ConditionalAccess.SecurityDefaults), Text(b.ConditionalAccess.SecurityDefaults)));
            }
        }

        void Setting(string item, string x, string y)
        {
            if (!string.Equals(x, y, StringComparison.Ordinal))
            {
                output.Add(new TenantDifference(Texts.T["compare.settings"], item, x, y));
            }
        }

        Setting(Texts.T["compare.roles-mode"], ca.RolesModeEffective.ToString(), cb.RolesModeEffective.ToString());
        Setting(Texts.T["compare.password-age"], ca.PasswordMaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture), cb.PasswordMaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Setting(Texts.T["compare.job-profiles"], string.Join(", ", ca.JobProfiles.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)),
            string.Join(", ", cb.JobProfiles.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)));
        Setting(Texts.T["compare.retention"], System.Text.Json.JsonSerializer.Serialize(ca.Retention), System.Text.Json.JsonSerializer.Serialize(cb.Retention));
        return output;
    }

    private static string Text(bool? value) => value switch { true => "aan", false => "uit", null => "onbekend" };
}
