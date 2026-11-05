using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Xunit;

namespace Moss365.Tests;

/// <summary>Kern voor 0.7: aanmelden met het account van een gedeeld postvak.</summary>
public sealed class Versie07Tests
{
    /// <summary>De testomgeving met twee gedeelde postvakken: een cloudaccount (Jeugd) en een account uit AD (Cultuur, uitgeschakeld).</summary>
    public static InventarisData MetPostvakAccounts() => InventarisLader.Bouw(PostvakBestanden());

    public static Dictionary<string, List<Dictionary<string, string>>> PostvakBestanden()
    {
        var b = TestData.Bestanden();
        b[InventarisLader.ExoPostvakken].Add(TestData.Postvak("Jeugd", "jeugd@test.be"));
        b[InventarisLader.ExoPostvakken].Add(TestData.Postvak("Cultuur", "cultuur@test.be"));
        b[InventarisLader.EntraGebruikers].Add(TestData.EntraGebruiker("e-jeugd", "jeugd@test.be", displayName: "Jeugd"));
        b[InventarisLader.AdGebruikers].Add(TestData.AdGebruiker("postvak.cultuur", TestData.Gemeente, "cultuur@test.be", enabled: false, lastLogon: ""));
        return b;
    }

    [Fact]
    public void AccountVanEenPostvak()
    {
        var d = MetPostvakAccounts();
        var jeugd = d.Index.Postvak("jeugd@test.be")!;
        Assert.Equal("e-jeugd", d.Index.AccountVan(jeugd.Smtp, jeugd.Alias)!.Entra!.Id);
        Assert.True(d.Index.IsPostvakAccount(d.Index.AccountVan(jeugd.Smtp)!));
        Assert.Equal("postvak.cultuur", d.Index.AccountVan("CULTUUR@test.be")!.Ad!.SamAccountName);
        Assert.Null(d.Index.AccountVan("bestaat.niet@test.be"));
    }

    [Fact]
    public void AanmeldenBlokkerenInEntraOfAd()
    {
        var d = MetPostvakAccounts();

        // Cloudaccount: in Entra, omkeerbaar via Vorige
        var blok = Assert.Single(PostvakPlannen.Aanmelden(d, d.Index.Postvak("jeugd@test.be")!, toestaan: false).Plan);
        Assert.Equal(Actie.GraphKenmerkenZetten, blok.Actie);
        Assert.Equal("e-jeugd", blok.Doel);
        Assert.Equal("false", blok.Parameters["accountEnabled"]);
        Assert.Equal("true", blok.Vorige!["accountEnabled"]);
        Assert.Equal("kan aanmelden", blok.HuidigeWaarde);

        // Account uit AD: in AD (Entra volgt), toestaan met een waarschuwing
        var toe = Assert.Single(PostvakPlannen.Aanmelden(d, d.Index.Postvak("cultuur@test.be")!, toestaan: true).Plan);
        Assert.Equal(Actie.AdAccountInschakelen, toe.Actie);
        Assert.StartsWith("CN=postvak.cultuur,", toe.Doel);
        Assert.Contains("Microsoft raadt aan", toe.Waarschuwing);
        Assert.Equal(Actie.AdAccountUitschakelen, Assert.Single(PostvakPlannen.Aanmelden(d, d.Index.Postvak("cultuur@test.be")!, false).Plan).Actie);

        // Zonder account: een duidelijke fout
        var info = PostvakPlannen.Aanmelden(d, d.Index.Postvak("info@test.be")!, false);
        Assert.Empty(info.Plan);
        Assert.Contains("niet in de geladen gegevens", Assert.Single(info.Fouten));
    }
}
