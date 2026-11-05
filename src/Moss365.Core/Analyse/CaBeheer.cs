using System.Net;
using System.Text.Json;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Core.Analyse;

/// <summary>Wat de veiligheidscontrole vond: fouten houden de wijziging tegen, waarschuwingen staan in het plan.</summary>
public sealed record CaControleUitkomst(IReadOnlyList<string> Fouten, IReadOnlyList<string> Waarschuwingen)
{
    public bool Mag => Fouten.Count == 0;
}

/// <summary>
/// De veiligheidscontroles vóór elke wijziging aan Conditional Access. Een beleid dat aan staat, moet alle noodaccounts
/// uitsluiten en mag de beheerder die de wijziging doet niet buitensluiten. Afgedwongen bij het plannen én in de motor
/// (<see cref="Weigering"/>), zodat ook ongedaan maken of een gepland plan er niet omheen kan.
/// </summary>
public static class CaVeiligheid
{
    /// <summary>De noodaccounts uit de configuratie, gevonden in Entra (of null met de reden).</summary>
    public static (IReadOnlyList<Persoon> Accounts, string? Fout) Noodaccounts(InventarisData d, OrganisatieConfig c)
    {
        var upns = c.Noodaccounts.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (upns.Count == 0)
        {
            return (Array.Empty<Persoon>(), "Er zijn geen noodaccounts aangeduid (Organisatie > Noodaccounts). Zonder noodaccounts wijzigt de app geen beleid dat aan staat.");
        }

        var gevonden = new List<Persoon>();
        foreach (var upn in upns)
        {
            var p = d.Personen.FirstOrDefault(x => string.Equals(x.Entra?.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                return (Array.Empty<Persoon>(), $"Het noodaccount {upn} staat niet in de geladen gegevens van Entra. Laad Entra live of kijk de naam na bij Organisatie > Noodaccounts.");
            }

            gevonden.Add(p);
        }

        return (gevonden, null);
    }

    /// <summary>Controleert een beleid zoals het na de wijziging zal zijn.</summary>
    public static CaControleUitkomst Controleer(CaBeleid nieuw, InventarisData d, OrganisatieConfig c, string? eigenUpn)
    {
        var fouten = new List<string>();
        var waarschuwingen = new List<string>();
        if (nieuw.Status == CaStatus.Uit)
        {
            return new CaControleUitkomst(fouten, waarschuwingen);
        }

        var ca = (d.ConditionalAccess ?? new CaGegevens(Array.Empty<CaBeleid>(), Array.Empty<BenoemdeLocatie>(), Array.Empty<Authenticatiesterkte>(),
            new Dictionary<string, string>(), null)) with { Beleiden = new[] { nieuw } };
        var naam = CaLezer.Namen(d);
        var lijst = nieuw.Status == CaStatus.Aan ? fouten : waarschuwingen;
        var achter = nieuw.Status == CaStatus.Aan ? string.Empty : " Aanzetten zal geweigerd worden tot dat in orde is.";

        // 1. Noodaccounts
        var (nood, noodFout) = Noodaccounts(d, c);
        if (noodFout is not null)
        {
            lijst.Add(noodFout + achter);
        }

        foreach (var p in nood)
        {
            var g = CaGebruiker.Van(p, d);
            var bereik = g is null ? CaAntwoord.Misschien("geen Entra-gegevens") : CaLezer.Bereik(nieuw, g, naam, ca);
            if (bereik.Geldt != CaGeldt.Nee)
            {
                lijst.Add($"Het noodaccount {p.Naam} valt {(bereik.Geldt == CaGeldt.Ja ? "onder" : "misschien onder")} dit beleid ({bereik.Reden}). Sluit het uit.{achter}");
            }
        }

        // 2. Jezelf niet buitensluiten
        var eigen = eigenUpn is null ? null
            : d.Personen.FirstOrDefault(x => string.Equals(x.Entra?.UserPrincipalName, eigenUpn, StringComparison.OrdinalIgnoreCase));
        var eigenG = eigen is null ? null : CaGebruiker.Van(eigen, d);
        if (eigenG is null)
        {
            waarschuwingen.Add("Je eigen account staat niet in de geladen gegevens van Entra: de app kan niet nakijken of je jezelf buitensluit.");
        }
        else
        {
            foreach (var s in new[] { new CaScenario(CaApp.Office365, CaPlek.Belgie), new CaScenario(CaApp.Beheerportalen, CaPlek.Belgie) })
            {
                var geldt = CaLezer.Geldt(nieuw, eigenG, s, ca, naam);
                if (geldt.Geldt == CaGeldt.Nee)
                {
                    continue;
                }

                var waar = s.App == CaApp.Office365 ? "Office 365" : "de beheerportalen";
                if (nieuw.Blokkeert)
                {
                    var tekst = $"Dit beleid blokkeert {(geldt.Geldt == CaGeldt.Ja ? "" : "misschien ")}jouw eigen account ({eigen!.Naam}) voor {waar} vanuit België ({geldt.Reden}).";
                    (nieuw.Status == CaStatus.Aan ? fouten : waarschuwingen).Add(tekst);
                    break;
                }

                if (nieuw.Controles.Count > 0 && nieuw.Controles.All(x => x is "compliantDevice" or "domainJoinedDevice"))
                {
                    waarschuwingen.Add($"Dit beleid vraagt voor jouw account een beheerd toestel ({waar}): zonder zo'n toestel geraak je er niet meer in.");
                    break;
                }
            }
        }

        if (nieuw.Status == CaStatus.Aan && nieuw.Blokkeert)
        {
            waarschuwingen.Add("Dit beleid blokkeert: kijk eerst het effect na in alleen rapport (Conditional Access > Beleiden > Effect laden).");
        }

        return new CaControleUitkomst(fouten.Distinct().ToList(), waarschuwingen.Distinct().ToList());
    }

    /// <summary>
    /// Voor de motor: waarom deze wijziging aan Conditional Access niet mag (of null). Een beleid dat na de wijziging aan staat,
    /// moet door de controle; is het beleid niet geladen, dan weigert de app (ze kan dan niets nakijken).
    /// </summary>
    public static string? Weigering(Wijziging w, InventarisData d, OrganisatieConfig c, string? eigenUpn)
    {
        if (w.Actie is not (Actie.GraphCaBeleidMaken or Actie.GraphCaBeleidWijzigen))
        {
            return null;
        }

        CaBeleid nieuw;
        try
        {
            if (w.Actie == Actie.GraphCaBeleidMaken)
            {
                using var doc = JsonDocument.Parse(w.P("json"));
                nieuw = CaJson.Lees(doc.RootElement);
            }
            else
            {
                var huidig = d.ConditionalAccess?.Beleiden.FirstOrDefault(b => b.Id == w.Doel);
                if (huidig is null)
                {
                    return "het beleid staat niet in de geladen gegevens; laad Conditional Access live, zodat de app de noodaccounts kan nakijken";
                }

                nieuw = CaJson.NaPatch(huidig, w.P("json"));
            }
        }
        catch (JsonException)
        {
            return "de wijziging is geen geldige JSON";
        }

        var uitkomst = Controleer(nieuw, d, c, eigenUpn);
        return uitkomst.Mag ? null : string.Join(" ", uitkomst.Fouten);
    }
}

/// <summary>Een sjabloon voor een nieuw beleid (de basislijn van Microsoft). Een nieuw beleid komt altijd in alleen rapport.</summary>
public sealed record CaSjabloon(string Sleutel, string Naam, string Uitleg, bool Blokkeert, bool KanPilot = true, bool VraagtLandenLocatie = false);

/// <summary>Opties bij een sjabloon: een pilotgroep in plaats van iedereen, de locatie met de toegelaten landen.</summary>
public sealed record CaSjabloonOpties(string? PilotGroepId = null, string? LandenLocatieId = null);

public static class CaSjablonen
{
    /// <summary>Beheerrollen die over de hele tenant gaan (roleTemplateId's van Microsoft, voor iedereen dezelfde).</summary>
    public static readonly IReadOnlyDictionary<string, string> KritiekeRollen = new Dictionary<string, string>
    {
        [CaGegevens.RolGlobalAdmin] = "Global Administrator",
        ["e8611ab8-c189-46e8-94e1-60213ab1f814"] = "Privileged Role Administrator",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Security Administrator",
        ["29232cdf-9323-42fd-ade2-1d097af3e4de"] = "Exchange Administrator",
        ["f28a1f50-f6e7-4571-818b-6a12f2af6b6c"] = "SharePoint Administrator",
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Privileged Authentication Administrator",
        ["b1be1c3e-b65d-4f19-8427-f6fa0d97feb9"] = "Conditional Access Administrator",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
        ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Cloud Application Administrator",
        ["8ac3fc64-6eca-42ea-9e69-59f4c7b60eb2"] = "Hybrid Identity Administrator",
        ["c4e39bd9-1100-46d3-8c65-fb160da0071f"] = "Authentication Administrator",
        ["729827e3-9c14-49f7-bb1b-9608f156bbb8"] = "Helpdesk Administrator",
        ["b0f54661-2d74-4c50-afa3-1ec803f12efe"] = "Billing Administrator",
    };

