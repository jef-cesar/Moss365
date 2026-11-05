using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>Een nieuwe gebruiker zoals de beheerder hem invult.</summary>
public sealed class NieuweGebruikerAanvraag
{
    public string Populatie { get; set; } = string.Empty;
    public string Voornaam { get; set; } = string.Empty;
    public string Achternaam { get; set; } = string.Empty;
    public string Dienst { get; set; } = string.Empty;
    public string Kantoor { get; set; } = string.Empty;
    public string Functie { get; set; } = string.Empty;
    public string? Logonscript { get; set; }
    public string? LicentieSku { get; set; }
    public Persoon? Spiegel { get; set; }
    public bool StandaardAdGroepen { get; set; } = true;
}

/// <summary>Wat de app voorstelt voor een nieuwe gebruiker: de afgeleide namen en twee plannen (nu en na de synchronisatie).</summary>
public sealed record NieuweGebruikerVoorstel(
    string Sam,
    string Upn,
    string WeergaveNaam,
    string Dn,
    string Wachtwoord,
    IReadOnlyList<string> Fouten,
    IReadOnlyList<string> Waarschuwingen,
    IReadOnlyList<Wijziging> Nu,
    IReadOnlyList<Wijziging> NaSynchronisatie)
{
    public bool KanUitgevoerdWorden => Fouten.Count == 0;
}

/// <summary>Gewenste roominstellingen; null betekent "niet wijzigen".</summary>
public sealed class RoomGewenst
{
    public string? AutomateProcessing { get; set; }
    public int? BookingWindowInDays { get; set; }
    public int? MaximumDurationInMinutes { get; set; }
    public bool? AllowConflicts { get; set; }
    public bool? AllowRecurringMeetings { get; set; }
    public bool? AddOrganizerToSubject { get; set; }
    public bool? DeleteSubject { get; set; }
    public string? Gebouw { get; set; }
    public string? Verdieping { get; set; }
    public string? VerdiepingLabel { get; set; }
    public int? Capaciteit { get; set; }
    public string? StandaardAgendaRecht { get; set; }

    public static RoomGewenst UitStandaard(RoomStandaard s) => new()
    {
        AutomateProcessing = s.AutomateProcessing,
        BookingWindowInDays = s.BookingWindowInDays,
        MaximumDurationInMinutes = s.MaximumDurationInMinutes,
        AllowConflicts = s.AllowConflicts,
        AllowRecurringMeetings = s.AllowRecurringMeetings,
        AddOrganizerToSubject = s.AddOrganizerToSubject,
        DeleteSubject = s.DeleteSubject,
        StandaardAgendaRecht = s.AgendaRechtStandaard,
    };
}

