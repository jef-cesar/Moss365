using System.Windows;
using System.Windows.Controls;

namespace Moss365.Desktop;

/// <summary>Asks the password or the recovery key of an encrypted tenant (R6), before the screens start.</summary>
public static class UnlockDialog
{
    /// <summary>What was typed, or null on Cancel.</summary>
    public static string? Ask(string tenant, bool password)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "Openen", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annuleren", IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"De databank van \"{tenant}\" is versleuteld.\n" + (password
                ? "Geef het wachtwoord, of de herstelsleutel als je het wachtwoord niet meer weet."
                : "Windows kon de sleutel niet openen voor deze gebruiker. Geef de herstelsleutel."),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = "Moss365 - tenant openen",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return window.ShowDialog() == true ? box.Password : null;
    }
}
