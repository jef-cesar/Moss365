using Moss365.Core.Storage;
using Serilog;

namespace Moss365.UI.Services;

/// <summary>
/// Logs next to the change log: the technical log (Serilog, in the tenant database since 0.12, kept 90 days) and the
/// access log in the database (who exported or viewed sign-ins and security). The technical log never contains
/// passwords, tokens, parameters of changes or names of people: only what happened, how many, and error messages.
/// </summary>
public sealed partial class ManagementState : IDisposable
{
    /// <summary>On closing: stop the clock and write and close the technical log.</summary>
    public void Dispose()
    {
        _autoClock?.Dispose();
        Log.Information("Moss365 afgesloten");
        (Log as IDisposable)?.Dispose();
        Log = Serilog.Core.Logger.None;
    }

    /// <summary>The technical log (errors and the course of loading and running).</summary>
    public ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    /// <summary>How long the technical log is kept (decision U9).</summary>
    public const int TechnicalLogDays = 90;

    private void StartLog()
    {
        try
        {
            var config = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Sink(new DatabaseLogSink(Database));
            Database.CleanupTechnicalLog(DateTimeOffset.Now.AddDays(-TechnicalLogDays));

            // Warnings and errors also in the Windows event log (Application, source Moss365). An administrator
            // registers the source once: New-EventLog -LogName Application -Source Moss365. Without the source only the
            // file is written.
            if (OperatingSystem.IsWindows())
            {
                config = config.WriteTo.EventLog("Moss365", manageEventSource: false, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning);
            }

            Log = config.CreateLogger();
        }
        catch (Exception)
        {
            // Without the technical log the app simply keeps working
            Log = Serilog.Core.Logger.None;
        }
    }

    /// <summary>Puts in the access log who viewed or exported what (only what and how many, never the
    /// content).</summary>
    public void Access(string what, string details)
    {
        try
        {
            Database.WriteAccess(new AccessEntry(DateTimeOffset.Now, User, what, details));
        }
        catch (Exception ex)
        {
            Log.Warning("Toegangslog schrijven mislukt: {Fout}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ automatic refresh

    /// <summary>How often to refresh automatically (when the setting is on).</summary>
    public static readonly TimeSpan AutoRefreshAfter = TimeSpan.FromMinutes(15);

    private System.Threading.Timer? _autoClock;

    private void StartAutoRefresh()
    {
        if (_factory is not null)
        {
            _autoClock = new System.Threading.Timer(_ => _ = RefreshAutoAsync(DateTime.Now), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Refreshes AD and Entra (quick load) when the setting is on, something was loaded live already, it was 15 minutes
    /// ago, no other task is running and the sign-in is still valid without showing anything. A sign-in link never
    /// appears by itself; Exchange (which starts its own session) is not refreshed automatically.
    /// </summary>
    public async Task<bool> RefreshAutoAsync(DateTime now)
    {
        if (!Settings.RefreshAuto || TaskBusy || !IsLive || LoadedOn is not { } previous || now - previous < AutoRefreshAfter)
        {
            return false;
        }

        if (Settings.GraphFilled && (Graph() is not { } graph || !await graph.SilentSignedInAsync(CancellationToken.None)))
        {
            Log.Information("Automatisch vernieuwen overgeslagen: niet (meer) aangemeld");
            return false;
        }

        Log.Information("Automatisch vernieuwen");
        return StartLoading(LoadKind.AdAndEntra);
    }
}

/// <summary>Writes the technical log into the tenant database (table TechnicalLog).</summary>
internal sealed class DatabaseLogSink(Database db) : Serilog.Core.ILogEventSink
{
    // Strings without quotes, as in the file of older versions
    private static readonly Serilog.Formatting.Display.MessageTemplateTextFormatter Formatter = new("{Message:lj}", System.Globalization.CultureInfo.InvariantCulture);

    public void Emit(Serilog.Events.LogEvent logEvent)
    {
        try
        {
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            Formatter.Format(logEvent, writer);
            var message = writer.ToString();
            if (logEvent.Exception is { } ex)
            {
                message += " | " + ex.GetType().Name + ": " + ex.Message;
            }

            db.WriteTechnicalLog(logEvent.Timestamp, logEvent.Level.ToString(), message);
        }
        catch (Exception)
        {
            // The technical log never stops the app
        }
    }
}
