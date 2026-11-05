using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Core.Storage;

/// <summary>
/// The retention of the change log (2 years by default): first archive older entries to a CSV in the export folder, and
/// only then, after a confirmation, remove them from the database. Never delete silently.
/// </summary>
public static class Retention
{
    public static DateTimeOffset Limit(DateTimeOffset now, int years) => now.AddYears(-Math.Max(1, years));

    /// <summary>Writes the entries older than the limit to a CSV; returns the path and the count.</summary>
    public static (string FilePath, int Count) Archive(Database db, DateTimeOffset limit, string exportFolder, DateTime now)
    {
        var rules = db.ReadLog(int.MaxValue, limit).Reverse().ToList();
        var path = Path.Combine(exportFolder, $"Logboek-archief-tot-{limit:yyyy-MM-dd}-{now:yyyyMMdd-HHmmss}.csv");
        CsvWriter.Write(path,
            new[] { "Tijd", "Gebruiker", "Context", "Systeem", "Actie", "Doel", "Naam", "Omschrijving", "Status", "Melding", "Parameters", "Vorige" },
            rules.Select(r => new[]
            {
                r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.User, r.Context, r.TargetSystem, r.ChangeAction, r.Target, r.TargetName, r.Description, r.Status, r.Message,
                string.Join("; ", r.Parameters.Select(p => $"{p.Key}={p.Value}")),
                r.Previous is null ? string.Empty : string.Join("; ", r.Previous.Select(p => $"{p.Key}={p.Value}")),
            }),
            forExcel: true);
        return (path, rules.Count);
    }
}