    public static readonly IReadOnlyList<CaSjabloon> Alle = new[]
    {
        new CaSjabloon("mfa-iedereen", "MFA voor alle gebruikers", "Iedereen moet MFA doen bij elke app. De basis.", false),
        new CaSjabloon("verouderd-blokkeren", "Verouderde aanmelding blokkeren", "Blokkeert oude programma's zonder MFA (IMAP, POP, SMTP-aanmelding, ActiveSync). Kijk eerst na wie die nog gebruikt (scanners, koppelingen).", true),
        new CaSjabloon("beheerders-phishingbestendig", "Phishingbestendige MFA voor beheerrollen", "Wie een beheerrol heeft, meldt zich aan met Authenticator zonder wachtwoord, een FIDO2-sleutel, passkey of Windows Hello.", false, KanPilot: false),
        new CaSjabloon("gasten-mfa", "MFA voor gasten", "Gastgebruikers moeten MFA doen.", false, KanPilot: false),
        new CaSjabloon("toestelcode-blokkeren", "Aanmelden met een toestelcode blokkeren", "Blokkeert de device code flow, een veelgebruikte phishingtruc. Vergaderzaaltoestellen gebruiken die soms.", true),
        new CaSjabloon("registreren-vanop-kantoor", "MFA registreren enkel vanop kantoor", "Beveiligingsgegevens (MFA) registreren kan enkel vanaf een vertrouwde locatie. Daarbuiten: met een Temporary Access Pass.", true),
        new CaSjabloon("landen-blokkeren", "Toegang blokkeren buiten de toegelaten landen", "Blokkeert aanmelden vanuit landen die niet in de gekozen benoemde locatie staan.", true, VraagtLandenLocatie: true),
        new CaSjabloon("beheerportalen-beheerd-toestel", "Beheerd toestel voor de beheerportalen", "Wie een beheerrol heeft, opent de beheerportalen enkel vanop een beheerd (Intune) of hybride toestel.", false, KanPilot: false),
    };

