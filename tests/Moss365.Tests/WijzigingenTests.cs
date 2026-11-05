using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Xunit;

namespace Moss365.Tests;

public class NaamRegelsTests
{
    [Theory]
    [InlineData("Jan", "jan")]
    [InlineData("Van den Broeck", "vandenbroeck")]
    [InlineData("D'Hondt", "dhondt")]
    [InlineData("Beliën", "belien")]
    [InlineData("  Anne-Marie ", "anne-marie")]
    [InlineData("", "")]
    public void Normaliseer(string naam, string verwacht) => Assert.Equal(verwacht, NaamRegels.Normaliseer(naam));

    [Fact]
    public void LokaalDeelEnSam()
    {
        Assert.Equal("jan.peeters", NaamRegels.LokaalDeel("Jan", "Peeters"));
        Assert.Equal("anne-marie.vandenbroeck", NaamRegels.LokaalDeel("Anne-Marie", "Van den Broeck"));
        Assert.Equal("anne-marie.vandenbro", NaamRegels.Sam("anne-marie.vandenbroeck"));
        Assert.Equal(20, NaamRegels.Sam("anne-marie.vandenbroeck").Length);
        Assert.Equal("jan.peeters2", NaamRegels.SamMetNummer("jan.peeters", 2));
        Assert.Equal("anne-marie.vandenbr2", NaamRegels.SamMetNummer("anne-marie.vandenbroeck", 2));
    }

    [Fact]
    public void StartwachtwoordIsSterkEnTelkensAnders()
    {
        var a = NaamRegels.Startwachtwoord();
        var b = NaamRegels.Startwachtwoord();
        Assert.Equal(16, a.Length);
        Assert.NotEqual(a, b);
        Assert.Contains(a, char.IsUpper);
        Assert.Contains(a, char.IsLower);
        Assert.Contains(a, char.IsDigit);
        Assert.Contains(a, c => !char.IsLetterOrDigit(c));
        Assert.DoesNotContain(a, c => "0O1lI".Contains(c));
    }
}

public class SpiegelPlanTests
{
    [Fact]
    public void ToevoegingenGaanLangsDeJuisteWeg()
    {
        var data = TestData.Omgeving();
        var an = data.ZoekPersoon("ad:an.peeters")!;
        var bo = data.ZoekPersoon("ad:bo.janssens")!;
        var regels = Spiegelgebruiker.Bereken(data, TestData.Config(), an, bo);
        var plan = PlanBouwer.Spiegel(data, regels, bo);

        var ad = Assert.Single(plan, w => w.Actie == Actie.AdGroepLidToevoegen);
        Assert.Equal(bo.Ad!.DistinguishedName, ad.Doel);
        Assert.Equal("gg_rww_foxbeleid", ad.P("groep"));
        Assert.EndsWith(TestData.Groepen, ad.P("groepDn"));

        var exchange = plan.Where(w => w.Actie == Actie.ExchangeGroepLidToevoegen).ToList();
        Assert.Equal(2, exchange.Count);
        Assert.Contains(exchange, w => w.P("groep") == "sg-sm-info-full@test.be" && w.Doel == "bo.janssens@test.be");
        Assert.Contains(exchange, w => w.P("groep") == "gemeentehuis@test.be");

        Assert.DoesNotContain(plan, w => w.P("groep") is "Domain Admins" or "Alle gebruikers" or "DNST_CULTUUR");
    }

    [Fact]
    public void GesynchroniseerdeGroepWordtDeAdGroepErachterZonderDubbel()
    {
        var data = TestData.Omgeving();
        var an = data.ZoekPersoon("ad:an.peeters")!;
        var cas = data.ZoekPersoon("ad:cas.vos")!;
        var plan = PlanBouwer.Spiegel(data, Spiegelgebruiker.Bereken(data, TestData.Config(), an, cas), cas);

        // gg_rww_foxbeleid komt zowel uit de AD-kant als via de gesynchroniseerde cloudgroep: maar een keer in het plan
        Assert.Single(plan, w => w.Actie == Actie.AdGroepLidToevoegen && w.P("groep") == "gg_rww_foxbeleid");
        Assert.Contains(plan, w => w.Actie == Actie.AdGroepLidToevoegen && w.P("groep") == "gg_wifiIntern");
        Assert.Contains(plan, w => w.Actie == Actie.GraphGroepLidToevoegen && w.P("groepId") == "g-team" && w.Doel == "e-cas");
    }
}

