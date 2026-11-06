using System.Diagnostics;
using System.IO;
using Moss365.UI.Services;

namespace Moss365.Desktop;

/// <summary>The folders of the app on Windows; exports go to Documents\Moss365.</summary>
public sealed class WindowsEnvironment : IAppEnvironment
{
    private static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moss365");

    /// <summary>The folder of a previous installation that was migrated at this start, or null.</summary>
    public static string? MigratedOf { get; } = MigrateOldInstallation();

    private static string? MigrateOldInstallation()
    {
        try
        {
            return Moss365.Core.Storage.OldInstallation.MigrateIfNeeded(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Local);
        }
        catch (Exception)
        {
            return null; // Migrating is a convenience; if it fails, the app simply starts empty
        }
    }

    public WindowsEnvironment()
    {
        _ = MigratedOf;
    }

    public string Root => Local;

    public string ConfigDefaultPath { get; } = DefaultConfig();

    public string ExportFolder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Moss365");

    /// <summary>
    /// The bundled organisatie.json next to the exe; if it is missing (e.g. Moss365.exe started straight from the zip),
    /// the copy inside the exe, unpacked in the temporary folder.
    /// </summary>
    private static string DefaultConfig()
    {
        var besideExe = Path.Combine(AppContext.BaseDirectory, "config", "organisatie.json");
        if (File.Exists(besideExe))
        {
            return besideExe;
        }

        try
        {
            using var source = typeof(WindowsEnvironment).Assembly.GetManifestResourceStream("organisatie.json");
            if (source is null)
            {
                return besideExe;
            }

            var temp = Path.Combine(Path.GetTempPath(), "Moss365");
            Directory.CreateDirectory(temp);
            var copy = Path.Combine(temp, "organisatie-meegeleverd.json");
            using (var target = File.Create(copy))
            {
                source.CopyTo(target);
            }

            return copy;
        }
        catch (Exception)
        {
            return besideExe;
        }
    }

    public void ShowFile(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        else if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
    }
}
