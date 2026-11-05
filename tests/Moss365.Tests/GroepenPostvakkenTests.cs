using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Xunit;

namespace Moss365.Tests;

/// <summary>Index over AD, Entra en Exchange heen, en de plannen voor groepen en gedeelde postvakken.</summary>
public sealed class GroepenPostvakkenTests
{
    private readonly InventarisData _d = TestData.Omgeving();

    private ObjectIndex Index => _d.Index;

    private Groep G(string naamOfAdres) => Index.Groep(Index.Zoek(naamOfAdres)!.Sleutel)!;

    private ObjectRef Persoon(string sam) => Index.Ref(_d.ZoekPersoon("ad:" + sam)!);

    // ---------------------------------------------------------------- index

    [Fact]
    public void GesynchroniseerdeGroepIsEenGroepMetTweeKanten()
    {
        var g = G("gg_rww_foxbeleid");
        Assert.NotNull(g.Ad);
        Assert.NotNull(g.Cloud);
        Assert.Equal("AD + Entra", g.Lagen);
        Assert.Equal(SchrijfRoute.ActiveDirectory, g.Route);
        Assert.Same(g, G("g-ad"));
    }

    [Fact]
    public void ExchangeGroepWordtGekoppeldOpAdres()
    {
        var g = G("gemeentehuis@test.be");
        Assert.NotNull(g.Cloud);
        Assert.NotNull(g.Exo);
        Assert.Equal(SchrijfRoute.Exchange, g.Route);
        Assert.Equal("Room list", G("rl-gh").SoortTekst);
    }

    [Fact]
    public void HerkentAdressenDnsEnIds()
    {
        Assert.Equal(ObjectSoort.Postvak, Index.Zoek("info@test.be")!.Soort);
        Assert.Equal(ObjectSoort.Room, Index.Zoek("rm-vz-a@test.be")!.Soort);
        Assert.Equal(ObjectSoort.Gebruiker, Index.Zoek(TestData.Dn("cas.vos", TestData.Ocmw))!.Soort);
        Assert.Equal(ObjectSoort.Gebruiker, Index.Zoek("TEST\\an.peeters")!.Soort);
        Assert.Equal(ObjectSoort.Groep, Index.Zoek("g-team")!.Soort);
        Assert.Null(Index.Zoek("bestaat.niet@test.be"));
        Assert.Equal(ObjectSoort.Onbekend, Index.Ref("bestaat.niet@test.be").Soort);
    }

    [Fact]
    public void LedenPerLaagEnGenesteGroepen()
    {
        var eigen = G("gg_eigen");
        var leden = Index.LedenVan(eigen);
        Assert.Contains(leden, l => l.Laag == "AD" && l.Object.Soort == ObjectSoort.Gebruiker && l.Object.Sleutel == "ad:bo.janssens");
        Assert.Contains(leden, l => l.Laag == "AD" && l.Object.Soort == ObjectSoort.Groep && l.Object.Naam == "Gesynchroniseerd");

        var ouders = Index.LidVan(G("gg_rww_foxbeleid"));
        Assert.Contains(ouders, o => o.Laag == "AD" && o.Object.Naam == "gg_eigen");

        // An staat in de SG-SM-groep zowel in Entra als in Exchange: één keer, met beide lagen
        var an = Assert.Single(Index.UniekeLedenVan(G("g-sm")), l => l.Object.Sleutel == "ad:an.peeters");
        Assert.Equal("Entra, Exchange", an.Laag);

        var room = Index.LedenVan(G("rl-gh"));
        Assert.Equal(ObjectSoort.Room, Assert.Single(room).Object.Soort);
        Assert.Contains(Index.GroepenVanAdres("rm-vz-a@test.be"), r => r.Object.Naam == "rl-gh");
    }

    [Fact]
    public void RechtenRechtstreeksOfViaGroep()
    {
        var info = Index.Postvak("info@test.be")!;
        var rechten = Index.RechtenOp(info);
        Assert.Equal(3, rechten.Count);
        Assert.Contains(rechten, r => r.Recht == "FullAccess" && !r.Direct && r.Wie.Naam == "SG-SM-info-Full");
        Assert.Contains(rechten, r => r.Recht == "SendAs" && r.Direct && r.Wie.Soort == ObjectSoort.Gebruiker);
        Assert.Contains(rechten, r => r.Wie.Soort == ObjectSoort.Onbekend);
        Assert.Equal("SG-SM-info-Full", Index.ToegangsgroepVan(info)!.Naam);

        // "FullAccess,SendAs" in één rij wordt twee rechten
        Assert.Equal(2, Index.RechtenOp(Index.Postvak("boekhouding@test.be")!).Count);
        Assert.Null(Index.ToegangsgroepVan(Index.Postvak("boekhouding@test.be")!));

        // An heeft toegang tot info via de groep en rechtstreeks tot boekhouding
        var an = Index.PostvakRechtenVan(_d.ZoekPersoon("ad:an.peeters")!);
        Assert.Contains(an, r => r.Postvak.Smtp == "info@test.be" && r.ViaGroep?.Naam == "SG-SM-info-Full");
        Assert.Contains(an, r => r.Postvak.Smtp == "boekhouding@test.be" && r.ViaGroep is null);
    }

