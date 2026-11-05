using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

public enum SpiegelStatus
{
    Toevoegen,
    ReedsLid,
    AlleenBijDoel,
    Automatisch,
    NietKopieren,
}

public enum SchrijfRoute
{
    Geen,
    ActiveDirectory,
    Graph,
    Exchange,
}

public sealed record SpiegelRegel(string Laag, string GroepId, string Groep, string Type, SpiegelStatus Status, SchrijfRoute Route, string Opmerking)
{
    public string StatusTekst => Status switch
    {
        SpiegelStatus.Toevoegen => "Toevoegen",
        SpiegelStatus.ReedsLid => "Reeds lid",
        SpiegelStatus.AlleenBijDoel => "Alleen bij doel",
        SpiegelStatus.Automatisch => "Automatisch",
        _ => "Niet kopiëren",
    };

    public string RouteTekst => Route switch
    {
        SchrijfRoute.ActiveDirectory => "AD",
        SchrijfRoute.Graph => "Graph",
        SchrijfRoute.Exchange => "Exchange",
        _ => "-",
    };
}

/// <summary>
/// Vergelijkt de groepen van een doelgebruiker met die van een referentiegebruiker (de "spiegel"), in AD en in de cloud,
/// en bepaalt per groep wat er moet gebeuren en via welke weg dat zou kunnen.
/// </summary>
public static class Spiegelgebruiker
{
    private static readonly HashSet<string> BevoorrechteGroepen = new(StringComparer.OrdinalIgnoreCase)
    {
        "Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators", "Account Operators", "Server Operators",
        "Print Operators", "Backup Operators", "Group Policy Creator Owners", "DnsAdmins", "Key Admins", "Enterprise Key Admins",
    };

