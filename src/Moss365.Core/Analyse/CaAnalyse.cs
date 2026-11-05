using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

/// <summary>Geldt een beleid? "Misschien" als de app dat met de gelezen gegevens niet zeker kan zeggen.</summary>
public enum CaGeldt
{
    Ja,
    Nee,
    Misschien,
}

/// <summary>Een antwoord met de reden in gewone taal.</summary>
public sealed record CaAntwoord(CaGeldt Geldt, string Reden)
{
    public static CaAntwoord Ja(string reden) => new(CaGeldt.Ja, reden);
    public static CaAntwoord Nee(string reden) => new(CaGeldt.Nee, reden);
    public static CaAntwoord Misschien(string reden) => new(CaGeldt.Misschien, reden);
}

/// <summary>Wat Conditional Access van een gebruiker weet: groepen (ook via geneste groepen) en beheerrollen.</summary>
public sealed record CaGebruiker(string Id, string Naam, bool IsGast, IReadOnlySet<string> Groepen, IReadOnlySet<string> Rollen, bool GroepenVolledig)
{
    /// <summary>Null als de persoon geen Entra-account heeft (Conditional Access geldt enkel in Entra).</summary>
    public static CaGebruiker? Van(Persoon p, InventarisData d)
    {
        if (p.Entra is not { } e)
        {
            return null;
        }

        var ca = d.ConditionalAccess;
        var groepen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (d.CloudLidmaatschap.TryGetValue(e.Id, out var direct))
        {
            groepen.UnionWith(direct);
        }

        if (ca?.GroepLeden is { } leden)
        {
            groepen.UnionWith(leden.Where(g => g.Value.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).Select(g => g.Key));
        }

        var rolIdOpNaam = ca?.Rollen.GroupBy(r => r.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase)
                          ?? new Dictionary<string, string>();
        var rollen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in d.Beveiliging?.RollenVan(e.Id) ?? Enumerable.Empty<RolLid>())
        {
            if (r.RoleTemplateId.Length > 0)
            {
                rollen.Add(r.RoleTemplateId);
            }
            else if (rolIdOpNaam.TryGetValue(r.Rol, out var id))
            {
                rollen.Add(id);
            }
            else if (r.Rol == "Global Administrator")
            {
                rollen.Add(CaGegevens.RolGlobalAdmin);
            }
        }

        return new CaGebruiker(e.Id, p.Naam, e.IsGast, groepen, rollen, ca?.GroepLeden is not null);
    }
}

/// <summary>Een beleid in gewone taal: wie, welke apps, onder welke voorwaarden en met welk gevolg.</summary>
public sealed record CaUitleg(IReadOnlyList<string> Wie, IReadOnlyList<string> Apps, IReadOnlyList<string> Voorwaarden, string Gevolg, IReadOnlyList<string> Sessie)
{
    public string Kort => $"{string.Join("; ", Wie)} - {string.Join("; ", Apps)}: {Gevolg}";
}