/// <summary>Welke stappen bij uitstroom; de standaard volgt de beslissingen in docs/conventies.md.</summary>
public sealed class UitstroomOpties
{
    public bool Uitschakelen { get; set; } = true;
    public bool OmschrijvingAanvullen { get; set; } = true;
    public bool Verplaatsen { get; set; } = true;
    public bool SessiesIntrekken { get; set; } = true;
    public bool AdGroepenVerwijderen { get; set; }
    public bool CloudGroepenVerwijderen { get; set; }
    public bool NaarGedeeldPostvak { get; set; }
    public bool LicentiesVerwijderen { get; set; }
    public DateOnly Datum { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>Bouwt de plannen (lijsten wijzigingen) voor de vier soorten beheeracties.</summary>
public static class PlanBouwer
{
    // ---------------------------------------------------------------- spiegelgebruiker

    /// <summary>De groepen met status Toevoegen worden wijzigingen langs de juiste weg (AD, Graph of Exchange).</summary>
    public static IReadOnlyList<Wijziging> Spiegel(InventarisData data, IReadOnlyList<SpiegelRegel> regels, Persoon doel)
    {
        var plan = new List<Wijziging>();
        var adDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in regels.Where(r => r.Status == SpiegelStatus.Toevoegen))
        {
            switch (r.Laag, r.Route)
            {
                case ("AD", SchrijfRoute.ActiveDirectory) when doel.Ad is not null:
                    if (adDns.Add(r.GroepId))
                    {
                        plan.Add(AdLidToevoegen(doel.Ad.DistinguishedName, doel.Naam, r.GroepId, r.Groep));
                    }

                    break;
                case ("Cloud", SchrijfRoute.ActiveDirectory) when doel.Ad is not null:
                    // Gesynchroniseerde cloudgroep: lid maken van de AD-groep erachter
                    var cloud = data.CloudGroepen.GetValueOrDefault(r.GroepId);
                    var adGroep = cloud is null ? null : data.ZoekAdGroepOpSam(cloud.OnPremisesSamAccountName);
                    var alLid = adGroep is not null && data.AdGroepenVan(doel).Any(g => string.Equals(g.DistinguishedName, adGroep.DistinguishedName, StringComparison.OrdinalIgnoreCase));
                    if (adGroep is not null && !alLid && adDns.Add(adGroep.DistinguishedName))
                    {
                        plan.Add(AdLidToevoegen(doel.Ad.DistinguishedName, doel.Naam, adGroep.DistinguishedName, adGroep.Name));
                    }

                    break;
                case ("Cloud", SchrijfRoute.Graph) when doel.Entra is not null:
                    plan.Add(GraphLidToevoegen(doel.Entra.Id, doel.Naam, r.GroepId, r.Groep));
                    break;
                case ("Cloud", SchrijfRoute.Exchange) when doel.Entra is not null:
                    plan.Add(ExchangeLidToevoegen(doel.Entra.UserPrincipalName, doel.Naam, data.CloudGroepen.GetValueOrDefault(r.GroepId), r.Groep, r.GroepId));
                    break;
            }
        }

        return plan;
    }

    // ---------------------------------------------------------------- rooms

    /// <summary>Wijzigingen om een room naar de gewenste instellingen te brengen; enkel wat verschilt.</summary>
    public static IReadOnlyList<Wijziging> Room(Room room, RoomGewenst gewenst)
    {
        var plan = new List<Wijziging>();

        var verwerking = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oud = new List<string>();
        var nieuw = new List<string>();
        void Vergelijk(string naam, string huidig, string? doelWaarde)
        {
            if (doelWaarde is not null && !string.Equals(huidig, doelWaarde, StringComparison.OrdinalIgnoreCase))
            {
                verwerking[naam] = doelWaarde;
                oud.Add($"{naam}={huidig}");
                nieuw.Add($"{naam}={doelWaarde}");
            }
        }

        Vergelijk("AutomateProcessing", room.AutomateProcessing, gewenst.AutomateProcessing);
        Vergelijk("BookingWindowInDays", room.BookingWindowInDays?.ToString() ?? string.Empty, gewenst.BookingWindowInDays?.ToString());
        Vergelijk("MaximumDurationInMinutes", room.MaximumDurationInMinutes?.ToString() ?? string.Empty, gewenst.MaximumDurationInMinutes?.ToString());
        Vergelijk("AllowConflicts", Bool(room.AllowConflicts), Bool(gewenst.AllowConflicts));
        Vergelijk("AllowRecurringMeetings", Bool(room.AllowRecurringMeetings), Bool(gewenst.AllowRecurringMeetings));
        Vergelijk("AddOrganizerToSubject", Bool(room.AddOrganizerToSubject), Bool(gewenst.AddOrganizerToSubject));
        Vergelijk("DeleteSubject", Bool(room.DeleteSubject), Bool(gewenst.DeleteSubject));
        if (verwerking.Count > 0)
        {
            plan.Add(new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangeRoomAgendaverwerking,
                Doel = room.Smtp,
                DoelNaam = room.DisplayName,
                Parameters = verwerking,
                Omschrijving = $"Agendaverwerking van {room.DisplayName} aanpassen",
                HuidigeWaarde = string.Join(", ", oud),
                NieuweWaarde = string.Join(", ", nieuw),
            });
        }

        var place = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        oud = new List<string>();
        nieuw = new List<string>();
        void VergelijkPlace(string naam, string huidig, string? doelWaarde)
        {
            if (doelWaarde is not null && !string.Equals(huidig.Trim(), doelWaarde.Trim(), StringComparison.Ordinal))
            {
                place[naam] = doelWaarde.Trim();
                oud.Add($"{naam}={huidig}");
                nieuw.Add($"{naam}={doelWaarde.Trim()}");
            }
        }

        VergelijkPlace("Building", room.Gebouw, gewenst.Gebouw);
        VergelijkPlace("Floor", room.Verdieping, gewenst.Verdieping);
        VergelijkPlace("FloorLabel", room.VerdiepingLabel, gewenst.VerdiepingLabel);
        VergelijkPlace("Capacity", room.Capaciteit?.ToString() ?? string.Empty, gewenst.Capaciteit?.ToString());
        if (place.Count > 0)
        {
            plan.Add(new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangeRoomPlace,
                Doel = room.Smtp,
                DoelNaam = room.DisplayName,
                Parameters = place,
                Omschrijving = $"Gebouw, verdieping of capaciteit van {room.DisplayName} aanpassen",
                HuidigeWaarde = string.Join(", ", oud),
                NieuweWaarde = string.Join(", ", nieuw),
            });
        }

