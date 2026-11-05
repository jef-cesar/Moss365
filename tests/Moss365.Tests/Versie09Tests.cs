using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Opslag;
using Moss365.Core.Wijzigingen;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.9: Conditional Access beheren. Vooral de veiligheid: noodaccounts, jezelf niet buitensluiten, alleen rapport.</summary>
public sealed class Versie09Tests : IDisposable
{
    private static readonly DateTime Nu = new(2026, 11, 5, 12, 0, 0);
    private const string Ik = "an.peeters@test.be"; // Global Administrator in deze testgegevens
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-v09-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, true);
        }
    }

    /// <summary>Een beleid zoals Graph het zou geven (met id en de JSON erbij).</summary>
    private static CaBeleid MetJson(CaBeleid b)
    {
        var node = JsonNode.Parse(CaJson.NaarGraph(b))!.AsObject();
        node["id"] = b.Id;
        node["createdDateTime"] = "2026-01-01T00:00:00Z";
        node["modifiedDateTime"] = "2026-02-01T00:00:00Z";
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return CaJson.Lees(doc.RootElement);
    }

    private static InventarisData Data(params CaBeleid[] extra)
    {
        var ca = TestData.Ca(new DateTimeOffset(Nu));
        return TestData.Omgeving() with
        {
            ConditionalAccess = ca with
            {
                Beleiden = ca.Beleiden.Concat(extra).Select(MetJson).ToList(),
                Locaties = ca.Locaties.Select(l => l with { Json = CaJson.LocatieJson(l.Naam, l.IsIp, l.Waarden, l.Vertrouwd).Replace("{", $"{{\"id\":\"{l.Id}\",", StringComparison.Ordinal) }).ToList(),
            },
            Beveiliging = new BeveiligingGegevens(Array.Empty<MfaRegistratie>(),
                new[] { new RolLid("Global Administrator", "e-an", "an peeters", Ik, "user", CaGegevens.RolGlobalAdmin) }),
        };
    }

    /// <summary>Het noodaccount in de testgegevens: het cloud-only account "Info".</summary>
    private static OrganisatieConfig MetNood()
    {
        var c = TestData.Config();
        c.Noodaccounts.Add("info@test.onmicrosoft.com");
        return c;
    }

    private static CaBeleid B(InventarisData d, string id) => d.ConditionalAccess!.Beleiden.Single(b => b.Id == id);

    private static JsonNode Json(Wijziging w, string parameter = "json") => JsonNode.Parse(w.P(parameter))!;

    private static LogRegel Gelogd(Wijziging w) => new(DateTimeOffset.Now, Ik, "c", "Graph", w.Actie.ToString(), w.Doel, w.DoelNaam,
        w.Omschrijving, w.Parameters, "Gelukt", "", Guid.NewGuid(), w.Id, w.Vorige);

    // ---------------------------------------------------------------- sjablonen

    [Fact]
    public void ZonderNoodaccountsGeenNieuwBeleid()
    {
        var u = CaSjablonen.Plan("mfa-iedereen", new CaSjabloonOpties(), Data(), TestData.Config(), Ik);
        Assert.Empty(u.Plan);
        Assert.Contains("geen noodaccounts", u.Fouten[0]);

        var c = TestData.Config();
        c.Noodaccounts.Add("bestaat.niet@test.onmicrosoft.com");
        Assert.Contains("bestaat.niet", CaSjablonen.Plan("mfa-iedereen", new CaSjabloonOpties(), Data(), c, Ik).Fouten[0]);
    }

    [Fact]
    public void EenSjabloonKomtAltijdInAlleenRapportMetDeNoodaccountsUitgesloten()
    {
        var d = Data();
        foreach (var s in CaSjablonen.Alle.Where(s => s.Sleutel != "landen-blokkeren"))
        {
            var u = CaSjablonen.Plan(s.Sleutel, new CaSjabloonOpties(), d, MetNood(), Ik);
            var w = Assert.Single(u.Plan);
            Assert.Equal(Actie.GraphCaBeleidMaken, w.Actie);
            var json = Json(w);
            Assert.Equal("enabledForReportingButNotEnforced", json["state"]!.GetValue<string>());
            Assert.Contains("e-info", json["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Null(CaVeiligheid.Weigering(w, d, MetNood(), Ik));
        }

        // Een pilotgroep in plaats van iedereen
        var pilot = Json(Assert.Single(CaSjablonen.Plan("mfa-iedereen", new CaSjabloonOpties("g-team"), d, MetNood(), Ik).Plan));
        Assert.Equal(new[] { "g-team" }, pilot["conditions"]!["users"]!["includeGroups"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("All", pilot["conditions"]!["users"]!["includeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Phishingbestendig: de ingebouwde sterkte, enkel met haar id
        var beheer = Json(Assert.Single(CaSjablonen.Plan("beheerders-phishingbestendig", new CaSjabloonOpties(), d, MetNood(), Ik).Plan));
        Assert.Equal(CaGegevens.SterktePhishingbestendig, beheer["grantControls"]!["authenticationStrength"]!["id"]!.GetValue<string>());
        Assert.Contains(CaGegevens.RolGlobalAdmin, beheer["conditions"]!["users"]!["includeRoles"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Toestelcode: de overdrachtsmethode
        var code = Json(Assert.Single(CaSjablonen.Plan("toestelcode-blokkeren", new CaSjabloonOpties(), d, MetNood(), Ik).Plan));
        Assert.Equal("deviceCodeFlow", code["conditions"]!["authenticationFlows"]!["transferMethods"]!.GetValue<string>());
    }

    [Fact]
    public void LandenBlokkerenVraagtEenLocatieMetBelgie()
    {
        var d = Data();
        Assert.Contains("Kies de benoemde locatie", CaSjablonen.Plan("landen-blokkeren", new CaSjabloonOpties(), d, MetNood(), Ik).Fouten[0]);
        Assert.Contains("Kies de benoemde locatie", CaSjablonen.Plan("landen-blokkeren", new CaSjabloonOpties(LandenLocatieId: "loc-kantoor"), d, MetNood(), Ik).Fouten[0]);
        var json = Json(Assert.Single(CaSjablonen.Plan("landen-blokkeren", new CaSjabloonOpties(LandenLocatieId: "loc-be"), d, MetNood(), Ik).Plan));
        Assert.Equal("loc-be", json["conditions"]!["locations"]!["excludeLocations"]![0]!.GetValue<string>());

        // Een bestaande naam: niet nog eens
        var dubbel = Data(new CaBeleid { Id = "x", Naam = "MFA voor alle gebruikers", Status = CaStatus.Uit, GebruikersIn = new[] { "All" }, AppsIn = new[] { "All" } });
        Assert.Contains("bestaat al", CaSjablonen.Plan("mfa-iedereen", new CaSjabloonOpties(), dubbel, MetNood(), Ik).Fouten[0]);
    }

    // ---------------------------------------------------------------- aanzetten, en de controles

    [Fact]
    public void AanzettenMagEnkelAlsDeNoodaccountsUitgeslotenZijn()
    {
        var d = Data();

        // "Beheerders phishingbestendig" (rapport) geldt enkel voor de rol: het noodaccount valt er niet onder
        var w = Assert.Single(CaPlannen.Status(B(d, "ca-beheer"), CaStatus.Aan, d, MetNood(), Ik).Plan);
        Assert.Equal("enabled", Json(w)["state"]!.GetValue<string>());
        Assert.Equal("enabledForReportingButNotEnforced", JsonNode.Parse(w.Vorige!["json"])!["state"]!.GetValue<string>());
        Assert.Null(CaVeiligheid.Weigering(w, d, MetNood(), Ik));

        // "Buitenland blokkeren" sluit het noodaccount niet uit: op rapport mag, aanzetten niet
        var land = B(d, "ca-land") with { Status = CaStatus.Rapport };
        var d2 = d with { ConditionalAccess = d.ConditionalAccess! with { Beleiden = d.ConditionalAccess.Beleiden.Select(b => b.Id == "ca-land" ? MetJson(land) : b).ToList() } };
        var aan = CaPlannen.Status(B(d2, "ca-land"), CaStatus.Aan, d2, MetNood(), Ik);
        Assert.Empty(aan.Plan);
        Assert.Contains("noodaccount", aan.Fouten[0]);
        Assert.Contains("Sluit het uit", aan.Fouten[0]);

        // Zonder noodaccounts in de configuratie: nooit aanzetten
        Assert.Contains("geen noodaccounts", CaPlannen.Status(B(d, "ca-beheer"), CaStatus.Aan, d, TestData.Config(), Ik).Fouten[0]);

        // Uitzetten mag altijd
        Assert.Single(CaPlannen.Status(B(d, "ca-land"), CaStatus.Uit, d, TestData.Config(), Ik).Plan);
    }

    [Fact]
    public void JezelfBuitensluitenWeigertDeApp()
    {
        var alles = new CaBeleid
        {
            Id = "ca-alles", Naam = "Alles blokkeren", Status = CaStatus.Uit, GebruikersIn = new[] { "All" }, GebruikersUit = new[] { "e-info" },
            AppsIn = new[] { "All" }, Controles = new[] { "block" },
        };
        var d = Data(alles);
        var u = CaPlannen.Status(B(d, "ca-alles"), CaStatus.Aan, d, MetNood(), Ik);
        Assert.Empty(u.Plan);
        Assert.Contains("jouw eigen account", u.Fouten[0]);

        // Op alleen rapport kan het wel, met een waarschuwing
        var rapport = Assert.Single(CaPlannen.Status(B(d, "ca-alles"), CaStatus.Rapport, d, MetNood(), Ik).Plan);
        Assert.Contains("jouw eigen account", rapport.Waarschuwing);

        // Wie de app gebruikt zonder Entra-account: een waarschuwing, de app kan het niet nakijken
        var onbekend = Assert.Single(CaPlannen.Status(B(d, "ca-beheer"), CaStatus.Aan, d, MetNood(), "iemand@fabrikam.com").Plan);
        Assert.Contains("eigen account staat niet", onbekend.Waarschuwing);
    }

    [Fact]
    public void DeMotorWeigertOokBijOngedaanMakenEnZonderGegevens()
    {
        var d = Data();

        // Een logregel van een beleid dat uitgezet werd: ongedaan maken zet het weer aan, maar het sluit het noodaccount niet uit
        var uit = Assert.Single(CaPlannen.Status(B(d, "ca-land"), CaStatus.Uit, d, MetNood(), Ik).Plan);
        var terug = Assert.Single(Terugdraaien.Plan(new[] { Gelogd(uit) }).Plan);
        Assert.Equal("enabled", Json(terug)["state"]!.GetValue<string>());
        Assert.Contains("noodaccount", CaVeiligheid.Weigering(terug, d, MetNood(), Ik));

        // Niet geladen: niets na te kijken, dus weigeren
        Assert.Contains("niet in de geladen gegevens", CaVeiligheid.Weigering(terug, TestData.Omgeving(), MetNood(), Ik));

        // Andere acties laat deze controle met rust
        Assert.Null(CaVeiligheid.Weigering(new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphGroepLidToevoegen, Doel = "x", Omschrijving = "x" }, d, MetNood(), Ik));
    }

    [Fact]
    public async Task DeMotorVoertEenGeweigerdeWijzigingNooitUit()
    {
        var d = Data();
        var w = new Wijziging
        {
            Systeem = Systeem.Graph, Actie = Actie.GraphCaBeleidWijzigen, Doel = "ca-land", DoelNaam = "Buitenland blokkeren", Omschrijving = "aan",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.StatusPatch(CaStatus.Aan) },
        };
        var uitgevoerd = new List<Wijziging>();
        var logboek = new GeheugenLogboek();
        var r = await new PlanUitvoerder(new[] { new Teller(uitgevoerd) }, logboek, () => Ik, x => CaVeiligheid.Weigering(x, d, MetNood(), Ik))
            .VoerUitAsync(new[] { w }, "test");
        Assert.Equal(ResultaatStatus.Mislukt, Assert.Single(r).Status);
        Assert.StartsWith("Geweigerd", r[0].Melding);
        Assert.Empty(uitgevoerd);
        Assert.Contains("Geweigerd", Assert.Single(logboek.Regels).Melding);
    }

    private sealed class Teller(List<Wijziging> uitgevoerd) : IUitvoerder
    {
        public Systeem Systeem => Systeem.Graph;

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> plan, CancellationToken annuleren)
        {
            uitgevoerd.AddRange(plan);
            return Task.FromResult<IReadOnlyList<WijzigingResultaat>>(plan.Select(x => WijzigingResultaat.Ok(x)).ToList());
        }
    }

    private sealed class GeheugenLogboek : ILogboek
    {
        public List<LogRegel> Regels { get; } = new();

        public void Schrijf(LogRegel regel) => Regels.Add(regel);

        public IReadOnlyList<LogRegel> Recent(int aantal) => Regels.TakeLast(aantal).Reverse().ToList();
    }

    // ---------------------------------------------------------------- uitzonderingen

    [Fact]
    public void UitzonderingMetEinddatumEnTerug()
    {
        var d = Data();
        var cas = d.ZoekPersoon("ad:cas.vos")!;

        // "Buitenland blokkeren" staat aan en sluit het noodaccount niet uit: elke wijziging weigert de app, tot het
        // noodaccount eerst uitgesloten is
        Assert.Contains("noodaccount", CaPlannen.Uitzondering(B(d, "ca-land"), cas, toevoegen: true, d, MetNood(), Ik).Fouten[0]);
        var info = d.Personen.Single(p => p.Entra?.Id == "e-info");
        var nood = Assert.Single(CaPlannen.Uitzondering(B(d, "ca-land"), info, toevoegen: true, d, MetNood(), Ik).Plan);
        d = d with { ConditionalAccess = d.ConditionalAccess! with { Beleiden = d.ConditionalAccess.Beleiden.Select(b => b.Id == "ca-land" ? CaJson.NaPatch(b, nood.P("json")) : b).ToList() } };

        var w = Assert.Single(CaPlannen.Uitzondering(B(d, "ca-land"), cas, toevoegen: true, d, MetNood(), Ik).Plan);
        var users = Json(w)["conditions"]!["users"]!;
        Assert.Equal(new[] { "e-info", "e-cas" }, users["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { "All" }, users["includeUsers"]!.AsArray().Select(x => x!.GetValue<string>())); // de opname gaat mee, niets raakt kwijt
        Assert.Equal(new[] { "e-info" }, JsonNode.Parse(w.Vorige!["json"])!["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Ongedaan maken: de vorige uitzonderingen
        var terug = Assert.Single(Terugdraaien.Plan(new[] { Gelogd(w) }).Plan);
        Assert.Equal(new[] { "e-info" }, Json(terug)["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Het einde staat klaar bij Gepland en wordt op het moment zelf berekend
        var beleidNa = CaJson.NaPatch(B(d, "ca-land"), w.P("json"));
        var dNa = d with { ConditionalAccess = d.ConditionalAccess! with { Beleiden = d.ConditionalAccess.Beleiden.Select(b => b.Id == "ca-land" ? beleidNa : b).ToList() } };
        var gepland = GeplandPlannen.CaUitzonderingEinde(beleidNa, cas, DateTimeOffset.Now.AddDays(7), Ik);
        Assert.Equal(GeplandSoort.CaUitzonderingEinde, gepland.Soort);
        var plan = GeplandPlannen.PlanVoor(gepland, dNa, MetNood());
        Assert.Empty(plan.Fouten);
        Assert.Equal(new[] { "e-info" }, Json(Assert.Single(plan.Plan))["conditions"]!["users"]!["excludeUsers"]!.AsArray().Select(x => x!.GetValue<string>()));

        // Al weg: geen plan, wel de reden
        Assert.Contains("al niet meer uitgezonderd", GeplandPlannen.PlanVoor(gepland, d, MetNood()).Fouten[0]);
    }

    [Fact]
    public void DeUitzonderingVanEenNoodaccountWeghalenWeigertDeApp()
    {
        var d = Data();
        var info = d.Personen.Single(p => p.Entra?.Id == "e-info");
        var u = CaPlannen.Uitzondering(B(d, "ca-mfa"), info, toevoegen: false, d, MetNood(), Ik);
        Assert.Empty(u.Plan);
        Assert.Contains("noodaccount", u.Fouten[0]);
        Assert.Contains("al uitgezonderd", CaPlannen.Uitzondering(B(d, "ca-mfa"), info, toevoegen: true).Fouten[0]);
    }

    // ---------------------------------------------------------------- benoemde locaties

    [Fact]
    public void BenoemdeLocatiesMetControleVanDeWaarden()
    {
        var d = Data();
        var fout = CaPlannen.Locatie(null, "Bibliotheek", isIp: true, new[] { "203.0.113.5", "300.1.1.1/24", "198.51.100.0/24" }, true, d);
        Assert.Empty(fout.Plan);
        Assert.Equal(2, fout.Fouten.Count);
        Assert.True(CaPlannen.IsCidr("2001:db8::/48"));
        Assert.False(CaPlannen.IsCidr("10.0.0.0/0"));

        var nieuw = Assert.Single(CaPlannen.Locatie(null, "Bibliotheek", true, new[] { "198.51.100.0/24", "2001:db8::/48" }, true, d).Plan);
        Assert.Equal(Actie.GraphCaLocatieMaken, nieuw.Actie);
        var json = Json(nieuw);
        Assert.Equal("#microsoft.graph.ipNamedLocation", json["@odata.type"]!.GetValue<string>());
        Assert.True(json["isTrusted"]!.GetValue<bool>());
        Assert.Equal(new[] { "#microsoft.graph.iPv4CidrRange", "#microsoft.graph.iPv6CidrRange" },
            json["ipRanges"]!.AsArray().Select(r => r!["@odata.type"]!.GetValue<string>()));

        // Landen wijzigen: de beleiden die de locatie gebruiken, staan in de waarschuwing
        var be = d.ConditionalAccess!.Locatie("loc-be")!;
        var landen = Assert.Single(CaPlannen.Locatie(be, "België", false, new[] { "be", "nl" }, false, d).Plan);
        Assert.Equal(Actie.GraphCaLocatieWijzigen, landen.Actie);
        Assert.Equal(new[] { "BE", "NL" }, Json(landen)["countriesAndRegions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("Buitenland blokkeren", landen.Waarschuwing);
        Assert.Equal(new[] { "BE" }, JsonNode.Parse(landen.Vorige!["json"])!["countriesAndRegions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("geen landcode", CaPlannen.Locatie(be, "België", false, new[] { "Belgie" }, false, d).Fouten[0]);
        Assert.Contains("kan geen locatie met landen", CaPlannen.Locatie(be, "België", true, new[] { "203.0.113.0/24" }, false, d).Fouten[0]);
        Assert.Contains("bestaat al", CaPlannen.Locatie(null, "gemeentehuis", true, new[] { "203.0.113.0/24" }, true, d).Fouten[0]);
    }

    // ---------------------------------------------------------------- JSON

    [Fact]
    public void JsonVoorGraphZonderWatGraphZelfBijhoudt()
    {
        const string gelezen = """
        {"@odata.context":"x","id":"p1","templateId":null,"displayName":"Test","createdDateTime":"2026-01-01T00:00:00Z","modifiedDateTime":"2026-02-01T00:00:00Z",
         "state":"enabled","conditions":{"users":{"includeUsers":["All"],"excludeUsers":["e-info"],"includeGroups":[],"excludeGroups":[],"includeRoles":[],"excludeRoles":[]},
         "applications":{"includeApplications":["All"]},"clientAppTypes":["all"]},
         "grantControls":{"operator":"OR","builtInControls":[],"authenticationStrength":{"id":"00000000-0000-0000-0000-000000000004","displayName":"Phishing","allowedCombinations":["fido2"],"createdDateTime":"2020-01-01T00:00:00Z"}}}
        """;
        var schrijfbaar = JsonNode.Parse(CaJson.Schrijfbaar(gelezen))!.AsObject();
        foreach (var veld in new[] { "@odata.context", "id", "templateId", "createdDateTime", "modifiedDateTime" })
        {
            Assert.False(schrijfbaar.ContainsKey(veld), veld);
        }

        Assert.Equal("{\"id\":\"00000000-0000-0000-0000-000000000004\"}", schrijfbaar["grantControls"]!["authenticationStrength"]!.ToJsonString());

        using var doc = JsonDocument.Parse(gelezen);
        var b = CaJson.Lees(doc.RootElement);
        Assert.Equal(gelezen, b.Json);
        var na = CaJson.NaPatch(b, CaJson.UitzonderingenPatch(b, new[] { "e-info", "e-cas" }));
        Assert.Equal(new[] { "e-info", "e-cas" }, na.GebruikersUit);
        Assert.Equal(new[] { "All" }, na.GebruikersIn);
        Assert.Equal(CaStatus.Aan, na.Status);
        Assert.Equal("{\"state\":\"enabled\"}", CaJson.Vorige(gelezen, CaJson.StatusPatch(CaStatus.Uit)));
    }

    // ---------------------------------------------------------------- back-ups

    [Fact]
    public void BackupEnkelBijEenVerschilEnTerugzetten()
    {
        var d = Data();
        var ca = d.ConditionalAccess!;
        var eerste = CaBackup.Bewaar(ca, _map, Nu);
        Assert.NotNull(eerste);
        Assert.Null(CaBackup.Bewaar(ca, _map, Nu.AddHours(1))); // niets veranderd

        // Iemand zet "Buitenland blokkeren" uit en verwijdert "Verouderd blokkeren"
        var land = MetJson(B(d, "ca-land") with { Status = CaStatus.Uit });
        var nu = ca with { Beleiden = ca.Beleiden.Where(b => b.Id != "ca-legacy").Select(b => b.Id == "ca-land" ? land : b).ToList() };
        Assert.NotNull(CaBackup.Bewaar(nu, _map, Nu.AddDays(1)));

        var lijst = CaBackup.Lijst(_map);
        Assert.Equal(2, lijst.Count);
        Assert.Equal(Nu.AddDays(1), lijst[0].Tijd);
        var oud = CaBackup.Lees(lijst[1].Pad);
        Assert.Equal(4, oud.Beleiden.Count);
        Assert.Equal(2, oud.Locaties.Count);

        var verschillen = CaVergelijking.Vergelijk(oud.Beleiden, nu.Beleiden, CaLezer.Namen(d), nu);
        Assert.Equal(2, verschillen.Count);
        Assert.Contains(verschillen, v => v.Soort == "verdwenen" && v.Naam == "Verouderd blokkeren");
        Assert.Contains(verschillen, v => v.Soort == "gewijzigd" && v.Details.Any(x => x.Equals("status: aan → uit", StringComparison.OrdinalIgnoreCase)));

        // Terugzetten: een gewijzigd beleid met de volledige JSON uit de back-up (zonder wat Graph bijhoudt), een verdwenen opnieuw maken
        var dNu = d with { ConditionalAccess = nu };
        var oudLand = oud.Beleiden.Single(b => b.Id == "ca-land");
        // Het noodaccount valt onder "Buitenland blokkeren" (aan): terugzetten naar "aan" weigert de app
        Assert.Contains("noodaccount", CaPlannen.Terugzetten(oudLand, B(dNu, "ca-land"), dNu, MetNood(), Ik).Fouten[0]);
        var w = Assert.Single(CaPlannen.Terugzetten(oudLand with { Status = CaStatus.Rapport, Json = oudLand.Json.Replace("\"enabled\"", "\"enabledForReportingButNotEnforced\"") },
            B(dNu, "ca-land"), dNu, MetNood(), Ik).Plan);
        Assert.Equal(Actie.GraphCaBeleidWijzigen, w.Actie);
        Assert.False(Json(w).AsObject().ContainsKey("id"));
        Assert.Equal("disabled", JsonNode.Parse(w.Vorige!["json"])!["state"]!.GetValue<string>());

        var legacy = Assert.Single(CaPlannen.Terugzetten(oud.Beleiden.Single(b => b.Id == "ca-legacy"), null, dNu, MetNood(), Ik).Plan);
        Assert.Equal(Actie.GraphCaBeleidMaken, legacy.Actie);
        Assert.Equal("Verouderd blokkeren", Json(legacy)["displayName"]!.GetValue<string>());

        // Een beleid zonder JSON (niet uit Graph): geen back-up die later iets kwijt zou spelen
        Assert.Null(CaBackup.Bewaar(TestData.Ca(new DateTimeOffset(Nu)), Path.Combine(_map, "ander"), Nu));
    }

    // ---------------------------------------------------------------- vernieuwen en Graph

    [Fact]
    public void NaEenWijzigingAanConditionalAccessWordtCaOpnieuwGelezen()
    {
        var d = Data();
        var w = Assert.Single(CaPlannen.Status(B(d, "ca-land"), CaStatus.Uit, d, MetNood(), Ik).Plan);
        var doelen = VernieuwDoelen.NaPlan(d, new[] { w }, new[] { WijzigingResultaat.Ok(w) });
        Assert.True(doelen.ConditionalAccess);
        Assert.Empty(doelen.EntraGebruikers);
        Assert.False(doelen.Leeg);
        Assert.Contains(Rechten(), r => r == Moss365.Core.Rechten.Recht.Beheren);

        static IEnumerable<Moss365.Core.Rechten.Recht> Rechten()
            => new[] { Actie.GraphCaBeleidMaken, Actie.GraphCaBeleidWijzigen, Actie.GraphCaLocatieMaken, Actie.GraphCaLocatieWijzigen }
                .Select(Moss365.Core.Rechten.Rechtentabel.VoorActie).Distinct();
    }

    private static GraphVerbinding Graph(NepGraph nep) => new(new GraphKlant(new HttpClient(nep), _ => Task.FromResult("token")));

    [Fact]
    public async Task GraphMaaktEnWijzigtBeleidenEnLocaties()
    {
        var d = Data();
        var nieuw = Assert.Single(CaSjablonen.Plan("mfa-iedereen", new CaSjabloonOpties(), d, MetNood(), Ik).Plan);
        var status = Assert.Single(CaPlannen.Status(B(d, "ca-land"), CaStatus.Uit, d, MetNood(), Ik).Plan);
        var locatie = Assert.Single(CaPlannen.Locatie(d.ConditionalAccess!.Locatie("loc-kantoor"), "Gemeentehuis", true, new[] { "203.0.113.0/25" }, true, d).Plan);
        var nep = new NepGraph()
            .Op(HttpMethod.Post, "/identity/conditionalAccess/policies", new { id = "nieuw-id" }, HttpStatusCode.Created)
            .Op(HttpMethod.Patch, "/identity/conditionalAccess/policies/ca-land", "", HttpStatusCode.NoContent)
            .Op(HttpMethod.Patch, "/identity/conditionalAccess/namedLocations/loc-kantoor", "", HttpStatusCode.NoContent);

        var r = await Graph(nep).VoerUitAsync(new[] { nieuw, status, locatie }, CancellationToken.None);
        Assert.All(r, x => Assert.Equal(ResultaatStatus.Gelukt, x.Status));
        Assert.Contains("nieuw-id", r[0].Melding);
        var post = nep.Verzoeken.Single(v => v.Methode == HttpMethod.Post);
        Assert.Equal("enabledForReportingButNotEnforced", JsonNode.Parse(post.Inhoud!)!["state"]!.GetValue<string>());
        Assert.Equal("{\"state\":\"disabled\"}", nep.Verzoeken.Single(v => v.Url.EndsWith("/policies/ca-land", StringComparison.Ordinal)).Inhoud);
        Assert.Contains("203.0.113.0/25", nep.Verzoeken.Single(v => v.Url.Contains("namedLocations", StringComparison.Ordinal)).Inhoud);
    }

    [Fact]
    public async Task GraphLeestDeBeleidenMetHunJsonEnHetAuditlogboek()
    {
        var nep = new NepGraph()
            .Op(HttpMethod.Get, "/identity/conditionalAccess/policies", new { value = new object[] { new { id = "p1", displayName = "Test", state = "disabled", conditions = new { users = new { includeUsers = new[] { "All" } } } } } })
            .Op(HttpMethod.Get, "/auditLogs/directoryAudits", new
            {
                value = new object[]
                {
                    new
                    {
                        activityDateTime = "2026-11-04T10:00:00Z", activityDisplayName = "Update conditional access policy", result = "success",
                        initiatedBy = new { user = new { userPrincipalName = Ik } },
                        targetResources = new[] { new { id = "p1", displayName = "Test" } },
                    },
                },
            });
        var ca = await Graph(nep).LeesConditionalAccessAsync(null, CancellationToken.None);
        Assert.Contains("\"id\":\"p1\"", ca!.Beleiden[0].Json);

        var audit = Assert.Single(await Graph(nep).LeesCaAuditAsync(30, CancellationToken.None));
        Assert.Equal(Ik, audit.Wie);
        Assert.Equal("Test", audit.DoelNaam);
        Assert.Contains("loggedByService", Uri.UnescapeDataString(nep.Verzoeken.Last().Url));

        var geenRecht = new NepGraph().Op(HttpMethod.Get, "/auditLogs/directoryAudits", "{\"error\":{\"code\":\"Authorization_RequestDenied\",\"message\":\"nee\"}}", HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<GeenRechtFout>(() => Graph(geenRecht).LeesCaAuditAsync(30, CancellationToken.None));
    }
}
