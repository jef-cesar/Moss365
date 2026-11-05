using Moss365.Core.Inventaris;
using Moss365.Core.Opslag;
using Moss365.Core.Rechten;
using Moss365.Core.Wijzigingen;
using Xunit;

namespace Moss365.Tests;

/// <summary>Kern voor 0.8: profielen (rollen, rechtentabel, de motor weigert).</summary>
public sealed class Versie08Tests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-v08-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, true);
        }
    }

    private static Wijziging W(Actie actie, string doel = "CN=x,DC=test,DC=internal", Dictionary<string, string>? p = null) => new()
    {
        Systeem = actie.ToString().StartsWith("Ad", StringComparison.Ordinal) ? Systeem.ActiveDirectory
            : actie.ToString().StartsWith("Graph", StringComparison.Ordinal) ? Systeem.Graph : Systeem.Exchange,
        Actie = actie,
        Doel = doel,
        Omschrijving = actie.ToString(),
        Parameters = p ?? new(StringComparer.OrdinalIgnoreCase),
    };

    public static IEnumerable<object[]> AlleActies() => Enum.GetValues<Actie>().Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(AlleActies))]
    public void ElkeActieHeeftEenMinimaleRol(Actie actie)
    {
        // Lezer en Auditor wijzigen nooit iets; Beheerder mag alles; Appbeheer enkel de instellingen van de app
        Assert.NotNull(Rechtentabel.Weigering(Rol.Lezer, W(actie)));
        Assert.NotNull(Rechtentabel.Weigering(Rol.Auditor, W(actie)));
        Assert.NotNull(Rechtentabel.Weigering(Rol.Appbeheer, W(actie)));
        Assert.Null(Rechtentabel.Weigering(Rol.Beheerder, W(actie)));

        var helpdesk = actie is Actie.AdWachtwoordInstellen or Actie.AdAccountOntgrendelen or Actie.AdAccountInschakelen
            or Actie.AdGroepLidToevoegen or Actie.AdGroepLidVerwijderen or Actie.GraphGroepLidToevoegen or Actie.GraphGroepLidVerwijderen
            or Actie.ExchangeGroepLidToevoegen or Actie.ExchangeGroepLidVerwijderen or Actie.GraphGastUitnodigen;
        Assert.Equal(helpdesk, Rechtentabel.Weigering(Rol.Helpdesk, W(actie)) is null);
    }

    [Fact]
    public void BevoorrechtVraagtAltijdBeheerder()
    {
        var d = TestData.Omgeving();
        var admin = d.AdGebruikers.Single(u => u.SamAccountName == "an.peeters"); // lid van Domain Admins
        var gewoon = d.AdGebruikers.Single(u => u.SamAccountName == "bo.janssens");

        Assert.Contains("an.peeters", Rechtentabel.Weigering(Rol.Helpdesk, W(Actie.AdWachtwoordInstellen, admin.DistinguishedName), d));
        Assert.Null(Rechtentabel.Weigering(Rol.Helpdesk, W(Actie.AdWachtwoordInstellen, gewoon.DistinguishedName), d));
        Assert.Null(Rechtentabel.Weigering(Rol.Beheerder, W(Actie.AdWachtwoordInstellen, admin.DistinguishedName), d));

        var da = d.AdGroepenOpDn.Values.Single(g => g.Name == "Domain Admins");
        var lid = W(Actie.AdGroepLidToevoegen, gewoon.DistinguishedName, new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = da.DistinguishedName, ["groep"] = da.Name });
        Assert.Contains("Domain Admins", Rechtentabel.Weigering(Rol.Helpdesk, lid, d));
        Assert.Null(Rechtentabel.Weigering(Rol.Beheerder, lid, d));
    }

    [Fact]
    public void RollenUitDeAanmeldingEnDeOvergang()
    {
        Assert.Equal(Rol.Helpdesk | Rol.Lezer, Rechtentabel.Bepaal(new[] { "Moss365.Helpdesk", "Moss365.Lezer", "iets.anders" }, verplicht: true));
        Assert.Equal(Rol.Alles, Rechtentabel.Bepaal(null, verplicht: false));
        Assert.Equal(Rol.Alles, Rechtentabel.Bepaal(Array.Empty<string>(), verplicht: false));
        Assert.Equal(Rol.Lezer, Rechtentabel.Bepaal(null, verplicht: true));
        Assert.Equal(Rol.Lezer, Rechtentabel.Bepaal(new[] { "iets.anders" }, verplicht: true));
        Assert.Equal("Helpdesk of Beheerder", Rechtentabel.Tekst(Rechtentabel.RollenVoor(Recht.Helpdesk)));
        Assert.Equal(5, Rechtentabel.AppRollen.Select(r => r.Id).Distinct().Count());
        Assert.True(Rechtentabel.Mag(Rol.Auditor, Recht.LogboekAlles));
        Assert.False(Rechtentabel.Mag(Rol.Helpdesk, Recht.LogboekAlles));
        Assert.False(Rechtentabel.Mag(Rol.Beheerder, Recht.Instellingen));
        Assert.True(Rechtentabel.Mag(Rol.Appbeheer, Recht.Instellingen));
    }

    private sealed class Teller(Systeem systeem) : IUitvoerder
    {
        public List<Wijziging> Gedaan { get; } = new();
        public Systeem Systeem => systeem;

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren)
        {
            Gedaan.AddRange(wijzigingen);
            return Task.FromResult<IReadOnlyList<WijzigingResultaat>>(wijzigingen.Select(w => WijzigingResultaat.Ok(w)).ToList());
        }
    }

    [Fact]
    public async Task DeMotorWeigertWatHetProfielNietToelaatEnLogtDePoging()
    {
        var ad = new Teller(Systeem.ActiveDirectory);
        var logboek = new JsonlLogboek(_map);
        var plan = new[] { W(Actie.AdAccountOntgrendelen), W(Actie.AdAccountUitschakelen) };
        var uitvoerder = new PlanUitvoerder(new[] { ad }, logboek, () => "helpdesk@test.be", w => Rechtentabel.Weigering(Rol.Helpdesk, w));

        var r = await uitvoerder.VoerUitAsync(plan, "test");
        Assert.Equal(ResultaatStatus.Gelukt, r[0].Status);
        Assert.Equal(ResultaatStatus.Mislukt, r[1].Status);
        Assert.StartsWith("Geweigerd:", r[1].Melding);
        Assert.Equal(new[] { Actie.AdAccountOntgrendelen }, ad.Gedaan.Select(w => w.Actie));
        Assert.Contains(logboek.Recent(10), l => l.Actie == nameof(Actie.AdAccountUitschakelen) && l.Status == "Mislukt" && l.Melding.Contains("Geweigerd"));

        // Een lezer voert niets uit, ook niet via een omweg
        var lezer = new PlanUitvoerder(new[] { ad }, logboek, () => "lezer@test.be", w => Rechtentabel.Weigering(Rol.Lezer, w));
        ad.Gedaan.Clear();
        await lezer.VoerUitAsync(Enum.GetValues<Actie>().Where(a => a.ToString().StartsWith("Ad", StringComparison.Ordinal)).Select(a => W(a)).ToList(), "omweg");
        Assert.Empty(ad.Gedaan);
    }

    // ---------------------------------------------------------------- gegevens bewerken (#14)

    [Fact]
    public void EenGesynchroniseerdAccountWijzigtInAdMetDeVorigeWaarden()
    {
        var d = TestData.Omgeving();
        var an = d.ZoekPersoon("ad:an.peeters")!;
        var gewenst = GebruikerGewenst.Van(an);
        gewenst.Functie = "Coordinator";
        gewenst.Kantoor = "bibliotheek";
        gewenst.Dienst = "GEZINSZORG "; // gespeld zoals in de organisatie

        var u = KenmerkPlannen.Gebruiker(d, TestData.Config(), an, gewenst);
        var w = u.Plan[0];
        Assert.Equal(Actie.AdKenmerkenZetten, w.Actie);
        Assert.Equal(an.Ad!.DistinguishedName, w.Doel);
        Assert.Equal("Coordinator", w.P("title"));
        Assert.Equal("bibliotheek", w.P("physicalDeliveryOfficeName"));
        Assert.Equal("gezinszorg", w.P("department"));
        Assert.Equal("cultuur", w.Vorige!["department"]);
        Assert.Contains("synchronisatie", w.Omschrijving);
        Assert.DoesNotContain("displayName", w.Parameters.Keys); // niet gewijzigd: niet in het plan

        // Andere dienst = andere afdeling: de afdelingsgroep AFD_VRIJETIJD komt mee in het plan (of de reden waarom niet)
        Assert.True(u.Plan.Skip(1).Any(x => x.P("groep") == "AFD_VRIJETIJD") || u.Fouten.Any(f => f.Contains("AFD_VRIJETIJD")));

        // Ongedaan maken zet de vorige waarden terug
        var terug = Terugdraaien.Plan(new[] { new LogRegel(DateTimeOffset.Now, "t", "c", "AD", w.Actie.ToString(), w.Doel, w.DoelNaam, w.Omschrijving,
            w.Parameters, "Gelukt", "", Guid.NewGuid(), w.Id, w.Vorige) });
        Assert.Equal("cultuur", Assert.Single(terug.Plan).P("department"));
    }

    [Fact]
    public void EenCloudaccountWijzigtViaGraph()
    {
        var d = TestData.Omgeving();
        var info = d.Personen.Single(p => p.Entra?.Id == "e-info");
        var u = KenmerkPlannen.Gebruiker(d, TestData.Config(), info, new GebruikerGewenst { Functie = "Algemeen adres", Omschrijving = "kan niet" });
        var w = Assert.Single(u.Plan);
        Assert.Equal(Actie.GraphKenmerkenZetten, w.Actie);
        Assert.Equal("e-info", w.Doel);
        Assert.Equal("Algemeen adres", w.P("jobTitle"));
        Assert.Contains("enkel in AD", w.Waarschuwing);

        Assert.Contains("niets gewijzigd", KenmerkPlannen.Gebruiker(d, TestData.Config(), info, GebruikerGewenst.Van(info)).Fouten[0]);
    }

    [Fact]
    public void EenVerschilRechtzettenInAd()
    {
        var d = TestData.Omgeving();
        var bo = d.ZoekPersoon("ad:bo.janssens")!; // dienst "cultuur " met een spatie
        var v = new Moss365.Core.Analyse.Verschil(bo, Moss365.Core.Analyse.VerschilSoort.KenmerkVerschilt, "Dienst (Department)", "cultuur ", "cultuur", "");
        var w = Assert.Single(KenmerkPlannen.Rechtzetten(d, TestData.Config(), v).Plan);
        Assert.Equal("cultuur", w.P("department"));
        Assert.Equal("cultuur ", w.Vorige!["department"]);
    }

    // ---------------------------------------------------------------- nieuwe groep (#13)

    private static Moss365.Core.Configuratie.OrganisatieConfig ConfigMetGroepenOu()
    {
        var c = TestData.Config();
        c.GroepenOu = "GEMEENTEBESTUUR/GROEPEN";
        return c;
    }

    [Fact]
    public void NieuweAdGroepMetBeheerderEnLeden()
    {
        var d = TestData.Omgeving();
        var u = NieuweGroepPlannen.Plan(d, ConfigMetGroepenOu(), new NieuweGroepAanvraag
        {
            Soort = NieuweGroepSoort.AdBeveiliging, Naam = "gg_test_map", Omschrijving = "Rechten op de testmap",
            Beheerder = "ad:an.peeters", Leden = new() { "ad:bo.janssens", "ad:cas.vos" },
        });
        Assert.Empty(u.Fouten);
        var aanmaken = u.Plan[0];
        Assert.Equal(Actie.AdGroepAanmaken, aanmaken.Actie);
        Assert.StartsWith("CN=gg_test_map,OU=GROEPEN,OU=GEMEENTEBESTUUR,", aanmaken.Doel);
        Assert.Equal(NieuweGroepPlannen.GlobalBeveiliging.ToString(System.Globalization.CultureInfo.InvariantCulture), aanmaken.P("groupType"));
        Assert.Equal(d.ZoekPersoon("ad:an.peeters")!.Ad!.DistinguishedName, aanmaken.P("managedBy"));
        Assert.Null(aanmaken.Waarschuwing);
        Assert.Equal(2, u.Plan.Count(w => w.Actie == Actie.AdGroepLidToevoegen && w.AfhankelijkVan == aanmaken.Id && w.P("groepDn") == aanmaken.Doel));
        Assert.Contains("Beheerder", Rechtentabel.Weigering(Rol.Helpdesk, aanmaken));
    }

    [Fact]
    public void NieuweGroepWordtGecontroleerd()
    {
        var d = TestData.Omgeving();
        var c = ConfigMetGroepenOu();
        NieuweGroepAanvraag A(string naam, NieuweGroepSoort soort = NieuweGroepSoort.AdBeveiliging, string mail = "") => new() { Naam = naam, Soort = soort, Mail = mail };

        Assert.Contains("bestaat al", NieuweGroepPlannen.Plan(d, c, A("gg_wifiIntern")).Fouten[0]);
        Assert.Contains("bevoorrechte", NieuweGroepPlannen.Plan(d, c, A("Test Admins")).Fouten[0]);
        Assert.Contains("dynamische", NieuweGroepPlannen.Plan(d, c, A("Alle gebruikers", NieuweGroepSoort.CloudBeveiliging)).Fouten.Last());
        Assert.Contains("al in gebruik", NieuweGroepPlannen.Plan(d, c, A("WKG_Info", NieuweGroepSoort.Microsoft365, "gemeentehuis@test.be")).Fouten[0]);
        Assert.Contains("OU voor nieuwe groepen", NieuweGroepPlannen.Plan(d, TestData.Config(), A("gg_nieuw")).Fouten[0]);
        Assert.Contains("naamgeving", NieuweGroepPlannen.Plan(d, c, A("Projectgroep")).Plan[0].Waarschuwing);
    }

    [Fact]
    public void NieuweCloudgroepEnMailgroep()
    {
        var d = TestData.Omgeving();
        var m365 = Assert.Single(NieuweGroepPlannen.Plan(d, TestData.Config(), new NieuweGroepAanvraag
        {
            Soort = NieuweGroepSoort.Microsoft365, Naam = "WKG_Feest", Mail = "feest@test.be", Beheerder = "ad:an.peeters", Leden = new() { "ad:bo.janssens" },
        }).Plan);
        Assert.Equal(Actie.GraphGroepAanmaken, m365.Actie);
        Assert.Equal("M365", m365.P("soort"));
        Assert.Equal("feest", m365.P("mailNickname"));
        Assert.Equal("e-an", m365.P("eigenaar"));
        Assert.Equal("e-bo", m365.P("leden"));

        var dl = NieuweGroepPlannen.Plan(d, TestData.Config(), new NieuweGroepAanvraag
        {
            Soort = NieuweGroepSoort.Distributie, Naam = "Personeelsfeest", Mail = "personeelsfeest@test.be", Leden = new() { "ad:cas.vos" },
        }).Plan;
        Assert.Equal("Distribution", dl[0].P("Type"));
        Assert.Equal(Actie.ExchangeGroepLidToevoegen, dl[1].Actie);
        Assert.Equal("personeelsfeest@test.be", dl[1].P("groep"));
        Assert.Equal(dl[0].Id, dl[1].AfhankelijkVan);
    }

    // ---------------------------------------------------------------- taken voor een selectie (#10)

    [Fact]
    public void GekozenGebruikersInEenGroepEnEenKenmerkZetten()
    {
        var d = TestData.Omgeving();
        var personen = new[] { "ad:an.peeters", "ad:bo.janssens", "ad:cas.vos" }.Select(k => d.ZoekPersoon(k)!).ToList();
        var groep = d.Index.Groepen.Single(g => g.Naam == "gg_eigen");

        var u = BulkPlannen.GebruikersInGroep(d, personen, groep, toevoegen: true);
        Assert.Equal(2, u.Plan.Count); // bo is al lid
        Assert.Contains(u.Fouten, f => f.Contains("al lid"));
        Assert.All(u.Plan, w => Assert.Equal(Actie.AdGroepLidToevoegen, w.Actie));

        var k = BulkPlannen.Kenmerk(d, TestData.Config(), personen, "Kantoor", "bibliotheek");
        Assert.Equal(3, k.Plan.Count(w => w.Actie == Actie.AdKenmerkenZetten && w.P("physicalDeliveryOfficeName") == "bibliotheek"));
    }

    [Fact]
    public void ToegangTotDeGekozenPostvakkenViaDeToegangsgroep()
    {
        var d = TestData.Omgeving();
        var info = d.Postvakken.Single(p => p.Smtp == "info@test.be");
        var bo = d.ZoekPersoon("ad:bo.janssens")!;
        var u = BulkPlannen.ToegangTotPostvakken(d, d.Postvakken.Where(p => p.IsGedeeld), bo);
        Assert.Contains(u.Plan, w => w.P("groepNaam").StartsWith("SG-SM-info", StringComparison.OrdinalIgnoreCase) || w.P("groep").StartsWith("SG-SM-info", StringComparison.OrdinalIgnoreCase));

        var verbergen = BulkPlannen.Verbergen(new[] { info }, !info.Verborgen);
        Assert.Single(verbergen.Plan);
        Assert.Contains("al", BulkPlannen.Verbergen(new[] { info }, info.Verborgen).Fouten[0]);
    }

    // ---------------------------------------------------------------- databank (#17)

    [Fact]
    public void DeOudeBestandenWordenEenmaligOvergezet()
    {
        Directory.CreateDirectory(Path.Combine(_map, "logboek"));
        var oud = new JsonlLogboek(Path.Combine(_map, "logboek"));
        oud.Schrijf(new LogRegel(DateTimeOffset.Now.AddDays(-40), "a@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "eerste", "o", new Dictionary<string, string>(), "Gelukt", ""));
        oud.Schrijf(new LogRegel(DateTimeOffset.Now.AddDays(-1), "a@test.be", "c", "AD", "AdAccountOntgrendelen", "d", "tweede", "o", new Dictionary<string, string>(), "Gelukt", ""));
        File.WriteAllText(Path.Combine(_map, "gepland.json"), "[]");
        File.WriteAllText(Path.Combine(_map, "opvolging.json"), "[]");
        File.WriteAllText(Path.Combine(_map, "weergave.json"), "{\"Tabellen\":{\"groepen\":{\"Sorteer\":\"soort\"}}}");
        File.WriteAllText(Path.Combine(_map, "verbinding.json"), "{\"AdServer\":\"dc.test.internal\"}");

        var db = new Databank(Path.Combine(_map, Databank.Bestandsnaam));
        var meldingen = Overzetten.VanBestanden(db, _map);
        Assert.Contains(meldingen, m => m.Contains("2 regels"));
        Assert.Equal(new[] { "tweede", "eerste" }, db.LeesLog(10).Select(r => r.DoelNaam));
        Assert.Contains("dc.test.internal", db.LeesDocument("verbinding"));
        Assert.Contains("soort", db.LeesDocument("weergave"));
        Assert.True(File.Exists(Path.Combine(_map, "gepland.json.bak")));
        Assert.False(File.Exists(Path.Combine(_map, "gepland.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_map, "logboek"), "*.jsonl"));

        // Opnieuw starten: niets dubbel
        Assert.Empty(Overzetten.VanBestanden(new Databank(db.Pad), _map));
        Assert.Equal(2, db.AantalLog());
    }

    [Fact]
    public void HetSchemaHeeftVersiesEnWordtBijgewerkt()
    {
        var pad = Path.Combine(_map, "oud.db");
        Directory.CreateDirectory(_map);
        // Een lege databank van "vroeger" (versie 0)
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={pad};Pooling=False"))
        {
            c.Open();
        }

        var db = new Databank(pad);
        Assert.Equal(Databank.Migraties.Count, db.Versie);
        db.BewaarDocument("x", "1");
        db.BewaarDocument("x", "2");
        Assert.Equal("2", db.LeesDocument("x"));

        // Nog eens openen: niets opnieuw, gegevens blijven
        Assert.Equal(Databank.Migraties.Count, new Databank(pad).Versie);
        Assert.Equal("2", new Databank(pad).LeesDocument("x"));

        db.SchrijfToegang(new ToegangRegel(DateTimeOffset.Now, "a@test.be", "Export", "Gebruikers: 12 rijen"));
        Assert.Equal("Export", Assert.Single(db.LeesToegang(10)).Wat);
    }

    [Fact]
    public async Task HetLogboekInDeDatabankIsEnkelToevoegen()
    {
        var db = new Databank(Path.Combine(_map, "log.db"));
        var log = new DatabankLogboek(db);
        var plan = new[] { W(Actie.AdAccountOntgrendelen) };
        plan[0].Geheim = "Nooit-In-Het-Logboek-1";
        await new PlanUitvoerder(new[] { new Teller(Systeem.ActiveDirectory) }, log, () => "t").VoerUitAsync(plan, "ctx");
        Assert.Single(log.Recent(10));
        Assert.DoesNotContain("Nooit-In-Het-Logboek-1", db.LeesLog(10).Single().ToString());
        Assert.Equal(1, db.AantalLog(DateTimeOffset.Now.AddMinutes(1)));
        Assert.Equal(0, db.AantalLog(DateTimeOffset.Now.AddDays(-1)));
    }
}
