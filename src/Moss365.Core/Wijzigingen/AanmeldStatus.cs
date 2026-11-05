namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Een lopende aanmelding bij Microsoft: de app opent zelf geen browser, maar toont de link zodat de beheerder hem in de
/// browser van zijn keuze kan plakken. De link verwijst na het aanmelden terug naar http://localhost op deze pc.
/// </summary>
public sealed class AanmeldStatus
{
    private readonly object _slot = new();
    private CancellationTokenSource? _annuleren;

    public event Action? Gewijzigd;

    /// <summary>De aanmeldlink, of null als er geen aanmelding loopt.</summary>
    public Uri? Link { get; private set; }

    public DateTimeOffset? Sinds { get; private set; }

    /// <summary>Wordt door de verbinding opgeroepen met de link en de annulering van de lopende aanmelding.</summary>
    public void Toon(Uri link, CancellationTokenSource annuleren)
    {
        lock (_slot)
        {
            Link = link;
            Sinds = DateTimeOffset.Now;
            _annuleren = annuleren;
        }

        Gewijzigd?.Invoke();
    }

    public void Klaar()
    {
        lock (_slot)
        {
            Link = null;
            Sinds = null;
            _annuleren = null;
        }

        Gewijzigd?.Invoke();
    }

    /// <summary>Breekt de lopende aanmelding af (de actie die erop wachtte, mislukt dan met "geannuleerd").</summary>
    public void Annuleer()
    {
        CancellationTokenSource? cts;
        lock (_slot)
        {
            cts = _annuleren;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Aanmelding was net klaar
        }
    }
}
