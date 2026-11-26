using System.Diagnostics;
using System.IO;
using System.Text;
using Moss365.Core.Scheduling;
using Moss365.UI.Services;

namespace Moss365.Desktop;

/// <summary>
/// The task of reading without a window in the Windows Task Scheduler (R18), through schtasks.exe: made, replaced and
/// removed only when the user chooses so, under their own Windows account and without a password.
/// </summary>
internal sealed class WindowsTaskScheduler : IUnattendedScheduler
{
    private static string SchTasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public string? Plan(string tenantId, int hour)
    {
        if (Environment.ProcessPath is not { Length: > 0 } exe)
        {
            return "schtasks: ?";
        }

        var file = Path.Combine(Path.GetTempPath(), $"moss365-taak-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(file, Unattended.TaskXml(exe, tenantId, hour, DateTime.Today), Encoding.Unicode);
            return Run("/Create", "/TN", Unattended.TaskName(tenantId), "/XML", file, "/F").Error;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A temporary file; Windows cleans up the temp folder
            }
        }
    }

    public string? Remove(string tenantId)
        => PlannedHour(tenantId) is null ? null : Run("/Delete", "/TN", Unattended.TaskName(tenantId), "/F").Error;

    public int? PlannedHour(string tenantId)
    {
        var (output, error) = Run("/Query", "/TN", Unattended.TaskName(tenantId), "/XML");
        return error is null ? Unattended.HourOf(output) : null;
    }

    private static (string Output, string? Error) Run(params string[] args)
    {
        var info = new ProcessStartInfo(SchTasks) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("schtasks");
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            var error = errors.GetAwaiter().GetResult().Trim();
            return (output, process.ExitCode == 0 ? null : error.Length > 0 ? error : $"schtasks {process.ExitCode}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (string.Empty, ex.Message);
        }
    }
}
