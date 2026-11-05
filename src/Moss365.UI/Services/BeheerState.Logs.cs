using Moss365.Core.Opslag;
using Serilog;

namespace Moss365.UI.Services;

/// <summary>
/// Logboeken naast het logboek van de wijzigingen (0.8): het technisch log (Serilog, een bestand per dag in de map logs,
/// 30 dagen bewaard) en het toegangslog in de databank (wie exporteerde of aanmeldingen en beveiliging bekeek).
/// Het technisch log bevat nooit wachtwoorden, tokens, parameters van wijzigingen of namen van personen: enkel wat er
/// gebeurde, hoeveel, en foutmeldingen.
/// </summary>
public sealed partial class BeheerState : IDisposable
{
    /// <summary>Bij het afsluiten: de klok stoppen en het technisch log wegschrijven en sluiten.</summary>
    public void Dispose()
    {
        _autoKlok?.Dispose();
        Log.Information("Moss365 afgesloten");
        (Log as IDisposable)?.Dispose();
        Log = Serilog.Core.Logger.None;
    }

    /// <summary>Het technisch log (fouten en het verloop van laden en uitvoeren).</summary>
    public ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    public string LogMap => Path.Combine(_omgeving.GegevensMap, "logs");

    private void StartLog()
    {
        try
        {
            var config = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(Path.Combine(LogMap, "moss365-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

            // Waarschuwingen en fouten ook in het Windows-logboek (Toepassing, bron Moss365). De bron registreert een
            // beheerder één keer: New-EventLog -LogName Application -Source Moss365. Zonder bron schrijft enkel het bestand.
            if (OperatingSystem.IsWindows())
            {
                config = config.WriteTo.EventLog("Moss365", manageEventSource: false, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Warning);
            }

            Log = config.CreateLogger();
        }
        catch (Exception)
        {
            // Zonder technisch log werkt de app gewoon verder
            Log = Serilog.Core.Logger.None;
        }
    }

    /// <summary>Zet in het toegangslog wie wat bekeek of exporteerde (enkel wat en hoeveel, nooit de inhoud).</summary>
    public void Toegang(string wat, string details)
    {
        try
        {
            Databank.SchrijfToegang(new ToegangRegel(DateTimeOffset.Now, Gebruiker, wat, details));
        }
        catch (Exception ex)
        {
            Log.Warning("Toegangslog schrijven mislukt: {Fout}", ex.Message);
        }
    }

    // ------------------------------------------------------------------ automatisch vernieuwen

    /// <summary>Om de hoeveel tijd automatisch vernieuwen (als de instelling aan staat).</summary>
    public static readonly TimeSpan AutoVernieuwenNa = TimeSpan.FromMinutes(15);

    private System.Threading.Timer? _autoKlok;

    private void StartAutoVernieuwen()
    {
        if (_fabriek is not null)
        {
            _autoKlok = new System.Threading.Timer(_ => _ = AutoVernieuwenAsync(DateTime.Now), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Vernieuwt AD en Entra (snel laden) als de instelling aan staat, er al live geladen is, het 15 minuten geleden is,
    /// er geen andere taak loopt en de aanmelding nog geldig is zonder iets te tonen. Er verschijnt nooit vanzelf een
    /// aanmeldlink; Exchange (dat een eigen sessie start) wordt niet automatisch vernieuwd.
    /// </summary>
    public async Task<bool> AutoVernieuwenAsync(DateTime nu)
    {
        if (!Instellingen.AutoVernieuwen || TaakBezig || !IsLive || GeladenOp is not { } vorige || nu - vorige < AutoVernieuwenNa)
        {
            return false;
        }

        if (Instellingen.GraphIngevuld && (Graph() is not { } graph || !await graph.StilAangemeldAsync(CancellationToken.None)))
        {
            Log.Information("Automatisch vernieuwen overgeslagen: niet (meer) aangemeld");
            return false;
        }

        Log.Information("Automatisch vernieuwen");
        return StartLaden(LaadSoort.AdEnEntra);
    }
}