public class RoomPlanTests
{
    [Fact]
    public void AlleenWatVerschilt()
    {
        var data = TestData.Omgeving();
        var a = data.Rooms.Single(r => r.Smtp == "rm-vz-a@test.be");
        var plan = PlanBouwer.Room(a, RoomGewenst.UitStandaard(new RoomStandaard()));

        var verwerking = Assert.Single(plan, w => w.Actie == Actie.ExchangeRoomAgendaverwerking);
        Assert.Equal(new[] { "BookingWindowInDays", "DeleteSubject" }, verwerking.Parameters.Keys.OrderBy(k => k));
        Assert.Equal("180", verwerking.P("BookingWindowInDays"));
        Assert.Equal("False", verwerking.P("DeleteSubject"));

        var recht = Assert.Single(plan, w => w.Actie == Actie.ExchangeAgendaRecht);
        Assert.Equal("LimitedDetails", recht.HuidigeWaarde);
        Assert.Equal("Reviewer", recht.P("AccessRights"));
        Assert.DoesNotContain(plan, w => w.Actie == Actie.ExchangeRoomPlace);
    }

    [Fact]
    public void RoomDieAlGoedStaatGeeftGeenPlan()
    {
        var b = TestData.Omgeving().Rooms.Single(r => r.Smtp == "rm-vz-b@test.be");
        Assert.Empty(PlanBouwer.Room(b, RoomGewenst.UitStandaard(new RoomStandaard())));
        Assert.Empty(PlanBouwer.RoomAfwijkingen(b, new RoomStandaard()));
    }

    [Fact]
    public void PlaceWijzigen()
    {
        var b = TestData.Omgeving().Rooms.Single(r => r.Smtp == "rm-vz-b@test.be");
        var w = Assert.Single(PlanBouwer.Room(b, new RoomGewenst { Verdieping = "1", Capaciteit = 8, Gebouw = "Gemeentehuis" }));
        Assert.Equal(Actie.ExchangeRoomPlace, w.Actie);
        Assert.Equal("1", Assert.Single(w.Parameters).Value);
    }

    [Fact]
    public void AfwijkingenTonen()
    {
        var a = TestData.Omgeving().Rooms.Single(r => r.Smtp == "rm-vz-a@test.be");
        var afwijkingen = PlanBouwer.RoomAfwijkingen(a, new RoomStandaard());
        Assert.Contains(afwijkingen, x => x.StartsWith("Boekingsvenster: 600"));
        Assert.Contains(afwijkingen, x => x.StartsWith("Standaardrecht agenda: LimitedDetails"));
        Assert.Equal(3, afwijkingen.Count);
    }
}

public class NieuweGebruikerPlanTests
{
    private static NieuweGebruikerAanvraag Aanvraag(string voornaam = "Eva", string achternaam = "Smet") => new()
    {
        Populatie = "Gemeente",
        Voornaam = voornaam,
        Achternaam = achternaam,
        Dienst = "Cultuur",
        Kantoor = "gemeentehuis",
        Functie = "Medewerker",
        LicentieSku = "DESKLESSPACK",
    };

