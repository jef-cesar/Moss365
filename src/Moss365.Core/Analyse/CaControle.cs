using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

public enum CaOordeel
{
    InOrde,
    Info,
    Aandacht,
    Probleem,
}

/// <summary>Eén punt van de controle, met wie het betreft en wat je eraan kan doen.</summary>
public sealed record CaBevinding(string Onderwerp, CaOordeel Oordeel, string Uitleg, string Advies = "")
{
    public IReadOnlyList<Persoon> Personen { get; init; } = Array.Empty<Persoon>();
    public IReadOnlyList<CaBeleid> Beleiden { get; init; } = Array.Empty<CaBeleid>();

    public string OordeelTekst => Oordeel switch
    {
        CaOordeel.InOrde => "In orde",
        CaOordeel.Info => "Info",
        CaOordeel.Aandacht => "Aandacht",
        _ => "Probleem",
    };
}

/// <summary>
/// Controle van Conditional Access tegen de aanbevelingen van Microsoft: MFA voor iedereen, verouderde aanmelding
/// geblokkeerd, beheerders sterk beschermd, noodaccounts uitgezonderd, enz. Leest alleen; wijzigt niets.
/// </summary>
public static class CaControle
{
    private static readonly string[] NoodWoorden = { "nood", "breakglass", "break-glass", "break.glass", "emergency", "glass" };

