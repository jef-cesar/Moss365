using Moss365.Core.Changes;

namespace Moss365.Core.Storage;

/// <summary>
/// Migrates the separate files from before 0.8 once to the database: the change log (logboek-*.jsonl), the scheduled
/// actions, the follow-up, the view choices and the connection settings. The old files stay as .bak. What is already in
/// the database is not overwritten; running again does nothing.
/// </summary>
public static class Migrate
{
    /// <summary>File in the data folder -> key of the document in the database.</summary>
    public static IReadOnlyList<(string File, string Document)> Documents { get; } = new[]
    {
        ("gepland.json", "gepland"),
        ("opvolging.json", "opvolging"),
        ("weergave.json", "weergave"),
        ("verbinding.json", "verbinding"),
    };

    public static IReadOnlyList<string> OfFiles(Database db, string dataFolder)
    {
        var messages = new List<string>();
        foreach (var (file, document) in Documents)
        {
            var path = Path.Combine(dataFolder, file);
            if (!File.Exists(path))
            {
                continue;
            }

            if (db.ReadDocument(document) is null)
            {
                db.SaveDocument(document, File.ReadAllText(path));
                messages.Add($"{file} overgezet.");
            }

            ToBin(path);
        }

        var logFolder = Path.Combine(dataFolder, "logboek");
        var logFiles = Directory.Exists(logFolder) ? Directory.GetFiles(logFolder, "logboek-*.jsonl") : Array.Empty<string>();
        if (logFiles.Length > 0)
        {
            // Old to new, so the order is kept
            var rules = new JsonlChangeLog(logFolder).Recent(int.MaxValue).Reverse().ToList();
            if (db.CountLog() == 0)
            {
                db.WriteLog(rules);
                messages.Add($"Logboek overgezet ({rules.Count} regels).");
            }

            foreach (var b in logFiles)
            {
                ToBin(b);
            }
        }

        return messages;
    }

    private static void ToBin(string path)
    {
        var bin = path + ".bak";
        if (File.Exists(bin))
        {
            bin = $"{path}.{DateTime.Now:yyyyMMddHHmmss}.bak";
        }

        File.Move(path, bin);
    }
}