    [Fact]
    public void NamenOuEnKenmerken()
    {
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), TestData.Config(), Aanvraag(), "Geheim!123");
        Assert.True(v.KanUitgevoerdWorden, string.Join("; ", v.Fouten));
        Assert.Equal("eva.smet", v.Sam);
        Assert.Equal("eva.smet@test.be", v.Upn);
        Assert.Equal("CN=Eva Smet,OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=TEST,DC=INTERNAL", v.Dn);

        var aanmaken = v.Nu[0];
        Assert.Equal(Actie.AdGebruikerAanmaken, aanmaken.Actie);
        Assert.Equal("Geheim!123", aanmaken.Geheim);
        Assert.Equal("cultuur", aanmaken.P("department"));
        Assert.Equal("Contoso", aanmaken.P("company"));
        Assert.Equal("gemeentehuis", aanmaken.P("physicalDeliveryOfficeName"));
        Assert.Equal("cultuur.bat", aanmaken.P("scriptPath"));
        Assert.Equal("OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=TEST,DC=INTERNAL", aanmaken.P("ou"));
        Assert.False(aanmaken.Parameters.ContainsKey("l"));
        Assert.DoesNotContain(aanmaken.Parameters.Values, p => p.Contains("Geheim"));
    }

    [Fact]
    public void StandaardgroepenAlleenAlsZeBestaanEnAfhankelijkVanAanmaken()
    {
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), TestData.Config(), Aanvraag());
        var groep = Assert.Single(v.Nu, w => w.Actie == Actie.AdGroepLidToevoegen);
        Assert.Equal("gg_wifiIntern", groep.P("groep"));
        Assert.Equal(v.Nu[0].Id, groep.AfhankelijkVan);
        Assert.Contains(v.Waarschuwingen, w => w.Contains("bestaat_niet"));
    }

    [Fact]
    public void NaSynchronisatieLicentieEnAfdelingsgroep()
    {
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), TestData.Config(), Aanvraag());
        Assert.Equal(new[] { Actie.GraphKenmerkenZetten, Actie.GraphLicentieToekennen, Actie.GraphGroepLidToevoegen }, v.NaSynchronisatie.Select(w => w.Actie));
        Assert.All(v.NaSynchronisatie, w => Assert.Equal("eva.smet@test.be", w.Doel));
        Assert.Equal("sku-f3", v.NaSynchronisatie[1].P("skuId"));
        Assert.Equal("g-afd", v.NaSynchronisatie[2].P("groepId"));
    }

    [Fact]
    public void VolleLicentiepoolGeeftEenWaarschuwing()
    {
        var a = Aanvraag();
        a.LicentieSku = "SPB";
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), TestData.Config(), a);
        Assert.Contains(v.Waarschuwingen, w => w.Contains("geen vrije SPB"));
    }

    [Fact]
    public void BestaandeNaamKrijgtEenVolgnummer()
    {
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), TestData.Config(), Aanvraag("An", "Peeters"));
        Assert.Equal("an.peeters2", v.Sam);
        Assert.Equal("an.peeters2@test.be", v.Upn);
        Assert.Contains(v.Waarschuwingen, w => w.Contains("bestaat al"));
    }

    [Fact]
    public void SpiegelGroepenZonderBevoorrechteEnDynamische()
    {
        var data = TestData.Omgeving();
        var a = Aanvraag();
        a.Spiegel = data.ZoekPersoon("ad:an.peeters");
        var v = PlanBouwer.NieuweGebruiker(data, TestData.Config(), a);

        var adGroepen = v.Nu.Where(w => w.Actie == Actie.AdGroepLidToevoegen).Select(w => w.P("groep")).ToList();
        Assert.Equal(new[] { "gg_rww_foxbeleid", "gg_wifiIntern" }, adGroepen.OrderBy(g => g, StringComparer.Ordinal));
        Assert.DoesNotContain(v.NaSynchronisatie, w => w.P("groep") is "Alle gebruikers" or "Gesynchroniseerd");
        Assert.Contains(v.NaSynchronisatie, w => w.Actie == Actie.ExchangeGroepLidToevoegen && w.P("groep") == "sg-sm-info-full@test.be");
        Assert.Contains(v.NaSynchronisatie, w => w.Actie == Actie.GraphGroepLidToevoegen && w.P("groepId") == "g-team");
    }

    [Fact]
    public void VrijwilligerZonderDienstMetCityEnAnderDomein()
    {
        var a = new NieuweGebruikerAanvraag { Populatie = "Vrijwilliger", Voornaam = "Piet", Achternaam = "Goossens" };
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), TestData.Config(), a);
        Assert.True(v.KanUitgevoerdWorden, string.Join("; ", v.Fouten));
        Assert.Equal("piet.goossens@example.org", v.Upn);
        Assert.Equal("intranet", v.Nu[0].P("l"));
        Assert.Empty(v.NaSynchronisatie);
    }

    [Fact]
    public void VerplichteVeldenEnOnbekendDomein()
    {
        var c = TestData.Config();
        c.AdDomein = null;
        var v = PlanBouwer.NieuweGebruiker(TestData.Omgeving(), c, new NieuweGebruikerAanvraag { Populatie = "Gemeente" });
        Assert.False(v.KanUitgevoerdWorden);
        Assert.Contains(v.Fouten, f => f.Contains("Voornaam"));
        Assert.Contains(v.Fouten, f => f.Contains("dienst"));
        Assert.Contains(v.Fouten, f => f.Contains("kantoor"));
        Assert.Contains(v.Fouten, f => f.Contains("domein"));
        Assert.Empty(v.Nu);
    }
}

