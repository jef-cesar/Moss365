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

        // One state per tenant, each with its own connections, tokens and sign-in (R3); the screens get the open tenant.
        // Switching reloads the page, which gives the screens a new scope with the other tenant.
        var hub = new TenantHub(environment, (tenant, key) =>
        {
            var signIn = new SignInStatus();
            return new ManagementState(environment, new ConnectionFactory(signIn), signIn, tenant, key);
        }, new Moss365.Connections.Check.WindowsKeyProtector());
        if (!UnlockAtStart(hub))
        {
            hub.Dispose();
            Loaded += (_, _) => Close();
            return;
        }

        services.AddSingleton(hub);
        services.AddScoped(sp => sp.GetRequiredService<TenantHub>().Current);
        var provider = services.BuildServiceProvider();
        Resources.Add("services", provider);

        InitializeComponent();
        Closing += (_, e) => e.Cancel = !CanClose(hub);
        Closed += (_, _) => hub.Dispose();
    }

    /// <summary>
    /// An encrypted tenant with a password (or whose key Windows cannot open) asks for its password or recovery key
    /// before the screens start. Cancel opens another tenant that needs nothing, if there is one.
    /// </summary>
    private static bool UnlockAtStart(TenantHub hub)
    {
        var tenant = hub.CurrentTenant;
        while (hub.NeedsUnlock(tenant))
        {
            var method = hub.Encryption(tenant.Id)?.Method;
            var secret = UnlockDialog.Ask(tenant.Name, method == Moss365.Core.Storage.EncryptionMethod.Password);
            if (secret is null)
            {
                if (hub.FirstOpenable() is { } other)
                {
                    hub.Switch(other.Id);
                    return true;
                }

                return false;
            }

            if (!hub.Unlock(tenant.Id, secret))
            {
                MessageBox.Show("Dat wachtwoord of die herstelsleutel past niet.", "Moss365", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        return true;
    }

    /// <summary>If something is still open in any tenant (not saved, plan ready, form filled in), ask first.</summary>
    private bool CanClose(TenantHub hub)
    {
        var open = hub.Opened.SelectMany(s => s.Pending.Everything.Select(i => i with { Screen = $"{s.Tenant.Name}: {(i.Screen.Length > 0 ? i.Screen : i.FilePath)}" })).ToList();
        if (open.Count == 0)
        {
            return true;
        }

        var list = string.Join(Environment.NewLine, open.Select(i => $"- {(i.Screen.Length > 0 ? i.Screen : i.FilePath)}: {i.Text}"));
        return MessageBox.Show(this, $"Er staat nog iets open:{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}Toch sluiten? Wat niet bewaard is, gaat verloren.",
            "Moss365", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }
}
