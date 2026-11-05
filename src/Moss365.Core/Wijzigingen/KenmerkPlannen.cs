using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>De gegevens van een gebruiker zoals ze moeten worden; null = niet wijzigen.</summary>
public sealed class GebruikerGewenst
{
    public string? Weergavenaam { get; set; }
    public string? Voornaam { get; set; }
    public string? Achternaam { get; set; }
    public string? Functie { get; set; }
    public string? Dienst { get; set; }
    public string? Kantoor { get; set; }
    public string? Stad { get; set; }

    /// <summary>Enkel in AD.</summary>
    public string? Omschrijving { get; set; }

    /// <summary>Enkel in AD.</summary>
    public string? Logonscript { get; set; }

    public string? Telefoon { get; set; }
    public string? Gsm { get; set; }

    /// <summary>Sleutel van de persoon die manager is; "" = geen manager; null = niet wijzigen.</summary>
    public string? Manager { get; set; }

    /// <summary>De huidige waarden (AD als het account uit AD komt, anders Entra), als begin van het formulier.</summary>
    public static GebruikerGewenst Van(Persoon p, InventarisData? d = null)
    {
        var g = p.Ad is { } a
            ? new GebruikerGewenst { Weergavenaam = a.DisplayName, Voornaam = a.GivenName, Achternaam = a.Surname, Functie = a.Title, Dienst = a.Department, Kantoor = a.Office, Stad = a.City, Omschrijving = a.Description, Logonscript = a.ScriptPath, Telefoon = a.Telefoon, Gsm = a.Gsm }
            : new GebruikerGewenst { Weergavenaam = p.Entra?.DisplayName, Voornaam = p.Entra?.GivenName, Achternaam = p.Entra?.Surname, Functie = p.Entra?.JobTitle, Dienst = p.Entra?.Department, Kantoor = p.Entra?.OfficeLocation, Stad = p.Entra?.City, Telefoon = p.Entra?.Telefoon, Gsm = p.Entra?.Gsm };
        g.Manager = d is null ? null : KenmerkPlannen.ManagerVan(d, p)?.Sleutel ?? string.Empty;
        return g;
    }
}

/// <summary>
/// Kenmerken van een gebruiker wijzigen langs de juiste weg: een gesynchroniseerd account in AD (Entra volgt na de
/// synchronisatie), een cloudaccount via Graph. Met de vorige waarden, zodat het ongedaan kan. UPN en e-mailadres niet:
/// die raken aanmelden en mail, en komen apart.
/// </summary>
public static class KenmerkPlannen
{
    // Kenmerk in het formulier -> (LDAP-naam in AD, naam in Graph of null als Entra het niet kent, leesbare naam)
    private static readonly (string Veld, string Ad, string? Graph, string Naam)[] Velden =
    {
        (nameof(GebruikerGewenst.Weergavenaam), "displayName", "displayName", "Weergavenaam"),
        (nameof(GebruikerGewenst.Voornaam), "givenName", "givenName", "Voornaam"),
        (nameof(GebruikerGewenst.Achternaam), "sn", "surname", "Achternaam"),
        (nameof(GebruikerGewenst.Functie), "title", "jobTitle", "Functie"),
        (nameof(GebruikerGewenst.Dienst), "department", "department", "Dienst"),
        (nameof(GebruikerGewenst.Kantoor), "physicalDeliveryOfficeName", "officeLocation", "Kantoor"),
        (nameof(GebruikerGewenst.Stad), "l", "city", "Stad"),
        (nameof(GebruikerGewenst.Omschrijving), "description", null, "Omschrijving"),
        (nameof(GebruikerGewenst.Logonscript), "scriptPath", null, "Logonscript"),
        (nameof(GebruikerGewenst.Telefoon), "telephoneNumber", "businessPhones", "Telefoon"),
        (nameof(GebruikerGewenst.Gsm), "mobile", "mobilePhone", "Gsm"),
    };