public class UitstroomPlanTests
{
    [Fact]
    public void VolgordeEnStandaardkeuzes()
    {
        var data = TestData.Omgeving();
        var an = data.ZoekPersoon("ad:an.peeters")!;
        var plan = PlanBouwer.Uitstroom(data, TestData.Config(), an, new UitstroomOpties { Datum = new DateOnly(2026, 10, 16) });

        var ad = plan.Where(w => w.Systeem == Systeem.ActiveDirectory).ToList();
        Assert.Equal(Actie.AdAccountUitschakelen, ad[0].Actie);
        Assert.Equal(Actie.AdVerplaatsen, ad[^1].Actie);
        Assert.Equal("OU=disabled_users,OU=ARCHIVE,DC=TEST,DC=INTERNAL", ad[^1].P("doelOu"));
        Assert.True(ad[^1].Geselecteerd);

        var omschrijving = Assert.Single(ad, w => w.Actie == Actie.AdKenmerkenZetten);
        Assert.StartsWith("Uit dienst 2026-10-16", omschrijving.P("description"));

        // Gewone groepen blijven standaard, bevoorrechte groepen gaan er altijd af
        Assert.False(ad.Single(w => w.P("groep") == "gg_wifiIntern").Geselecteerd);
        Assert.True(ad.Single(w => w.P("groep") == "Domain Admins").Geselecteerd);

        Assert.True(plan.Single(w => w.Actie == Actie.GraphSessiesIntrekken).Geselecteerd);
        Assert.False(plan.Single(w => w.Actie == Actie.ExchangeNaarGedeeldPostvak).Geselecteerd);
        Assert.DoesNotContain(plan, w => w.Actie == Actie.GraphKenmerkenZetten);
        Assert.DoesNotContain(plan, w => w.P("groep") == "Alle gebruikers");
    }

    [Fact]
    public void CloudAccountZonderAdWordtInEntraUitgeschakeld()
    {
        var data = TestData.Omgeving();
        var info = data.ZoekPersoon("entra:e-info")!;
        var plan = PlanBouwer.Uitstroom(data, TestData.Config(), info, new UitstroomOpties());
        var w = Assert.Single(plan, x => x.Actie == Actie.GraphKenmerkenZetten);
        Assert.Equal("false", w.P("accountEnabled"));
        Assert.DoesNotContain(plan, x => x.Systeem == Systeem.ActiveDirectory);
    }
}

