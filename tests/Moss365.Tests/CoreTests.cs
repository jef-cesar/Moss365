using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Xunit;

namespace Moss365.Tests;

public class CsvLezerTests
{
    [Fact]
    public void LeestAanhalingstekensScheidingstekensEnRegeleindenInEenVeld()
    {
        var tekst = "﻿\"Naam\";\"Opmerking\";\"Getal\"\r\n\"Jan\";\"een; twee\";\"1\"\r\n\"Els\";\"regel 1\nregel 2 met \"\"citaat\"\"\";\"2\"\r\n";
        var rijen = CsvLezer.Lees(tekst);

        Assert.Equal(2, rijen.Count);
        Assert.Equal("Jan", rijen[0]["Naam"]);
        Assert.Equal("een; twee", rijen[0]["Opmerking"]);
        Assert.Equal("regel 1\nregel 2 met \"citaat\"", rijen[1]["Opmerking"]);
        Assert.Equal("2", rijen[1]["getal"]);
    }

    [Fact]
    public void LeegBestandGeeftGeenRijen()
    {
        Assert.Empty(CsvLezer.Lees(string.Empty));
        Assert.Empty(CsvLezer.Lees("\"Kop\"\r\n"));
    }

    [Fact]
    public void OntbrekendeVeldenWordenLeeg()
    {
        var rijen = CsvLezer.Lees("A;B;C\n1;2\n");
        Assert.Equal(string.Empty, rijen[0]["C"]);
    }
}

public class DnHulpTests
{
    [Theory]
    [InlineData("CN=Jan,OU=TD,OU=Personeel,DC=x,DC=be", "Personeel/TD/Jan")]
    [InlineData("OU=Zorg\\, Welzijn,DC=x,DC=be", "Zorg, Welzijn")]
    [InlineData("", "")]
    public void OuPad(string dn, string verwacht) => Assert.Equal(verwacht, DnHulp.OuPad(dn));

    [Fact]
    public void OuderEnNaam()
    {
        Assert.Equal("OU=TD,DC=x,DC=be", DnHulp.Ouder("CN=Jan,OU=TD,DC=x,DC=be"));
        Assert.Equal("TD", DnHulp.OuPadVanObject("CN=Jan,OU=TD,DC=x,DC=be"));
        Assert.Equal("gg_ICT", DnHulp.Naam("CN=gg_ICT,OU=GROEPEN,DC=x"));
    }

    [Fact]
    public void ValtOnderHoudtRekeningMetSubOus()
    {
        var paden = new[] { "GEMEENTEBESTUUR/GEBRUIKERS" };
        Assert.True(DnHulp.ValtOnder("GEMEENTEBESTUUR/GEBRUIKERS", paden));
        Assert.True(DnHulp.ValtOnder("gemeentebestuur/gebruikers/Extra", paden));
        Assert.False(DnHulp.ValtOnder("GEMEENTEBESTUUR/GEBRUIKERSOUD", paden));
    }
}

public class InventarisLaderTests : IDisposable
{
    private readonly string _basis = Path.Combine(Path.GetTempPath(), "kb-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_basis))
        {
            Directory.Delete(_basis, recursive: true);
        }
    }

    private string Map(string naam, params (string Bestand, string Inhoud)[] bestanden)
    {
        var map = Path.Combine(_basis, naam);
        Directory.CreateDirectory(map);
        foreach (var (bestand, inhoud) in bestanden)
        {
            File.WriteAllText(Path.Combine(map, bestand), inhoud);
        }

        return map;
    }

    [Fact]
    public void NeemtPerBestandDeNieuwsteMap()
    {
        var oud = Map("Inventaris-20261015-1546",
            (InventarisLader.EntraGebruikers, "\"Id\";\"UserPrincipalName\"\n\"oud\";\"a@example.com\"\n"),
            (InventarisLader.AdGebruikers, "\"SamAccountName\"\n\"oud\"\n"));
        var nieuw = Map("Inventaris-20261016-1343",
            (InventarisLader.AdGebruikers, "\"SamAccountName\"\n\"nieuw1\"\n\"nieuw2\"\n"));
        Map("IetsAnders", (InventarisLader.AdGebruikers, "\"SamAccountName\"\n\"fout\"\n"));

        var mappen = InventarisLader.ZoekMappen(_basis);
        Assert.Equal(new[] { nieuw, oud }, mappen);

        var data = InventarisLader.Laad(mappen);
        Assert.Equal(new[] { "nieuw1", "nieuw2" }, data.AdGebruikers.Select(a => a.SamAccountName));
        Assert.Single(data.EntraGebruikers);
        Assert.Equal(nieuw, data.Bronnen.Single(b => b.Bestand == InventarisLader.AdGebruikers).Map);
        Assert.Equal(oud, data.Bronnen.Single(b => b.Bestand == InventarisLader.EntraGebruikers).Map);
        Assert.Null(data.Bronnen.Single(b => b.Bestand == InventarisLader.EntraGroepen).Map);
    }

