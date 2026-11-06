using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Moss365.Desktop;

public partial class App : Application
{
    /// <summary>Microsoft's bootstrapper for the WebView2 runtime (evergreen).</summary>
    private const string WebView2Bootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnexpectedError;
        if (!WebView2Present() && !InstallWebView2())
        {
            StartupUri = null;
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
    }

    private static bool WebView2Present()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The screens need the WebView2 runtime: download Microsoft's installer and run it, after confirmation.</summary>
    private static bool InstallWebView2()
    {
        if (MessageBox.Show("De schermen van Moss365 hebben de Microsoft Edge WebView2 Runtime nodig, en die ontbreekt op deze pc.\n\n"
                            + "Nu downloaden van Microsoft en installeren?", "Moss365", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            var file = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                File.WriteAllBytes(file, http.GetByteArrayAsync(WebView2Bootstrapper).GetAwaiter().GetResult());
            }

            using var process = Process.Start(new ProcessStartInfo(file, "/install") { UseShellExecute = true }) ?? throw new InvalidOperationException("niet gestart");
            process.WaitForExit();
            if (WebView2Present())
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TaskCanceledException)
        {
            MessageBox.Show("Installeren mislukt: " + ex.Message, "Moss365", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        MessageBox.Show("De WebView2 Runtime is nog niet gevonden. Installeer hem via aka.ms/webview2 en start Moss365 opnieuw.", "Moss365", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void OnUnexpectedError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Moss365 - onverwachte fout", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