public class PlanUitvoerderTests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    private sealed class NepUitvoerder : IUitvoerder
    {
        public NepUitvoerder(Systeem systeem, Func<Wijziging, WijzigingResultaat>? gedrag = null)
        {
            Systeem = systeem;
            Gedrag = gedrag ?? (w => WijzigingResultaat.Ok(w));
        }

        public Systeem Systeem { get; }
        public Func<Wijziging, WijzigingResultaat> Gedrag { get; }
        public List<List<Wijziging>> Aanroepen { get; } = new();

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren)
        {
            Aanroepen.Add(wijzigingen.ToList());
            return Task.FromResult<IReadOnlyList<WijzigingResultaat>>(wijzigingen.Select(Gedrag).ToList());
        }
    }

    private static Wijziging W(Systeem s, string doel, Guid? van = null, bool geselecteerd = true) => new()
    {
        Systeem = s,
        Actie = Actie.AdKenmerkenZetten,
        Doel = doel,
        Omschrijving = doel,
        AfhankelijkVan = van,
        Geselecteerd = geselecteerd,
    };

    [Fact]
    public async Task BundeltPerSysteemInVolgordeEnLogt()
    {
        var ad = new NepUitvoerder(Systeem.ActiveDirectory);
        var ex = new NepUitvoerder(Systeem.Exchange);
        var log = new JsonlLogboek(_map);
        var plan = new[] { W(Systeem.ActiveDirectory, "1"), W(Systeem.ActiveDirectory, "2"), W(Systeem.Exchange, "3"), W(Systeem.ActiveDirectory, "4") };
        plan[0].Geheim = "NIET-LOGGEN";

        var r = await new PlanUitvoerder(new IUitvoerder[] { ad, ex }, log, () => "tester").VoerUitAsync(plan, "test");

        Assert.Equal(4, r.Count);
        Assert.All(r, x => Assert.Equal(ResultaatStatus.Gelukt, x.Status));
        Assert.Equal(new[] { 2, 1 }, ad.Aanroepen.Select(a => a.Count));
        Assert.Single(ex.Aanroepen);

        var regels = log.Recent(10);
        Assert.Equal(4, regels.Count);
        Assert.Equal("4", regels[0].Doel);
        Assert.All(regels, x => Assert.Equal("tester", x.Gebruiker));
        Assert.DoesNotContain("NIET-LOGGEN", File.ReadAllText(Directory.GetFiles(_map).Single()));
    }

    [Fact]
    public async Task NietGeselecteerdEnAfhankelijkeStappen()
    {
        var ad = new NepUitvoerder(Systeem.ActiveDirectory, w => w.Doel == "maak" ? WijzigingResultaat.Fout(w, "kapot") : WijzigingResultaat.Ok(w));
        var maak = W(Systeem.ActiveDirectory, "maak");
        var uit = W(Systeem.ActiveDirectory, "uit", geselecteerd: false);
        var plan = new[] { maak, W(Systeem.ActiveDirectory, "groep", maak.Id), uit, W(Systeem.ActiveDirectory, "na-uit", uit.Id), W(Systeem.ActiveDirectory, "los") };

        var r = await new PlanUitvoerder(new[] { ad }, new JsonlLogboek(_map), () => "t").VoerUitAsync(plan, "test");

        Assert.Equal(ResultaatStatus.Mislukt, r[0].Status);
        Assert.Equal(ResultaatStatus.Overgeslagen, r[1].Status);
        Assert.Equal(ResultaatStatus.Overgeslagen, r[2].Status);
        Assert.Equal(ResultaatStatus.Gelukt, r[3].Status);
        Assert.Equal(4, r.Count);
        Assert.DoesNotContain(ad.Aanroepen.SelectMany(a => a), w => w.Doel is "uit" or "groep" or "na-uit");
    }

    [Fact]
    public async Task ZonderVerbindingOfBijEenUitzonderingMislukt()
    {
        var kapot = new NepUitvoerder(Systeem.Graph, _ => throw new InvalidOperationException("geen netwerk"));
        var plan = new[] { W(Systeem.Graph, "g"), W(Systeem.Exchange, "e") };
        var r = await new PlanUitvoerder(new[] { kapot }, new JsonlLogboek(_map), () => "t").VoerUitAsync(plan, "test");
        Assert.Equal("geen netwerk", r[0].Melding);
        Assert.Equal("Geen verbinding met dit systeem.", r[1].Melding);
    }
}

public class OpvolgOpslagTests : IDisposable
{
    private readonly string _pad = Path.Combine(Path.GetTempPath(), "kb-opv-" + Guid.NewGuid().ToString("N"), "opvolging.json");

    public void Dispose()
    {
        var map = Path.GetDirectoryName(_pad)!;
        if (Directory.Exists(map))
        {
            Directory.Delete(map, recursive: true);
        }
    }

    [Fact]
    public void BewaartTakenZonderGeheimen()
    {
        var opslag = new OpvolgOpslag(_pad);
        Assert.Empty(opslag.Lees());
        var w = new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphLicentieToekennen, Doel = "eva@test.be", Omschrijving = "licentie", Geheim = "geheim" };
        w.Parameters["sku"] = "SPB";
        opslag.Voegtoe(new OpvolgTaak { Upn = "eva@test.be", Omschrijving = "Eva", Wijzigingen = new() { w } });

        Assert.DoesNotContain("geheim", File.ReadAllText(_pad));
        var taak = Assert.Single(opslag.Lees());
        var terug = Assert.Single(taak.Wijzigingen);
        Assert.Equal(Actie.GraphLicentieToekennen, terug.Actie);
        Assert.Equal("SPB", terug.P("sku"));
        Assert.Equal(w.Id, terug.Id);
    }
}
