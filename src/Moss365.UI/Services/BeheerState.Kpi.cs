using Moss365.Core.Analyse;

namespace Moss365.UI.Services;

/// <summary>KPI's: het getal op een tegel en de lijst erachter komen uit dezelfde berekening (<see cref="Kpis"/>).</summary>
public sealed partial class BeheerState
{
    private KpiBron? _kpiBron;

    /// <summary>De bron voor de KPI's, opnieuw opgebouwd als de gegevens of de configuratie veranderen (of na 5 minuten).</summary>
    public KpiBron KpiBron
    {
        get
        {
            var b = _kpiBron;
            if (b is null || !ReferenceEquals(b.Data, Data) || !ReferenceEquals(b.Config, Config) || DateTime.Now - b.Nu > TimeSpan.FromMinutes(5))
            {
                b = _kpiBron = new KpiBron(Data, Config, DateTime.Now);
            }

            return b;
        }
    }

    /// <summary>Het logboek waarop de KPI's van het logboek rekenen (de laatste 2000 regels).</summary>
    public IReadOnlyList<Moss365.Core.Wijzigingen.LogRegel> LogboekVoorKpi() => Logboek.Recent(2000);

    /// <summary>
    /// Na het live laden: de stand van de KPI's van vandaag in de databank (enkel aantallen, geen namen), voor het verloop.
    /// Uit een oude inventaris komt geen stand: die zou op de verkeerde dag staan.
    /// </summary>
    public void BewaarKpiStand()
    {
        try
        {
            Databank.BewaarKpiStand(DateOnly.FromDateTime(DateTime.Now), BiStatistiek.Stand(new KpiBron(Data, Config, DateTime.Now)));
        }
        catch (Exception ex)
        {
            Log.Warning("KPI-stand bewaren mislukt: {Fout}", ex.Message);
        }
    }

    /// <summary>Het verloop van de KPI's met een historiek, de laatste dagen.</summary>
    public IReadOnlyList<KpiVerloop> KpiVerloop(int dagen = 90)
    {
        var stand = Databank.LeesKpiStand(DateOnly.FromDateTime(DateTime.Now.AddDays(-dagen)));
        return BiStatistiek.MetVerloop
            .Select(k => new KpiVerloop(k, stand.TryGetValue(k.Sleutel, out var punten) ? punten : Array.Empty<(DateOnly, int)>()))
            .ToList();
    }

    public int KpiAantal(Kpi k) => Kpis.Aantal(k, KpiBron, k.Lijst == KpiLijst.Logboek ? LogboekVoorKpi() : null);

    /// <summary>
    /// Een pagina opent met <c>?kpi=...</c>. Kwam je via een tegel (niet met Terug of Vooruit), dan beginnen de filters
    /// van de pagina en de kolomfilters van haar lijsten opnieuw, zodat je precies de rijen achter het getal ziet.
    /// Geeft true als de pagina haar filters opnieuw moet zetten.
    /// </summary>
    public bool KpiAankomst(string? kpi, string uri, string paginaSleutel, params string[] tabellen)
    {
        if (Kpis.Zoek(kpi) is null || Geschiedenis.IsSprongNaar(uri))
        {
            return false;
        }

        Paginastatus.Vergeet(paginaSleutel);
        foreach (var t in tabellen)
        {
            var sessie = TabelSessie(t);
            sessie.Filters.Clear();
            sessie.Pagina = 0;
        }

        return true;
    }
}

/// <summary>De actieve KPI-filter van een pagina zonder eigen zoekvelden (Rooms, Conditional Access), in de paginastatus.</summary>
public sealed class KpiKeuze
{
    public string? Kpi { get; set; }
}
