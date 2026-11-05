using System.Text.Json;
using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

/// <summary>Conditional Access (0.6): lezen uit Graph, bewaren, uitleg, bereik, wat als, controle en effect.</summary>
public sealed class ConditionalAccessTests : IDisposable
{
    private static readonly DateTime Nu = new(2026, 10, 30, 12, 0, 0);
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-ca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    private static InventarisData Data(bool groepLeden = true) => TestData.Omgeving() with
    {
        ConditionalAccess = TestData.Ca(new DateTimeOffset(Nu), groepLeden),
        Beveiliging = new BeveiligingGegevens(
            new[]
            {
                new MfaRegistratie("e-an", "an.peeters@test.be", true, true, new[] { "microsoftAuthenticatorPush" }, "push", true),
                new MfaRegistratie("e-cas", "cas.vos@test.be", false, false, Array.Empty<string>(), "", false),
            },
            new[] { new RolLid("Global Administrator", "e-an", "an peeters", "an.peeters@test.be", "user", CaGegevens.RolGlobalAdmin) }),
    };

    private static CaGebruiker G(InventarisData d, string sleutel) => CaGebruiker.Van(d.ZoekPersoon(sleutel)!, d)!;

    private static CaBeleid B(InventarisData d, string id) => d.ConditionalAccess!.Beleiden.Single(b => b.Id == id);

    // ---------------------------------------------------------------- lezen en bewaren

