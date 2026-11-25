using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Reporting;

/// <summary>One difference between two tenants.</summary>
public sealed record TenantDifference(string Area, string Item, string This, string Other);

/// <summary>
/// Compare tenants (R14, 0.37): the Conditional Access baseline (by policy name: state and the main settings, without
/// ids of users or groups) and the settings of Moss365. Only the differences are shown; nothing is stored or mixed.
/// </summary>
public static class TenantComparison
{
    public const string Missing = "(ontbreekt)";

    /// <summary>A policy in words, without tenant-specific ids.</summary>
    public static string Summary(CaPolicy p)
    {
        var parts = new List<string> { p.Status.ToString() };
        if (p.UsersIn.Contains("All", StringComparer.OrdinalIgnoreCase)) parts.Add("alle gebruikers");
        if (p.RolesIn.Count > 0) parts.Add($"{p.RolesIn.Count} rol(len)");
        if (p.GuestsIn) parts.Add("gasten");
        if (p.AppsIn.Count > 0) parts.Add("apps: " + string.Join(",", p.AppsIn.Select(a => a.Equals("All", StringComparison.OrdinalIgnoreCase) ? "alle" : "keuze").Distinct()));
        if (p.ClientApps.Count > 0) parts.Add("clients: " + string.Join(",", p.ClientApps.OrderBy(c => c, StringComparer.Ordinal)));
        if (p.PlatformsIn.Count > 0) parts.Add("platformen: " + string.Join(",", p.PlatformsIn.OrderBy(c => c, StringComparer.Ordinal)));
        if (p.LocationsIn.Count > 0 || p.LocationsOff.Count > 0) parts.Add("locaties");
        if (p.SignInRisk.Count > 0) parts.Add("aanmeldrisico " + string.Join(",", p.SignInRisk));
        if (p.UsersRisk.Count > 0) parts.Add("gebruikersrisico " + string.Join(",", p.UsersRisk));
        if (p.Checks.Count > 0) parts.Add("controles: " + string.Join($" {p.Operator} ", p.Checks.OrderBy(c => c, StringComparer.Ordinal)));
        if (p.StrengthName is { Length: > 0 } s) parts.Add("sterkte " + s);
        if (p.SignInFrequency is { Length: > 0 } f) parts.Add("frequentie " + f);
        return string.Join("; ", parts);
    }

    public static IReadOnlyList<TenantDifference> Compare(InventoryData a, OrganisationConfig ca, InventoryData b, OrganisationConfig cb)
    {
        var output = new List<TenantDifference>();
        var pa = a.ConditionalAccess?.Policies ?? Array.Empty<CaPolicy>();
        var pb = b.ConditionalAccess?.Policies ?? Array.Empty<CaPolicy>();
        if (a.ConditionalAccess is null || b.ConditionalAccess is null)
        {
            output.Add(new TenantDifference("Conditional Access", "Gelezen", a.ConditionalAccess is null ? "nee" : "ja", b.ConditionalAccess is null ? "nee" : "ja"));
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
                output.Add(new TenantDifference("Instellingen", item, x, y));
            }
        }

        Setting("Rollen in Moss365", ca.RolesModeEffective.ToString(), cb.RolesModeEffective.ToString());
        Setting("Wachtwoord verloopt na (dagen)", ca.PasswordMaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture), cb.PasswordMaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Setting("Functieprofielen", string.Join(", ", ca.JobProfiles.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)),
            string.Join(", ", cb.JobProfiles.Select(p => p.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)));
        Setting("Bewaartermijnen", System.Text.Json.JsonSerializer.Serialize(ca.Retention), System.Text.Json.JsonSerializer.Serialize(cb.Retention));
        return output;
    }

    private static string Text(bool? value) => value switch { true => "aan", false => "uit", null => "onbekend" };
}