    [Fact]
    public void OnbestaandeBasismapGeeftGeenMappen() => Assert.Empty(InventarisLader.ZoekMappen(Path.Combine(_basis, "bestaat-niet")));

    [Fact]
    public void KoppeltAdEnEntraOpSamAccountName()
    {
        var data = TestData.Omgeving();
        var bo = data.ZoekPersoon("ad:bo.janssens")!;
        Assert.NotNull(bo.Entra);
        Assert.Equal("e-bo", bo.Entra!.Id);
        Assert.Equal("AD + Entra", bo.Bron);

        Assert.Equal("Alleen AD", data.ZoekPersoon("ad:dirk.intern")!.Bron);
        Assert.Equal("Alleen Entra", data.ZoekPersoon("entra:e-info")!.Bron);
        Assert.True(data.ZoekPersoon("entra:e-gast")!.IsGast);
    }

    [Fact]
    public void BewaartSpatiesInKenmerkenMaarNietInSleutels()
    {
        var data = TestData.Omgeving();
        Assert.Equal("cultuur ", data.ZoekPersoon("ad:bo.janssens")!.Ad!.Department);
        Assert.Equal("cultuur", data.ZoekPersoon("ad:bo.janssens")!.Dienst);
    }

    [Fact]
    public void GroepenVanEenPersoon()
    {
        var data = TestData.Omgeving();
        var an = data.ZoekPersoon("ad:an.peeters")!;
        Assert.Equal(new[] { "Domain Admins", "gg_rww_foxbeleid", "gg_wifiIntern" }, data.AdGroepenVan(an).Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(5, data.CloudGroepenVan(an).Count());
    }
}

public class AdEntraVergelijkingTests
{
    private static IReadOnlyList<Verschil> Verschillen() => AdEntraVergelijking.Vergelijk(TestData.Omgeving(), TestData.Config());

    [Fact]
    public void OntbrekendInEntraMetReden()
    {
        var v = Verschillen().Where(x => x.Soort == VerschilSoort.OntbreektInEntra).ToList();
        var dirk = Assert.Single(v);
        Assert.Equal("dirk.intern", dirk.Persoon.Ad!.SamAccountName);
        Assert.Contains("niet routeerbaar", dirk.Toelichting);
        Assert.Contains("nooit aangemeld", dirk.Toelichting);
    }

    [Fact]
    public void BuitenDeGesynchroniseerdeOusGeenMelding()
        => Assert.DoesNotContain(Verschillen(), x => x.Persoon.Ad?.SamAccountName == "vpn.leverancier");

    [Fact]
    public void UitgeschakeldInAdMaarActiefInEntra()
    {
        var v = Assert.Single(Verschillen(), x => x.Soort == VerschilSoort.StatusVerschilt);
        Assert.Equal("oud.account", v.Persoon.Ad!.SamAccountName);
    }

    [Fact]
    public void SpatieAchteraanIsGeenVerschilHoofdlettersWel()
    {
        var verschillen = Verschillen();
        Assert.DoesNotContain(verschillen, x => x.Persoon.Ad?.SamAccountName == "bo.janssens" && x.Kenmerk.StartsWith("Dienst"));
        var cas = Assert.Single(verschillen, x => x.Persoon.Ad?.SamAccountName == "cas.vos" && x.Kenmerk == "UPN");
        Assert.Equal("Alleen hoofdletters verschillen", cas.Toelichting);
    }

    [Fact]
    public void ZonderAdOfEntraGeenVergelijking()
        => Assert.Empty(AdEntraVergelijking.Vergelijk(new InventarisData(), TestData.Config()));
}

public class SpiegelgebruikerTests
{
    private static IReadOnlyList<SpiegelRegel> Regels(string referentie = "ad:an.peeters", string doel = "ad:bo.janssens")
    {
        var data = TestData.Omgeving();
        return Spiegelgebruiker.Bereken(data, TestData.Config(), data.ZoekPersoon(referentie)!, data.ZoekPersoon(doel)!);
    }

