using System.Threading;
using Moss365.Core.Scheduling;
using Velopack;

namespace Moss365.Desktop;

/// <summary>
/// The start of the app. Velopack (R7) must run first: during installing, updating and removing it starts the exe with
/// its own arguments and stops it right after. With <c>--lezen &lt;tenant&gt;</c> (the Windows Task Scheduler, R18) the app
/// reads that tenant without a window and stops.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (Unattended.TenantFrom(args) is { } tenant)
        {
            return Headless.Run(tenant);
        }

        // While the window is open, a read without a window leaves the reading to the app
        using var open = new Mutex(false, Headless.OpenMutex);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