/// <summary>Uitleg en bereik van Conditional Access-beleiden.</summary>
public static class CaLezer
{
    private static readonly Dictionary<string, string> AppNamen = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = "alle cloud-apps",
        ["None"] = "geen enkele app",
        ["Office365"] = "Office 365 (Exchange, SharePoint, OneDrive, Teams ...)",
        ["MicrosoftAdminPortals"] = "de Microsoft-beheerportalen",
        ["00000002-0000-0ff1-ce00-000000000000"] = "Exchange Online",
        ["00000003-0000-0ff1-ce00-000000000000"] = "SharePoint Online",
        ["cc15fd57-2c6c-4117-a88c-83b1d56b4bbe"] = "Microsoft Teams",
        ["797f4846-ba00-4fd7-ba43-dac1f8f63013"] = "Azure-beheer",
        ["00000003-0000-0000-c000-000000000000"] = "Microsoft Graph",
        ["0000000a-0000-0000-c000-000000000000"] = "Microsoft Intune",
        ["d4ebce55-015a-49b5-a083-c84d1797ae8c"] = "Intune-inschrijving",
    };

    /// <summary>De apps die bij Office 365 horen (een beleid voor één ervan geldt ook in het scenario Office 365).</summary>
    private static readonly HashSet<string> OfficeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Office365", "00000002-0000-0ff1-ce00-000000000000", "00000003-0000-0ff1-ce00-000000000000", "cc15fd57-2c6c-4117-a88c-83b1d56b4bbe",
    };

    public const string ActieBeveiligingsgegevens = "urn:user:registersecurityinfo";

    public static string AppNaam(string id) => AppNamen.TryGetValue(id, out var n) ? n : "app " + id;

    public static string ActieNaam(string actie) => actie.ToLowerInvariant() switch
    {
        ActieBeveiligingsgegevens => "beveiligingsgegevens registreren (MFA instellen)",
        "urn:user:registerdevice" => "een toestel registreren of koppelen",
        _ => actie,
    };

    public static string ClientNaam(string c) => c switch
    {
        "all" => "alle soorten",
        "browser" => "de browser",
        "mobileAppsAndDesktopClients" => "apps en desktopprogramma's",
        "exchangeActiveSync" => "Exchange ActiveSync (verouderd)",
        "other" => "andere verouderde programma's (IMAP, POP, SMTP ...)",
        _ => c,
    };

    public static string PlatformNaam(string p) => p.ToLowerInvariant() switch
    {
        "all" => "alle platformen",
        "windows" => "Windows",
        "macos" => "macOS",
        "ios" => "iOS",
        "android" => "Android",
        "linux" => "Linux",
        "windowsphone" => "Windows Phone",
        _ => p,
    };

    public static string ControleNaam(string c) => c switch
    {
        "block" => "blokkeren",
        "mfa" => "MFA",
        "compliantDevice" => "een conform toestel (Intune)",
        "domainJoinedDevice" => "een toestel dat hybride aan Entra gekoppeld is",
        "approvedApplication" => "een goedgekeurde app",
        "compliantApplication" => "een app met app-beveiligingsbeleid",
        "passwordChange" => "het wachtwoord wijzigen",
        _ => c,
    };

    public static string RisicoNaam(string r) => r switch
    {
        "low" => "laag",
        "medium" => "gemiddeld",
        "high" => "hoog",
        "none" => "geen",
        _ => r,
    };

    public static string StatusNaam(CaStatus s) => s switch
    {
        CaStatus.Aan => "Aan",
        CaStatus.Rapport => "Alleen rapport",
        _ => "Uit",
    };

    public static string LocatieNaam(string id, CaGegevens ca) => id switch
    {
        "All" => "elke locatie",
        "AllTrusted" => "alle vertrouwde locaties",
        _ => ca.Locatie(id) is { } l ? l.Naam : "locatie " + id,
    };

    // ------------------------------------------------------------------ uitleg

    public static CaUitleg Uitleg(CaBeleid b, CaGegevens ca, Func<string, string> naam)
    {
        string Namen(IEnumerable<string> ids) => string.Join(", ", ids.Select(naam));
        string Rollen(IEnumerable<string> ids) => string.Join(", ", ids.Select(ca.RolNaam));

        var wie = new List<string>();
        var echteIn = b.GebruikersIn.Where(u => u is not ("All" or "None" or "GuestsOrExternalUsers")).ToList();
        if (b.GebruikersIn.Contains("All"))
        {
            wie.Add("Alle gebruikers");
        }
        else if (b.GebruikersIn.Contains("None") && b.GroepenIn.Count + b.RollenIn.Count == 0 && !b.GastenIn)
        {
            wie.Add("Niemand (het beleid geldt voor geen enkele gebruiker)");
        }
        else
        {
            if (echteIn.Count > 0) wie.Add("Gebruikers: " + Namen(echteIn));
            if (b.GroepenIn.Count > 0) wie.Add("Leden van: " + Namen(b.GroepenIn));
            if (b.RollenIn.Count > 0) wie.Add("Met de rol: " + Rollen(b.RollenIn));
            if (b.GastenIn) wie.Add("Gasten en externe gebruikers");
            if (wie.Count == 0) wie.Add("Niemand");
        }

        var echteUit = b.GebruikersUit.Where(u => u != "GuestsOrExternalUsers").ToList();
        if (echteUit.Count > 0) wie.Add("Behalve: " + Namen(echteUit));
        if (b.GroepenUit.Count > 0) wie.Add("Behalve leden van: " + Namen(b.GroepenUit));
        if (b.RollenUit.Count > 0) wie.Add("Behalve wie de rol heeft: " + Rollen(b.RollenUit));
        if (b.GastenUit) wie.Add("Behalve gasten");

        var apps = new List<string>();
        if (b.Gebruikersacties.Count > 0)
        {
            apps.Add("Bij het " + string.Join(" of ", b.Gebruikersacties.Select(ActieNaam)));
        }
        else if (b.AppsIn.Count > 0)
        {
            apps.Add("Voor " + string.Join(", ", b.AppsIn.Select(AppNaam)));
        }

        if (b.AppsUit.Count > 0) apps.Add("Behalve " + string.Join(", ", b.AppsUit.Select(AppNaam)));
        if (apps.Count == 0) apps.Add("Geen apps");

        var voorwaarden = new List<string>();
        if (b.ClientApps.Count > 0 && !b.ClientApps.Contains("all"))
        {
            voorwaarden.Add("Enkel via " + string.Join(", ", b.ClientApps.Select(ClientNaam)));
        }

        if (b.PlatformsIn.Count > 0) voorwaarden.Add("Op " + string.Join(", ", b.PlatformsIn.Select(PlatformNaam)));
        if (b.PlatformsUit.Count > 0) voorwaarden.Add("Niet op " + string.Join(", ", b.PlatformsUit.Select(PlatformNaam)));
        if (b.LocatiesIn.Count > 0 && !(b.LocatiesIn.Count == 1 && b.LocatiesIn[0] == "All"))
        {
            voorwaarden.Add("Vanaf " + string.Join(", ", b.LocatiesIn.Select(l => LocatieNaam(l, ca))));
        }

        if (b.LocatiesUit.Count > 0) voorwaarden.Add("Niet vanaf " + string.Join(", ", b.LocatiesUit.Select(l => LocatieNaam(l, ca))));
        if (b.AanmeldRisico.Count > 0) voorwaarden.Add("Bij een aanmelding met risico " + string.Join(", ", b.AanmeldRisico.Select(RisicoNaam)) + " (Entra ID P2)");
        if (b.GebruikersRisico.Count > 0) voorwaarden.Add("Bij een gebruiker met risico " + string.Join(", ", b.GebruikersRisico.Select(RisicoNaam)) + " (Entra ID P2)");
        if (b.Toestelfilter) voorwaarden.Add("Met een filter op toestellen");
        if (b.Overdracht.Contains("deviceCodeFlow")) voorwaarden.Add("Bij aanmelden met een toestelcode (device code flow)");
        if (b.Overdracht.Contains("authenticationTransfer")) voorwaarden.Add("Bij het doorgeven van een aanmelding naar een ander toestel");
        if (voorwaarden.Count == 0) voorwaarden.Add("Altijd (geen extra voorwaarden)");

        string gevolg;
        if (b.Blokkeert)
        {
            gevolg = "Toegang blokkeren";
        }
        else
        {
            var eisen = b.Controles.Where(c => !(c == "mfa" && b.SterkteId is not null)).Select(ControleNaam).ToList();
            if (b.SterkteId is not null) eisen.Insert(0, "MFA met sterkte " + (b.SterkteNaam ?? ca.Sterktes.FirstOrDefault(s => s.Id == b.SterkteId)?.Naam ?? b.SterkteId));
            if (b.Gebruiksvoorwaarden) eisen.Add("de gebruiksvoorwaarden aanvaarden");
            gevolg = eisen.Count switch
            {
                0 => "Toegang toestaan (geen eis)",
                1 => "Toegang mits " + eisen[0],
                _ => "Toegang mits " + string.Join(b.Operator == "AND" ? " en " : " of ", eisen),
            };
        }

        var sessie = new List<string>();
        if (b.AanmeldFrequentie is { } f) sessie.Add("Opnieuw aanmelden: " + f);
        if (b.BlijvendeBrowser is { } br) sessie.Add("Browser: " + br);

        return new CaUitleg(wie, apps, voorwaarden, gevolg, sessie);
    }

    // ------------------------------------------------------------------ bereik (wie)

    /// <summary>Valt deze gebruiker onder het beleid (enkel het deel "wie")? Uitsluitingen gaan voor.</summary>
    public static CaAntwoord Bereik(CaBeleid b, CaGebruiker g, Func<string, string> naam, CaGegevens ca)
    {
        if (b.GebruikersUit.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
        {
            return CaAntwoord.Nee("rechtstreeks uitgezonderd");
        }

        if (b.GroepenUit.FirstOrDefault(g.Groepen.Contains) is { } groepUit)
        {
            return CaAntwoord.Nee("uitgezonderd via de groep " + naam(groepUit));
        }

        if (b.RollenUit.FirstOrDefault(g.Rollen.Contains) is { } rolUit)
        {
            return CaAntwoord.Nee("uitgezonderd via de rol " + ca.RolNaam(rolUit));
        }

        if (b.GastenUit && g.IsGast)
        {
            return CaAntwoord.Nee("gasten zijn uitgezonderd");
        }

        CaAntwoord? erin = null;
        if (b.GebruikersIn.Contains("All"))
        {
            erin = CaAntwoord.Ja("alle gebruikers");
        }
        else if (b.GebruikersIn.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
        {
            erin = CaAntwoord.Ja("rechtstreeks opgenomen");
        }
        else if (b.GastenIn && g.IsGast)
        {
            erin = CaAntwoord.Ja("gasten en externe gebruikers");
        }
        else if (b.GroepenIn.FirstOrDefault(g.Groepen.Contains) is { } groepIn)
        {
            erin = CaAntwoord.Ja("lid van " + naam(groepIn));
        }
        else if (b.RollenIn.FirstOrDefault(g.Rollen.Contains) is { } rolIn)
        {
            erin = CaAntwoord.Ja("heeft de rol " + ca.RolNaam(rolIn));
        }

        // Zonder de volledige groepsleden (ook geneste groepen) is een groep in het beleid niet zeker
        if (!g.GroepenVolledig)
        {
            if (erin is not null && b.GroepenUit.Count > 0)
            {
                return CaAntwoord.Misschien(erin.Reden + ", tenzij lid van een uitgezonderde groep via een groep in de groep");
            }

            if (erin is null && b.GroepenIn.Count > 0)
            {
                return CaAntwoord.Misschien("misschien lid van een opgenomen groep via een groep in de groep");
            }
        }

        return erin ?? CaAntwoord.Nee("niet opgenomen");
    }

    /// <summary>Alle personen met een Entra-account die onder het beleid vallen (zeker of misschien).</summary>
    public static IReadOnlyList<(Persoon Persoon, CaAntwoord Antwoord)> WieValtEronder(CaBeleid b, InventarisData d)
    {
        if (d.ConditionalAccess is not { } ca)
        {
            return Array.Empty<(Persoon, CaAntwoord)>();
        }

        var naam = Namen(d);
        return d.Personen
            .Select(p => (Persoon: p, G: CaGebruiker.Van(p, d)))
            .Where(x => x.G is not null)
            .Select(x => (x.Persoon, Antwoord: Bereik(b, x.G!, naam, ca)))
            .Where(x => x.Antwoord.Geldt != CaGeldt.Nee)
            .OrderBy(x => x.Persoon.Naam, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Namen voor id's van gebruikers en groepen, via de index.</summary>
    public static Func<string, string> Namen(InventarisData d) => id => d.Index.Zoek(id)?.Naam ?? id;

    // ------------------------------------------------------------------ wat als

    /// <summary>Of een beleid een app dekt in het scenario.</summary>
    internal static CaAntwoord DektApp(CaBeleid b, CaApp app)
    {
        if (b.Gebruikersacties.Count > 0 && b.AppsIn.Count == 0)
        {
            return CaAntwoord.Nee("geldt enkel bij " + string.Join(", ", b.Gebruikersacties.Select(ActieNaam)));
        }

        bool Hoort(string id) => id == "All"
                                 || (app == CaApp.Office365 && OfficeApps.Contains(id))
                                 || (app == CaApp.Beheerportalen && id == "MicrosoftAdminPortals");

        if (b.AppsUit.FirstOrDefault(Hoort) is { } uit)
        {
            return CaAntwoord.Nee(AppNaam(uit) + " uitgezonderd");
        }

        if (b.AppsIn.FirstOrDefault(Hoort) is { } erin)
        {
            return CaAntwoord.Ja(AppNaam(erin));
        }

        var specifiek = b.AppsIn.Where(a => a is not "None" && !AppNamen.ContainsKey(a)).ToList();
        return app == CaApp.Andere && specifiek.Count > 0
            ? CaAntwoord.Misschien("geldt voor specifieke apps")
            : CaAntwoord.Nee("andere apps");
    }

    private static bool? LocatieKlopt(string id, CaPlek plek, CaGegevens ca)
    {
        if (id == "All")
        {
            return true;
        }

        if (id == "AllTrusted")
        {
            return plek == CaPlek.Kantoor;
        }

        if (ca.Locatie(id) is not { } l)
        {
            return null;
        }

        if (l.IsIp)
        {
            // Het kantoor: de vertrouwde IP-bereiken. Een andere IP-locatie kan de app niet plaatsen.
            return plek == CaPlek.Kantoor ? (l.Vertrouwd ? true : null) : false;
        }

        if (plek is CaPlek.Kantoor or CaPlek.Belgie)
        {
            return l.BevatBelgie;
        }

        return l.Waarden.Any(w => !w.Equals("BE", StringComparison.OrdinalIgnoreCase)) || l.OnbekendeLanden ? null : false;
    }

    private static bool? EenVan(IEnumerable<string> ids, CaPlek plek, CaGegevens ca)
    {
        var uitkomsten = ids.Select(i => LocatieKlopt(i, plek, ca)).ToList();
        return uitkomsten.Contains(true) ? true : uitkomsten.Contains(null) ? null : false;
    }

    /// <summary>Geldt het beleid voor deze gebruiker in dit scenario? Combineert wie, app en voorwaarden.</summary>
    public static CaAntwoord Geldt(CaBeleid b, CaGebruiker g, CaScenario s, CaGegevens ca, Func<string, string> naam)
    {
        var wie = Bereik(b, g, naam, ca);
        if (wie.Geldt == CaGeldt.Nee)
        {
            return wie;
        }

        var redenen = new List<string> { wie.Reden };
        var zeker = wie.Geldt == CaGeldt.Ja;

        var app = DektApp(b, s.App);
        if (app.Geldt == CaGeldt.Nee)
        {
            return app;
        }

        zeker &= app.Geldt == CaGeldt.Ja;
        redenen.Add(app.Reden);

        var client = s.Client switch
        {
            CaClient.Browser => "browser",
            CaClient.Desktop => "mobileAppsAndDesktopClients",
            _ => null,
        };
        if (b.ClientApps.Count > 0 && !b.ClientApps.Contains("all"))
        {
            var klopt = client is null
                ? b.ClientApps.Any(c => c is "exchangeActiveSync" or "other")
                : b.ClientApps.Contains(client);
            if (!klopt)
            {
                return CaAntwoord.Nee("enkel via " + string.Join(", ", b.ClientApps.Select(ClientNaam)));
            }
        }

        if (b.PlatformsUit.Any(p => p.Equals(s.Platform, StringComparison.OrdinalIgnoreCase) || p == "all"))
        {
            return CaAntwoord.Nee("niet op " + PlatformNaam(s.Platform));
        }

        if (b.PlatformsIn.Count > 0 && !b.PlatformsIn.Any(p => p.Equals(s.Platform, StringComparison.OrdinalIgnoreCase) || p == "all"))
        {
            return CaAntwoord.Nee("enkel op " + string.Join(", ", b.PlatformsIn.Select(PlatformNaam)));
        }

        if (b.LocatiesUit.Count > 0)
        {
            switch (EenVan(b.LocatiesUit, s.Plek, ca))
            {
                case true:
                    return CaAntwoord.Nee("niet vanaf " + string.Join(", ", b.LocatiesUit.Select(l => LocatieNaam(l, ca))));
                case null:
                    zeker = false;
                    redenen.Add("hangt af van de locatie");
                    break;
            }
        }

        if (b.LocatiesIn.Count > 0)
        {
            switch (EenVan(b.LocatiesIn, s.Plek, ca))
            {
                case false:
                    return CaAntwoord.Nee("enkel vanaf " + string.Join(", ", b.LocatiesIn.Select(l => LocatieNaam(l, ca))));
                case null:
                    zeker = false;
                    redenen.Add("hangt af van de locatie");
                    break;
            }
        }

        if (b.AanmeldRisico.Count > 0 || b.GebruikersRisico.Count > 0)
        {
            return CaAntwoord.Nee("geldt enkel bij een riskante aanmelding of gebruiker");
        }

        if (b.Overdracht.Count > 0)
        {
            return CaAntwoord.Nee("geldt enkel bij aanmelden met een toestelcode of overdracht");
        }

        if (b.Toestelfilter)
        {
            zeker = false;
            redenen.Add("hangt af van het toestel (filter)");
        }

        return new CaAntwoord(zeker ? CaGeldt.Ja : CaGeldt.Misschien, string.Join("; ", redenen.Where(r => r.Length > 0)));
    }

    /// <summary>Alle beleiden (aan en alleen rapport) voor een gebruiker in een scenario, met de samenvatting.</summary>
    public static CaWatAlsUitkomst WatAls(CaGebruiker g, CaScenario s, CaGegevens ca, Func<string, string> naam)
    {
        var regels = ca.Beleiden
            .Where(b => b.Status != CaStatus.Uit)
            .Select(b => new CaWatAlsRegel(b, Geldt(b, g, s, ca, naam), Uitleg(b, ca, naam).Gevolg))
            .ToList();

        var aan = regels.Where(r => r.Beleid.Status == CaStatus.Aan).ToList();
        var zeker = aan.Where(r => r.Antwoord.Geldt == CaGeldt.Ja).ToList();
        var geblokkeerd = zeker.Any(r => r.Beleid.Blokkeert);
        var eisen = new List<string>();
        foreach (var r in zeker.Where(r => !r.Beleid.Blokkeert))
        {
            var b = r.Beleid;
            var opties = b.Controles.Where(c => !(c == "mfa" && b.SterkteId is not null)).ToList();
            if (b.SterkteId is not null) opties.Insert(0, "mfa");

            var toestel = opties.Where(c => c is "compliantDevice" or "domainJoinedDevice").ToList();
            if (s.BeheerdToestel && toestel.Count > 0)
            {
                // Een beheerd toestel volstaat bij "één van", en is bij "allemaal" al in orde
                if (b.Operator == "OR")
                {
                    continue;
                }

                opties = opties.Except(toestel).ToList();
            }

            var teksten = opties.Select(c => c == "mfa" && b.VraagtPhishingbestendig ? "phishingbestendige MFA" : ControleNaam(c)).ToList();
            if (b.Gebruiksvoorwaarden)
            {
                teksten.Add("de gebruiksvoorwaarden aanvaarden");
            }

            var eis = string.Join(b.Operator == "AND" ? " en " : " of ", teksten);
            if (eis.Length > 0 && !eisen.Contains(eis))
            {
                eisen.Add(eis);
            }
        }

        string samenvatting;
        if (geblokkeerd)
        {
            samenvatting = "Geblokkeerd door " + string.Join(", ", zeker.Where(r => r.Beleid.Blokkeert).Select(r => r.Beleid.Naam));
        }
        else if (eisen.Count > 0)
        {
            samenvatting = "Toegang mits " + string.Join(" en ", eisen);
        }
        else if (zeker.Count > 0)
        {
            samenvatting = "Toegang zonder extra eisen";
        }
        else
        {
            samenvatting = "Geen enkel beleid is zeker van toepassing: aanmelden met enkel een wachtwoord";
        }

        if (aan.Any(r => r.Antwoord.Geldt == CaGeldt.Misschien))
        {
            samenvatting += " (en misschien meer, zie de beleiden met \"misschien\")";
        }

        return new CaWatAlsUitkomst(regels, geblokkeerd, eisen, samenvatting);
    }
}

public enum CaApp
{
    Office365,
    Beheerportalen,
    Andere,
}

public enum CaPlek
{
    /// <summary>Op kantoor: vanaf een vertrouwde locatie.</summary>
    Kantoor,
    Belgie,
    Buitenland,
}

public enum CaClient
{
    Browser,
    Desktop,

    /// <summary>Verouderde programma's (IMAP, POP, SMTP, ActiveSync): kunnen geen MFA.</summary>
    Verouderd,
}

/// <summary>Een aanmelding om na te spelen: welke app, waarvandaan, op welk platform, met welk programma.</summary>
public sealed record CaScenario(CaApp App = CaApp.Office365, CaPlek Plek = CaPlek.Belgie, string Platform = "windows", CaClient Client = CaClient.Browser, bool BeheerdToestel = false);

public sealed record CaWatAlsRegel(CaBeleid Beleid, CaAntwoord Antwoord, string Gevolg);

public sealed record CaWatAlsUitkomst(IReadOnlyList<CaWatAlsRegel> Regels, bool Geblokkeerd, IReadOnlyList<string> Eisen, string Samenvatting)
{
    public IEnumerable<CaWatAlsRegel> Afgedwongen => Regels.Where(r => r.Beleid.Status == CaStatus.Aan);
    public IEnumerable<CaWatAlsRegel> AlleenRapport => Regels.Where(r => r.Beleid.Status == CaStatus.Rapport);
}

/// <summary>Hoe vaak een beleid in de aanmeldingen meespeelde (appliedConditionalAccessPolicies).</summary>
public sealed record CaEffectRegel(string BeleidId, string Naam, int Toegepast, int Mislukt, int NietVanToepassing, int RapportGelukt, int RapportMislukt, int RapportOnderbroken)
{
    public int Totaal => Toegepast + Mislukt + NietVanToepassing + RapportGelukt + RapportMislukt + RapportOnderbroken;

    /// <summary>Report-only: hoeveel aanmeldingen het beleid zou tegengehouden of onderbroken hebben.</summary>
    public int ZouTegenhouden => RapportMislukt + RapportOnderbroken;
}

public static class CaEffect
{
    public static IReadOnlyList<CaEffectRegel> Bereken(IEnumerable<Aanmelding> aanmeldingen, CaGegevens ca)
    {
        var tellers = ca.Beleiden.ToDictionary(b => b.Id, _ => new int[6], StringComparer.OrdinalIgnoreCase);
        foreach (var t in aanmeldingen.SelectMany(a => a.Ca ?? Array.Empty<CaToepassing>()))
        {
            if (!tellers.TryGetValue(t.BeleidId, out var n))
            {
                continue;
            }

            var i = t.Resultaat switch
            {
                "success" => 0,
                "failure" => 1,
                "notApplied" or "notEnabled" or "reportOnlyNotApplied" => 2,
                "reportOnlySuccess" => 3,
                "reportOnlyFailure" => 4,
                "reportOnlyInterrupted" => 5,
                _ => -1,
            };
            if (i >= 0)
            {
                n[i]++;
            }
        }

        return ca.Beleiden.Select(b =>
        {
            var n = tellers[b.Id];
            return new CaEffectRegel(b.Id, b.Naam, n[0], n[1], n[2], n[3], n[4], n[5]);
        }).ToList();
    }
}
