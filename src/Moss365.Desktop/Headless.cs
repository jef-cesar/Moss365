using System.Globalization;
using System.Threading;
using Moss365.Connections;
using Moss365.Core.Scheduling;
using Moss365.Core.Storage;
using Moss365.UI.Services;

namespace Moss365.Desktop;

/// <summary>
/// Reading without a window (R18, U19), started by the Windows Task Scheduler with <c>--lezen &lt;tenant&gt;</c>: no
/// window, no sign-in link and no password prompt, only what the kept sign-in and the Windows account can read. It
/// never writes to AD, Entra or Exchange. Exit codes: 0 done, 1 done with errors, 2 skipped (the app is open, reading
/// without a window is off, or nothing can be read), 3 unknown tenant, 4 the tenant needs a password, 5 failed.
/// </summary>
internal static class Headless
{
    /// <summary>Exists while the app is open; a read without a window then leaves the reading to the app.</summary>
    internal const string OpenMutex = @"Local\Moss365.Desktop.Open";

    public static int Run(string tenantId)
    {
        if (Mutex.TryOpenExisting(OpenMutex, out var open))
        {
            open.Dispose();
            return 2;
        }

        // One read per tenant at a time, also when the task starts twice
        using var running = new Mutex(false, $@"Local\Moss365.Desktop.Lezen.{tenantId}");
        if (!running.WaitOne(TimeSpan.Zero))
        {
            return 2;
        }

        try
        {
            var environment = new WindowsEnvironment();
            using var hub = new TenantHub(environment,
                (tenant, key) => new ManagementState(environment, new ConnectionFactory(withoutWindow: true), null, tenant, key, withoutWindow: true),
                new Moss365.Connections.Check.WindowsKeyProtector());
            Moss365.UI.Pages.LanguagePage.System = CultureInfo.CurrentUICulture;
            UiText.Start(hub.App, Moss365.UI.Pages.LanguagePage.System);

            var tenant = hub.Tenants.FirstOrDefault(t => t.Id == tenantId);
            if (tenant is null)
            {
                return 3;
            }

            if (hub.NeedsUnlock(tenant))
            {
                return 4;
            }

            var state = hub.Open(tenant);
            if (!state.UnattendedSettings.On)
            {
                return 2;
            }

            var run = state.RunUnattendedAsync(hub.Encryption(tenant.Id)?.Method ?? EncryptionMethod.None).GetAwaiter().GetResult();
            return run.Result switch
            {
                UnattendedResult.Done => 0,
                UnattendedResult.DoneWithErrors => 1,
                UnattendedResult.Skipped => 2,
                _ => 5,
            };
        }
        catch (Exception)
        {
            // Without a window there is nobody to tell; the result of the task in the Task Scheduler says it
            return 5;
        }
        finally
        {
            running.ReleaseMutex();
        }
    }
}
