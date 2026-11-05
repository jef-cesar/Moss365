using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Laadt live gegevens uit AD en Entra in hetzelfde model als de inventaris. Wat niet live geladen wordt (bv. rooms, of AD
/// als er geen domeincontroller ingesteld is), blijft uit de huidige gegevens.
/// </summary>
public static class LiveLader
{
    public static async Task<InventarisData> LaadAsync(
        IAdVerbinding? ad,
        IGraphVerbinding? graph,
        InventarisData huidig,
        IProgress<string>? voortgang,
        CancellationToken annuleren)
        => (await LeesAsync(ad, graph, huidig, voortgang, annuleren)).Data;

    /// <summary>Zoals LaadAsync, met een vorige lezing om enkel de wijzigingen te vragen, en wat er gelezen is.</summary>
    public static async Task<LiveLezing> LeesAsync(
        IAdVerbinding? ad,
        IGraphVerbinding? graph,
        InventarisData huidig,
        IProgress<string>? voortgang,
        CancellationToken annuleren,
        AdBasis? adBasis = null,
        GraphBasis? graphBasis = null)
    {
        var adGegevens = ad is null ? null : await ad.LeesAsync(adBasis, voortgang, annuleren);
        var graphGegevens = graph is null ? null : await graph.LeesAsync(graphBasis, voortgang, annuleren);
        return new LiveLezing(Samenvoegen(huidig, adGegevens, graphGegevens), adGegevens, graphGegevens);
    }

    /// <summary>
    /// Zet wat uit AD en/of Entra gelezen is in de huidige gegevens; de rest blijft. Los van het lezen, zodat AD, Entra en
    /// Exchange tegelijk gelezen kunnen worden en daarna één voor één samengevoegd.
    /// </summary>
    public static InventarisData Samenvoegen(InventarisData huidig, AdGegevens? adGegevens, GraphGegevens? graphGegevens)
    {
        var tijd = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var bronnen = new List<BronBestand>();
        if (adGegevens is not null)
        {
            bronnen.Add(new BronBestand("AD-gebruikers en -groepen", $"live {tijd}" + (adGegevens.Snel ? " (enkel wijzigingen)" : string.Empty), adGegevens.Gebruikers.Count + adGegevens.Groepen.Count));
        }

        if (graphGegevens is not null)
        {
            bronnen.Add(new BronBestand("Entra-gebruikers, -groepen en licenties", $"live {tijd}" + (graphGegevens.Snel ? " (groepen: enkel wijzigingen)" : string.Empty), graphGegevens.Gebruikers.Count + graphGegevens.Groepen.Count));
        }

        bronnen.AddRange(huidig.Bronnen.Where(b => (adGegevens is null || !b.Bestand.StartsWith("ad-", StringComparison.Ordinal))
                                                   && (graphGegevens is null || !b.Bestand.StartsWith("entra-", StringComparison.Ordinal))
                                                   && !b.Bestand.Contains("live", StringComparison.Ordinal)));

        var lidmaatschap = graphGegevens?.Lidmaatschap
                           ?? huidig.CloudLidmaatschap.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var data = InventarisLader.Samenstellen(
            adGegevens?.Gebruikers ?? huidig.AdGebruikers,
            adGegevens?.Groepen ?? huidig.AdGroepenOpDn.Values.ToList(),
            graphGegevens?.Gebruikers ?? huidig.EntraGebruikers,
            graphGegevens?.Groepen ?? huidig.CloudGroepen.Values.ToList(),
            lidmaatschap,
            huidig.Rooms,
            graphGegevens?.Licenties ?? huidig.Licenties,
            bronnen,
            adGegevens is null && graphGegevens is null ? huidig.Herkomst : "Live",
            adGegevens?.DomeinDn ?? huidig.DomeinDn) with
        {
            // Wat niet live gelezen werd, blijft uit de huidige gegevens
            CloudLidNamen = huidig.CloudLidNamen,
            ExoGroepen = huidig.ExoGroepen,
            Postvakken = huidig.Postvakken,
            PostvakRechten = huidig.PostvakRechten,
            Beveiliging = huidig.Beveiliging,
            ConditionalAccess = huidig.ConditionalAccess,
        };
        return data;
    }

    /// <summary>Vervangt de beveiligingsgegevens (MFA en beheerrollen) na een live lezing uit Entra.</summary>
    public static InventarisData MetBeveiliging(InventarisData huidig, BeveiligingGegevens beveiliging)
    {
        var vervangen = new[] { InventarisLader.EntraMfa, InventarisLader.EntraRollen };
        var bronnen = huidig.Bronnen.Where(b => !vervangen.Contains(b.Bestand) && !b.Bestand.StartsWith("Beveiliging", StringComparison.Ordinal)).ToList();
        bronnen.Add(new BronBestand("Beveiliging (MFA en beheerrollen)", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", beveiliging.Mfa.Count + beveiliging.Rollen.Count));
        return huidig with { Beveiliging = beveiliging, Bronnen = bronnen };
    }

    /// <summary>Vervangt de Conditional Access-gegevens na een live lezing uit Entra.</summary>
    public static InventarisData MetConditionalAccess(InventarisData huidig, CaGegevens ca)
    {
        var bronnen = huidig.Bronnen.Where(b => b.Bestand != InventarisLader.EntraCa && !b.Bestand.StartsWith("Conditional Access", StringComparison.Ordinal)).ToList();
        bronnen.Add(new BronBestand("Conditional Access", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", ca.Beleiden.Count));
        return huidig with { ConditionalAccess = ca, Bronnen = bronnen };
    }

    /// <summary>Vervangt de rooms (na een live lezing uit Exchange) en houdt de rest.</summary>
    public static InventarisData MetRooms(InventarisData huidig, IReadOnlyList<Room> rooms)
    {
        var bronnen = huidig.Bronnen.Where(b => b.Bestand != InventarisLader.ExoRooms && !b.Bestand.StartsWith("Rooms", StringComparison.Ordinal)).ToList();
        bronnen.Add(new BronBestand("Rooms (Exchange)", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", rooms.Count));
        return huidig with
        {
            Rooms = rooms.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Bronnen = bronnen,
        };
    }

    /// <summary>Vervangt de Exchange-groepen, gedeelde postvakken en hun rechten (na een live lezing uit Exchange).</summary>
    public static InventarisData MetExchange(InventarisData huidig, ExchangeGegevens exchange)
    {
        var vervangen = new[] { InventarisLader.ExoGroepen, InventarisLader.ExoGroepsleden, InventarisLader.ExoPostvakken, InventarisLader.ExoPostvakRechten };
        var bronnen = huidig.Bronnen.Where(b => !vervangen.Contains(b.Bestand) && !b.Bestand.StartsWith("Postvakken", StringComparison.Ordinal)).ToList();
        bronnen.Add(new BronBestand("Postvakken en groepen (Exchange)", $"live {DateTime.Now:yyyy-MM-dd HH:mm}", exchange.Postvakken.Count + exchange.Groepen.Count));
        return huidig with
        {
            ExoGroepen = exchange.Groepen,
            Postvakken = exchange.Postvakken.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            PostvakRechten = exchange.Rechten,
            Bronnen = bronnen,
        };
    }
}

/// <summary>Het resultaat van live lezen, met wat AD en Graph opleverden (stand en deltaLink voor de volgende keer).</summary>
public sealed record LiveLezing(InventarisData Data, AdGegevens? Ad, GraphGegevens? Graph);
