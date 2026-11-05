using System.Windows;
using System.Windows.Threading;

namespace Moss365.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OpOnverwachteFout;
        base.OnStartup(e);
    }

    private static void OpOnverwachteFout(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Moss365 - onverwachte fout", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