    /// <summary>Een nieuw beleid uit een sjabloon, altijd in alleen rapport, met de noodaccounts uitgesloten.</summary>
    public static PlanUitkomst Plan(string sleutel, CaSjabloonOpties opties, InventarisData d, OrganisatieConfig c, string? eigenUpn)
    {
        var sjabloon = Alle.FirstOrDefault(s => s.Sleutel == sleutel);
        if (sjabloon is null)
        {
            return PlanUitkomst.Fout("Onbekend sjabloon.");
        }

        if (d.ConditionalAccess is not { Melding: null } ca)
        {
            return PlanUitkomst.Fout("Conditional Access is niet geladen. Laad eerst live (Verbindingen).");
        }

        if (ca.Beleiden.Any(b => string.Equals(b.Naam, sjabloon.Naam, StringComparison.OrdinalIgnoreCase)))
        {
            return PlanUitkomst.Fout($"Er bestaat al een beleid \"{sjabloon.Naam}\".");
        }

        var (nood, noodFout) = CaVeiligheid.Noodaccounts(d, c);
        if (noodFout is not null)
        {
            return PlanUitkomst.Fout(noodFout);
        }

        var pilot = sjabloon.KanPilot && opties.PilotGroepId is { Length: > 0 } g ? g : null;
        string[] iedereen = pilot is null ? new[] { "All" } : Array.Empty<string>();
        string[] pilotGroep = pilot is null ? Array.Empty<string>() : new[] { pilot };
        var rollen = KritiekeRollen.Keys.ToList();
        var basis = new CaBeleid
        {
            Naam = sjabloon.Naam,
            Status = CaStatus.Rapport,
            GebruikersIn = iedereen,
            GroepenIn = pilotGroep,
            GebruikersUit = nood.Select(p => p.Entra!.Id).ToList(),
            AppsIn = new[] { "All" },
        };

        CaBeleid beleid;
        switch (sjabloon.Sleutel)
        {
            case "mfa-iedereen":
                beleid = basis with { Controles = new[] { "mfa" } };
                break;
            case "verouderd-blokkeren":
                beleid = basis with { ClientApps = new[] { "exchangeActiveSync", "other" }, Controles = new[] { "block" } };
                break;
            case "beheerders-phishingbestendig":
                beleid = basis with { GebruikersIn = Array.Empty<string>(), GroepenIn = Array.Empty<string>(), RollenIn = rollen, SterkteId = CaGegevens.SterktePhishingbestendig };
                break;
            case "gasten-mfa":
                beleid = basis with { GebruikersIn = new[] { "GuestsOrExternalUsers" }, GroepenIn = Array.Empty<string>(), GastenIn = true, Controles = new[] { "mfa" } };
                break;
            case "toestelcode-blokkeren":
                beleid = basis with { Overdracht = new[] { "deviceCodeFlow" }, Controles = new[] { "block" } };
                break;
            case "registreren-vanop-kantoor":
                if (!ca.Locaties.Any(l => l.Vertrouwd))
                {
                    return PlanUitkomst.Fout("Er is nog geen vertrouwde benoemde locatie (bv. de IP-adressen van de gebouwen). Maak die eerst bij Beheren > Benoemde locaties.");
                }

                beleid = basis with
                {
                    AppsIn = Array.Empty<string>(),
                    Gebruikersacties = new[] { "urn:user:registersecurityinfo" },
                    LocatiesIn = new[] { "All" },
                    LocatiesUit = new[] { "AllTrusted" },
                    Controles = new[] { "block" },
                };
                break;
            case "landen-blokkeren":
                if (opties.LandenLocatieId is not { Length: > 0 } landen || ca.Locatie(landen) is not { IsIp: false } locatie)
                {
                    return PlanUitkomst.Fout("Kies de benoemde locatie met de toegelaten landen (een locatie met landen, geen IP-adressen).");
                }

                if (!locatie.BevatBelgie)
                {
                    return PlanUitkomst.Fout($"De locatie \"{locatie.Naam}\" bevat België niet: dan blokkeert dit beleid iedereen.");
                }

                beleid = basis with { LocatiesIn = new[] { "All" }, LocatiesUit = new[] { landen }, Controles = new[] { "block" } };
                break;
            default:
                beleid = basis with
                {
                    GebruikersIn = Array.Empty<string>(),
                    GroepenIn = Array.Empty<string>(),
                    RollenIn = rollen,
                    AppsIn = new[] { "MicrosoftAdminPortals" },
                    Controles = new[] { "compliantDevice", "domainJoinedDevice" },
                };
                break;
        }

        var controle = CaVeiligheid.Controleer(beleid, d, c, eigenUpn);
        if (!controle.Mag)
        {
            return new PlanUitkomst(Array.Empty<Wijziging>(), controle.Fouten);
        }

        var json = CaJson.NaarGraph(beleid);
        var w = new Wijziging
        {
            Systeem = Systeem.Graph,
            Actie = Actie.GraphCaBeleidMaken,
            Doel = "nieuw",
            DoelNaam = beleid.Naam,
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
            Omschrijving = $"Nieuw Conditional Access-beleid \"{beleid.Naam}\" in alleen rapport" + (pilot is null ? string.Empty : " (enkel de pilotgroep)"),
            HuidigeWaarde = "(bestaat niet)",
            NieuweWaarde = $"alleen rapport; {nood.Count} noodaccount(s) uitgesloten",
            Waarschuwing = controle.Waarschuwingen.Count == 0 ? null : string.Join(" ", controle.Waarschuwingen),
        };
        return PlanUitkomst.Van(new[] { w });
    }
}

/// <summary>Plannen die een bestaand beleid of een benoemde locatie wijzigen; telkens met de oude waarden voor ongedaan maken.</summary>
public static class CaPlannen
{
    private static string HuidigeJson(CaBeleid b) => b.Json.Length > 0 ? b.Json : CaJson.NaarGraph(b);