    [Fact]
    public void IndexHoortBijDeGegevens()
    {
        var kopie = _d with { Postvakken = Array.Empty<Postvak>() };
        Assert.NotNull(_d.Index.Postvak("info@test.be"));
        Assert.Null(kopie.Index.Postvak("info@test.be"));
    }

    // ---------------------------------------------------------------- groepen

    [Fact]
    public void LidToevoegenKiestDeJuisteWeg()
    {
        var cas = Persoon("cas.vos");

        var ad = Assert.Single(GroepPlannen.LidToevoegen(_d, G("gg_rww_foxbeleid"), cas).Plan);
        Assert.Equal(Actie.AdGroepLidToevoegen, ad.Actie);
        Assert.Equal(TestData.Dn("cas.vos", TestData.Ocmw), ad.Doel);

        var graph = Assert.Single(GroepPlannen.LidToevoegen(_d, G("g-team"), cas).Plan);
        Assert.Equal(Actie.GraphGroepLidToevoegen, graph.Actie);
        Assert.Equal("e-cas", graph.Doel);

        var exo = Assert.Single(GroepPlannen.LidToevoegen(_d, G("gemeentehuis@test.be"), Index.Ref("info@test.be")).Plan);
        Assert.Equal(Actie.ExchangeGroepLidToevoegen, exo.Actie);
        Assert.Equal("info@test.be", exo.Doel);
        Assert.Equal("gemeentehuis@test.be", exo.P("groep"));
    }

    [Fact]
    public void DynamischeEnBevoorrechteGroepenNiet()
    {
        Assert.Contains("dynamische", Assert.Single(GroepPlannen.LidToevoegen(_d, G("g-alle"), Persoon("cas.vos")).Fouten));
        Assert.Contains("bevoorrecht", Assert.Single(GroepPlannen.LidToevoegen(_d, G("Domain Admins"), Persoon("cas.vos")).Fouten));

        // Een postvak heeft geen AD-object
        Assert.Contains("geen AD-object", Assert.Single(GroepPlannen.LidToevoegen(_d, G("gg_eigen"), Index.Ref("info@test.be")).Fouten));
    }

    [Fact]
    public void KenmerkenPerWeg()
    {
        var ad = Assert.Single(GroepPlannen.Kenmerken(G("gg_eigen"), new GroepGewenst { Omschrijving = "Eigen groep" }).Plan);
        Assert.Equal(Actie.AdKenmerkenZetten, ad.Actie);
        Assert.Equal("Eigen groep", ad.P("description"));

        var cloud = Assert.Single(GroepPlannen.Kenmerken(G("g-team"), new GroepGewenst { Weergavenaam = "DNST_Cultuur" }).Plan);
        Assert.Equal(Actie.GraphGroepKenmerkenZetten, cloud.Actie);
        Assert.Equal("g-team", cloud.Doel);

        var exo = GroepPlannen.Kenmerken(G("gemeentehuis@test.be"), new GroepGewenst { Weergavenaam = "Gemeentehuis (alle)", Omschrijving = "x" });
        Assert.Equal(Actie.ExchangeGroepInstellen, Assert.Single(exo.Plan).Actie);
        Assert.Contains("omschrijving", Assert.Single(exo.Fouten));

        // Niets veranderd = geen plan
        Assert.Empty(GroepPlannen.Kenmerken(G("g-team"), new GroepGewenst { Weergavenaam = "DNST_CULTUUR" }).Plan);
    }

    [Fact]
    public void BeheerdersPerWeg()
    {
        var bo = Persoon("bo.janssens");
        Assert.Equal("managedBy", Assert.Single(Assert.Single(GroepPlannen.Beheerder(_d, G("gg_eigen"), bo, true).Plan).Parameters).Key);
        Assert.Equal(Actie.GraphEigenaarToevoegen, Assert.Single(GroepPlannen.Beheerder(_d, G("g-team"), bo, true).Plan).Actie);
        var exo = Assert.Single(GroepPlannen.Beheerder(_d, G("gemeentehuis@test.be"), bo, false).Plan);
        Assert.Equal(Actie.ExchangeGroepBeheerder, exo.Actie);
        Assert.Equal("bo.janssens@test.be", exo.P("Verwijderen"));
    }

    // ---------------------------------------------------------------- postvakken