    public static IReadOnlyList<CaBevinding> Controleer(InventarisData d, OrganisatieConfig config, DateTime nu)
    {
        var uit = new List<CaBevinding>();
        if (d.ConditionalAccess is not { } ca)
        {
            uit.Add(new CaBevinding("Conditional Access", CaOordeel.Info, "Niet geladen.", "Laad live (Verbindingen > Alles live laden) met een account dat de beleiden mag lezen."));
            return uit;
        }

        if (ca.Melding is not null)
        {
            uit.Add(new CaBevinding("Conditional Access", CaOordeel.Probleem, ca.Melding));
            return uit;
        }

        var naam = CaLezer.Namen(d);
        var aan = ca.Beleiden.Where(b => b.Status == CaStatus.Aan).ToList();
        var gebruikers = d.Personen
            .Select(p => (Persoon: p, G: CaGebruiker.Van(p, d)))
            .Where(x => x.G is not null)
            .Select(x => (x.Persoon, G: x.G!))
            .ToList();
        var medewerkers = gebruikers.Where(x => !x.Persoon.IsGast && x.Persoon.Ingeschakeld && !d.Index.IsPostvakAccount(x.Persoon)).ToList();

        bool Iedereen(CaBeleid b) => b.GebruikersIn.Contains("All");
        bool AlleApps(CaBeleid b) => b.AppsIn.Contains("All");
        bool ZonderVoorwaarden(CaBeleid b) => (b.ClientApps.Count == 0 || b.ClientApps.Contains("all")) && b.PlatformsIn.Count == 0
                                              && b.LocatiesIn.Count == 0 && b.AanmeldRisico.Count + b.GebruikersRisico.Count == 0 && !b.Toestelfilter
                                              && b.Overdracht.Count == 0;
        bool Mfa(CaBeleid b) => b.VraagtMfa && !b.Blokkeert;

        // Beveiligingsstandaarden
        if (ca.Beveiligingsstandaarden == true)
        {
            uit.Add(aan.Count > 0
                ? new CaBevinding("Beveiligingsstandaarden", CaOordeel.Aandacht, "De beveiligingsstandaarden staan aan, naast Conditional Access-beleiden.",
                    "Kies één van de twee: met Entra ID P1 schakel je de standaarden uit en regel je alles met Conditional Access.")
                : new CaBevinding("Beveiligingsstandaarden", CaOordeel.Info, "De beveiligingsstandaarden van Microsoft staan aan (basis-MFA voor iedereen)."));
        }
        else if (aan.Count == 0)
        {
            uit.Add(new CaBevinding("Bescherming", CaOordeel.Probleem, "Geen enkel beleid staat aan en de beveiligingsstandaarden staan uit: aanmelden kan met enkel een wachtwoord.",
                "Zet minstens MFA voor alle gebruikers aan."));
        }

        // 1. MFA voor iedereen
        var mfaAlle = aan.Where(b => Iedereen(b) && AlleApps(b) && Mfa(b) && ZonderVoorwaarden(b)).ToList();
        if (mfaAlle.Count > 0)
        {
            var metLocatie = mfaAlle.Where(b => b.LocatiesUit.Count > 0).ToList();
            uit.Add(metLocatie.Count == mfaAlle.Count
                ? new CaBevinding("MFA voor iedereen", CaOordeel.Aandacht, "MFA geldt voor alle gebruikers, maar niet vanaf " + string.Join(", ", metLocatie[0].LocatiesUit.Select(l => CaLezer.LocatieNaam(l, ca))) + ".",
                    "Microsoft raadt MFA aan ook op kantoor: een gestolen wachtwoord werkt anders in het gebouw (of via een gekaapte pc) zonder tweede stap.") { Beleiden = metLocatie }
                : new CaBevinding("MFA voor iedereen", CaOordeel.InOrde, "MFA voor alle gebruikers en alle apps.") { Beleiden = mfaAlle });
        }
        else
        {
            uit.Add(new CaBevinding("MFA voor iedereen", CaOordeel.Probleem, "Er is geen beleid dat MFA vraagt voor alle gebruikers en alle apps.",
                "Maak (eerst als alleen rapport) een beleid: alle gebruikers, alle apps, MFA vereisen; de noodaccounts uitgezonderd."));
        }

        // 2. Wie valt buiten MFA
        bool Gedekt(CaGebruiker g) => aan.Any(b => Mfa(b)
                                                  && (AlleApps(b) || b.AppsIn.Contains("Office365"))
                                                  && (b.ClientApps.Count == 0 || b.ClientApps.Contains("all") || b.ClientApps.Contains("browser"))
                                                  && CaLezer.Bereik(b, g, naam, ca).Geldt == CaGeldt.Ja);
        var noodUpns = config.Noodaccounts.Select(u => u.Trim()).Where(u => u.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buiten = medewerkers.Where(x => !noodUpns.Contains(x.Persoon.Upn) && !noodUpns.Contains(x.Persoon.Entra!.UserPrincipalName) && !Gedekt(x.G)).ToList();
        var kritiek = buiten.Where(x => d.Beveiliging?.RollenVan(x.G.Id).Any(r => BeveiligingGegevens.IsKritiekeRol(r.Rol)) == true).ToList();
        if (buiten.Count == 0)
        {
            uit.Add(new CaBevinding("Medewerkers zonder MFA-beleid", CaOordeel.InOrde, $"Alle {medewerkers.Count} actieve medewerkers vallen onder een beleid dat MFA vraagt."));
        }
        else
        {
            var geenRegistratie = d.Beveiliging is { MfaBekend: true } bev ? buiten.Count(x => bev.MfaVan(x.G.Id) is not { MfaGeregistreerd: true }) : (int?)null;
            uit.Add(new CaBevinding("Medewerkers zonder MFA-beleid", kritiek.Count > 0 ? CaOordeel.Probleem : CaOordeel.Aandacht,
                $"{buiten.Count} van de {medewerkers.Count} actieve medewerkers vallen onder geen enkel beleid dat MFA vraagt"
                + (geenRegistratie is { } n ? $" ({n} van hen hebben ook geen MFA geregistreerd)" : string.Empty)
                + (kritiek.Count > 0 ? $"; {kritiek.Count} met een kritieke beheerrol." : "."),
                "Kijk na of ze bewust uitgezonderd zijn (bv. via een groep). Zie Wat als voor de reden per persoon.")
            {
                Personen = buiten.Select(x => x.Persoon).ToList(),
            });
        }

        if (d.Beveiliging is { MfaBekend: true } b2)
        {
            var nietGeregistreerd = medewerkers.Where(x => Gedekt(x.G) && b2.MfaVan(x.G.Id) is not { MfaGeregistreerd: true }).ToList();
            if (nietGeregistreerd.Count > 0)
            {
                uit.Add(new CaBevinding("MFA nog niet ingesteld", aan.Any(b => b.Gebruikersacties.Contains(CaLezer.ActieBeveiligingsgegevens)) ? CaOordeel.Info : CaOordeel.Aandacht,
                    $"{nietGeregistreerd.Count} medewerkers vallen onder MFA maar hebben nog geen methode geregistreerd: bij de volgende aanmelding moeten ze die instellen, met enkel hun wachtwoord.",
                    "Laat ze MFA instellen op kantoor, of bescherm het registreren (zie hieronder).")
                {
                    Personen = nietGeregistreerd.Select(x => x.Persoon).ToList(),
                });
            }
        }

        // 3. Verouderde aanmelding
        var legacy = aan.Where(b => b.Blokkeert && Iedereen(b) && AlleApps(b) && b.ClientApps.Contains("exchangeActiveSync") && b.ClientApps.Contains("other")).ToList();
        uit.Add(legacy.Count > 0
            ? new CaBevinding("Verouderde aanmelding geblokkeerd", CaOordeel.InOrde, "IMAP, POP, SMTP en ActiveSync (zonder MFA) zijn geblokkeerd.") { Beleiden = legacy }
            : new CaBevinding("Verouderde aanmelding geblokkeerd", CaOordeel.Probleem, "Er is geen beleid dat verouderde aanmelding (IMAP, POP, SMTP, ActiveSync) blokkeert. Die kunnen geen MFA: een wachtwoord volstaat.",
                "Maak een beleid: alle gebruikers, alle apps, client-apps Exchange ActiveSync en andere, blokkeren."));

        // 4. Beheerders
        var beheer = aan.Where(b => Mfa(b) && (Iedereen(b) || b.RollenIn.Contains(CaGegevens.RolGlobalAdmin, StringComparer.OrdinalIgnoreCase))
                                    && (AlleApps(b) || b.AppsIn.Contains("MicrosoftAdminPortals"))).ToList();
        var phishing = beheer.Where(b => b.VraagtPhishingbestendig && b.RollenIn.Count > 0).ToList();
        if (beheer.Count == 0)
        {
            uit.Add(new CaBevinding("Beheerders", CaOordeel.Probleem, "Geen beleid vraagt MFA voor de rol Global Administrator.",
                "Maak een beleid voor de beheerrollen met MFA (liefst phishingbestendig: FIDO2-sleutel of Windows Hello)."));
        }
        else
        {
            uit.Add(phishing.Count > 0
                ? new CaBevinding("Beheerders", CaOordeel.InOrde, "Beheerrollen moeten phishingbestendige MFA gebruiken.") { Beleiden = phishing }
                : new CaBevinding("Beheerders", CaOordeel.Aandacht, "Beheerders vallen onder MFA, maar niet onder phishingbestendige MFA.",
                    "Microsoft raadt voor beheerrollen de sterkte \"Phishingbestendige MFA\" aan (FIDO2-sleutel, Windows Hello, certificaat).") { Beleiden = beheer });
        }

        // 5. Noodaccounts
        uit.AddRange(ControleerNoodaccounts(d, ca, config, gebruikers, aan, naam, nu));

        // 6. Gasten
        var gasten = gebruikers.Where(x => x.Persoon.IsGast && x.Persoon.Ingeschakeld).ToList();
        if (gasten.Count > 0)
        {
            var gastBeleid = aan.Where(b => Mfa(b) && (b.GastenIn || (Iedereen(b) && !b.GastenUit))).ToList();
            uit.Add(gastBeleid.Count > 0
                ? new CaBevinding("Gasten", CaOordeel.InOrde, $"Gasten ({gasten.Count}) moeten MFA gebruiken.") { Beleiden = gastBeleid }
                : new CaBevinding("Gasten", CaOordeel.Aandacht, $"Er zijn {gasten.Count} gasten, maar geen beleid vraagt hen MFA.", "Neem gasten op in het MFA-beleid voor iedereen."));
        }

        // 7. Toestelcode
        var code = aan.Where(b => b.Blokkeert && b.Overdracht.Contains("deviceCodeFlow")).ToList();
        uit.Add(code.Count > 0
            ? new CaBevinding("Aanmelden met toestelcode", CaOordeel.InOrde, "Aanmelden met een toestelcode (device code flow) is geblokkeerd.") { Beleiden = code }
            : new CaBevinding("Aanmelden met toestelcode", CaOordeel.Aandacht, "Aanmelden met een toestelcode is niet geblokkeerd. Phishing gebruikt dat vaak (\"geef deze code in op microsoft.com/devicelogin\").",
                "Blokkeer de device code flow voor iedereen behalve wie het echt nodig heeft (bv. vergaderzaal-toestellen)."));

        // 8. MFA registreren
        var registreren = aan.Where(b => b.Gebruikersacties.Contains(CaLezer.ActieBeveiligingsgegevens)).ToList();
        uit.Add(registreren.Count > 0
            ? new CaBevinding("MFA registreren beschermd", CaOordeel.InOrde, "Beveiligingsgegevens registreren is beschermd.") { Beleiden = registreren }
            : new CaBevinding("MFA registreren beschermd", CaOordeel.Aandacht, "Iedereen kan zijn MFA-methode registreren met enkel een wachtwoord, van overal.",
                "Beperk het registreren tot vertrouwde locaties (kantoor) of eis een Temporary Access Pass."));

        // 9. Opvallende beleiden
        foreach (var b in ca.Beleiden)
        {
            var dagen = b.Gewijzigd is { } g ? (int)(nu - g.LocalDateTime).TotalDays : (int?)null;
            if (b.Status == CaStatus.Rapport && dagen > 30)
            {
                uit.Add(new CaBevinding("Lang op alleen rapport", CaOordeel.Aandacht, $"\"{b.Naam}\" staat al {dagen} dagen op alleen rapport.",
                    "Kijk het effect na (aanmeldingen) en zet het aan, of verwijder het.") { Beleiden = new[] { b } });
            }

            if (b.Status == CaStatus.Uit)
            {
                uit.Add(new CaBevinding("Uitgeschakeld beleid", CaOordeel.Info, $"\"{b.Naam}\" staat uit.") { Beleiden = new[] { b } });
            }

            var niemand = !Iedereen(b) && !b.GastenIn && b.GroepenIn.Count + b.RollenIn.Count == 0 && b.GebruikersIn.All(u => u == "None");
            if (niemand)
            {
                uit.Add(new CaBevinding("Beleid zonder gebruikers", CaOordeel.Info, $"\"{b.Naam}\" geldt voor niemand.") { Beleiden = new[] { b } });
            }

            var uitzonderingen = b.GebruikersUit.Count + b.GroepenUit.Count + b.RollenUit.Count;
            if (b.Status != CaStatus.Uit && uitzonderingen > 5)
            {
                uit.Add(new CaBevinding("Veel uitzonderingen", CaOordeel.Aandacht, $"\"{b.Naam}\" heeft {uitzonderingen} uitzonderingen.",
                    "Elke uitzondering is een gat. Werk liefst met één groep uitzonderingen die je regelmatig nakijkt.") { Beleiden = new[] { b } });
            }

            if (b.Status == CaStatus.Aan && b.Blokkeert && Iedereen(b) && AlleApps(b) && ZonderVoorwaarden(b) && b.LocatiesUit.Count == 0 && b.PlatformsUit.Count == 0)
            {
                uit.Add(new CaBevinding("Iedereen buitengesloten", CaOordeel.Probleem, $"\"{b.Naam}\" blokkeert alle gebruikers voor alle apps"
                    + (uitzonderingen == 0 ? ", zonder uitzonderingen." : $", met enkel {uitzonderingen} uitzondering(en)."),
                    "Kijk dit meteen na: niemand (ook geen beheerder) kan zich zo aanmelden.") { Beleiden = new[] { b } });
            }

            if (b.AanmeldRisico.Count + b.GebruikersRisico.Count > 0)
            {
                uit.Add(new CaBevinding("Risicobeleid", CaOordeel.Info, $"\"{b.Naam}\" werkt met aanmeld- of gebruikersrisico: dat vraagt Entra ID P2.") { Beleiden = new[] { b } });
            }
        }

        return uit;
    }

    private static IEnumerable<CaBevinding> ControleerNoodaccounts(
        InventarisData d, CaGegevens ca, OrganisatieConfig config, IReadOnlyList<(Persoon Persoon, CaGebruiker G)> gebruikers,
        IReadOnlyList<CaBeleid> aan, Func<string, string> naam, DateTime nu)
    {
        var upns = config.Noodaccounts.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (upns.Count == 0)
        {
            var kandidaten = gebruikers.Where(x => NoodWoorden.Any(w => x.Persoon.Upn.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                                        || x.Persoon.Naam.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Persoon).ToList();
            yield return new CaBevinding("Noodaccounts", CaOordeel.Aandacht,
                "Er zijn geen noodaccounts aangeduid" + (kandidaten.Count > 0 ? $"; {kandidaten.Count} account(s) lijken er een (naam)." : "."),
                "Maak twee cloud-only accounts met de rol Global Administrator, sluit ze uit van alle beleiden en vul ze in bij Organisatie > Noodaccounts.")
            {
                Personen = kandidaten,
            };
            yield break;
        }

        foreach (var upn in upns)
        {
            var x = gebruikers.FirstOrDefault(g => string.Equals(g.Persoon.Entra!.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
            if (x.Persoon is null)
            {
                yield return new CaBevinding("Noodaccount " + upn, CaOordeel.Probleem, "Niet gevonden in Entra.", "Kijk de naam na bij Organisatie > Noodaccounts.");
                continue;
            }

            var problemen = new List<string>();
            var aandacht = new List<string>();
            if (x.Persoon.Entra!.OnPremisesSyncEnabled)
            {
                problemen.Add("komt uit AD (moet cloud-only zijn, anders hangt het af van de synchronisatie)");
            }

            if (!x.Persoon.Entra.AccountEnabled)
            {
                problemen.Add("is uitgeschakeld");
            }

            if (d.Beveiliging is { RollenBekend: true } bev && !bev.RollenVan(x.G.Id).Any(r => r.Rol == "Global Administrator" || r.RoleTemplateId == CaGegevens.RolGlobalAdmin))
            {
                aandacht.Add("heeft geen (permanente) rol Global Administrator");
            }

            var erin = aan.Where(b => CaLezer.Bereik(b, x.G, naam, ca).Geldt != CaGeldt.Nee).ToList();
            if (erin.Count > 0)
            {
                problemen.Add("valt onder " + erin.Count + " beleid(en) die aan staan: " + string.Join(", ", erin.Select(b => b.Naam)));
            }

            if (Statistiek.LaatsteAanmelding(x.Persoon) is { } laatst && (nu - laatst).TotalDays <= 30)
            {
                aandacht.Add($"meldde zich aan op {laatst:dd-MM-yyyy} (klopt dat? een noodaccount gebruik je enkel in nood)");
            }

            var oordeel = problemen.Count > 0 ? CaOordeel.Probleem : aandacht.Count > 0 ? CaOordeel.Aandacht : CaOordeel.InOrde;
            var tekst = oordeel == CaOordeel.InOrde
                ? "Cloud-only, ingeschakeld en uitgezonderd van alle beleiden."
                : string.Join("; ", problemen.Concat(aandacht)) + ".";
            yield return new CaBevinding("Noodaccount " + x.Persoon.Naam, oordeel, char.ToUpperInvariant(tekst[0]) + tekst[1..],
                erin.Count > 0 ? "Sluit het account (of een groep met de noodaccounts) uit van elk beleid." : string.Empty)
            {
                Personen = new[] { x.Persoon },
                Beleiden = erin,
            };
        }
    }
}
