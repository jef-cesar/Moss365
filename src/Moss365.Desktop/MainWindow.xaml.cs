using System.Windows;
using Moss365.Core.Changes;
using Moss365.UI.Services;
using Moss365.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace Moss365.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        var environment = new WindowsEnvironment();

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<IAppEnvironment>(environment);
        services.AddSingleton<SignInStatus>();
        services.AddSingleton<IConnectionFactory, ConnectionFactory>();
        services.AddSingleton<ManagementState>();
        var provider = services.BuildServiceProvider();
        Resources.Add("services", provider);

        InitializeComponent();
        Closing += (_, e) => e.Cancel = !CanClose(provider.GetRequiredService<ManagementState>());
    }

    /// <summary>If something is still open (not saved, plan ready, form filled in), ask first.</summary>
    private bool CanClose(ManagementState state)
    {
        var open = state.Pending.Everything;
        if (open.Count == 0)
        {
            return true;
        }

        var list = string.Join(Environment.NewLine, open.Select(i => $"- {(i.Screen.Length > 0 ? i.Screen : i.FilePath)}: {i.Text}"));
        return MessageBox.Show(this, $"Er staat nog iets open:{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}Toch sluiten? Wat niet bewaard is, gaat verloren.",
            "Moss365", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
