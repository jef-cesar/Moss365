namespace Moss365.Core.Storage;

/// <summary>
/// Migrates an installation of the internal predecessor (0.9). It lived in another folder under %LOCALAPPDATA%; the app
/// recognises it by its content (the database and the organisation settings), not by its name. It only copies: the old
/// folder stays until the administrator removes it.
/// </summary>
public static class OldInstallation
{
    /// <summary>The file in the new data folder that says where the data came from.</summary>
    public const string Marker = "imported-from.txt";

    /// <summary>A folder next to the new data folder with a database and organisation settings, or null.</summary>
    public static string? Find(string baseline, string newFolder)
    {
        if (!Directory.Exists(baseline))
        {
            return null;
        }

        var newValue = Path.GetFullPath(newFolder).TrimEnd(Path.DirectorySeparatorChar);
        return Directory.EnumerateDirectories(baseline)
            .Where(m => !string.Equals(Path.GetFullPath(m).TrimEnd(Path.DirectorySeparatorChar), newValue, StringComparison.OrdinalIgnoreCase))
            .Where(m => File.Exists(Path.Combine(m, Database.FileName)) && File.Exists(Path.Combine(m, "organisatie.json")))
            .OrderByDescending(m => File.GetLastWriteTimeUtc(Path.Combine(m, Database.FileName)))
            .FirstOrDefault();
    }

    /// <summary>
    /// Copies the old installation to the new data folder, when that has no database yet. Returns the number of copied
    /// files (0 when there was nothing to do).
    /// </summary>
    public static int Copy(string source, string target)
    {
        if (File.Exists(Path.Combine(target, Database.FileName)))
        {
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var to = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            using (var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var write = File.Create(to))
            {
                read.CopyTo(write);
            }

            count++;
        }

        File.WriteAllText(Path.Combine(target, Marker), $"{source}{Environment.NewLine}{DateTime.Now:yyyy-MM-dd HH:mm}{Environment.NewLine}");
        return count;
    }

    /// <summary>At startup: find an old installation and migrate it when the new data folder is still empty.</summary>
    public static string? MigrateIfNeeded(string baseline, string newFolder)
    {
        if (File.Exists(Path.Combine(newFolder, Database.FileName)) || Find(baseline, newFolder) is not { } old)
        {
            return null;
        }

        return Copy(old, newFolder) > 0 ? old : null;
    }
}
