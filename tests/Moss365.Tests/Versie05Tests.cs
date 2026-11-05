using System.Net;
using System.Text.Json;
using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.Connections.Ad;
using Moss365.Connections.Graph;
using Moss365.Connections.Updates;
using Xunit;

namespace Moss365.Tests;

/// <summary>Kern en verbindingen voor 0.5: export, versies, helpdesk, logboek en ongedaan maken, inplannen, opkuis, beveiliging, delta.</summary>
public sealed class Versie05Tests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-v05-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    // ---------------------------------------------------------------- export en versies

    [Fact]
    public void CsvVoorExcelVoertGeenFormulesUit()
    {
        var tekst = CsvSchrijver.Tekst(new[] { "A", "B" }, new[] { new[] { "=HYPERLINK(\"x\")", "-5" }, new[] { "@som", "gewoon \"tekst\"" } }, voorExcel: true);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", tekst);
        Assert.Contains("\"-5\"", tekst);
        Assert.Contains("\"'@som\"", tekst);
        Assert.Contains("\"gewoon \"\"tekst\"\"\"", tekst);

        // De inventaris (die de app zelf terugleest) blijft ongewijzigd
        Assert.Contains("\"=HYPERLINK", CsvSchrijver.Tekst(new[] { "A" }, new[] { new[] { "=HYPERLINK(\"x\")" } }));
        Assert.Equal("Opkuis-Medewerkers-180d", CsvSchrijver.Bestandsnaam("Opkuis-Medewerkers-180d"));
        Assert.Equal("a-b-c", CsvSchrijver.Bestandsnaam("a/b:c"));
    }

    [Theory]
    [InlineData("v0.5.1", "0.5.0", true)]
    [InlineData("0.6", "0.5.9", true)]
    [InlineData("v0.5.0", "0.5.0", false)]
    [InlineData("0.4.9", "0.5.0", false)]
    [InlineData("v1.0.0+abc", "0.9.0", true)]
    [InlineData("onzin", "0.5.0", false)]
    public void VersiesVergelijken(string kandidaat, string huidig, bool nieuwer)
        => Assert.Equal(nieuwer, Versies.IsNieuwer(kandidaat, huidig));

    [Fact]
    public async Task UpdateZoekenOpGitHub()
    {
        var gevonden = await new GitHubUpdates(new HttpClient(new NepHttp(HttpStatusCode.OK, """{"tag_name":"v0.6.0","html_url":"https://github.com/x/y/releases/tag/v0.6.0"}""")), "x/y")
            .ControleerAsync(CancellationToken.None);
        Assert.Equal("0.6.0", gevonden.NieuwsteVersie);
        Assert.True(gevonden.IsNieuwer("0.5.0"));
        Assert.EndsWith("/tag/v0.6.0", gevonden.Link);

        var prive = await new GitHubUpdates(new HttpClient(new NepHttp(HttpStatusCode.NotFound, "{}")), "x/y").ControleerAsync(CancellationToken.None);
        Assert.Null(prive.NieuwsteVersie);
        Assert.Contains("privé", prive.Melding);
        Assert.Equal("https://github.com/x/y/releases/latest", prive.Link);
    }

    // ---------------------------------------------------------------- helpdesk

    [Fact]
    public void WachtwoordPlanZitEnkelInHetGeheim()
    {
        var d = TestData.Omgeving();
        var bo = d.ZoekPersoon("ad:bo.janssens")!;
        Assert.Contains("minstens 12", Assert.Single(HelpdeskPlannen.WachtwoordInstellen(d, bo, "kort", true, false).Fouten));

        var plan = HelpdeskPlannen.WachtwoordInstellen(d, bo, "Een-Lang-Wachtwoord-1", wijzigenBijAanmelding: false, ontgrendelen: true).Plan;
        Assert.Equal(new[] { Actie.AdWachtwoordInstellen, Actie.AdAccountOntgrendelen }, plan.Select(w => w.Actie));
        Assert.Equal("Een-Lang-Wachtwoord-1", plan[0].Geheim);
        Assert.Equal("False", plan[0].P("wijzigenBijAanmelding"));
        Assert.DoesNotContain(plan[0].Parameters.Values, v => v.Contains("Wachtwoord-1", StringComparison.Ordinal));
        Assert.Null(plan[0].Waarschuwing);

        // Bevoorrecht account: waarschuwing; uitgeschakeld: inschakelen erbij (niet aangevinkt)
        Assert.Contains("Domain Admins", HelpdeskPlannen.WachtwoordInstellen(d, d.ZoekPersoon("ad:an.peeters")!, "Een-Lang-Wachtwoord-1", true, false).Plan[0].Waarschuwing);
        var oud = HelpdeskPlannen.WachtwoordInstellen(d, d.ZoekPersoon("ad:oud.account")!, "Een-Lang-Wachtwoord-1", true, false).Plan;
        Assert.False(Assert.Single(oud, w => w.Actie == Actie.AdAccountInschakelen).Geselecteerd);

        // Een cloudaccount: niet via de app
        var info = d.Personen.Single(p => p.Naam == "Info");
        Assert.Contains("Entra-portaal", Assert.Single(HelpdeskPlannen.WachtwoordInstellen(d, info, "Een-Lang-Wachtwoord-1", true, false).Fouten));
    }

    [Fact]
    public void VergrendeldUitLdap()
    {
        Dictionary<string, object?[]> K(params (string, object?)[] w) => w.ToDictionary(x => x.Item1, x => new[] { x.Item2 }, StringComparer.OrdinalIgnoreCase);
        Assert.True(AdLdapMapper.NaarGebruiker(K(("sAMAccountName", "a"), ("msDS-User-Account-Control-Computed", 0x10))).LockedOut);
        Assert.False(AdLdapMapper.NaarGebruiker(K(("sAMAccountName", "a"), ("msDS-User-Account-Control-Computed", 0), ("lockoutTime", 5L))).LockedOut);
        Assert.True(AdLdapMapper.NaarGebruiker(K(("sAMAccountName", "a"), ("lockoutTime", 132000000000000000L))).LockedOut);
        Assert.False(AdLdapMapper.NaarGebruiker(K(("sAMAccountName", "a"))).LockedOut);
    }

    // ---------------------------------------------------------------- logboek en ongedaan maken

    [Fact]
    public async Task LogboekBewaartPlanEnVorigeWaardenMaarGeenGeheim()
    {
        var logboek = new JsonlLogboek(_map);
        var plan = new[]
        {
            new Wijziging
            {
                Systeem = Systeem.ActiveDirectory, Actie = Actie.AdWachtwoordInstellen, Doel = "CN=a,DC=t", Omschrijving = "w",
                Geheim = "Geheim-Wachtwoord-123",
            },
            new Wijziging
            {
                Systeem = Systeem.ActiveDirectory, Actie = Actie.AdKenmerkenZetten, Doel = "CN=a,DC=t", Omschrijving = "k",
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["description"] = "nieuw" },
                Vorige = new(StringComparer.OrdinalIgnoreCase) { ["description"] = "oud" },
            },
        };
        await new PlanUitvoerder(new[] { new AllesOk(Systeem.ActiveDirectory) }, logboek, () => "test").VoerUitAsync(plan, "ctx");

        Assert.DoesNotContain("Geheim-Wachtwoord-123", File.ReadAllText(Directory.GetFiles(_map).Single()));
        var regels = logboek.Recent(10);
        Assert.Single(regels.Select(r => r.PlanId).Distinct());
        Assert.NotNull(regels[0].PlanId);
        Assert.Equal("oud", regels.Single(r => r.Actie == nameof(Actie.AdKenmerkenZetten)).Vorige!["description"]);

        // Oudere regels (van voor 0.5) zonder de nieuwe velden blijven leesbaar
        File.AppendAllText(Path.Combine(_map, "logboek-2025-01.jsonl"),
            """{"tijd":"2025-01-02T10:00:00+01:00","gebruiker":"x","context":"c","systeem":"AD","actie":"AdGroepLidToevoegen","doel":"d","doelNaam":"n","omschrijving":"o","parameters":{},"status":"Gelukt","melding":""}""" + "\n");
        var oud = logboek.Recent(10).Single(r => r.Gebruiker == "x");
        Assert.Null(oud.PlanId);
        Assert.True(Terugdraaien.KanOmkeren(oud));
    }

    [Fact]
    public void UitstroomTerugdraaienInOmgekeerdeVolgorde()
    {
        var d = TestData.Omgeving();
        var an = d.ZoekPersoon("ad:an.peeters")!;
        var plan = PlanBouwer.Uitstroom(d, TestData.Config(), an, new UitstroomOpties { AdGroepenVerwijderen = true, Datum = new DateOnly(2026, 10, 31) })
            .Where(w => w.Geselecteerd && w.Systeem == Systeem.ActiveDirectory).ToList();
        var t = new DateTimeOffset(2026, 10, 31, 17, 0, 0, TimeSpan.Zero);
        var log = plan.Select((w, i) => Regel(w, t.AddMilliseconds(i))).ToList();

        // Het account staat nu in de uitstroom-OU
        var nieuweDn = "CN=an.peeters,OU=disabled_users,OU=ARCHIVE,DC=test,DC=internal";
        var nu = d with { AdGebruikers = d.AdGebruikers.Select(u => u.SamAccountName == "an.peeters" ? u with { DistinguishedName = nieuweDn, Enabled = false } : u).ToList() };

        var terug = Terugdraaien.Plan(log, nu);
        Assert.Empty(terug.NietOmkeerbaar);
        Assert.Equal(Actie.AdVerplaatsen, terug.Plan[0].Actie);
        Assert.Equal(nieuweDn, terug.Plan[0].Doel, ignoreCase: true);
        Assert.Equal(TestData.Gemeente, terug.Plan[0].P("doelOu"));

        // Na het terugverplaatsen: de oude DN voor de groepen
        var groepen = terug.Plan.Where(w => w.Actie == Actie.AdGroepLidToevoegen).ToList();
        Assert.Equal(3, groepen.Count);
        Assert.All(groepen, w => Assert.Equal(TestData.Dn("an.peeters", TestData.Gemeente), w.Doel));
        var admins = groepen.Single(w => w.P("groep") == "Domain Admins");
        Assert.False(admins.Geselecteerd);
        Assert.Contains("bevoorrechte groep", admins.Waarschuwing);

        var omschrijving = terug.Plan.Single(w => w.Actie == Actie.AdKenmerkenZetten);
        Assert.Equal(string.Empty, omschrijving.P("description"));
        Assert.Equal(Actie.AdAccountInschakelen, terug.Plan[^1].Actie);
    }

    [Fact]
    public void NietAllesKanTerug()
    {
        LogRegel R(Actie a, string status = "Gelukt", string melding = "", Dictionary<string, string>? p = null)
            => new(DateTimeOffset.Now, "x", "c", "s", a.ToString(), "doel", "naam", "o", p ?? new(), status, melding);

        Assert.Contains("niets", Terugdraaien.WaaromNiet(R(Actie.GraphGroepLidToevoegen, melding: "Was al lid.")));
        Assert.Contains("niet gelukt", Terugdraaien.WaaromNiet(R(Actie.GraphGroepLidToevoegen, "Mislukt")));
        Assert.Contains("wachtwoord", Terugdraaien.WaaromNiet(R(Actie.AdWachtwoordInstellen)));
        Assert.Contains("versie 0.5", Terugdraaien.WaaromNiet(R(Actie.AdKenmerkenZetten)));
        Assert.Contains("Gastgebruikers", Terugdraaien.WaaromNiet(R(Actie.GraphGastUitnodigen)));

        var beheerder = Assert.Single(Terugdraaien.Plan(new[] { R(Actie.ExchangeGroepBeheerder, p: new() { ["Toevoegen"] = "an@test.be" }) }).Plan);
        Assert.Equal("an@test.be", beheerder.P("Verwijderen"));
        Assert.False(beheerder.Parameters.ContainsKey("Toevoegen"));

        var recht = Assert.Single(Terugdraaien.Plan(new[] { R(Actie.ExchangePostvakRechtVerwijderen, p: new() { ["Recht"] = "SendAs", ["Trustee"] = "bo@test.be" }) }).Plan);
        Assert.Equal(Actie.ExchangePostvakRechtToevoegen, recht.Actie);
        Assert.Equal(Systeem.Exchange, recht.Systeem);
    }

    // ---------------------------------------------------------------- inplannen

    [Fact]
    public void GeplandeUitstroomBewarenEnOpnieuwBerekenen()
    {
        var d = TestData.Omgeving();
        var opslag = new GeplandOpslag(Path.Combine(_map, "gepland.json"));
        var wanneer = new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.FromHours(1));
        opslag.Voegtoe(GeplandPlannen.Uitstroom(d.ZoekPersoon("ad:bo.janssens")!, new UitstroomOpties { Datum = new DateOnly(2026, 12, 31), LicentiesVerwijderen = true }, wanneer, "test"));

        var a = Assert.Single(opslag.Lees());
        Assert.Equal(wanneer, a.Wanneer);
        Assert.True(a.Opties!.LicentiesVerwijderen);
        Assert.False(a.IsVervallen(wanneer.AddMinutes(-1)));
        Assert.True(a.IsVervallen(wanneer));

        var plan = GeplandPlannen.PlanVoor(a, d, TestData.Config());
        Assert.Empty(plan.Fouten);
        Assert.Contains(plan.Plan, w => w.Actie == Actie.AdAccountUitschakelen && w.Geselecteerd);
        Assert.Contains("2026-12-31", plan.Plan.Single(w => w.Actie == Actie.AdKenmerkenZetten).P("description"));

        // De gebruiker bestaat niet meer
        var weg = GeplandPlannen.PlanVoor(a, d with { Personen = Array.Empty<Persoon>() }, TestData.Config());
        Assert.Contains("niet (meer)", Assert.Single(weg.Fouten));

        opslag.Verwijder(a.Id);
        Assert.Empty(opslag.Lees());
    }

    // ---------------------------------------------------------------- opkuis

    [Fact]
    public void OpkuisKandidatenEnPlan()
    {
        var d = TestData.Omgeving();
        var nu = new DateTime(2027, 6, 1);
        var medewerkers = OpkuisPlannen.Kandidaten(d, TestData.Config(), OpkuisSoort.Medewerkers, 180, nu);
        Assert.Contains(medewerkers, k => k.Persoon.Sleutel == "ad:cas.vos");
        Assert.DoesNotContain(medewerkers, k => k.Persoon.Sleutel == "ad:oud.account"); // al uitgeschakeld
        Assert.DoesNotContain(medewerkers, k => k.Persoon.IsGast);
        Assert.True(medewerkers.Single(k => k.Persoon.Sleutel == "ad:an.peeters").Bevoorrecht);
        Assert.Empty(OpkuisPlannen.Kandidaten(d, TestData.Config(), OpkuisSoort.Medewerkers, 365, new DateTime(2026, 10, 2)));

        var cas = medewerkers.Single(k => k.Persoon.Sleutel == "ad:cas.vos").Persoon;
        var plan = OpkuisPlannen.Plan(d, OpkuisSoort.Medewerkers, new[] { cas }, new DateOnly(2027, 6, 1)).Plan;
        Assert.Equal(new[] { Actie.AdAccountUitschakelen, Actie.AdKenmerkenZetten }, plan.Select(w => w.Actie));
        Assert.StartsWith("Uitgeschakeld bij opkuisronde 2027-06-01", plan[1].P("description"));
        Assert.Equal(string.Empty, plan[1].Vorige!["description"]);

        var info = d.Personen.Single(p => p.Naam == "Info");
        var cloud = Assert.Single(OpkuisPlannen.Plan(d, OpkuisSoort.Medewerkers, new[] { info }, new DateOnly(2027, 6, 1)).Plan);
        Assert.Equal(Actie.GraphKenmerkenZetten, cloud.Actie);
        Assert.Equal("false", cloud.P("accountEnabled"));

        var gast = d.Personen.Single(p => p.IsGast);
        Assert.Equal(Actie.GraphGastVerwijderen, Assert.Single(OpkuisPlannen.Plan(d, OpkuisSoort.Gasten, new[] { gast }, new DateOnly(2027, 6, 1)).Plan).Actie);
    }

    // ---------------------------------------------------------------- beveiliging

    [Fact]
    public void BeveiligingUitGraphEnStatistiek()
    {
        var mfa = GraphVerbinding.NaarMfa(Json("""{"id":"e-bo","userPrincipalName":"bo@test.be","isMfaRegistered":false,"isMfaCapable":false,"isAdmin":true,"methodsRegistered":["email"],"defaultMfaMethod":"none"}"""));
        Assert.False(mfa.MfaGeregistreerd);
        Assert.Equal(new[] { "email" }, mfa.Methodes);
        Assert.Equal(string.Empty, mfa.StandaardMethode);

        var a = GraphVerbinding.NaarAanmelding(Json("""{"createdDateTime":"2026-10-20T08:00:00Z","userId":"e-bo","userPrincipalName":"bo@test.be","userDisplayName":"Bo","appDisplayName":"Office 365","ipAddress":"203.0.113.5","clientAppUsed":"Browser","location":{"city":"Gent","countryOrRegion":"BE"},"status":{"errorCode":50126,"failureReason":"Invalid username or password"}}"""));
        Assert.False(a.Gelukt);
        Assert.Equal("Gent, BE", a.Locatie);
        Assert.Equal(50126, a.Foutcode);

        var d = TestData.Omgeving() with
        {
            Beveiliging = new BeveiligingGegevens(
                new[] { mfa, new MfaRegistratie("e-an", "an@test.be", true, true, new[] { "microsoftAuthenticatorPush", "mobilePhone" }, "microsoftAuthenticatorPush", true) },
                new[] { new RolLid("Global Administrator", "e-bo", "Bo", "bo@test.be", "user"), new RolLid("Directory Readers", "sp-1", "App", "", "servicePrincipal") }),
        };
        var b = Statistiek.Bereken(d, TestData.Config(), new DateTime(2026, 10, 24)).Beveiliging!;
        Assert.Equal(2, b.MetMfaGegevens);
        Assert.Equal(1, b.ZonderMfa);
        Assert.Equal(1, b.MetBeheerrol);
        Assert.Equal(1, b.GlobaleBeheerders);
        Assert.Equal(1, b.KritiekZonderMfa);
        Assert.Contains(b.Methodes, s => s.Naam == "Authenticator (melding)" && s.Aantal == 1);
    }

    // ---------------------------------------------------------------- snel laden

    [Fact]
    public void AdWijzigingenToepassen()
    {
        const string ou = "OU=U,DC=t";
        const string ou2 = "OU=Weg,DC=t";
        AdGebruiker U(string sam, params string[] lidVan) => new() { SamAccountName = sam, DistinguishedName = $"CN={sam},{ou}", OuPad = "U", MemberOf = lidVan };
        AdGroep G(string sam, params string[] leden) => new() { SamAccountName = sam, Name = sam, DistinguishedName = $"CN={sam},{ou}", Leden = leden, LedenBekend = true, AantalLeden = leden.Length };

        var basis = new AdBasis(new AdStand("dc1", 100),
            new[] { U("a", $"CN=g1,{ou}"), U("b", $"CN=g2,{ou}"), U("d", $"CN=g2,{ou}") },
            new[] { G("g1", $"CN=a,{ou}"), G("g2", $"CN=b,{ou}", $"CN=d,{ou}") });

        // g1 kreeg b erbij; c is nieuw; d is verwijderd; b is verplaatst (zonder dat b zelf gewijzigd is)
        var (gebruikers, groepen) = AdDelta.Toepassen(basis,
            new[] { U("c") },
            new[] { G("g1", $"CN=a,{ou}", $"CN=b,{ou2}") },
            new Dictionary<string, string> { ["a"] = $"CN=a,{ou}", ["b"] = $"CN=b,{ou2}", ["c"] = $"CN=c,{ou}" },
            new Dictionary<string, string> { ["g1"] = $"CN=g1,{ou}", ["g2"] = $"CN=g2,{ou}" });

        Assert.Equal(new[] { "a", "b", "c" }, gebruikers.Select(u => u.SamAccountName).OrderBy(s => s));
        var b = gebruikers.Single(u => u.SamAccountName == "b");
        Assert.Equal($"CN=b,{ou2}", b.DistinguishedName);
        Assert.Equal("Weg", b.OuPad);
        Assert.Equal(new[] { $"CN=g1,{ou}", $"CN=g2,{ou}" }, b.MemberOf.OrderBy(x => x));

        var g2 = groepen.Single(g => g.SamAccountName == "g2");
        Assert.Equal(new[] { $"CN=b,{ou2}" }, g2.Leden);
        Assert.Equal(1, g2.AantalLeden);
    }

    [Fact]
    public void GraphGroepWijzigingenToepassen()
    {
        var basis = new GraphBasis("link",
            new Dictionary<string, CloudGroep>
            {
                ["g1"] = new() { Id = "g1", DisplayName = "Een", Type = CloudGroepType.Microsoft365, AantalLeden = 1 },
                ["g2"] = new() { Id = "g2", DisplayName = "Twee", AantalLeden = 1 },
            },
            new Dictionary<string, IReadOnlySet<string>> { ["u1"] = new HashSet<string> { "g1", "g2" } });

        var items = new[]
        {
            GraphVerbinding.NaarDeltaItem(Json("""{"id":"g1","displayName":"Een (nieuw)","members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u2"},{"@odata.type":"#microsoft.graph.user","id":"u1","@removed":{"reason":"deleted"}},{"@odata.type":"#microsoft.graph.device","id":"pc1"}]}""")),
            GraphVerbinding.NaarDeltaItem(Json("""{"id":"g2","@removed":{"reason":"changed"}}""")),
            GraphVerbinding.NaarDeltaItem(Json("""{"id":"g3","displayName":"Drie","groupTypes":[],"mailEnabled":false,"securityEnabled":true,"members@delta":[{"@odata.type":"#microsoft.graph.user","id":"u1"}]}""")),
        };
        var (groepen, lid) = GraphDelta.Toepassen(basis, items);

        var g1 = groepen.Single(g => g.Id == "g1");
        Assert.Equal("Een (nieuw)", g1.DisplayName);
        Assert.Equal(CloudGroepType.Microsoft365, g1.Type); // soort niet volledig in het antwoord: blijft
        Assert.Equal(1, g1.AantalLeden);
        Assert.DoesNotContain(groepen, g => g.Id == "g2");
        Assert.Equal(CloudGroepType.Beveiliging, groepen.Single(g => g.Id == "g3").Type);
        Assert.Equal(new[] { "g3" }, lid["u1"]);
        Assert.Equal(new[] { "g1" }, lid["u2"]);
        Assert.False(lid.ContainsKey("pc1"));
    }

    [Fact]
    public void AdStandEnDomeincontroller()
    {
        Assert.Equal(new AdStand("CN=NTDS,CN=DC1", 12345), AdLdapMapper.NaarStand("CN=NTDS,CN=DC1", "12345"));
        Assert.Null(AdLdapMapper.NaarStand("", "1"));
        Assert.Null(AdLdapMapper.NaarStand("dc", "geen getal"));
        Assert.True(AdLdapMapper.KanVerderVan(new AdStand("dc1", 100), new AdStand("DC1", 150)));
        Assert.False(AdLdapMapper.KanVerderVan(new AdStand("dc1", 100), new AdStand("dc2", 150)));
        Assert.False(AdLdapMapper.KanVerderVan(new AdStand("dc1", 100), new AdStand("dc1", 50)));
    }

    // ---------------------------------------------------------------- 0.5.1: organisatie en postvakaccounts

    [Fact]
    public void OrganisatieLadenValtTerugOpDeMeegeleverde()
    {
        Directory.CreateDirectory(_map);
        var standaard = Path.Combine(_map, "standaard.json");
        var eigen = Path.Combine(_map, "eigen.json");

        var (_, bron, melding) = ConfigOpslag.LaadVeilig(standaard, eigen);
        Assert.Null(bron);
        Assert.Contains("niet rechtstreeks vanuit de zip", melding);

        ConfigOpslag.Bewaar(TestData.Config(), standaard);
        Assert.Equal((standaard, (string?)null), (ConfigOpslag.LaadVeilig(standaard, eigen).Bron, ConfigOpslag.LaadVeilig(standaard, eigen).Waarschuwing));

        File.WriteAllText(eigen, "{ kapot");
        var kapot = ConfigOpslag.LaadVeilig(standaard, eigen);
        Assert.Equal(standaard, kapot.Bron);
        Assert.NotEmpty(kapot.Config.Populaties);
        Assert.Contains("niet leesbaar", kapot.Waarschuwing);

        File.WriteAllText(eigen, """{ "company": "Contoso" }""");
        var leeg = ConfigOpslag.LaadVeilig(standaard, eigen);
        Assert.Equal(standaard, leeg.Bron);
        Assert.Contains("geen soorten", leeg.Waarschuwing);

        var goed = TestData.Config();
        goed.Company = "Eigen";
        ConfigOpslag.Bewaar(goed, eigen);
        var eigenGoed = ConfigOpslag.LaadVeilig(standaard, eigen);
        Assert.Equal((eigen, "Eigen", (string?)null), (eigenGoed.Bron, eigenGoed.Config.Company, eigenGoed.Waarschuwing));
    }

    [Fact]
    public void PostvakEnRoomaccountsTellenNietMeeAlsMedewerker()
    {
        var bestanden = TestData.Bestanden();
        bestanden[InventarisLader.EntraGebruikers].Add(TestData.EntraGebruiker("e-boek", "boekhouding@test.be", displayName: "Boekhouding"));
        bestanden[InventarisLader.EntraGebruikers].Add(TestData.EntraGebruiker("e-zaal", "rm-vz-a@test.be", displayName: "Vergaderzaal A"));
        var d = InventarisLader.Bouw(bestanden);
        var zonder = Statistiek.Bereken(TestData.Omgeving(), TestData.Config(), new DateTime(2026, 10, 24));
        var met = Statistiek.Bereken(d, TestData.Config(), new DateTime(2026, 10, 24));

        Assert.Equal(zonder.Accounts, met.Accounts);
        Assert.Equal(zonder.NooitAangemeld, met.NooitAangemeld);
        Assert.Equal(2, met.PostvakAccounts);
        Assert.True(d.Index.IsPostvakAccount(d.Personen.Single(p => p.Naam == "Boekhouding")));
        Assert.False(d.Index.IsPostvakAccount(d.ZoekPersoon("ad:an.peeters")!));
        Assert.DoesNotContain(OpkuisPlannen.Kandidaten(d, TestData.Config(), OpkuisSoort.Medewerkers, 1, new DateTime(2030, 1, 1)),
            k => k.Persoon.Naam is "Boekhouding" or "Vergaderzaal A");
    }

    // ---------------------------------------------------------------- hulp

    private static LogRegel Regel(Wijziging w, DateTimeOffset t)
        => new(t, "test", "Uitstroom", w.SysteemTekst, w.Actie.ToString(), w.Doel, w.DoelNaam, w.Omschrijving,
            new Dictionary<string, string>(w.Parameters), "Gelukt", string.Empty, Guid.Empty, w.Id, w.Vorige);

    private static JsonElement Json(string tekst) => JsonDocument.Parse(tekst).RootElement.Clone();

    private sealed class AllesOk(Systeem systeem) : IUitvoerder
    {
        public Systeem Systeem => systeem;

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren)
            => Task.FromResult<IReadOnlyList<WijzigingResultaat>>(wijzigingen.Select(w => WijzigingResultaat.Ok(w)).ToList());
    }

    private sealed class NepHttp(HttpStatusCode status, string inhoud) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Contains("Moss365", request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(inhoud) });
        }
    }
}
