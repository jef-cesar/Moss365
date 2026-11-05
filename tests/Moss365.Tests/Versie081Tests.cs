using System.Net;
using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.Connections.Ad;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.8.1: telefoon, gsm en manager; taken voor een selectie (beheerder, uitstroom inplannen).</summary>
public sealed class Versie081Tests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-v081-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, true);
        }
    }

    /// <summary>De verzonnen omgeving met telefoon, gsm en manager: an is manager van bo (AD), en van info (cloud).</summary>
    private static InventarisData MetManagers()
    {
        var b = TestData.Bestanden();
        var an = b[InventarisLader.AdGebruikers].Single(r => r["SamAccountName"] == "an.peeters");
        var bo = b[InventarisLader.AdGebruikers].Single(r => r["SamAccountName"] == "bo.janssens");
        an["OfficePhone"] = "03 000 00 01";
        an["MobilePhone"] = "0470 00 00 01";
        bo["Manager"] = an["DistinguishedName"];
        var info = b[InventarisLader.EntraGebruikers].Single(r => r["Id"] == "e-info");
        info["BusinessPhone"] = "03 000 00 09";
        info["ManagerId"] = "e-an";
        return InventarisLader.Bouw(b);
    }

    private static LogRegel Gelogd(Wijziging w) => new(DateTimeOffset.Now, "t", "c", w.Systeem.ToString(), w.Actie.ToString(), w.Doel, w.DoelNaam,
        w.Omschrijving, w.Parameters, "Gelukt", "", Guid.NewGuid(), w.Id, w.Vorige);

    // ---------------------------------------------------------------- telefoon, gsm, manager

    [Fact]
    public void TelefoonGsmEnManagerKomenUitDeInventarisEnGaanErTerugIn()
    {
        var d = MetManagers();
        var an = d.ZoekPersoon("ad:an.peeters")!;
        var bo = d.ZoekPersoon("ad:bo.janssens")!;
        var info = d.Personen.Single(p => p.Entra?.Id == "e-info");
        Assert.Equal("03 000 00 01", an.Ad!.Telefoon);
        Assert.Equal("0470 00 00 01", an.Ad.Gsm);
        Assert.Same(an, KenmerkPlannen.ManagerVan(d, bo));
        Assert.Same(an, KenmerkPlannen.ManagerVan(d, info));
        Assert.Null(KenmerkPlannen.ManagerVan(d, an));
        Assert.Equal(an.Sleutel, GebruikerGewenst.Van(bo, d).Manager);
        Assert.Equal(string.Empty, GebruikerGewenst.Van(an, d).Manager);
        Assert.Null(GebruikerGewenst.Van(an).Manager); // zonder gegevens: niet wijzigen

        var map = InventarisSchrijver.Bewaar(d, _map, InventarisOnderdeel.Ad | InventarisOnderdeel.Entra, new DateTime(2026, 10, 31, 9, 0, 0));
        var terug = InventarisLader.Laad(InventarisLader.ZoekMappen(_map));
        Assert.True(Directory.Exists(map));
        Assert.Equal("0470 00 00 01", terug.ZoekPersoon("ad:an.peeters")!.Ad!.Gsm);
        Assert.Equal(an.Ad.DistinguishedName, terug.ZoekPersoon("ad:bo.janssens")!.Ad!.Manager);
        var infoTerug = terug.Personen.Single(p => p.Entra?.Id == "e-info");
        Assert.Equal("e-an", infoTerug.Entra!.ManagerId);
        Assert.Equal("03 000 00 09", infoTerug.Entra.Telefoon);
    }

    [Fact]
    public void LdapLeestTelefoonGsmEnManager()
    {
        var g = AdLdapMapper.NaarGebruiker(new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = new object?[] { "an.peeters" },
            ["telephoneNumber"] = new object?[] { "03 000 00 01" },
            ["mobile"] = new object?[] { "0470 00 00 01" },
            ["manager"] = new object?[] { "CN=Bo Janssens,OU=GEBRUIKERS,DC=test,DC=internal" },
        });
        Assert.Equal("03 000 00 01", g.Telefoon);
        Assert.Equal("0470 00 00 01", g.Gsm);
        Assert.Equal("CN=Bo Janssens,OU=GEBRUIKERS,DC=test,DC=internal", g.Manager);
        Assert.Contains("manager", AdLdapMapper.GebruikerKenmerken, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void InAdIsDeManagerEenKenmerkMetDeVorigeWaarde()
    {
        var d = MetManagers();
        var bo = d.ZoekPersoon("ad:bo.janssens")!;
        var cas = d.ZoekPersoon("ad:cas.vos")!;
        var gewenst = GebruikerGewenst.Van(bo, d);
        gewenst.Manager = cas.Sleutel;
        gewenst.Telefoon = "03 000 00 02";

        var w = Assert.Single(KenmerkPlannen.Gebruiker(d, TestData.Config(), bo, gewenst).Plan);
        Assert.Equal(Actie.AdKenmerkenZetten, w.Actie);
        Assert.Equal(cas.Ad!.DistinguishedName, w.P("manager"));
        Assert.Equal("03 000 00 02", w.P("telephoneNumber"));
        Assert.Equal(d.ZoekPersoon("ad:an.peeters")!.Ad!.DistinguishedName, w.Vorige!["manager"]);
        Assert.Contains("Manager: " + cas.Naam, w.NieuweWaarde);

        // Geen manager meer: leeg (de AD-schrijver wist het kenmerk)
        gewenst.Manager = string.Empty;
        gewenst.Telefoon = bo.Ad!.Telefoon;
        Assert.Equal(string.Empty, Assert.Single(KenmerkPlannen.Gebruiker(d, TestData.Config(), bo, gewenst).Plan).P("manager"));

        // Iemand zonder AD-account kan in AD geen manager zijn
        gewenst.Manager = d.Personen.Single(p => p.Entra?.Id == "e-info").Sleutel;
        var fout = KenmerkPlannen.Gebruiker(d, TestData.Config(), bo, gewenst);
        Assert.DoesNotContain(fout.Plan, w => w.Parameters.ContainsKey("manager"));
        Assert.Contains("geen AD-account", string.Join(" ", fout.Fouten.Concat(fout.Plan.Select(w => w.Waarschuwing ?? ""))));
    }

    [Fact]
    public void InDeCloudIsDeManagerEenAparteOpdrachtDieOngedaanKan()
    {
        var d = MetManagers();
        var info = d.Personen.Single(p => p.Entra?.Id == "e-info");
        var bo = d.ZoekPersoon("ad:bo.janssens")!;
        var gewenst = GebruikerGewenst.Van(info, d);
        Assert.Equal("03 000 00 09", gewenst.Telefoon);
        gewenst.Manager = bo.Sleutel;
        gewenst.Gsm = "0470 00 00 09";

        var u = KenmerkPlannen.Gebruiker(d, TestData.Config(), info, gewenst);
        Assert.Equal(2, u.Plan.Count);
        Assert.Equal(Actie.GraphKenmerkenZetten, u.Plan[0].Actie);
        Assert.Equal("0470 00 00 09", u.Plan[0].P("mobilePhone"));
        var m = u.Plan[1];
        Assert.Equal(Actie.GraphManagerZetten, m.Actie);
        Assert.Equal("e-info", m.Doel);
        Assert.Equal("e-bo", m.P("managerId"));
        Assert.Equal("e-an", m.Vorige!["managerId"]);
        Assert.Null(Terugdraaien.WaaromNiet(Gelogd(m)));

        var terug = Assert.Single(Terugdraaien.Plan(new[] { Gelogd(m) }).Plan);
        Assert.Equal(Actie.GraphManagerZetten, terug.Actie);
        Assert.Equal("e-an", terug.P("managerId"));

        // Enkel de manager: één opdracht
        var enkel = GebruikerGewenst.Van(info, d);
        enkel.Manager = string.Empty;
        var w = Assert.Single(KenmerkPlannen.Gebruiker(d, TestData.Config(), info, enkel).Plan);
        Assert.Equal(string.Empty, w.P("managerId"));
    }

    [Fact]
    public void BusinessPhonesIsEenLijstInGraph()
    {
        var inhoud = GraphVerbinding.KenmerkenInhoud(new Dictionary<string, string> { ["businessPhones"] = "03 000 00 01", ["mobilePhone"] = "", ["jobTitle"] = "x" });
        Assert.Equal(new[] { "03 000 00 01" }, inhoud["businessPhones"]);
        Assert.Null(inhoud["mobilePhone"]);
        Assert.Empty((string[])GraphVerbinding.KenmerkenInhoud(new Dictionary<string, string> { ["businessPhones"] = "" })["businessPhones"]!);
    }

    private static GraphVerbinding Graph(NepGraph nep) => new(new GraphKlant(new HttpClient(nep), _ => Task.FromResult("token")));

    private static Wijziging ManagerOpdracht(string managerId) => new()
    {
        Systeem = Systeem.Graph,
        Actie = Actie.GraphManagerZetten,
        Doel = "e-info",
        Omschrijving = "Manager zetten",
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = managerId },
    };

    [Fact]
    public async Task GraphZetEnWistDeManager()
    {
        var nep = new NepGraph().Op(HttpMethod.Put, "/users/e-info/manager/$ref", "", HttpStatusCode.NoContent);
        var r = Assert.Single(await Graph(nep).VoerUitAsync(new[] { ManagerOpdracht("e-bo") }, CancellationToken.None));
        Assert.Equal(ResultaatStatus.Gelukt, r.Status);
        var put = nep.Verzoeken.Single(v => v.Methode == HttpMethod.Put);
        Assert.Contains("/users/e-bo", put.Inhoud);
        Assert.Contains("@odata.id", put.Inhoud);

        // Wissen: DELETE; had al geen manager (404) is geen fout
        var wis = new NepGraph();
        var r2 = Assert.Single(await Graph(wis).VoerUitAsync(new[] { ManagerOpdracht("") }, CancellationToken.None));
        Assert.Equal(ResultaatStatus.Gelukt, r2.Status);
        Assert.Equal(HttpMethod.Delete, Assert.Single(wis.Verzoeken).Methode);
        Assert.Contains("geen manager", r2.Melding);
    }

    // ---------------------------------------------------------------- agenda van een room (#20)

    [Fact]
    public void AgendaPeriodePerWeekOfGekozen()
    {
        var woensdag = new DateTime(2026, 11, 4, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 11, 2), AgendaPeriode.Maandag(woensdag));
        Assert.Equal(new DateTime(2026, 11, 2), AgendaPeriode.Maandag(new DateTime(2026, 11, 2)));
        Assert.Equal(new DateTime(2026, 10, 26), AgendaPeriode.Maandag(new DateTime(2026, 11, 1))); // zondag hoort bij de week ervoor

        var (van, tot, fout) = AgendaPeriode.Bereken(AgendaKeuze.VolgendeWeek, woensdag);
        Assert.Null(fout);
        Assert.Equal(new DateTime(2026, 11, 9), van);
        Assert.Equal(new DateTime(2026, 11, 16), tot);

        var gekozen = AgendaPeriode.Bereken(AgendaKeuze.Gekozen, woensdag, new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 4));
        Assert.Equal(TimeSpan.FromDays(1), gekozen.Tot - gekozen.Van); // de einddatum telt mee
        Assert.Contains("hoogstens", AgendaPeriode.Bereken(AgendaKeuze.Gekozen, woensdag, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)).Fout);
        Assert.NotNull(AgendaPeriode.Bereken(AgendaKeuze.Gekozen, woensdag).Fout);
    }

    [Fact]
    public async Task GraphLeestDeAgendaMetPaginasEnZegtWanneerHetNietMag()
    {
        var nep = new NepGraph()
            .Op(HttpMethod.Get, "/users/rm-vz-a%40test.be/calendarView", new Dictionary<string, object>
            {
                ["value"] = new object[]
                {
                    new
                    {
                        subject = "Organisator Een", isAllDay = false, sensitivity = "normal", showAs = "busy", isCancelled = false,
                        organizer = new { emailAddress = new { name = "Organisator Een", address = "een@test.be" } },
                        start = new { dateTime = "2026-11-03T08:00:00.0000000", timeZone = "UTC" },
                        end = new { dateTime = "2026-11-03T09:00:00.0000000", timeZone = "UTC" },
                    },
                },
                ["@odata.nextLink"] = GraphKlant.Basis + "/agenda-pagina2",
            })
            .Op(HttpMethod.Get, "/agenda-pagina2", new
            {
                value = new object[]
                {
                    new
                    {
                        subject = "", isAllDay = true, sensitivity = "private", showAs = "oof", isCancelled = false,
                        start = new { dateTime = "2026-11-05T00:00:00.0000000", timeZone = "UTC" },
                        end = new { dateTime = "2026-11-06T00:00:00.0000000", timeZone = "UTC" },
                    },
                },
            });
        var van = new DateTime(2026, 11, 2);
        var agenda = await Graph(nep).LeesAgendaAsync("rm-vz-a@test.be", van, van.AddDays(7), CancellationToken.None);

        Assert.Equal(2, agenda.Count);
        Assert.Equal(new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc).ToLocalTime(), agenda[0].Begin);
        Assert.Equal("een@test.be", agenda[0].OrganisatorAdres);
        Assert.True(agenda[1].HeleDag);
        Assert.Equal(new DateTime(2026, 11, 5), agenda[1].Begin); // een hele dag schuift niet door de tijdzone
        Assert.Equal("Privé", agenda[1].OnderwerpTekst);
        Assert.Equal("afwezig", agenda[1].StatusTekst);
        var url = nep.Verzoeken[0].Url;
        Assert.Contains("startDateTime=" + van.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), url);
        Assert.All(nep.Verzoeken, v => Assert.Equal(HttpMethod.Get, v.Methode));

        var geenRecht = new NepGraph().Op(HttpMethod.Get, "/users/rm-vz-a%40test.be/calendarView",
            "{\"error\":{\"code\":\"ErrorAccessDenied\",\"message\":\"Access is denied.\"}}", HttpStatusCode.Forbidden);
        var fout = await Assert.ThrowsAsync<GeenRechtFout>(() => Graph(geenRecht).LeesAgendaAsync("rm-vz-a@test.be", van, van.AddDays(7), CancellationToken.None));
        Assert.Contains("Access is denied", fout.Message);
    }

    // ---------------------------------------------------------------- KPI's (#21)

    [Fact]
    public void ElkeKpiHeeftEenUniekeSleutelEnWordtTeruggevonden()
    {
        Assert.Equal(Kpis.Vast.Count, Kpis.Vast.Select(k => k.Sleutel).Distinct().Count());
        Assert.All(Kpis.Vast, k => Assert.Same(k, Kpis.Zoek(k.Sleutel)));
        Assert.Equal("gebruikers?kpi=afdeling%3AVrije%20tijd", Kpis.Afdeling("Vrije tijd").Link);
        Assert.Equal("Vrije tijd", Kpis.Zoek("afdeling:Vrije tijd")!.Waarde);
        Assert.Null(Kpis.Zoek("bestaat-niet"));
        Assert.Null(Kpis.Zoek("afdeling:"));
    }

    [Fact]
    public void DeKpisTellenZoalsDeStatistieken()
    {
        var d = MetManagers();
        var c = TestData.Config();
        var nu = new DateTime(2026, 10, 31, 12, 0, 0);
        var s = Statistiek.Bereken(d, c, nu);
        var b = new KpiBron(d, c, nu);
        int N(Kpi k) => Kpis.Aantal(k, b);

        Assert.Equal(s.Actief, N(Kpis.Actief));
        Assert.Equal(s.Uitgeschakeld, N(Kpis.Uitgeschakeld));
        Assert.Equal(s.Gasten, N(Kpis.Gasten));
        Assert.Equal(s.AdEnEntra, N(Kpis.AdEnEntra));
        Assert.Equal(s.AlleenAd, N(Kpis.AlleenAd));
        Assert.Equal(s.AlleenEntra, N(Kpis.AlleenEntra));
        Assert.Equal(s.PostvakAccounts, N(Kpis.PostvakAccounts));
        Assert.Equal(s.Inactief90, N(Kpis.Inactief90));
        Assert.Equal(s.Inactief180, N(Kpis.Inactief180));
        Assert.Equal(s.NooitAangemeld, N(Kpis.NooitAangemeld));
        Assert.Equal(s.WachtwoordVerlooptNooit, N(Kpis.WachtwoordVerlooptNooit));
        Assert.Equal(s.BevoorrechteLeden, N(Kpis.Bevoorrecht));
        Assert.Equal(s.ZonderToegangsgroep, N(Kpis.ZonderToegangsgroep));
        Assert.Equal(s.LegeGroepen, N(Kpis.LegeGroepen));
        Assert.Equal(s.DynamischeGroepen, N(Kpis.DynamischeGroepen));
        Assert.Equal(s.RoomsMetAfwijking, N(Kpis.RoomsMetAfwijking));
        Assert.All(s.PerAfdeling, st => Assert.Equal(st.Aantal, N(Kpis.Zoek(Uri.UnescapeDataString(st.Link!.Split("kpi=")[1]))!)));
        Assert.All(s.Rechtenmodel, st => Assert.Equal(st.Aantal, N(Kpis.Rechtenmodel(st.Naam))));
        Assert.All(s.GroepenPerSoort, st => Assert.Equal(st.Aantal, N(Kpis.Groepsoort(st.Naam))));
    }

    // ---------------------------------------------------------------- statistieken in BI-stijl

    private static InventarisData MetDatums(DateTime nu)
    {
        var b = TestData.Bestanden();
        string D(int dagen) => nu.AddDays(-dagen).ToString("yyyy-MM-dd HH:mm:ss");
        var ad = b[InventarisLader.AdGebruikers];
        ad.Single(r => r["SamAccountName"] == "an.peeters")["LastLogonDate"] = D(5);
        ad.Single(r => r["SamAccountName"] == "an.peeters")["PasswordLastSet"] = D(10);
        ad.Single(r => r["SamAccountName"] == "an.peeters")["WhenCreated"] = D(3);
        ad.Single(r => r["SamAccountName"] == "bo.janssens")["LastLogonDate"] = D(45);
        ad.Single(r => r["SamAccountName"] == "bo.janssens")["PasswordLastSet"] = D(400);
        ad.Single(r => r["SamAccountName"] == "bo.janssens")["Office"] = "bibliotheek";
        ad.Single(r => r["SamAccountName"] == "cas.vos")["LastLogonDate"] = D(200);
        ad.Single(r => r["SamAccountName"] == "cas.vos")["PasswordLastSet"] = D(900);
        return InventarisLader.Bouw(b);
    }

    [Fact]
    public void VerdelingenTellenOpTotDeActieveMedewerkers()
    {
        var nu = new DateTime(2026, 10, 31, 12, 0, 0);
        var b = new KpiBron(MetDatums(nu), TestData.Config(), nu);
        var actief = b.Actief.Count;

        var aanmelding = BiStatistiek.Aanmeldrecentheid(b);
        Assert.Equal(actief, aanmelding.Sum(x => x.Aantal));
        Assert.Equal(new[] { "schaal-1", "schaal-2", "schaal-3", "schaal-4", "neutraal" }, aanmelding.Select(x => x.Kleur));
        Assert.Contains(Kpis.Personen(aanmelding[0].Kpi!, b), p => p.Sleutel == "ad:an.peeters");
        Assert.Contains(Kpis.Personen(aanmelding[1].Kpi!, b), p => p.Sleutel == "ad:bo.janssens");
        Assert.Contains(Kpis.Personen(aanmelding[3].Kpi!, b), p => p.Sleutel == "ad:cas.vos");

        var wachtwoord = BiStatistiek.Wachtwoordleeftijd(b);
        Assert.Equal(b.Actief.Count(p => p.Ad is not null), wachtwoord.Sum(x => x.Aantal));
        Assert.Equal(new[] { 1, 0, 1, 1 }, wachtwoord.Take(4).Select(x => x.Aantal));

        var mfa = BiStatistiek.Mfa(b);
        Assert.Equal(actief, mfa.Sum(x => x.Aantal));
        Assert.Equal(actief, mfa.Single(x => x.Kpi!.Sleutel == "mfa:onbekend").Aantal); // geen beveiligingsgegevens: onbekend, nooit "in orde"

        var kantoren = BiStatistiek.PerKantoor(b);
        Assert.Equal(actief, kantoren.Sum(x => x.Aantal));
        Assert.Contains(kantoren, k => k.Naam == "bibliotheek" && k.Aantal == 1);

        var maanden = BiStatistiek.AangemaaktPerMaand(b);
        Assert.Equal(12, maanden.Count);
        Assert.Equal("aangemaakt:2026-10", maanden[^1].Kpi!.Sleutel);
        Assert.True(maanden[^1].Totaal >= 1);
    }

    [Fact]
    public void DeAfdelingsfilterIsEenSamengesteldeKpi()
    {
        var nu = new DateTime(2026, 10, 31, 12, 0, 0);
        var b = new KpiBron(MetDatums(nu), TestData.Config(), nu);
        string AfdelingVan(Persoon p) => b.Config.AfdelingVoor(p.Dienst)?.Naam ?? Statistiek.GeenAfdeling;
        var afdeling = AfdelingVan(b.Data.ZoekPersoon("ad:cas.vos")!);
        var delen = BiStatistiek.Aanmeldrecentheid(b, afdeling);
        Assert.All(delen, d => Assert.EndsWith("|afdeling-alle:" + afdeling, d.Kpi!.Sleutel));
        Assert.Equal(b.Actief.Count(p => AfdelingVan(p) == afdeling), delen.Sum(d => d.Aantal));
        Assert.True(delen.Sum(d => d.Aantal) < b.Actief.Count);
        var k = delen[3].Kpi!;
        Assert.Equal(KpiLijst.Gebruikers, k.Lijst);
        Assert.Contains(" · afdeling " + afdeling, k.Label);
        Assert.Same(Kpis.Zoek(k.Sleutel)!.Sleutel, k.Sleutel);
        Assert.Null(Kpis.Zoek("aanmelding:0-30|rechtstreeks")); // geen gebruikerslijst: geen samenstelling
    }

    [Fact]
    public void GastenPerDomeinEnLogboekPerWeek()
    {
        var d = TestData.Omgeving();
        var b = new KpiBron(d, TestData.Config(), DateTime.Now);
        var gast = d.Personen.Single(p => p.IsGast);
        Assert.Equal("example.net", Kpis.GastDomein(gast));
        var domein = Assert.Single(BiStatistiek.GastenPerDomein(b));
        Assert.Equal(1, domein.Aantal);
        Assert.Same(gast, Assert.Single(Kpis.Personen(domein.Kpi!, b)));

        var nu = new DateTime(2026, 10, 31, 12, 0, 0); // zaterdag, week 44
        LogRegel R(DateTime t, string status) => new(new DateTimeOffset(t), "t", "c", "AD", "x", "d", "d", "o", new Dictionary<string, string>(), status, "");
        var weken = BiStatistiek.LogboekPerWeek(new[] { R(nu.AddHours(-1), "Gelukt"), R(nu.AddDays(-1), "Mislukt"), R(nu.AddDays(-8), "Gelukt"), R(nu.AddDays(-200), "Gelukt") }, nu);
        Assert.Equal(12, weken.Count);
        Assert.Equal("w44", weken[^1].Label);
        Assert.Equal(new[] { 1, 1 }, weken[^1].Waarden);
        Assert.Equal(new[] { 1, 0 }, weken[^2].Waarden);
        Assert.Equal(3, weken.Sum(w => w.Totaal)); // 200 dagen geleden valt erbuiten
    }

    [Fact]
    public void DeStandPerDagInDeDatabankEnHetVerschil()
    {
        Directory.CreateDirectory(_map);
        var db = new Moss365.Core.Opslag.Databank(Path.Combine(_map, "moss365.db"));
        Assert.Equal(2, db.Versie);
        var vandaag = new DateOnly(2026, 10, 31);
        db.BewaarKpiStand(vandaag.AddDays(-35), new Dictionary<string, int> { ["actief"] = 150 });
        db.BewaarKpiStand(vandaag.AddDays(-10), new Dictionary<string, int> { ["actief"] = 155 });
        db.BewaarKpiStand(vandaag, new Dictionary<string, int> { ["actief"] = 157, ["gasten"] = 4 });
        db.BewaarKpiStand(vandaag, new Dictionary<string, int> { ["actief"] = 158 }); // dezelfde dag: vervangen

        var stand = db.LeesKpiStand(vandaag.AddDays(-90));
        Assert.Equal(new[] { 150, 155, 158 }, stand["actief"].Select(p => p.Waarde));
        var verloop = new KpiVerloop(Kpis.Actief, stand["actief"]);
        Assert.Equal(8, verloop.Verschil(30, 158));
        Assert.Null(new KpiVerloop(Kpis.Gasten, stand["gasten"]).Verschil(30, 4));
        Assert.Single(db.LeesKpiStand(vandaag)["actief"]);

        var b = new KpiBron(TestData.Omgeving(), TestData.Config(), DateTime.Now);
        var dag = BiStatistiek.Stand(b);
        Assert.DoesNotContain(dag.Keys, k => k.StartsWith("gelukt", StringComparison.Ordinal) || k.StartsWith("mislukt", StringComparison.Ordinal));
        Assert.All(BiStatistiek.MetVerloop, k => Assert.True(dag.ContainsKey(k.Sleutel)));
    }

    // ---------------------------------------------------------------- taken voor een selectie (#22)

    [Fact]
    public void BeheerderVanAlleGekozenGroepen()
    {
        var d = TestData.Omgeving();
        var an = d.ZoekPersoon("ad:an.peeters")!;
        var groepen = d.Index.Groepen.Where(g => g.Naam is "gg_eigen" or "gg_wifiIntern").ToList();
        var u = BulkPlannen.BeheerderVoorGroepen(d, groepen, an);
        Assert.Equal(2, u.Plan.Count);
        Assert.All(u.Plan, w =>
        {
            Assert.Equal(Actie.AdKenmerkenZetten, w.Actie);
            Assert.Equal(an.Ad!.DistinguishedName, w.P("managedBy"));
        });
    }

    [Fact]
    public void UitstroomInplannenVoorDeGekozenMaarNooitVoorGasten()
    {
        var d = TestData.Omgeving();
        var gast = d.Personen.Single(p => p.IsGast);
        var personen = new[] { d.ZoekPersoon("ad:an.peeters")!, d.ZoekPersoon("ad:cas.vos")!, gast };
        var wanneer = new DateTimeOffset(2026, 11, 30, 17, 0, 0, TimeSpan.FromHours(1));

        var gepland = BulkPlannen.UitstroomInplannen(personen, wanneer, "beheerder@test.be");
        Assert.Equal(2, gepland.Count);
        Assert.All(gepland, g =>
        {
            Assert.Equal(GeplandSoort.Uitstroom, g.Soort);
            Assert.Equal(wanneer, g.Wanneer);
            Assert.Equal(new DateOnly(2026, 11, 30), g.Opties!.Datum);
        });
        Assert.DoesNotContain(gepland, g => g.PersoonSleutel == gast.Sleutel);
    }
}
