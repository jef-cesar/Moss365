using Velopack;

namespace Moss365.Desktop;

/// <summary>
/// The start of the app. Velopack (R7) must run first: during installing, updating and removing it starts the exe with
/// its own arguments and stops it right after.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