    [Fact]
    public void RechtstreekseRechtenGaanNaarDeGroep()
    {
        var u = PostvakPlannen.DirecteRechtenOpruimen(_d, Index.Postvak("info@test.be")!);
        Assert.Empty(u.Fouten);

        // De groep heeft al FullAccess; SendAs ontbreekt nog
        var groepRecht = Assert.Single(u.Plan, w => w.Actie == Actie.ExchangePostvakRechtToevoegen);
        Assert.Equal("SendAs", groepRecht.P("Recht"));
        Assert.Equal("sg-sm-info-full@test.be", groepRecht.P("Trustee"));

        // Bo wordt lid, daarna verliest hij het rechtstreekse SendAs
        var lid = Assert.Single(u.Plan, w => w.Actie == Actie.ExchangeGroepLidToevoegen);
        Assert.Equal("bo.janssens@test.be", lid.Doel);
        var weg = u.Plan.Where(w => w.Actie == Actie.ExchangePostvakRechtVerwijderen).ToList();
        var boWeg = Assert.Single(weg, w => w.P("Trustee") == "bo.janssens@test.be");
        Assert.Equal(lid.Id, boWeg.AfhankelijkVan);
        Assert.True(boWeg.Geselecteerd);

        // Een onbekende rechthebbende staat erin, maar is niet aangevinkt
        Assert.False(Assert.Single(weg, w => w.P("Trustee").StartsWith("S-1-5")).Geselecteerd);
    }

    [Fact]
    public void ZonderToegangsgroepEerstAanmaken()
    {
        var boekhouding = Index.Postvak("boekhouding@test.be")!;
        Assert.Contains("SG-SM-boekhouding-Full", Assert.Single(PostvakPlannen.DirecteRechtenOpruimen(_d, boekhouding).Fouten));

        var plan = PostvakPlannen.ToegangsgroepAanmaken(_d, boekhouding).Plan;
        var groep = plan[0];
        Assert.Equal(Actie.ExchangeGroepAanmaken, groep.Actie);
        Assert.Equal("sg-sm-boekhouding-full@test.be", groep.Doel);
        Assert.Equal(new[] { "FullAccess", "SendAs" }, plan.Skip(1).Select(w => w.P("Recht")));
        Assert.All(plan.Skip(1), w => Assert.Equal(groep.Id, w.AfhankelijkVan));

        Assert.Contains("bestaat al", Assert.Single(PostvakPlannen.ToegangsgroepAanmaken(_d, Index.Postvak("info@test.be")!).Fouten));
    }

    [Fact]
    public void NieuwPostvakIsEenKeten()
    {
        var a = new NieuwPostvakAanvraag { Weergavenaam = "Jeugddienst", LokaalDeel = "Jeugddienst", Domein = "test.be", Leden = { Persoon("cas.vos") } };
        var u = PostvakPlannen.NieuwPostvak(_d, a);
        Assert.Empty(u.Fouten);
        Assert.Equal(
            new[] { Actie.ExchangeGedeeldPostvakAanmaken, Actie.ExchangeGroepAanmaken, Actie.ExchangePostvakRechtToevoegen, Actie.ExchangePostvakRechtToevoegen, Actie.ExchangeGroepLidToevoegen },
            u.Plan.Select(w => w.Actie));
        Assert.Equal("jeugddienst@test.be", u.Plan[0].Doel);
        Assert.Equal("SG-SM-jeugddienst-Full", u.Plan[1].DoelNaam);
        Assert.Equal(u.Plan[0].Id, u.Plan[1].AfhankelijkVan);
        Assert.All(u.Plan.Skip(2), w => Assert.Equal(u.Plan[1].Id, w.AfhankelijkVan));

        Assert.Contains("al in gebruik", Assert.Single(PostvakPlannen.NieuwPostvak(_d, new NieuwPostvakAanvraag { Weergavenaam = "x", LokaalDeel = "info", Domein = "test.be" }).Fouten));
        Assert.NotEmpty(PostvakPlannen.NieuwPostvak(_d, new NieuwPostvakAanvraag { Weergavenaam = "x", LokaalDeel = "met spatie", Domein = "test.be" }).Fouten);
    }

    [Fact]
    public void InstellingenEnkelWatVerschilt()
    {
        var info = Index.Postvak("info@test.be")!;
        Assert.Empty(PostvakPlannen.Instellingen(info, new PostvakGewenst { Weergavenaam = "Info", Verborgen = false }).Plan);

        var w = Assert.Single(PostvakPlannen.Instellingen(info, new PostvakGewenst { Verborgen = true, DoorsturenNaar = "iemand@fabrikam.com" }).Plan);
        Assert.Equal("True", w.P("HiddenFromAddressListsEnabled"));
        Assert.Equal("iemand@fabrikam.com", w.P("ForwardingSmtpAddress"));
        Assert.NotNull(w.Waarschuwing);

        var auto = PostvakPlannen.Instellingen(info, new PostvakGewenst { AutoAntwoord = "Enabled", AutoAntwoordIntern = "Afwezig" }).Plan;
        Assert.Equal("Afwezig", Assert.Single(auto).P("InternalMessage"));
        Assert.NotEmpty(PostvakPlannen.Instellingen(info, new PostvakGewenst { AutoAntwoord = "Enabled" }).Fouten);
        Assert.NotEmpty(PostvakPlannen.Instellingen(info, new PostvakGewenst { DoorsturenNaar = "geen-adres" }).Fouten);
    }
}