    private static SpiegelRegel Regel(IReadOnlyList<SpiegelRegel> regels, string groep) => Assert.Single(regels, r => r.Groep == groep);

    [Fact]
    public void AdGroepen()
    {
        var r = Regels();
        Assert.Equal(SpiegelStatus.ReedsLid, Regel(r, "gg_wifiIntern").Status);
        var fox = Regel(r, "gg_rww_foxbeleid");
        Assert.Equal(SpiegelStatus.Toevoegen, fox.Status);
        Assert.Equal(SchrijfRoute.ActiveDirectory, fox.Route);
        Assert.Equal(string.Empty, fox.Opmerking);
        Assert.Equal(SpiegelStatus.AlleenBijDoel, Regel(r, "gg_eigen").Status);
    }

    [Fact]
    public void BevoorrechteGroepWordtNooitGekopieerd()
    {
        var da = Regel(Regels(), "Domain Admins");
        Assert.Equal(SpiegelStatus.NietKopieren, da.Status);
        Assert.Equal(SchrijfRoute.Geen, da.Route);
    }

    [Fact]
    public void CloudGroepenKrijgenDeJuisteRoute()
    {
        var r = Regels();
        Assert.Equal(SpiegelStatus.ReedsLid, Regel(r, "DNST_CULTUUR").Status);
        Assert.Equal(SchrijfRoute.Exchange, Regel(r, "SG-SM-info-Full").Route);
        Assert.Contains("gedeeld postvak", Regel(r, "SG-SM-info-Full").Opmerking);
        Assert.Equal(SchrijfRoute.Exchange, Regel(r, "Gemeentehuis").Route);
        Assert.Equal(SchrijfRoute.ActiveDirectory, Regel(r, "Gesynchroniseerd").Route);
        Assert.Equal(SpiegelStatus.AlleenBijDoel, Regel(r, "WKG_Eigen").Status);
    }

    [Fact]
    public void DynamischeGroepIsAutomatisch()
    {
        var alle = Regel(Regels("ad:an.peeters", "ad:cas.vos"), "Alle gebruikers");
        Assert.Equal(SpiegelStatus.Automatisch, alle.Status);
        Assert.Equal(SchrijfRoute.Geen, alle.Route);
    }

    [Fact]
    public void DoelZonderEntraKanGeenCloudgroepenKrijgen()
    {
        var r = Regels("ad:an.peeters", "ad:dirk.intern");
        Assert.All(r.Where(x => x.Laag == "Cloud" && x.Status != SpiegelStatus.Automatisch),
            x => Assert.Equal(SpiegelStatus.NietKopieren, x.Status));
        Assert.Equal(SpiegelStatus.Toevoegen, Regel(r, "gg_wifiIntern").Status);
    }
}

public class OrganisatieConfigTests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-cfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    [Fact]
    public void ZoektDienstZonderTeLettenOpSpatiesEnHoofdletters()
    {
        var c = TestData.Config();
        Assert.Equal("cultuur", c.ZoekDienst("  Cultuur ")!.Department);
        Assert.Equal("Vrije tijd", c.AfdelingVoor("cultuur ")!.Naam);
        Assert.Null(c.AfdelingVoor("gezinszorg"));
        Assert.Null(c.ZoekDienst(""));
    }

    [Fact]
    public void GebruikersbestandKrijgtVoorrangEnBewarenBehoudtSpecialeTekens()
    {
        var standaard = Path.Combine(_map, "standaard.json");
        var eigen = Path.Combine(_map, "eigen", "organisatie.json");
        ConfigOpslag.Bewaar(TestData.Config(), standaard);

        var (c1, bron1) = ConfigOpslag.Laad(standaard, eigen);
        Assert.Equal(standaard, bron1);

        c1.Diensten.Add(new Dienst { Department = "financiën", Afdeling = "Burger en welzijn" });
        ConfigOpslag.Bewaar(c1, eigen);
        Assert.Contains("financiën", File.ReadAllText(eigen));

        var (c2, bron2) = ConfigOpslag.Laad(standaard, eigen);
        Assert.Equal(eigen, bron2);
        Assert.Equal("Burger en welzijn", c2.AfdelingVoor("Financiën")!.Naam);
    }

    [Fact]
    public void ZonderBestandenEenLegeConfiguratie()
    {
        var (c, bron) = ConfigOpslag.Laad(Path.Combine(_map, "x.json"), null);
        Assert.Null(bron);
        Assert.Empty(c.Diensten);
    }