    [Fact]
    public void BeleidUitGraphLezen()
    {
        var b = GraphVerbinding.NaarCaBeleid(Json("""
            {"id":"p1","displayName":"MFA beheerders","state":"enabledForReportingButNotEnforced","modifiedDateTime":"2026-09-01T10:00:00Z",
             "conditions":{"clientAppTypes":["all"],
               "users":{"includeUsers":[],"excludeUsers":["u-nood"],"includeGroups":[],"excludeGroups":["g-x"],"includeRoles":["62e90394-69f5-4237-9190-012177145e10"],"excludeRoles":[],
                        "includeGuestsOrExternalUsers":null,"excludeGuestsOrExternalUsers":{"guestOrExternalUserTypes":"b2bCollaborationGuest"}},
               "applications":{"includeApplications":["MicrosoftAdminPortals"],"excludeApplications":[],"includeUserActions":[]},
               "platforms":null,"locations":{"includeLocations":["All"],"excludeLocations":["AllTrusted"]},
               "signInRiskLevels":[],"userRiskLevels":[],"authenticationFlows":{"transferMethods":"deviceCodeFlow"}},
             "grantControls":{"operator":"OR","builtInControls":[],"termsOfUse":[],"authenticationStrength":{"id":"00000000-0000-0000-0000-000000000004","displayName":"Phishing-resistant MFA"}},
             "sessionControls":{"signInFrequency":{"value":4,"type":"hours","isEnabled":true},"persistentBrowser":{"mode":"never","isEnabled":true}}}
            """));

        Assert.Equal(CaStatus.Rapport, b.Status);
        Assert.Equal(new[] { CaGegevens.RolGlobalAdmin }, b.RollenIn);
        Assert.Equal(new[] { "u-nood" }, b.GebruikersUit);
        Assert.True(b.GastenUit);
        Assert.False(b.GastenIn);
        Assert.Equal(new[] { "AllTrusted" }, b.LocatiesUit);
        Assert.Equal(new[] { "deviceCodeFlow" }, b.Overdracht);
        Assert.True(b.VraagtMfa);
        Assert.True(b.VraagtPhishingbestendig);
        Assert.Equal("4 uur", b.AanmeldFrequentie);
        Assert.Equal("nooit aangemeld blijven", b.BlijvendeBrowser);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), b.Gewijzigd);

        var ip = GraphVerbinding.NaarLocatie(Json("""{"@odata.type":"#microsoft.graph.ipNamedLocation","id":"l1","displayName":"Kantoor","isTrusted":true,"ipRanges":[{"cidrAddress":"203.0.113.0/24"}]}"""));
        Assert.True(ip.IsIp && ip.Vertrouwd);
        Assert.Equal("203.0.113.0/24", Assert.Single(ip.Waarden));
        var land = GraphVerbinding.NaarLocatie(Json("""{"@odata.type":"#microsoft.graph.countryNamedLocation","id":"l2","displayName":"BE","countriesAndRegions":["BE","NL"],"includeUnknownCountriesAndRegions":false}"""));
        Assert.True(land.BevatBelgie);
        Assert.False(land.IsIp);

        var a = GraphVerbinding.NaarAanmelding(Json("""{"createdDateTime":"2026-10-20T08:00:00Z","userId":"e-bo","status":{"errorCode":0},"appliedConditionalAccessPolicies":[{"id":"ca-mfa","displayName":"MFA","result":"success"},{"id":"ca-beheer","displayName":"B","result":"reportOnlyFailure"}]}"""));
        Assert.Equal(new[] { "success", "reportOnlyFailure" }, a.Ca!.Select(c => c.Resultaat));
    }

    [Fact]
    public void BewarenEnTerugLezen()
    {
        var d = Data();
        var map = InventarisSchrijver.Bewaar(d, _map, InventarisOnderdeel.Entra, Nu);
        Assert.True(File.Exists(Path.Combine(map, InventarisLader.EntraCa)));

        var terug = InventarisLader.Laad(new[] { map });
        Assert.NotNull(terug.ConditionalAccess);
        Assert.Equal(d.ConditionalAccess!.Beleiden.Select(b => b.Naam), terug.ConditionalAccess!.Beleiden.Select(b => b.Naam));
        Assert.Equal(CaStatus.Rapport, terug.ConditionalAccess.Beleiden.Single(b => b.Id == "ca-beheer").Status);
        Assert.Equal(new[] { "e-bo" }, terug.ConditionalAccess.GroepLeden!["g-eigen"]);
        Assert.Contains(terug.Bronnen, b => b.Bestand == InventarisLader.EntraCa && b.Rijen == 4);
        Assert.Equal(CaGegevens.RolGlobalAdmin, terug.Beveiliging!.Rollen.Single().RoleTemplateId);

        // Kapot bestand: gewoon niet geladen
        File.WriteAllText(Path.Combine(map, InventarisLader.EntraCa), "{kapot");
        Assert.Null(InventarisLader.Laad(new[] { map }).ConditionalAccess);
    }

    [Fact]
    public void NietLeesbaarWordtNietBewaard()
    {
        var d = Data() with { ConditionalAccess = new CaGegevens(Array.Empty<CaBeleid>(), Array.Empty<BenoemdeLocatie>(), Array.Empty<Authenticatiesterkte>(), new Dictionary<string, string>(), null, "Niet leesbaar") };
        var map = InventarisSchrijver.Bewaar(d, _map, InventarisOnderdeel.Entra, Nu);
        Assert.False(File.Exists(Path.Combine(map, InventarisLader.EntraCa)));
        Assert.Equal(CaOordeel.Probleem, Assert.Single(CaControle.Controleer(d, TestData.Config(), Nu)).Oordeel);
    }

    // ---------------------------------------------------------------- uitleg en bereik

    [Fact]
    public void UitlegInGewoneTaal()
    {
        var d = Data();
        var naam = CaLezer.Namen(d);
        var u = CaLezer.Uitleg(B(d, "ca-mfa"), d.ConditionalAccess!, naam);
        Assert.Equal(new[] { "Alle gebruikers", "Behalve: Info", "Behalve leden van: WKG_Eigen" }, u.Wie);
        Assert.Equal("Voor alle cloud-apps", Assert.Single(u.Apps));
        Assert.Equal("Toegang mits MFA", u.Gevolg);

        var legacy = CaLezer.Uitleg(B(d, "ca-legacy"), d.ConditionalAccess!, naam);
        Assert.Contains("Exchange ActiveSync", Assert.Single(legacy.Voorwaarden));
        Assert.Equal("Toegang blokkeren", legacy.Gevolg);

        var beheer = CaLezer.Uitleg(B(d, "ca-beheer"), d.ConditionalAccess!, naam);
        Assert.Equal("Met de rol: Global Administrator", Assert.Single(beheer.Wie));
        Assert.Equal("Toegang mits MFA met sterkte Phishingbestendige MFA", beheer.Gevolg);

        var land = CaLezer.Uitleg(B(d, "ca-land"), d.ConditionalAccess!, naam);
        Assert.Equal("Niet vanaf België", Assert.Single(land.Voorwaarden));
    }

    [Fact]
    public void BereikPerGebruikerMetUitsluitingenEerst()
    {
        var d = Data();
        var ca = d.ConditionalAccess!;
        var naam = CaLezer.Namen(d);
        var mfa = B(d, "ca-mfa");

        Assert.Equal(CaGeldt.Ja, CaLezer.Bereik(mfa, G(d, "ad:an.peeters"), naam, ca).Geldt);
        var bo = CaLezer.Bereik(mfa, G(d, "ad:bo.janssens"), naam, ca);
        Assert.Equal(CaGeldt.Nee, bo.Geldt);
        Assert.Equal("uitgezonderd via de groep WKG_Eigen", bo.Reden);
        Assert.Equal("rechtstreeks uitgezonderd", CaLezer.Bereik(mfa, G(d, "entra:e-info"), naam, ca).Reden);

        // Rollen via de roleTemplateId
        var beheer = B(d, "ca-beheer");
        Assert.Equal("heeft de rol Global Administrator", CaLezer.Bereik(beheer, G(d, "ad:an.peeters"), naam, ca).Reden);
        Assert.Equal(CaGeldt.Nee, CaLezer.Bereik(beheer, G(d, "ad:cas.vos"), naam, ca).Geldt);

        // Zonder de volledige groepsleden is een uitgezonderde groep niet zeker
        var zonder = Data(groepLeden: false);
        Assert.Equal(CaGeldt.Misschien, CaLezer.Bereik(mfa, G(zonder, "ad:an.peeters"), CaLezer.Namen(zonder), zonder.ConditionalAccess!).Geldt);
        Assert.Equal(CaGeldt.Nee, CaLezer.Bereik(mfa, G(zonder, "ad:bo.janssens"), CaLezer.Namen(zonder), zonder.ConditionalAccess!).Geldt);

        // Wie valt eronder: iedereen met Entra, behalve Info en Bo
        var eronder = CaLezer.WieValtEronder(mfa, d).Select(x => x.Persoon.Entra!.Id).ToList();
        Assert.Contains("e-an", eronder);
        Assert.Contains("e-gast", eronder);
        Assert.DoesNotContain("e-bo", eronder);
        Assert.DoesNotContain("e-info", eronder);
        Assert.Null(CaGebruiker.Van(d.ZoekPersoon("ad:dirk.intern")!, d));
    }

    // ---------------------------------------------------------------- wat als

    [Fact]
    public void WatAlsScenario()
    {
        var d = Data();
        var ca = d.ConditionalAccess!;
        var naam = CaLezer.Namen(d);
        var an = G(d, "ad:an.peeters");

        var thuis = CaLezer.WatAls(an, new CaScenario(CaApp.Office365, CaPlek.Belgie), ca, naam);
        Assert.False(thuis.Geblokkeerd);
        Assert.Equal(new[] { "MFA" }, thuis.Eisen);
        Assert.Equal("Toegang mits MFA", thuis.Samenvatting);
        Assert.Equal(CaGeldt.Nee, thuis.Regels.Single(r => r.Beleid.Id == "ca-land").Antwoord.Geldt);
        Assert.Equal(CaGeldt.Ja, Assert.Single(thuis.AlleenRapport).Antwoord.Geldt);

        var buitenland = CaLezer.WatAls(an, new CaScenario(CaApp.Office365, CaPlek.Buitenland), ca, naam);
        Assert.True(buitenland.Geblokkeerd);
        Assert.StartsWith("Geblokkeerd door Buitenland blokkeren", buitenland.Samenvatting);

        var imap = CaLezer.WatAls(an, new CaScenario(CaApp.Office365, CaPlek.Belgie, Client: CaClient.Verouderd), ca, naam);
        Assert.True(imap.Geblokkeerd);
        Assert.Equal(CaGeldt.Nee, CaLezer.WatAls(an, new CaScenario(), ca, naam).Regels.Single(r => r.Beleid.Id == "ca-legacy").Antwoord.Geldt);

        // Bo valt buiten MFA: enkel een wachtwoord
        var bo = CaLezer.WatAls(G(d, "ad:bo.janssens"), new CaScenario(), ca, naam);
        Assert.Empty(bo.Eisen);
        Assert.StartsWith("Geen enkel beleid is zeker van toepassing", bo.Samenvatting);
    }

    [Fact]
    public void WatAlsBeheerdToestelEnLocaties()
    {
        var d = Data();
        var basis = d.ConditionalAccess!;
        var ca = basis with
        {
            Beleiden = new[]
            {
                new CaBeleid
                {
                    Id = "x", Naam = "MFA of toestel buiten kantoor", Status = CaStatus.Aan, GebruikersIn = new[] { "All" }, AppsIn = new[] { "Office365" },
                    LocatiesIn = new[] { "All" }, LocatiesUit = new[] { "AllTrusted" }, Controles = new[] { "mfa", "compliantDevice" },
                },
            },
        };
        var an = G(d, "ad:an.peeters");
        var naam = CaLezer.Namen(d);

        Assert.Equal("Toegang mits MFA of een conform toestel (Intune)", CaLezer.WatAls(an, new CaScenario(), ca, naam).Samenvatting);
        Assert.Empty(CaLezer.WatAls(an, new CaScenario(BeheerdToestel: true), ca, naam).Eisen);
        Assert.Equal(CaGeldt.Nee, CaLezer.WatAls(an, new CaScenario(Plek: CaPlek.Kantoor), ca, naam).Regels.Single().Antwoord.Geldt);
        Assert.Equal(CaGeldt.Nee, CaLezer.WatAls(an, new CaScenario(App: CaApp.Beheerportalen), ca, naam).Regels.Single().Antwoord.Geldt);
    }

    // ---------------------------------------------------------------- controle en effect

    [Fact]
    public void ControleVolgensDeAanbevelingen()
    {
        var d = Data();
        var config = TestData.Config();
        config.Noodaccounts.Add("info@test.onmicrosoft.com");
        var bevindingen = CaControle.Controleer(d, config, Nu);
        CaBevinding Over(string onderwerp) => bevindingen.First(b => b.Onderwerp == onderwerp);

        Assert.Equal(CaOordeel.InOrde, Over("MFA voor iedereen").Oordeel);
        Assert.Equal(CaOordeel.InOrde, Over("Verouderde aanmelding geblokkeerd").Oordeel);

        var buiten = Over("Medewerkers zonder MFA-beleid");
        Assert.Equal(CaOordeel.Aandacht, buiten.Oordeel);
        Assert.Equal(new[] { "e-bo" }, buiten.Personen.Select(p => p.Entra!.Id));

        // Beheerders: MFA via "iedereen", het phishingbestendige beleid staat enkel op rapport
        Assert.Equal(CaOordeel.Aandacht, Over("Beheerders").Oordeel);
        var rapport = Over("Lang op alleen rapport");
        Assert.Contains("60 dagen", rapport.Uitleg);

        // Info is het noodaccount, maar valt onder "Buitenland blokkeren"
        var nood = Over("Noodaccount Info");
        Assert.Equal(CaOordeel.Probleem, nood.Oordeel);
        Assert.Contains("Buitenland blokkeren", nood.Uitleg);
        Assert.Equal("ca-land", Assert.Single(nood.Beleiden).Id);

        Assert.Equal(CaOordeel.Aandacht, Over("Aanmelden met toestelcode").Oordeel);
        Assert.Equal(CaOordeel.Aandacht, Over("MFA registreren beschermd").Oordeel);
        Assert.Equal(CaOordeel.InOrde, Over("Gasten").Oordeel);

        // Cas valt onder MFA maar heeft nog niets geregistreerd
        Assert.Contains(Over("MFA nog niet ingesteld").Personen, p => p.Entra!.Id == "e-cas");
    }

    [Fact]
    public void ControleZonderNoodaccountsSteltKandidatenVoor()
    {
        var d = Data();
        var nood = CaControle.Controleer(d, TestData.Config(), Nu).Single(b => b.Onderwerp == "Noodaccounts");
        Assert.Equal(CaOordeel.Aandacht, nood.Oordeel);
        Assert.Contains("Organisatie > Noodaccounts", nood.Advies);

        var leeg = d with { ConditionalAccess = d.ConditionalAccess! with { Beleiden = Array.Empty<CaBeleid>() } };
        Assert.Contains(CaControle.Controleer(leeg, TestData.Config(), Nu), b => b.Onderwerp == "Bescherming" && b.Oordeel == CaOordeel.Probleem);
        Assert.Equal(CaOordeel.Info, Assert.Single(CaControle.Controleer(d with { ConditionalAccess = null }, TestData.Config(), Nu)).Oordeel);
    }

    [Fact]
    public void EffectUitDeAanmeldingen()
    {
        var d = Data();
        Aanmelding A(params CaToepassing[] ca) => new(DateTimeOffset.Now, "e-an", "an", "An", "Office", "", "", 0, "", "Browser", ca);
        var effect = CaEffect.Bereken(new[]
        {
            A(new CaToepassing("ca-mfa", "MFA", "success"), new CaToepassing("ca-beheer", "B", "reportOnlyFailure")),
            A(new CaToepassing("ca-mfa", "MFA", "success"), new CaToepassing("ca-beheer", "B", "reportOnlyInterrupted")),
            A(new CaToepassing("ca-land", "L", "notApplied"), new CaToepassing("onbekend", "?", "success")),
            new Aanmelding(DateTimeOffset.Now, "e-bo", "bo", "Bo", "Office", "", "", 0, "", "Browser"),
        }, d.ConditionalAccess!);

        var mfa = effect.Single(e => e.BeleidId == "ca-mfa");
        Assert.Equal(2, mfa.Toegepast);
        var beheer = effect.Single(e => e.BeleidId == "ca-beheer");
        Assert.Equal(2, beheer.ZouTegenhouden);
        Assert.Equal(1, effect.Single(e => e.BeleidId == "ca-land").NietVanToepassing);
        Assert.Equal(0, effect.Single(e => e.BeleidId == "ca-legacy").Totaal);
    }

    private static JsonElement Json(string tekst) => JsonDocument.Parse(tekst).RootElement.Clone();
}
