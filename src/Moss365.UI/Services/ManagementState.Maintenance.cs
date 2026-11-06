using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Scripts;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>What one clean-up removed from a part, and where its archive is (if any).</summary>
public sealed record CleanupResult(CleanupPart Part, int Removed, string? Archive);

/// <summary>
/// Maintenance of the tenant database (R6): sizes, the clean-up according to the retention per part (always a preview
/// first; the change log and the access log are archived to a CSV before anything is removed), compacting and the
/// integrity check.
/// </summary>
public sealed partial class ManagementState
{
    /// <summary>Where archives of this tenant go (in its own export folder).</summary>
    public string ArchiveFolder => Path.Combine(ExportFolder, "Archief");

    /// <summary>What a clean-up would remove now, per part (nothing is removed).</summary>
    public IReadOnlyList<CleanupItem> CleanupPreview(DateTimeOffset now)
    {
        var r = Config.Retention;
        var output = new List<CleanupItem>();

        var daily = Math.Max(1, r.SnapshotsDailyDays);
        output.Add(new(CleanupPart.Snapshots, Database.SnapshotsToThin(now, daily).Count, now.AddDays(-daily),
            $"Ouder dan {daily} dagen: enkel de laatste momentopname van elke maand blijft. Elke datum die blijft, toont exact dezelfde stand."));

        var changeLog = Retention.Limit(now, Config.ChangeLogSaveYears);
        output.Add(new(CleanupPart.ChangeLog, Database.CountLog(changeLog), changeLog,
            $"Ouder dan {Math.Max(1, Config.ChangeLogSaveYears)} jaar. Eerst naar een CSV in {ArchiveFolder}, pas daarna uit de databank."));

        var access = now.AddYears(-Math.Max(1, r.AccessLogYears));
        output.Add(new(CleanupPart.AccessLog, Database.CountAccess(access), access,
            $"Ouder dan {Math.Max(1, r.AccessLogYears)} jaar. Eerst naar een CSV, pas daarna uit de databank."));

        var technical = now.AddDays(-Math.Max(1, r.TechnicalLogDays));
        output.Add(new(CleanupPart.TechnicalLog, Database.CountTechnicalLog(technical), technical,
            $"Ouder dan {Math.Max(1, r.TechnicalLogDays)} dagen (gebeurt ook vanzelf bij het starten)."));

        var runs = now.AddDays(-Math.Max(1, r.ScriptRunDays));
        output.Add(new(CleanupPart.ScriptRuns, ScriptRuns.Items(ScriptFolders).Count(x => x.Start < runs), runs,
            $"Ouder dan {Math.Max(1, r.ScriptRunDays)} dagen, met hun bestanden (gebeurt ook vanzelf bij het starten)."));

        var copies = now.AddDays(-Math.Max(1, r.MigrationCopyDays));
        output.Add(new(CleanupPart.MigrationCopies, Database.OldMigrationCopies(copies).Count, copies,
            $"Kopieën van voor een update van de databank, ouder dan {Math.Max(1, r.MigrationCopyDays)} dagen; de nieuwste blijft altijd."));
        return output;
    }

    /// <summary>
    /// Removes the chosen parts as the preview showed them, archives first where needed, then compacts the database.
    /// The clean-up itself goes to the change log.
    /// </summary>
    public IReadOnlyList<CleanupResult> Cleanup(IReadOnlyCollection<CleanupPart> parts, DateTimeOffset now)
    {
        if (IsHistoric)
        {
            throw new InvalidOperationException("Je bekijkt een stand uit het verleden: ga eerst terug naar nu.");
        }

        var r = Config.Retention;
        var output = new List<CleanupResult>();
        foreach (var part in parts.Distinct().OrderBy(p => p))
        {
            switch (part)
            {
                case CleanupPart.Snapshots:
                    output.Add(new(part, Database.RemoveSnapshots(Database.SnapshotsToThin(now, Math.Max(1, r.SnapshotsDailyDays)).Select(s => s.Id).ToList()), null));
                    break;

                case CleanupPart.ChangeLog:
                {
                    var limit = Retention.Limit(now, Config.ChangeLogSaveYears);
                    if (Database.CountLog(limit) == 0)
                    {
                        output.Add(new(part, 0, null));
                        break;
                    }

                    // Archive first: when writing the CSV fails, nothing is removed
                    var (file, count) = Retention.Archive(Database, limit, ArchiveFolder, now.LocalDateTime);
                    var removed = count > 0 && File.Exists(file) ? Database.RemoveLog(limit) : 0;
                    output.Add(new(part, removed, file));
                    break;
                }

                case CleanupPart.AccessLog:
                {
                    var limit = now.AddYears(-Math.Max(1, r.AccessLogYears));
                    var old = Database.ReadAccessOlder(limit);
                    if (old.Count == 0)
                    {
                        output.Add(new(part, 0, null));
                        break;
                    }

                    var file = Path.Combine(ArchiveFolder, $"Toegangslog-archief-tot-{limit:yyyy-MM-dd}-{now.LocalDateTime:yyyyMMdd-HHmmss}.csv");
                    Directory.CreateDirectory(ArchiveFolder);
                    File.WriteAllText(file, CsvWriter.Text(new[] { "Tijd", "Gebruiker", "Wat", "Details" },
                        old.Select(a => new[] { a.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), a.User, a.What, a.Details }), forExcel: true), new System.Text.UTF8Encoding(true));
                    output.Add(new(part, Database.RemoveAccess(limit), file));
                    break;
                }

                case CleanupPart.TechnicalLog:
                    output.Add(new(part, Database.CleanupTechnicalLog(now.AddDays(-Math.Max(1, r.TechnicalLogDays))), null));
                    break;

                case CleanupPart.ScriptRuns:
                    output.Add(new(part, ScriptRuns.Cleanup(ScriptFolders, now, Math.Max(1, r.ScriptRunDays)), null));
                    break;

                case CleanupPart.MigrationCopies:
                {
                    var removed = 0;
                    foreach (var f in Database.OldMigrationCopies(now.AddDays(-Math.Max(1, r.MigrationCopyDays))))
                    {
                        f.Delete();
                        removed++;
                    }

                    output.Add(new(part, removed, null));
                    break;
                }
            }
        }

        Database.Compact();
        ChangeLog.Write(new LogEntry(DateTimeOffset.Now, User, "Databank", "Moss365", "Cleanup", Tenant.Id, Tenant.Name,
            "Databank opgeruimd: " + string.Join(", ", output.Select(o => $"{o.Part} {o.Removed}")),
            output.ToDictionary(o => o.Part.ToString(), o => o.Removed.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            nameof(ResultStatus.Succeeded), string.Join("; ", output.Where(o => o.Archive is not null).Select(o => "archief " + Path.GetFileName(o.Archive)))));
        Log.Information("Databank opgeruimd: {Delen}", string.Join(", ", output.Select(o => $"{o.Part} {o.Removed}")));
        Changed?.Invoke();
        return output;
    }

    /// <summary>Gives the free space back to the disk.</summary>
    public void Compact()
    {
        Database.Compact();
        Log.Information("Databank gecomprimeerd");
    }

    /// <summary>PRAGMA integrity_check.</summary>
    public string IntegrityCheck()
    {
        var outcome = Database.IntegrityCheck();
        Log.Information("Integriteitscontrole: {Resultaat}", outcome == "ok" ? "ok" : "fouten");
        return outcome;
    }
}