    [Fact]
    public void DeMeegeleverdeOrganisatieIsGeldig()
    {
        var pad = Path.Combine(AppContext.BaseDirectory, "config", "organisatie.json");
        var (c, bron) = ConfigOpslag.Laad(pad, null);
        Assert.Equal(pad, bron);
        Assert.Equal("Contoso", c.Company);
        Assert.NotEmpty(c.GesynchroniseerdeOus);
        Assert.Equal(4, c.Afdelingen.Count);
        Assert.All(c.Diensten.Where(d => d.Afdeling is not null),
            d => Assert.Contains(c.Afdelingen, a => a.Naam == d.Afdeling));
        Assert.DoesNotContain(c.Diensten.GroupBy(d => OrganisatieConfig.Normaliseer(d.Department)), g => g.Count() > 1);
        Assert.Equal("Social services", c.AfdelingVoor("home care")!.Naam);
        Assert.Equal("IT", c.AfdelingVoor("Helpdesk ")!.Naam);
    }
}

public class OrganisatieAnalyseTests
{
    [Fact]
    public void HerkentSchrijfvariantenEnOnbekendeDiensten()
    {
        var c = TestData.Config();
        var lijst = OrganisatieAnalyse.DienstenInData(TestData.Omgeving(), c);

        var exact = Assert.Single(lijst, x => x.Waarde == "cultuur");
        Assert.True(exact.InConfig);
        Assert.False(exact.IsSchrijfvariant);
        Assert.True(Assert.Single(lijst, x => x.Waarde == "cultuur ").IsSchrijfvariant);

        c.Diensten.RemoveAll(d => d.Department == "gezinszorg");
        Assert.False(Assert.Single(OrganisatieAnalyse.DienstenInData(TestData.Omgeving(), c), x => x.Waarde == "gezinszorg").InConfig);
    }

    [Fact]
    public void TeltGebruikersPerDienst()
    {
        var aantallen = OrganisatieAnalyse.AantalPerDienst(TestData.Omgeving(), TestData.Config());
        Assert.Equal(2, aantallen["cultuur"]);
        Assert.Equal(1, aantallen["gezinszorg"]);
    }
}

public sealed class OudeInstallatieTests : IDisposable
{
    private readonly string _basis = Path.Combine(Path.GetTempPath(), "m365-oud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_basis))
        {
            Directory.Delete(_basis, true);
        }
    }

    [Fact]
    public void EenVorigeInstallatieWordtHerkendEnGekopieerd()
    {
        var oud = Path.Combine(_basis, "OldApp");
        var nieuw = Path.Combine(_basis, "Moss365");
        Directory.CreateDirectory(Path.Combine(oud, "Inventaris", "Inventaris-1"));
        Directory.CreateDirectory(Path.Combine(_basis, "Iets anders"));
        File.WriteAllText(Path.Combine(oud, Moss365.Core.Opslag.Databank.Bestandsnaam), "db");
        File.WriteAllText(Path.Combine(oud, "organisatie.json"), "{}");
        File.WriteAllText(Path.Combine(oud, "Inventaris", "Inventaris-1", "ad-users.csv"), "x");
        File.WriteAllText(Path.Combine(_basis, "Iets anders", Moss365.Core.Opslag.Databank.Bestandsnaam), "geen organisatie.json");

        Assert.Equal(oud, Moss365.Core.Opslag.OudeInstallatie.Zoek(_basis, nieuw));
        Assert.Equal(oud, Moss365.Core.Opslag.OudeInstallatie.ZetOverAlsNodig(_basis, nieuw));
        Assert.True(File.Exists(Path.Combine(nieuw, "Inventaris", "Inventaris-1", "ad-users.csv")));
        Assert.True(File.Exists(Path.Combine(oud, Moss365.Core.Opslag.Databank.Bestandsnaam))); // de oude blijft staan
        Assert.Contains(oud, File.ReadAllText(Path.Combine(nieuw, Moss365.Core.Opslag.OudeInstallatie.Merkteken)));

        // Een tweede keer: de nieuwe map heeft al een databank, niets te doen
        Assert.Null(Moss365.Core.Opslag.OudeInstallatie.ZetOverAlsNodig(_basis, nieuw));
        Assert.Null(Moss365.Core.Opslag.OudeInstallatie.Zoek(Path.Combine(_basis, "bestaat-niet"), nieuw));
    }
}