    private static Wijziging Wijzigen(CaBeleid b, string patch, string omschrijving, string huidig, string nieuw, string? waarschuwing) => new()
    {
        Systeem = Systeem.Graph,
        Actie = Actie.GraphCaBeleidWijzigen,
        Doel = b.Id,
        DoelNaam = b.Naam,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = patch },
        Vorige = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.Vorige(HuidigeJson(b), patch) },
        Omschrijving = omschrijving,
        HuidigeWaarde = huidig,
        NieuweWaarde = nieuw,
        Waarschuwing = waarschuwing,
    };

    private static PlanUitkomst MetControle(CaBeleid b, string patch, InventarisData d, OrganisatieConfig c, string? eigenUpn,
        Func<string?, Wijziging> maak)
    {
        var controle = CaVeiligheid.Controleer(CaJson.NaPatch(b, patch), d, c, eigenUpn);
        return controle.Mag
            ? PlanUitkomst.Van(new[] { maak(controle.Waarschuwingen.Count == 0 ? null : string.Join(" ", controle.Waarschuwingen)) })
            : new PlanUitkomst(Array.Empty<Wijziging>(), controle.Fouten);
    }

    /// <summary>Een beleid aanzetten, op alleen rapport zetten of uitzetten.</summary>
    public static PlanUitkomst Status(CaBeleid b, CaStatus nieuw, InventarisData d, OrganisatieConfig c, string? eigenUpn)
    {
        if (b.Status == nieuw)
        {
            return PlanUitkomst.Fout($"Het beleid staat al op {CaLezer.StatusNaam(nieuw).ToLowerInvariant()}.");
        }

        var patch = CaJson.StatusPatch(nieuw);
        return MetControle(b, patch, d, c, eigenUpn, waarschuwing => Wijzigen(b, patch,
            $"\"{b.Naam}\" op {CaLezer.StatusNaam(nieuw).ToLowerInvariant()} zetten", CaLezer.StatusNaam(b.Status), CaLezer.StatusNaam(nieuw), waarschuwing));
    }

    /// <summary>Iemand uitsluiten van een beleid (of de uitzondering weghalen).</summary>
    public static PlanUitkomst Uitzondering(CaBeleid b, Persoon p, bool toevoegen, InventarisData? d = null, OrganisatieConfig? c = null, string? eigenUpn = null)
    {
        if (p.Entra is not { } e)
        {
            return PlanUitkomst.Fout($"{p.Naam} heeft geen Entra-account; Conditional Access geldt enkel in Entra.");
        }

        var staat = b.GebruikersUit.Contains(e.Id, StringComparer.OrdinalIgnoreCase);
        if (staat == toevoegen)
        {
            return PlanUitkomst.Fout(toevoegen ? $"{p.Naam} is al uitgezonderd van \"{b.Naam}\"." : $"{p.Naam} staat al niet meer uitgezonderd van \"{b.Naam}\".");
        }

        var lijst = toevoegen
            ? b.GebruikersUit.Append(e.Id).ToList()
            : b.GebruikersUit.Where(x => !string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var patch = CaJson.UitzonderingenPatch(b, lijst);
        Wijziging Maak(string? waarschuwing) => Wijzigen(b, patch,
            toevoegen ? $"{p.Naam} uitzonderen van \"{b.Naam}\"" : $"Uitzondering van {p.Naam} op \"{b.Naam}\" weghalen",
            $"{b.GebruikersUit.Count} rechtstreeks uitgezonderd", $"{lijst.Count} rechtstreeks uitgezonderd", waarschuwing);

        // Weghalen kan een noodaccount weer onder het beleid brengen: controleren als de gegevens er zijn
        return d is not null && c is not null ? MetControle(b, patch, d, c, eigenUpn, Maak) : PlanUitkomst.Van(new[] { Maak(null) });
    }

    /// <summary>Een beleid terugzetten zoals het in een back-up stond (of opnieuw maken als het intussen weg is).</summary>
    public static PlanUitkomst Terugzetten(CaBeleid uitBackup, CaBeleid? huidig, InventarisData d, OrganisatieConfig c, string? eigenUpn)
    {
        if (uitBackup.Json.Length == 0)
        {
            return PlanUitkomst.Fout("De back-up bevat de volledige gegevens van dit beleid niet.");
        }

        var controle = CaVeiligheid.Controleer(uitBackup, d, c, eigenUpn);
        if (!controle.Mag)
        {
            return new PlanUitkomst(Array.Empty<Wijziging>(), controle.Fouten);
        }

        var waarschuwing = controle.Waarschuwingen.Count == 0 ? null : string.Join(" ", controle.Waarschuwingen);
        var json = CaJson.Schrijfbaar(uitBackup.Json);
        if (huidig is null)
        {
            return PlanUitkomst.Van(new[]
            {
                new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphCaBeleidMaken,
                    Doel = "nieuw",
                    DoelNaam = uitBackup.Naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                    Omschrijving = $"Beleid \"{uitBackup.Naam}\" opnieuw maken uit de back-up (het bestaat niet meer)",
                    HuidigeWaarde = "(bestaat niet)",
                    NieuweWaarde = CaLezer.StatusNaam(uitBackup.Status),
                    Waarschuwing = waarschuwing,
                },
            });
        }

        return PlanUitkomst.Van(new[]
        {
            new Wijziging
            {
                Systeem = Systeem.Graph,
                Actie = Actie.GraphCaBeleidWijzigen,
                Doel = huidig.Id,
                DoelNaam = huidig.Naam,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Vorige = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.Schrijfbaar(HuidigeJson(huidig)) },
                Omschrijving = $"Beleid \"{huidig.Naam}\" terugzetten zoals in de back-up",
                HuidigeWaarde = CaLezer.StatusNaam(huidig.Status),
                NieuweWaarde = CaLezer.StatusNaam(uitBackup.Status),
                Waarschuwing = waarschuwing,
            },
        });
    }

    /// <summary>Is dit een geldig IP-bereik in CIDR-notatie (bv. 203.0.113.0/24)?</summary>
    public static bool IsCidr(string waarde)
    {
        var delen = waarde.Trim().Split('/');
        if (delen.Length != 2 || !IPAddress.TryParse(delen[0], out var ip) || !int.TryParse(delen[1], out var lengte))
        {
            return false;
        }

        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return lengte >= 1 && lengte <= max;
    }

    /// <summary>Een benoemde locatie maken of wijzigen: IP-bereiken (CIDR) of landen (ISO-code, bv. BE).</summary>
    public static PlanUitkomst Locatie(BenoemdeLocatie? bestaand, string naam, bool isIp, IEnumerable<string> waarden, bool vertrouwd, InventarisData d)
    {
        var lijst = waarden.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        naam = naam.Trim();
        var fouten = new List<string>();
        if (naam.Length == 0)
        {
            fouten.Add("Geef de locatie een naam.");
        }

        if (lijst.Count == 0)
        {
            fouten.Add(isIp ? "Geef minstens één IP-bereik (bv. 203.0.113.10/32)." : "Geef minstens één land (bv. BE).");
        }

        fouten.AddRange(isIp
            ? lijst.Where(w => !IsCidr(w)).Select(w => $"\"{w}\" is geen IP-bereik in CIDR-notatie (bv. 203.0.113.0/24).")
            : lijst.Where(w => w.Length != 2 || !w.All(char.IsAsciiLetter)).Select(w => $"\"{w}\" is geen landcode van twee letters (bv. BE, NL)."));
        if (bestaand is not null && bestaand.IsIp != isIp)
        {
            fouten.Add("Een locatie met IP-adressen kan geen locatie met landen worden (of omgekeerd).");
        }

        if (bestaand is null && d.ConditionalAccess?.Locaties.Any(l => string.Equals(l.Naam, naam, StringComparison.OrdinalIgnoreCase)) == true)
        {
            fouten.Add($"Er bestaat al een locatie \"{naam}\".");
        }

        if (fouten.Count > 0)
        {
            return new PlanUitkomst(Array.Empty<Wijziging>(), fouten);
        }

        var json = CaJson.LocatieJson(naam, isIp, lijst, vertrouwd, bestaand?.OnbekendeLanden ?? false);
        var gebruikt = bestaand is null ? new List<CaBeleid>()
            : d.ConditionalAccess?.Beleiden.Where(b => b.LocatiesIn.Concat(b.LocatiesUit).Contains(bestaand.Id, StringComparer.OrdinalIgnoreCase)).ToList() ?? new();
        var waarschuwing = gebruikt.Count == 0 ? null
            : $"Deze locatie staat in {gebruikt.Count} beleid(en): {string.Join(", ", gebruikt.Select(b => b.Naam))}. De wijziging geldt meteen voor die beleiden.";
        var tekst = string.Join(", ", lijst);
        var w = bestaand is null
            ? new Wijziging
            {
                Systeem = Systeem.Graph,
                Actie = Actie.GraphCaLocatieMaken,
                Doel = "nieuw",
                DoelNaam = naam,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Omschrijving = $"Nieuwe benoemde locatie \"{naam}\"" + (isIp && vertrouwd ? " (vertrouwd)" : string.Empty),
                HuidigeWaarde = "(bestaat niet)",
                NieuweWaarde = tekst,
            }
            : new Wijziging
            {
                Systeem = Systeem.Graph,
                Actie = Actie.GraphCaLocatieWijzigen,
                Doel = bestaand.Id,
                DoelNaam = bestaand.Naam,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Vorige = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["json"] = bestaand.Json.Length > 0
                        ? CaJson.LocatieSchrijfbaar(bestaand.Json)
                        : CaJson.LocatieJson(bestaand.Naam, bestaand.IsIp, bestaand.Waarden, bestaand.Vertrouwd, bestaand.OnbekendeLanden),
                },
                Omschrijving = $"Benoemde locatie \"{bestaand.Naam}\" wijzigen",
                HuidigeWaarde = string.Join(", ", bestaand.Waarden),
                NieuweWaarde = tekst,
                Waarschuwing = waarschuwing,
            };
        return PlanUitkomst.Van(new[] { w });
    }
}