    public static IReadOnlyList<SpiegelRegel> Bereken(InventarisData data, OrganisatieConfig config, Persoon referentie, Persoon doel)
    {
        var regels = new List<SpiegelRegel>();
        regels.AddRange(BerekenAd(data, referentie, doel));
        regels.AddRange(BerekenCloud(data, config, referentie, doel));
        return regels
            .OrderBy(r => r.Laag)
            .ThenBy(r => r.Status)
            .ThenBy(r => r.Groep, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsBevoorrecht(AdGroep groep)
        => BevoorrechteGroepen.Contains(groep.Name)
           || groep.OuPad.Equals("Builtin", StringComparison.OrdinalIgnoreCase)
           || groep.Name.EndsWith(" Admins", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SpiegelRegel> BerekenAd(InventarisData data, Persoon referentie, Persoon doel)
    {
        const string laag = "AD";
        var vanRef = data.AdGroepenVan(referentie).ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        var vanDoel = data.AdGroepenVan(doel).ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);

        foreach (var g in vanRef.Values)
        {
            var type = AdType(g);
            if (IsBevoorrecht(g))
            {
                yield return new SpiegelRegel(laag, g.DistinguishedName, g.Name, type, SpiegelStatus.NietKopieren, SchrijfRoute.Geen, "Bevoorrechte groep: nooit via een spiegel");
            }
            else if (vanDoel.ContainsKey(g.DistinguishedName))
            {
                yield return new SpiegelRegel(laag, g.DistinguishedName, g.Name, type, SpiegelStatus.ReedsLid, SchrijfRoute.Geen, string.Empty);
            }
            else if (doel.Ad is null)
            {
                yield return new SpiegelRegel(laag, g.DistinguishedName, g.Name, type, SpiegelStatus.NietKopieren, SchrijfRoute.Geen, "Doelgebruiker heeft geen AD-account");
            }
            else
            {
                yield return new SpiegelRegel(laag, g.DistinguishedName, g.Name, type, SpiegelStatus.Toevoegen, SchrijfRoute.ActiveDirectory, AdOpmerking(g));
            }
        }

        foreach (var g in vanDoel.Values.Where(g => !vanRef.ContainsKey(g.DistinguishedName)))
        {
            yield return new SpiegelRegel(laag, g.DistinguishedName, g.Name, AdType(g), SpiegelStatus.AlleenBijDoel, SchrijfRoute.Geen,
                IsBevoorrecht(g) ? "Bevoorrechte groep" : string.Empty);
        }
    }

    private static IEnumerable<SpiegelRegel> BerekenCloud(InventarisData data, OrganisatieConfig config, Persoon referentie, Persoon doel)
    {
        const string laag = "Cloud";
        var vanRef = data.CloudGroepenVan(referentie).ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var vanDoel = data.CloudGroepenVan(doel).ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var g in vanRef.Values)
        {
            if (g.Dynamisch || config.IsDynamischeGroep(g.DisplayName))
            {
                var regel = g.Lidmaatschapsregel.Length > 0 ? g.Lidmaatschapsregel : config.DynamischeRegels.FirstOrDefault(r => r.Groep == g.DisplayName)?.Regel ?? string.Empty;
                var status = vanDoel.ContainsKey(g.Id) ? SpiegelStatus.ReedsLid : SpiegelStatus.Automatisch;
                yield return new SpiegelRegel(laag, g.Id, g.DisplayName, g.TypeTekst, status, SchrijfRoute.Geen,
                    $"Dynamische groep: volgt uit de kenmerken ({regel})");
            }
            else if (vanDoel.ContainsKey(g.Id))
            {
                yield return new SpiegelRegel(laag, g.Id, g.DisplayName, g.TypeTekst, SpiegelStatus.ReedsLid, SchrijfRoute.Geen, string.Empty);
            }
            else if (doel.Entra is null)
            {
                yield return new SpiegelRegel(laag, g.Id, g.DisplayName, g.TypeTekst, SpiegelStatus.NietKopieren, SchrijfRoute.Geen,
                    "Doelgebruiker bestaat (nog) niet in Entra");
            }
            else if (g.UitAd)
            {
                yield return new SpiegelRegel(laag, g.Id, g.DisplayName, g.TypeTekst, SpiegelStatus.Toevoegen, SchrijfRoute.ActiveDirectory,
                    "Gesynchroniseerde groep: lidmaatschap wijzigen in AD");
            }
            else
            {
                yield return new SpiegelRegel(laag, g.Id, g.DisplayName, g.TypeTekst, SpiegelStatus.Toevoegen, RouteVoor(g), CloudOpmerking(g, config));
            }
        }

        foreach (var g in vanDoel.Values.Where(g => !vanRef.ContainsKey(g.Id)))
        {
            yield return new SpiegelRegel(laag, g.Id, g.DisplayName, g.TypeTekst, SpiegelStatus.AlleenBijDoel, SchrijfRoute.Geen,
                g.Dynamisch ? "Dynamische groep" : string.Empty);
        }
    }

    /// <summary>Graph kan geen leden wijzigen van mail-beveiligingsgroepen en distributielijsten; daarvoor is Exchange nodig.</summary>
    public static SchrijfRoute RouteVoor(CloudGroep groep) => groep.Type switch
    {
        CloudGroepType.MailBeveiliging or CloudGroepType.Distributie => SchrijfRoute.Exchange,
        _ => SchrijfRoute.Graph,
    };

    private static string AdType(AdGroep g)
        => string.Join(", ", new[] { g.Category, g.Scope }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>Een opmerking bij een AD-groep; organisatie-specifieke opmerkingen worden instelbaar (R9).</summary>
    private static string AdOpmerking(AdGroep g) => string.Empty;

    private static string CloudOpmerking(CloudGroep g, OrganisatieConfig config)
    {
        var opmerkingen = new List<string>();
        if (g.DisplayName.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase))
        {
            opmerkingen.Add("Toegang tot een gedeeld postvak (FullAccess en SendAs)");
        }

        if (config.Afdelingen.Any(a => string.Equals(a.Groep, g.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            opmerkingen.Add("Afdelingsgroep: hoort te volgen uit de dienst");
        }

        if (g.GroepLicentie)
        {
            opmerkingen.Add("Kent een licentie toe");
        }

        return string.Join("; ", opmerkingen);
    }
}
