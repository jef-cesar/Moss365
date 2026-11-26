using System.ComponentModel;
using System.Diagnostics;
using Moss365.Core.Localization;

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
            return Texts.T["installer.windows-only"];
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
            return Texts.T["code.component-installer.failed-unknown-component"];
        }

        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException(Texts.T["code.component-installer.not-started"]);
            await process.WaitForExitAsync(cancel);
            return process.ExitCode == 0 ? Texts.T["report.succeeded"] : Texts.T.Format("code.component-installer.failed-code", process.ExitCode);
        }
        catch (Win32Exception ex)
        {
            // Also when the administrator says no to UAC
            return component == SystemCheck.PowerShell7 && ex.NativeErrorCode == 2
                ? Texts.T["code.component-installer.failed-winget-is-missing-install"]
                : Texts.T.Format("code.component-installer.failed", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Texts.T.Format("code.component-installer.failed", ex.Message);
        }
    }
}
