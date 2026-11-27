<#
.SYNOPSIS
Starts Moss365.exe and checks that the main window opens without the window of an unexpected error.

.DESCRIPTION
For the build server (build.yml) and the release check: 0.39.0 stopped at the start with an empty error, because the
screens of BlazorWebView 10 need the Windows SDK projection. The tests of the code cannot see that; only starting the
real exe can. The script waits until the main window is there, then a while longer for the screens, and fails when a
window of an unexpected error appears (in any language of the app), when the process stops, or when the main window
never comes. It prints the text of such an error window and saves a screenshot.

.PARAMETER Exe
The Moss365.exe to start.

.PARAMETER Seconds
How long to wait for the main window, and how long the app must keep running without an error after that.

.PARAMETER Screenshot
Where to save a screenshot of the screen at the end (optional).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [int]$Seconds = 60,
    [string]$Screenshot
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class Moss365StartTest
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    private static string Text(IntPtr hwnd)
    {
        var text = new StringBuilder(4096);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>The visible top-level windows of a process: the title, then the texts inside it.</summary>
    public static List<string[]> Windows(int processId)
    {
        var found = new List<string[]>();
        EnumWindows((hwnd, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == processId && IsWindowVisible(hwnd))
            {
                var texts = new List<string> { Text(hwnd) };
                EnumChildWindows(hwnd, (child, l2) => { var t = Text(child); if (t.Length > 0) texts.Add(t); return true; }, IntPtr.Zero);
                found.Add(texts.ToArray());
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

# The title of the error window in the five languages of the app (desktop.error.title)
$errorTitles = 'unexpected error', 'onverwachte fout', 'unerwarteter Fehler', 'erreur inattendue', 'erro inesperado'
# The title of the main window: "Moss365 <middle dot> The Moss Directory" (this script stays ASCII)
$mainTitle = 'Moss365 * The Moss Directory'

function Save-Screenshot {
    if (-not $Screenshot) { return }
    try {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bitmap.Save($Screenshot)
        Write-Host "Screenshot: $Screenshot"
    }
    catch {
        Write-Host "No screenshot: $($_.Exception.Message)"
    }
}

function Find-Error([int]$processId) {
    foreach ($window in [Moss365StartTest]::Windows($processId)) {
        foreach ($title in $errorTitles) {
            if ($window[0] -like "*$title*") { return $window }
        }
    }
    return $null
}

$process = Start-Process -FilePath $Exe -PassThru
Write-Host "Started $Exe (process $($process.Id))"
$failure = $null
$mainSeen = $false
try {
    # First the main window, then the screens get the same time to start
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if ($process.HasExited) { $failure = "Moss365 stopped at the start (exit code $($process.ExitCode))."; break }
        if ($err = Find-Error $process.Id) { $failure = "The window of an unexpected error appeared:`n" + ($err -join "`n"); break }
        if (-not $mainSeen -and ([Moss365StartTest]::Windows($process.Id) | Where-Object { $_[0] -like $mainTitle })) {
            $mainSeen = $true
            Write-Host 'The main window is there; waiting for the screens.'
            $deadline = (Get-Date).AddSeconds($Seconds)
        }
    }

    if (-not $failure -and -not $mainSeen) {
        $titles = ([Moss365StartTest]::Windows($process.Id) | ForEach-Object { $_[0] }) -join ', '
        $failure = "The main window did not open within $Seconds seconds (windows: $titles)."
    }
}
finally {
    Save-Screenshot
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

if ($failure) {
    Write-Host "::error::$($failure -replace "`n", ' | ')"
    throw $failure
}

Write-Host 'Moss365 started: the main window opened and no error appeared.'
