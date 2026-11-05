using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

/// <summary>A value of Department as it occurs in the data, with the number of users.</summary>
public sealed record DepartmentUse(string Value, int Count, bool InConfig, bool IsSpellingVariant);

/// <summary>Compares the choice lists of the configuration with what is really in AD (or otherwise Entra).</summary>
public static class OrganisationAnalysis
{
    /// <summary>All Department values of enabled AD users (or Entra members when there is no AD), with their
    /// count.</summary>
    public static IReadOnlyList<DepartmentUse> DepartmentsInData(InventoryData data, OrganisationConfig config)
    {
        var values = data.HasAd
            ? data.AdUsers.Where(g => g.Enabled).Select(g => g.Department)
            : data.EntraUsers.Where(g => g.AccountEnabled && !g.IsGuest).Select(g => g.Department);

        return values
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .GroupBy(w => w, StringComparer.Ordinal)
            .Select(g =>
            {
                var department = config.SearchDepartment(g.Key);
                var variant = department is not null && !string.Equals(department.Department, g.Key, StringComparison.Ordinal);
                return new DepartmentUse(g.Key, g.Count(), department is not null, variant);
            })
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>How many enabled users per department from the configuration (spaces and case do not count).</summary>
    public static IReadOnlyDictionary<string, int> CountPerDepartment(InventoryData data, OrganisationConfig config)
    {
        var output = config.Departments.GroupBy(d => d.Department, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var use in DepartmentsInData(data, config))
        {
            var department = config.SearchDepartment(use.Value);
            if (department is not null)
            {
                output[department.Department] += use.Count;
            }
        }

        return output;
    }

    /// <summary>All Office values of enabled AD users (or Entra members), with their count and whether they are in the
    /// list.</summary>
    public static IReadOnlyList<DepartmentUse> OfficesInData(InventoryData data, OrganisationConfig config)
    {
        var values = data.HasAd
            ? data.AdUsers.Where(g => g.Enabled).Select(g => g.Office)
            : data.EntraUsers.Where(g => g.AccountEnabled && !g.IsGuest).Select(g => g.OfficeLocation);
        var known = config.Offices.GroupBy(OrganisationConfig.Normalise).ToDictionary(g => g.Key, g => g.First());

        return values
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .GroupBy(w => w, StringComparer.Ordinal)
            .Select(g =>
            {
                var inList = known.TryGetValue(OrganisationConfig.Normalise(g.Key), out var listValue);
                return new DepartmentUse(g.Key, g.Count(), inList, inList && !string.Equals(listValue, g.Key, StringComparison.Ordinal));
            })
            .OrderByDescending(d => d.Count)
            .ThenBy(d => d.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
