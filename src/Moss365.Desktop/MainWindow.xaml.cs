using System.Windows;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Services;
using Moss365.Connections;
using Microsoft.Extensions.DependencyInjection;

namespace Moss365.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        var omgeving = new WindowsOmgeving();

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<IOmgeving>(omgeving);
        services.AddSingleton<AanmeldStatus>();
        services.AddSingleton<IVerbindingsFabriek, VerbindingsFabriek>();
        services.AddSingleton<BeheerState>();
        var provider = services.BuildServiceProvider();
        Resources.Add("services", provider);

        InitializeComponent();
        Closing += (_, e) => e.Cancel = !MagSluiten(provider.GetRequiredService<BeheerState>());
    }

    /// <summary>Staat er nog iets open (niet bewaard, plan klaar, formulier ingevuld), dan eerst vragen.</summary>
    private bool MagSluiten(BeheerState state)
    {
        var open = state.Openstaand.Alles;
        if (open.Count == 0)
        {
            return true;
        }

        var lijst = string.Join(Environment.NewLine, open.Select(i => $"- {(i.Scherm.Length > 0 ? i.Scherm : i.Pad)}: {i.Tekst}"));
        return MessageBox.Show(this, $"Er staat nog iets open:{Environment.NewLine}{lijst}{Environment.NewLine}{Environment.NewLine}Toch sluiten? Wat niet bewaard is, gaat verloren.",
            "Moss365", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
