using System.ComponentModel;
using System.Diagnostics;

namespace Moss365.Connections.Check;

/// <summary>
/// Installs a component outside PowerShell from an official source, with fixed commands only: PowerShell 7 via winget,
/// the AD module (RSAT) as a Windows capability. Both may ask Windows for administrator rights (UAC).
/// </summary>
public static class ComponentInstaller
{
    public static async Task<string> InstallAsync(string component, CancellationToken cancel)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Mislukt: enkel op Windows.";
        }

        var info = component switch
        {
            SystemCheck.PowerShell7 => new ProcessStartInfo("winget")
            {
                ArgumentList = { "install", "--id", "Microsoft.PowerShell", "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements" },
                UseShellExecute = false,
                CreateNoWindow = true,
            },
            SystemCheck.Rsat => new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                Arguments = "-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0' | Out-Null\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            },
            _ => null,
        };
        if (info is null)
        {
            return "Mislukt: onbekend onderdeel.";
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("niet gestart");
            await process.WaitForExitAsync(cancel);
            return process.ExitCode == 0 ? "Gelukt" : $"Mislukt: code {process.ExitCode}.";
        }
        catch (Win32Exception ex)
        {
            // Also when the administrator says no to UAC
            return component == SystemCheck.PowerShell7 && ex.NativeErrorCode == 2
                ? "Mislukt: winget ontbreekt. Installeer 'App Installer' uit de Microsoft Store, of PowerShell 7 van aka.ms/powershell."
                : "Mislukt: " + ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            return "Mislukt: " + ex.Message;
        }
    }
}