        if (gewenst.StandaardAgendaRecht is { } recht && !string.Equals(room.StandaardAgendaRecht, recht, StringComparison.OrdinalIgnoreCase))
        {
            plan.Add(new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangeAgendaRecht,
                Doel = room.Smtp,
                DoelNaam = room.DisplayName,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["User"] = "Default", ["AccessRights"] = recht },
                Omschrijving = $"Standaardrecht op de agenda van {room.DisplayName} instellen",
                HuidigeWaarde = room.StandaardAgendaRecht.Length > 0 ? room.StandaardAgendaRecht : "(onbekend)",
                NieuweWaarde = recht,
            });
        }

        return plan;
    }

    /// <summary>Afwijkingen van een room tegenover de standaard, als leesbare tekst.</summary>
    public static IReadOnlyList<string> RoomAfwijkingen(Room room, RoomStandaard standaard)
    {
        var lijst = new List<string>();
        void Check(string naam, string huidig, string verwacht)
        {
            if (!string.Equals(huidig, verwacht, StringComparison.OrdinalIgnoreCase))
            {
                lijst.Add($"{naam}: {(huidig.Length == 0 ? "(leeg)" : huidig)} (standaard {verwacht})");
            }
        }

        Check("AutomateProcessing", room.AutomateProcessing, standaard.AutomateProcessing);
        Check("Boekingsvenster", room.BookingWindowInDays?.ToString() ?? string.Empty, standaard.BookingWindowInDays.ToString());
        Check("Maximale duur", room.MaximumDurationInMinutes?.ToString() ?? string.Empty, standaard.MaximumDurationInMinutes.ToString());
        Check("Conflicten toestaan", Bool(room.AllowConflicts), Bool(standaard.AllowConflicts));
        Check("Terugkerende vergaderingen", Bool(room.AllowRecurringMeetings), Bool(standaard.AllowRecurringMeetings));
        Check("Organisator in onderwerp", Bool(room.AddOrganizerToSubject), Bool(standaard.AddOrganizerToSubject));
        Check("Onderwerp wissen", Bool(room.DeleteSubject), Bool(standaard.DeleteSubject));
        Check("Standaardrecht agenda", room.StandaardAgendaRecht, standaard.AgendaRechtStandaard);
        if (string.IsNullOrWhiteSpace(room.Gebouw))
        {
            lijst.Add("Gebouw: (leeg)");
        }

        if (room.Capaciteit is null or 0)
        {
            lijst.Add("Capaciteit: (leeg)");
        }

        return lijst;
    }

    // ---------------------------------------------------------------- nieuwe gebruiker

    public static NieuweGebruikerVoorstel NieuweGebruiker(InventarisData data, OrganisatieConfig config, NieuweGebruikerAanvraag aanvraag, string? wachtwoord = null)
    {
        var fouten = new List<string>();
        var waarschuwingen = new List<string>();

        var populatie = config.ZoekPopulatie(aanvraag.Populatie);
        if (populatie is null)
        {
            fouten.Add("Kies of het om de gemeente, het OCMW of een vrijwilliger gaat.");
        }

        if (string.IsNullOrWhiteSpace(aanvraag.Voornaam) || string.IsNullOrWhiteSpace(aanvraag.Achternaam))
        {
            fouten.Add("Voornaam en achternaam zijn verplicht.");
        }

        var dienst = config.ZoekDienst(aanvraag.Dienst);
        if (populatie is not null && populatie.City is null && dienst is null)
        {
            fouten.Add("Kies een dienst uit de lijst.");
        }

        if (populatie is not null && populatie.City is null && string.IsNullOrWhiteSpace(aanvraag.Kantoor))
        {
            fouten.Add("Kies een kantoor uit de lijst.");
        }

        var domeinDn = data.DomeinDn.Length > 0 ? data.DomeinDn : config.DomeinDn;
        if (domeinDn.Length == 0)
        {
            fouten.Add("Het AD-domein is onbekend: vul adDomein in de organisatie in of laad live gegevens uit AD.");
        }

        if (!data.HeeftAd)
        {
            waarschuwingen.Add("Er zijn geen AD-gegevens geladen: de app kan niet controleren of de naam al bestaat.");
        }

        // Namen afleiden en uniek maken
        var lokaal = NaamRegels.LokaalDeel(aanvraag.Voornaam, aanvraag.Achternaam);
        var domein = populatie?.UpnDomein ?? string.Empty;
        var sam = NaamRegels.Sam(lokaal);
        var upn = $"{lokaal}@{domein}";
        if (lokaal.Length > 0 && Bestaat(data, sam, upn))
        {
            var gevonden = false;
            for (var n = 2; n <= 9 && !gevonden; n++)
            {
                var s = NaamRegels.SamMetNummer(lokaal, n);
                var u = $"{lokaal}{n}@{domein}";
                if (!Bestaat(data, s, u))
                {
                    waarschuwingen.Add($"{lokaal} bestaat al; voorgesteld: {lokaal}{n}. Controleer of het niet om dezelfde persoon gaat.");
                    (sam, upn, gevonden) = (s, u, true);
                }
            }

            if (!gevonden)
            {
                fouten.Add($"Geen vrije naam gevonden voor {lokaal}.");
            }
        }

        if (sam != upn.Split('@')[0])
        {
            waarschuwingen.Add($"sAMAccountName is beperkt tot {NaamRegels.MaxSam} tekens: {sam} (UPN blijft {upn}).");
        }

        var weergave = NaamRegels.WeergaveNaam(aanvraag.Voornaam, aanvraag.Achternaam);
        var ouDn = populatie is null ? string.Empty : config.OuDn(populatie.Ou, domeinDn);
        var cn = weergave;
        if (data.AdGebruikers.Any(a => string.Equals(a.DistinguishedName, $"CN={cn},{ouDn}", StringComparison.OrdinalIgnoreCase)))
        {
            cn = $"{weergave} ({sam})";
        }

        var dn = $"CN={EscapeCn(cn)},{ouDn}";
        wachtwoord ??= NaamRegels.Startwachtwoord();

        var nu = new List<Wijziging>();
        var naSync = new List<Wijziging>();
        if (fouten.Count == 0 && populatie is not null)
        {
            var kenmerken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ou"] = ouDn,
                ["cn"] = cn,
                ["sAMAccountName"] = sam,
                ["userPrincipalName"] = upn,
                ["givenName"] = aanvraag.Voornaam.Trim(),
                ["sn"] = aanvraag.Achternaam.Trim(),
                ["displayName"] = weergave,
                ["mail"] = upn,
                ["company"] = config.Company,
            };
            if (dienst is not null)
            {
                kenmerken["department"] = dienst.Department;
            }

            if (!string.IsNullOrWhiteSpace(aanvraag.Kantoor))
            {
                kenmerken["physicalDeliveryOfficeName"] = aanvraag.Kantoor.Trim();
            }

            if (!string.IsNullOrWhiteSpace(aanvraag.Functie))
            {
                kenmerken["title"] = aanvraag.Functie.Trim();
            }

            var script = aanvraag.Logonscript ?? dienst?.Logonscript;
            if (!string.IsNullOrWhiteSpace(script))
            {
                kenmerken["scriptPath"] = script.Trim();
            }

            if (!string.IsNullOrWhiteSpace(populatie.City))
            {
                kenmerken["l"] = populatie.City;
            }

            var aanmaken = new Wijziging
            {
                Systeem = Systeem.ActiveDirectory,
                Actie = Actie.AdGebruikerAanmaken,
                Doel = dn,
                DoelNaam = weergave,
                Parameters = kenmerken,
                Omschrijving = $"Account {sam} aanmaken in {populatie.Ou} (ingeschakeld, wachtwoord wijzigen bij eerste aanmelding)",
                NieuweWaarde = upn,
                Geheim = wachtwoord,
            };
            nu.Add(aanmaken);

            // AD-groepen: standaardgroepen van de populatie en de groepen van de spiegelgebruiker
            var adGroepen = new Dictionary<string, AdGroep>(StringComparer.OrdinalIgnoreCase);
            if (aanvraag.StandaardAdGroepen)
            {
                foreach (var naam in populatie.StandaardAdGroepen)
                {
                    var g = data.ZoekAdGroepOpNaam(naam);
                    if (g is null)
                    {
                        waarschuwingen.Add($"Standaardgroep {naam} niet gevonden in AD; overgeslagen.");
                    }
                    else
                    {
                        adGroepen[g.DistinguishedName] = g;
                    }
                }
            }

            var cloudGroepen = new Dictionary<string, CloudGroep>(StringComparer.OrdinalIgnoreCase);
            if (aanvraag.Spiegel is { } spiegel)
            {
                foreach (var g in data.AdGroepenVan(spiegel).Where(g => !Spiegelgebruiker.IsBevoorrecht(g)))
                {
                    adGroepen[g.DistinguishedName] = g;
                }

                foreach (var g in data.CloudGroepenVan(spiegel).Where(g => !g.Dynamisch && !config.IsDynamischeGroep(g.DisplayName)))
                {
                    if (g.UitAd)
                    {
                        if (data.ZoekAdGroepOpSam(g.OnPremisesSamAccountName) is { } achterliggend)
                        {
                            adGroepen[achterliggend.DistinguishedName] = achterliggend;
                        }
                    }
                    else if (!IsAfdelingsgroep(config, g))
                    {
                        cloudGroepen[g.Id] = g;
                    }
                }
            }

            foreach (var g in adGroepen.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                nu.Add(AdLidToevoegen(dn, weergave, g.DistinguishedName, g.Name, aanmaken.Id));
            }

            // Na de synchronisatie naar Entra: licentie en cloudgroepen
            if (!string.IsNullOrWhiteSpace(aanvraag.LicentieSku))
            {
                if (populatie.GeenLicentie)
                {
                    waarschuwingen.Add($"{populatie.Naam} krijgt normaal geen licentie.");
                }

                var licentie = data.Licenties.FirstOrDefault(l => string.Equals(l.SkuPartNumber, aanvraag.LicentieSku, StringComparison.OrdinalIgnoreCase));
                if (licentie is { Vrij: 0 })
                {
                    waarschuwingen.Add($"Er zijn geen vrije {aanvraag.LicentieSku}-licenties ({licentie.Toegewezen}/{licentie.Totaal}). De toekenning zal mislukken tot er een vrijkomt.");
                }

                var locatie = new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphKenmerkenZetten,
                    Doel = upn,
                    DoelNaam = weergave,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["usageLocation"] = "BE" },
                    Omschrijving = "Gebruikslocatie op BE zetten (nodig voor een licentie)",
                    NieuweWaarde = "BE",
                };
                naSync.Add(locatie);
                naSync.Add(new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphLicentieToekennen,
                    Doel = upn,
                    DoelNaam = weergave,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["sku"] = aanvraag.LicentieSku!, ["skuId"] = licentie?.SkuId ?? string.Empty },
                    Omschrijving = $"Licentie {aanvraag.LicentieSku} toekennen",
                    NieuweWaarde = aanvraag.LicentieSku,
                    AfhankelijkVan = locatie.Id,
                });
            }

            if (dienst is not null && config.AfdelingVoor(dienst.Department) is { Groep: { Length: > 0 } afdelingsGroep })
            {
                if (data.ZoekCloudGroepOpNaam(afdelingsGroep) is { } g)
                {
                    cloudGroepen[g.Id] = g;
                }
                else
                {
                    waarschuwingen.Add($"Afdelingsgroep {afdelingsGroep} niet gevonden in Entra; overgeslagen.");
                }
            }

            foreach (var g in cloudGroepen.Values.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                naSync.Add(Spiegelgebruiker.RouteVoor(g) == SchrijfRoute.Exchange
                    ? ExchangeLidToevoegen(upn, weergave, g, g.DisplayName, g.Id)
                    : GraphLidToevoegen(upn, weergave, g.Id, g.DisplayName));
            }
        }

        return new NieuweGebruikerVoorstel(sam, upn, weergave, dn, wachtwoord, fouten, waarschuwingen, nu, naSync);
    }

    // ---------------------------------------------------------------- uitstroom

    public static IReadOnlyList<Wijziging> Uitstroom(InventarisData data, OrganisatieConfig config, Persoon persoon, UitstroomOpties opties)
    {
        var plan = new List<Wijziging>();
        var ad = persoon.Ad;
        var entra = persoon.Entra;
        var naam = persoon.Naam;

        if (ad is not null)
        {
            plan.Add(new Wijziging
            {
                Systeem = Systeem.ActiveDirectory,
                Actie = Actie.AdAccountUitschakelen,
                Doel = ad.DistinguishedName,
                DoelNaam = naam,
                Omschrijving = "AD-account uitschakelen",
                HuidigeWaarde = ad.Enabled ? "ingeschakeld" : "al uitgeschakeld",
                NieuweWaarde = "uitgeschakeld",
                Geselecteerd = opties.Uitschakelen && ad.Enabled,
            });

            var omschrijving = $"Uit dienst {opties.Datum:yyyy-MM-dd}" + (string.IsNullOrWhiteSpace(ad.Description) ? string.Empty : " - " + ad.Description.Trim());
            plan.Add(new Wijziging
            {
                Systeem = Systeem.ActiveDirectory,
                Actie = Actie.AdKenmerkenZetten,
                Doel = ad.DistinguishedName,
                DoelNaam = naam,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["description"] = omschrijving },
                Vorige = new(StringComparer.OrdinalIgnoreCase) { ["description"] = ad.Description },
                Omschrijving = "Datum van uitstroom in de omschrijving zetten",
                HuidigeWaarde = ad.Description,
                NieuweWaarde = omschrijving,
                Geselecteerd = opties.OmschrijvingAanvullen,
            });

            foreach (var g in data.AdGroepenVan(persoon).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.ActiveDirectory,
                    Actie = Actie.AdGroepLidVerwijderen,
                    Doel = ad.DistinguishedName,
                    DoelNaam = naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = g.DistinguishedName, ["groep"] = g.Name },
                    Omschrijving = $"Uit AD-groep {g.Name} halen" + (Spiegelgebruiker.IsBevoorrecht(g) ? " (bevoorrecht)" : string.Empty),
                    Geselecteerd = opties.AdGroepenVerwijderen || Spiegelgebruiker.IsBevoorrecht(g),
                });
            }

            var doelOu = config.Uitstroom?.Ou;
            if (!string.IsNullOrWhiteSpace(doelOu))
            {
                // Verplaatsen als laatste AD-stap: daarna verandert de DN van het account
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.ActiveDirectory,
                    Actie = Actie.AdVerplaatsen,
                    Doel = ad.DistinguishedName,
                    DoelNaam = naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["doelOu"] = config.OuDn(doelOu, data.DomeinDn) },
                    Omschrijving = $"Verplaatsen naar {doelOu}",
                    HuidigeWaarde = ad.OuPad,
                    NieuweWaarde = doelOu,
                    Geselecteerd = opties.Verplaatsen && !string.Equals(ad.OuPad, doelOu, StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        if (entra is not null)
        {
            if (ad is null && !entra.OnPremisesSyncEnabled)
            {
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphKenmerkenZetten,
                    Doel = entra.Id,
                    DoelNaam = naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
                    Vorige = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = entra.AccountEnabled ? "true" : "false" },
                    Omschrijving = "Cloudaccount uitschakelen (aanmelden blokkeren)",
                    HuidigeWaarde = entra.AccountEnabled ? "ingeschakeld" : "al uitgeschakeld",
                    NieuweWaarde = "uitgeschakeld",
                    Geselecteerd = opties.Uitschakelen && entra.AccountEnabled,
                });
            }

            plan.Add(new Wijziging
            {
                Systeem = Systeem.Graph,
                Actie = Actie.GraphSessiesIntrekken,
                Doel = entra.Id,
                DoelNaam = naam,
                Omschrijving = "Alle aanmeldsessies intrekken (meteen afgemeld op alle toestellen, niet pas na de synchronisatie)",
                Geselecteerd = opties.SessiesIntrekken,
            });

            foreach (var g in data.CloudGroepenVan(persoon).Where(g => !g.Dynamisch && !g.UitAd).OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var exchange = Spiegelgebruiker.RouteVoor(g) == SchrijfRoute.Exchange;
                plan.Add(new Wijziging
                {
                    Systeem = exchange ? Systeem.Exchange : Systeem.Graph,
                    Actie = exchange ? Actie.ExchangeGroepLidVerwijderen : Actie.GraphGroepLidVerwijderen,
                    Doel = exchange ? entra.UserPrincipalName : entra.Id,
                    DoelNaam = naam,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepId"] = g.Id, ["groep"] = exchange && g.Mail.Length > 0 ? g.Mail : g.DisplayName },
                    Omschrijving = $"Uit cloudgroep {g.DisplayName} halen",
                    Geselecteerd = opties.CloudGroepenVerwijderen,
                });
            }

            var omzetten = new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangeNaarGedeeldPostvak,
                Doel = entra.UserPrincipalName,
                DoelNaam = naam,
                Omschrijving = "Postvak omzetten naar een gedeeld postvak (blijft bewaard zonder licentie)",
                Geselecteerd = opties.NaarGedeeldPostvak,
            };
            plan.Add(omzetten);

            if (entra.Licenties.Count > 0)
            {
                plan.Add(new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphLicentiesVerwijderen,
                    Doel = entra.Id,
                    DoelNaam = naam,
                    Omschrijving = "Alle licenties verwijderen: " + string.Join(", ", entra.Licenties),
                    HuidigeWaarde = string.Join(", ", entra.Licenties),
                    Waarschuwing = "Zonder licentie verdwijnt het postvak na 30 dagen, tenzij het eerst naar een gedeeld postvak wordt omgezet.",
                    Geselecteerd = opties.LicentiesVerwijderen,
                });
            }
        }

        return plan;
    }

    // ---------------------------------------------------------------- hulpfuncties

    private static Wijziging AdLidToevoegen(string gebruikerDn, string naam, string groepDn, string groep, Guid? afhankelijkVan = null) => new()
    {
        Systeem = Systeem.ActiveDirectory,
        Actie = Actie.AdGroepLidToevoegen,
        Doel = gebruikerDn,
        DoelNaam = naam,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = groepDn, ["groep"] = groep },
        Omschrijving = $"Lid maken van AD-groep {groep}",
        AfhankelijkVan = afhankelijkVan,
    };

    private static Wijziging GraphLidToevoegen(string gebruiker, string naam, string groepId, string groep) => new()
    {
        Systeem = Systeem.Graph,
        Actie = Actie.GraphGroepLidToevoegen,
        Doel = gebruiker,
        DoelNaam = naam,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepId"] = groepId, ["groep"] = groep },
        Omschrijving = $"Lid maken van cloudgroep {groep}",
    };

    private static Wijziging ExchangeLidToevoegen(string upn, string naam, CloudGroep? groep, string groepNaam, string groepId) => new()
    {
        Systeem = Systeem.Exchange,
        Actie = Actie.ExchangeGroepLidToevoegen,
        Doel = upn,
        DoelNaam = naam,
        Parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["groep"] = groep is { Mail.Length: > 0 } ? groep.Mail : groepNaam,
            ["groepId"] = groepId,
            ["groepNaam"] = groepNaam,
        },
        Omschrijving = $"Lid maken van {groepNaam} (Exchange)" + (groepNaam.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase) ? ": toegang tot een gedeeld postvak" : string.Empty),
    };

    private static bool Bestaat(InventarisData data, string sam, string upn)
        => data.AdGebruikers.Any(a => string.Equals(a.SamAccountName, sam, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(a.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(a.EmailAddress, upn, StringComparison.OrdinalIgnoreCase))
           || data.EntraGebruikers.Any(e => string.Equals(e.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(e.Mail, upn, StringComparison.OrdinalIgnoreCase));

    private static bool IsAfdelingsgroep(OrganisatieConfig config, CloudGroep g)
        => config.Afdelingen.Any(a => string.Equals(a.Groep, g.DisplayName, StringComparison.OrdinalIgnoreCase));

    private static string Bool(bool waarde) => waarde ? "True" : "False";

    /// <summary>null blijft null: een niet-ingevuld veld betekent "niet wijzigen".</summary>
    private static string? Bool(bool? waarde) => waarde is null ? null : Bool(waarde.Value);

    /// <summary>Komma's, plustekens en dergelijke moeten in een CN ontsnapt worden.</summary>
    public static string EscapeCn(string cn)
    {
        var uit = new System.Text.StringBuilder();
        foreach (var c in cn)
        {
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')
            {
                uit.Append('\\');
            }

            uit.Append(c);
        }

        return uit.ToString();
    }
}