    /// <summary>De manager van een persoon (AD: manager-DN; cloud: manager-id), of null.</summary>
    public static Persoon? ManagerVan(InventarisData d, Persoon p)
        => ViaAd(p)
            ? p.Ad!.Manager is { Length: > 0 } dn ? d.Personen.FirstOrDefault(x => string.Equals(x.Ad?.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase)) : null
            : p.Entra?.ManagerId is { Length: > 0 } id ? d.Personen.FirstOrDefault(x => string.Equals(x.Entra?.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>Wordt dit account in AD gewijzigd (gesynchroniseerd of enkel AD), of via Graph (cloud)?</summary>
    public static bool ViaAd(Persoon p) => p.Ad is not null && (p.Entra is null || p.Entra.OnPremisesSyncEnabled);

    public static PlanUitkomst Gebruiker(InventarisData data, OrganisatieConfig config, Persoon p, GebruikerGewenst gewenst)
    {
        if (p.Ad is null && p.Entra is null)
        {
            return PlanUitkomst.Fout("Dit account staat niet in de geladen gegevens.");
        }

        if (p.IsGast)
        {
            return PlanUitkomst.Fout("Gastgebruikers beheer je in Entra zelf; hun gegevens komen uit hun eigen organisatie.");
        }

        var huidig = GebruikerGewenst.Van(p, data);
        var viaAd = ViaAd(p);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vorige = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nu = new List<string>();
        var wordt = new List<string>();
        var waarschuwingen = new List<string>();

        // De dienst zoals in de organisatie gespeld (spaties en hoofdletters)
        if (gewenst.Dienst is { } d && config.ZoekDienst(d) is { } dienst)
        {
            gewenst.Dienst = dienst.Department;
        }
        else if (!string.IsNullOrWhiteSpace(gewenst.Dienst) && config.Diensten.Count > 0)
        {
            waarschuwingen.Add($"De dienst \"{gewenst.Dienst.Trim()}\" staat niet in de lijst bij Organisatie.");
        }

        foreach (var (veld, adNaam, graphNaam, naam) in Velden)
        {
            var oud = Waarde(huidig, veld) ?? string.Empty;
            var nieuw = Waarde(gewenst, veld);
            if (nieuw is null || string.Equals(oud, nieuw.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            var sleutel = viaAd ? adNaam : graphNaam;
            if (sleutel is null)
            {
                waarschuwingen.Add($"{naam} bestaat enkel in AD; dit account komt uit de cloud.");
                continue;
            }

            parameters[sleutel] = nieuw.Trim();
            vorige[sleutel] = oud;
            nu.Add($"{naam}: {(oud.Length == 0 ? "(leeg)" : oud)}");
            wordt.Add($"{naam}: {(nieuw.Trim().Length == 0 ? "(leeg)" : nieuw.Trim())}");
        }

        // Manager: in AD een kenmerk (DN), in de cloud een aparte opdracht
        Wijziging? managerZetten = null;
        if (gewenst.Manager is { } nieuweSleutel && !string.Equals(nieuweSleutel, huidig.Manager ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            var oudeManager = ManagerVan(data, p);
            var nieuweManager = nieuweSleutel.Length == 0 ? null : data.ZoekPersoon(nieuweSleutel);
            if (nieuweSleutel.Length > 0 && nieuweManager is null)
            {
                waarschuwingen.Add("De gekozen manager staat niet in de geladen gegevens.");
            }
            else if (viaAd)
            {
                if (nieuweManager is not null && nieuweManager.Ad is null)
                {
                    waarschuwingen.Add($"{nieuweManager.Naam} heeft geen AD-account en kan in AD geen manager zijn.");
                }
                else
                {
                    parameters["manager"] = nieuweManager?.Ad!.DistinguishedName ?? string.Empty;
                    vorige["manager"] = p.Ad!.Manager;
                    nu.Add($"Manager: {oudeManager?.Naam ?? "(geen)"}");
                    wordt.Add($"Manager: {nieuweManager?.Naam ?? "(geen)"}");
                }
            }
            else if (nieuweManager is not null && nieuweManager.Entra is null)
            {
                waarschuwingen.Add($"{nieuweManager.Naam} bestaat niet in Entra en kan geen manager zijn.");
            }
            else
            {
                managerZetten = new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphManagerZetten,
                    Doel = p.Entra!.Id,
                    DoelNaam = p.Naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = nieuweManager?.Entra!.Id ?? string.Empty },
                    Vorige = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = p.Entra.ManagerId },
                    Omschrijving = "Manager zetten in Entra",
                    HuidigeWaarde = oudeManager?.Naam ?? "(geen)",
                    NieuweWaarde = nieuweManager?.Naam ?? "(geen)",
                };
            }
        }

        if (parameters.Count == 0 && managerZetten is null)
        {
            return new PlanUitkomst(Array.Empty<Wijziging>(), new[] { waarschuwingen.Count > 0 ? string.Join(" ", waarschuwingen) : "Er is niets gewijzigd." });
        }

        var plan = new List<Wijziging>();
        if (managerZetten is not null)
        {
            plan.Add(managerZetten);
        }

        if (parameters.Count > 0)
        {
            plan.Insert(0, new()
            {
                Systeem = viaAd ? Systeem.ActiveDirectory : Systeem.Graph,
                Actie = viaAd ? Actie.AdKenmerkenZetten : Actie.GraphKenmerkenZetten,
                Doel = viaAd ? p.Ad!.DistinguishedName : p.Entra!.Id,
                DoelNaam = p.Naam,
                Parameters = parameters,
                Vorige = vorige,
                Omschrijving = viaAd
                    ? $"Gegevens wijzigen in AD ({parameters.Count})" + (p.Entra is not null ? "; Entra volgt na de synchronisatie (± 30 min)" : string.Empty)
                    : $"Gegevens wijzigen in Entra ({parameters.Count})",
                HuidigeWaarde = string.Join("; ", nu),
                NieuweWaarde = string.Join("; ", wordt),
                Waarschuwing = waarschuwingen.Count == 0 ? null : string.Join(" ", waarschuwingen),
            });
        }

        // Een andere dienst kan een andere afdeling zijn: de afdelingsgroep mee wisselen (dynamische groepen volgen vanzelf)
        var fouten = new List<string>();
        if (parameters.ContainsKey("department"))
        {
            var oudeAfdeling = config.AfdelingVoor(huidig.Dienst);
            var nieuweAfdeling = config.AfdelingVoor(gewenst.Dienst);
            if (!string.Equals(oudeAfdeling?.Groep, nieuweAfdeling?.Groep, StringComparison.OrdinalIgnoreCase))
            {
                var r = data.Index.Ref(p);
                foreach (var (afdeling, erbij) in new[] { (oudeAfdeling, false), (nieuweAfdeling, true) })
                {
                    if (afdeling?.Groep is not { Length: > 0 } naam)
                    {
                        continue;
                    }

                    if (config.IsDynamischeGroep(naam) || data.Index.Groepen.FirstOrDefault(g => string.Equals(g.Naam, naam, StringComparison.OrdinalIgnoreCase)) is not { } groep)
                    {
                        fouten.Add($"{naam} volgt vanzelf uit de dienst (dynamisch) of staat niet in de geladen groepen; niets te doen.");
                        continue;
                    }

                    if (groep.Dynamisch)
                    {
                        fouten.Add($"{naam} is dynamisch en volgt vanzelf uit de dienst.");
                        continue;
                    }

                    var u = erbij ? GroepPlannen.LidToevoegen(data, groep, r) : GroepPlannen.LidVerwijderen(data, groep, r);
                    plan.AddRange(u.Plan);
                    fouten.AddRange(u.Fouten);
                }
            }
        }

        return new PlanUitkomst(plan, fouten);
    }

    /// <summary>
    /// Een verschil tussen AD en Entra rechtzetten in AD (bij een gesynchroniseerd account volgt Entra dan vanzelf): de
    /// waarde zonder overbodige spaties, en een dienst zoals hij in de organisatie gespeld wordt.
    /// </summary>
    public static PlanUitkomst Rechtzetten(InventarisData data, OrganisatieConfig config, Verschil v)
    {
        if (v.Soort != VerschilSoort.KenmerkVerschilt || v.Persoon.Ad is null)
        {
            return PlanUitkomst.Fout("Enkel een kenmerk van een account in AD kan rechtgezet worden.");
        }

        // "Dienst (Department)" -> "Dienst"
        var kort = v.Kenmerk.Split(" (")[0].Trim();
        var veld = Velden.FirstOrDefault(x => string.Equals(x.Naam, kort, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Ad, v.Kenmerk, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Graph, v.Kenmerk, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Veld, v.Kenmerk, StringComparison.OrdinalIgnoreCase));
        if (veld.Veld is null)
        {
            return PlanUitkomst.Fout($"{v.Kenmerk} kan de app niet rechtzetten.");
        }

        var waarde = string.Join(' ', v.AdWaarde.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (veld.Veld == nameof(GebruikerGewenst.Dienst) && config.ZoekDienst(waarde) is { } dienst)
        {
            waarde = dienst.Department;
        }

        var gewenst = new GebruikerGewenst();
        typeof(GebruikerGewenst).GetProperty(veld.Veld)!.SetValue(gewenst, waarde);
        var uit = Gebruiker(data, config, v.Persoon, gewenst);
        return uit.Plan.Count == 0
            ? PlanUitkomst.Fout($"In AD staat al \"{waarde}\"; het verschil verdwijnt na de volgende synchronisatie.")
            : uit;
    }

    private static string? Waarde(GebruikerGewenst g, string veld) => (string?)typeof(GebruikerGewenst).GetProperty(veld)!.GetValue(g);
}