/// <summary>Een verschil tussen twee standen van een beleid.</summary>
public sealed record CaVerschil(string BeleidId, string Naam, string Soort, IReadOnlyList<string> Details);

/// <summary>Vergelijkt twee standen van de beleiden (een back-up met nu, of twee back-ups).</summary>
public static class CaVergelijking
{
    public static IReadOnlyList<CaVerschil> Vergelijk(IReadOnlyList<CaBeleid> oud, IReadOnlyList<CaBeleid> nieuw, Func<string, string> naam, CaGegevens? ca = null)
    {
        var uit = new List<CaVerschil>();
        foreach (var n in nieuw)
        {
            var o = oud.FirstOrDefault(x => x.Id == n.Id);
            if (o is null)
            {
                uit.Add(new CaVerschil(n.Id, n.Naam, "nieuw", new[] { "status: " + CaLezer.StatusNaam(n.Status) }));
                continue;
            }

            var details = Details(o, n, naam, ca).ToList();
            if (details.Count == 0 && Gewoon(o.Json) != Gewoon(n.Json))
            {
                details.Add("andere instellingen (sessie, toestelfilter, ...)");
            }

            if (details.Count > 0)
            {
                uit.Add(new CaVerschil(n.Id, n.Naam, "gewijzigd", details));
            }
        }

        uit.AddRange(oud.Where(o => nieuw.All(n => n.Id != o.Id)).Select(o => new CaVerschil(o.Id, o.Naam, "verdwenen", new[] { "bestaat niet meer" })));
        return uit.OrderBy(v => v.Naam, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>De JSON zonder wat Graph zelf bijhoudt (wijzigingsdatum), om echte verschillen te zien.</summary>
    private static string Gewoon(string json) => json.Length == 0 ? string.Empty : CaJson.Schrijfbaar(json);

    private static IEnumerable<string> Details(CaBeleid o, CaBeleid n, Func<string, string> naam, CaGegevens? ca)
    {
        if (o.Naam != n.Naam)
        {
            yield return $"naam: {o.Naam} → {n.Naam}";
        }

        if (o.Status != n.Status)
        {
            yield return $"status: {CaLezer.StatusNaam(o.Status)} → {CaLezer.StatusNaam(n.Status)}";
        }

        string Rol(string id) => ca?.RolNaam(id) ?? (CaSjablonen.KritiekeRollen.TryGetValue(id, out var r) ? r : id);
        string Locatie(string id) => ca is null ? id : CaLezer.LocatieNaam(id, ca);
        foreach (var (titel, a, b, namen) in new (string, IReadOnlyList<string>, IReadOnlyList<string>, Func<string, string>)[]
                 {
                     ("opgenomen gebruikers", o.GebruikersIn, n.GebruikersIn, naam),
                     ("uitgezonderde gebruikers", o.GebruikersUit, n.GebruikersUit, naam),
                     ("opgenomen groepen", o.GroepenIn, n.GroepenIn, naam),
                     ("uitgezonderde groepen", o.GroepenUit, n.GroepenUit, naam),
                     ("opgenomen rollen", o.RollenIn, n.RollenIn, Rol),
                     ("uitgezonderde rollen", o.RollenUit, n.RollenUit, Rol),
                     ("apps", o.AppsIn, n.AppsIn, CaLezer.AppNaam),
                     ("uitgezonderde apps", o.AppsUit, n.AppsUit, CaLezer.AppNaam),
                     ("programma's", o.ClientApps, n.ClientApps, CaLezer.ClientNaam),
                     ("locaties", o.LocatiesIn, n.LocatiesIn, Locatie),
                     ("uitgezonderde locaties", o.LocatiesUit, n.LocatiesUit, Locatie),
                     ("vereisten", o.Controles, n.Controles, CaLezer.ControleNaam),
                 })
        {
            var erbij = b.Except(a, StringComparer.OrdinalIgnoreCase).Select(namen).ToList();
            var eraf = a.Except(b, StringComparer.OrdinalIgnoreCase).Select(namen).ToList();
            if (erbij.Count + eraf.Count > 0)
            {
                yield return $"{titel}: " + string.Join("; ", erbij.Select(x => "+ " + x).Concat(eraf.Select(x => "− " + x)));
            }
        }

        if (o.SterkteId != n.SterkteId)
        {
            yield return $"authenticatiesterkte: {o.SterkteNaam ?? o.SterkteId ?? "(geen)"} → {n.SterkteNaam ?? n.SterkteId ?? "(geen)"}";
        }
    }
}
