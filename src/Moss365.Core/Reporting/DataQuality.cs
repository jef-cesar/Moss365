using Moss365.Core.Localization;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

namespace Moss365.Core.Reporting;

/// <summary>Per department: how many active employees have each field filled in.</summary>
public sealed record QualityRow(string Department, int People, int JobTitle, int Office, int Manager, int Phone, int WithDepartment)
{
    /// <summary>The share of filled-in fields (0 to 100).</summary>
    public int Score => People == 0 ? 100 : (int)Math.Round(100.0 * (JobTitle + Office + Manager + Phone + WithDepartment) / (5.0 * People));

    public static int Percent(int part, int total) => total == 0 ? 100 : (int)Math.Round(100.0 * part / total);
}

/// <summary>
/// Data quality (R14, 0.37): job title, department, office, manager and phone of the active employees. Good data makes
/// dynamic groups, the address book and the job profiles reliable.
/// </summary>
public static class DataQuality
{
    public const string NoDepartment = "(geen dienst)";

    public static bool HasJobTitle(Person p) => First(p.Ad?.Title, p.Entra?.JobTitle).Length > 0;
    public static bool HasOffice(Person p) => First(p.Ad?.Office, p.Entra?.OfficeLocation).Length > 0;
    public static bool HasManager(Person p) => First(p.Ad?.Manager, p.Entra?.ManagerId).Length > 0;
    public static bool HasPhone(Person p) => First(p.Ad?.Phone, p.Ad?.Mobile, p.Entra?.Phone, p.Entra?.Mobile).Length > 0;
    public static bool HasDepartment(Person p) => p.Department.Length > 0;

    /// <summary>The fields that are missing for a person, in words.</summary>
    public static IReadOnlyList<string> Missing(Person p)
    {
        var output = new List<string>();
        if (!HasJobTitle(p)) output.Add("functie");
        if (!HasDepartment(p)) output.Add("dienst");
        if (!HasOffice(p)) output.Add("kantoor");
        if (!HasManager(p)) output.Add("manager");
        if (!HasPhone(p)) output.Add("telefoon");
        return output;
    }

    /// <summary>Per department (the worst first) and a total row at the end.</summary>
    public static IReadOnlyList<QualityRow> PerDepartment(KpiSource source)
    {
        static QualityRow Row(string name, IReadOnlyCollection<Person> people) => new(name, people.Count,
            people.Count(HasJobTitle), people.Count(HasOffice), people.Count(HasManager), people.Count(HasPhone), people.Count(HasDepartment));

        var rows = source.Active.GroupBy(p => p.Department.Length > 0 ? p.Department : NoDepartment, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => Row(g.Key, g.ToList()))
            .OrderBy(r => r.Score).ThenBy(r => r.Department, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        rows.Add(Row(Texts.T["report.total"], source.Active));
        return rows;
    }

    private static string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
