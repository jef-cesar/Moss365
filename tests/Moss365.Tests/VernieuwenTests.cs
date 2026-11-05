using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.Connections.Ad;
using Moss365.Connections.Controle;
using Moss365.Connections.Exchange;
using Xunit;

namespace Moss365.Tests;

/// <summary>Objecten vernieuwen, inventaris bewaren, gasten, statistieken en de controle van de pc.</summary>
public sealed class VernieuwenTests : IDisposable
{
    private readonly InventarisData _d = TestData.Omgeving();
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-vernieuw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    private static string WifiDn => TestData.Dn("gg_wifiIntern", TestData.Groepen);

    // ---------------------------------------------------------------- ObjectVernieuwer

    [Fact]
    public void AdGebruikerNaUitstroom()
    {
        var oud = _d.AdGebruikers.Single(u => u.SamAccountName == "an.peeters");
        var dn = "CN=an.peeters,OU=disabled_users,OU=ARCHIVE,DC=test,DC=internal";
        var nieuw = oud with { Enabled = false, DistinguishedName = dn, OuPad = DnHulp.OuPadVanObject(dn), MemberOf = Array.Empty<string>() };

        var d = ObjectVernieuwer.MetAdGebruiker(_d, "an.peeters", nieuw);

        var an = d.ZoekPersoon("ad:an.peeters")!;
        Assert.False(an.Ingeschakeld);
        Assert.Equal("ARCHIVE/disabled_users", an.Ad!.OuPad);
        Assert.NotNull(an.Entra);   // koppeling met Entra blijft
        Assert.DoesNotContain(oud.DistinguishedName, d.AdGroepenOpDn[WifiDn].Leden);
        Assert.Contains(TestData.Dn("bo.janssens", TestData.Gemeente), d.AdGroepenOpDn[WifiDn].Leden);
        Assert.NotSame(_d.Index, d.Index);
        Assert.Equal(_d.Postvakken.Count, d.Postvakken.Count);
    }

    [Fact]
    public void AdGroepWerktMemberOfBij()
    {
        var groep = _d.AdGroepenOpDn[WifiDn];
        var cas = TestData.Dn("cas.vos", TestData.Ocmw);
        var d = ObjectVernieuwer.MetAdGroep(_d, "gg_wifiIntern", groep with { Leden = new[] { cas } });

        Assert.Contains(WifiDn, d.ZoekPersoon("ad:cas.vos")!.Ad!.MemberOf);
        Assert.DoesNotContain(WifiDn, d.ZoekPersoon("ad:an.peeters")!.Ad!.MemberOf);

        var weg = ObjectVernieuwer.MetAdGroep(_d, "gg_wifiIntern", null);
        Assert.False(weg.AdGroepenOpDn.ContainsKey(WifiDn));
    }

    [Fact]
    public void EntraEnCloudgroepen()
    {
        var bo = _d.EntraGebruikers.Single(e => e.Id == "e-bo");
        var d = ObjectVernieuwer.MetEntraGebruiker(_d, "e-bo", new GraphGebruikerLezing(bo with { AccountEnabled = false }, new[] { "g-sm" }));
        Assert.False(d.EntraGebruikers.Single(e => e.Id == "e-bo").AccountEnabled);
        Assert.Equal(new[] { "g-sm" }, d.CloudLidmaatschap["e-bo"]);

        var team = _d.CloudGroepen["g-team"];
        var d2 = ObjectVernieuwer.MetCloudGroep(_d, "g-team", new GraphGroepLezing(team, new[] { "e-cas" }));
        Assert.Contains("g-team", d2.CloudLidmaatschap["e-cas"]);
        Assert.DoesNotContain("g-team", d2.CloudLidmaatschap["e-an"]);
        Assert.Equal(1, d2.CloudGroepen["g-team"].AantalLeden);
    }

    [Fact]
    public void ExchangeObjectenVervangenEnVerwijderen()
    {
        var info = _d.Postvakken.Single(p => p.Smtp == "info@test.be");
        var o = new ExchangeObjecten(Array.Empty<Room>(),
            new ExchangeGegevens(Array.Empty<ExoGroep>(), new[] { info with { DisplayName = "Info (nieuw)" } }, new[] { new PostvakRecht("info@test.be", "FullAccess", "sg-sm-info-full@test.be") }),
            new[] { "boekhouding@test.be" });

        var d = ObjectVernieuwer.MetExchange(_d, o);

        Assert.Equal("Info (nieuw)", d.Postvakken.Single(p => p.Smtp == "info@test.be").DisplayName);
        Assert.Single(d.PostvakRechten, r => r.Postvak == "info@test.be");
        Assert.DoesNotContain(d.Postvakken, p => p.Smtp == "boekhouding@test.be");
        Assert.DoesNotContain(d.PostvakRechten, r => r.Postvak == "boekhouding@test.be");
        Assert.Equal(_d.Rooms.Count, d.Rooms.Count);
    }

    // ---------------------------------------------------------------- VernieuwDoelen

    [Fact]
    public void NaUitstroomDeGebruikerEnZijnGroepen()
    {
        var an = _d.ZoekPersoon("ad:an.peeters")!;
        var plan = PlanBouwer.Uitstroom(_d, TestData.Config(), an, new UitstroomOpties { AdGroepenVerwijderen = true });
        var resultaten = plan.Where(w => w.Geselecteerd).Select(w => WijzigingResultaat.Ok(w)).ToList();

        var doelen = VernieuwDoelen.NaPlan(_d, plan, resultaten);

        Assert.Contains("an.peeters", doelen.AdGebruikers);
        Assert.Contains("e-an", doelen.EntraGebruikers);
        Assert.Contains("gg_wifiIntern", doelen.AdGroepen);
        Assert.Contains("gebruiker:ad:an.peeters", doelen.Objecten);

        // Niet gelukt = niet vernieuwen
        Assert.True(VernieuwDoelen.NaPlan(_d, plan, Array.Empty<WijzigingResultaat>()).Leeg);
    }

    [Fact]
    public void NieuweObjectenEnGasten()
    {
        var nieuw = new Wijziging { Systeem = Systeem.ActiveDirectory, Actie = Actie.AdGebruikerAanmaken, Doel = "CN=x", Omschrijving = "x" };
        nieuw.Parameters["sAMAccountName"] = "eva.desmet";
        var postvak = new Wijziging { Systeem = Systeem.Exchange, Actie = Actie.ExchangeGedeeldPostvakAanmaken, Doel = "jeugd@test.be", Omschrijving = "x" };
        var gast = new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphGastUitnodigen, Doel = "x@fabrikam.com", Omschrijving = "x" };
        var id = Guid.NewGuid().ToString();

        var doelen = VernieuwDoelen.NaPlan(_d, new[] { nieuw, postvak, gast },
            new[] { WijzigingResultaat.Ok(nieuw), WijzigingResultaat.Ok(postvak), WijzigingResultaat.Ok(gast, $"Uitgenodigd; id {id}") });

        Assert.Contains("eva.desmet", doelen.AdGebruikers);
        Assert.Contains("jeugd@test.be", doelen.ExchangeAdressen);
        Assert.Contains(id, doelen.EntraGebruikers);
    }

    [Fact]
    public void VoorEenGroepAlleKanten()
    {
        var g = _d.Index.Groep("cloud:g-sm")!;
        var doelen = VernieuwDoelen.Voor(_d, new[] { g.Ref, _d.Index.Ref("rm-vz-a@test.be") });
        Assert.Contains("g-sm", doelen.CloudGroepen);
        Assert.Contains("sg-sm-info-full@test.be", doelen.ExchangeAdressen);
        Assert.Contains("rm-vz-a@test.be", doelen.ExchangeAdressen);
    }

    // ---------------------------------------------------------------- inventaris bewaren

    [Fact]
    public void BewarenEnTerugLezenGeeftDezelfdeGegevens()
    {
        var alles = InventarisOnderdeel.Ad | InventarisOnderdeel.Entra | InventarisOnderdeel.Rooms | InventarisOnderdeel.Postvakken;
        var map = InventarisSchrijver.Bewaar(_d, _map, alles, new DateTime(2026, 10, 23, 10, 42, 0));
        Assert.EndsWith("Inventaris-20261023-1042-live", map);

        var terug = InventarisLader.Laad(InventarisLader.ZoekMappen(_map));
        Assert.Equal(_d.AdGebruikers.Count, terug.AdGebruikers.Count);
        Assert.Equal(_d.ZoekPersoon("ad:an.peeters")!.Ad!.MemberOf, terug.ZoekPersoon("ad:an.peeters")!.Ad!.MemberOf);
        Assert.Equal(_d.AdGroepenOpDn[WifiDn].Leden, terug.AdGroepenOpDn[WifiDn].Leden);
        Assert.True(terug.AdGroepenOpDn[WifiDn].LedenBekend);
        Assert.Equal(_d.EntraGebruikers.Count, terug.EntraGebruikers.Count);
        Assert.Equal(_d.CloudLidmaatschap["e-an"].OrderBy(x => x), terug.CloudLidmaatschap["e-an"].OrderBy(x => x));
        Assert.Equal(_d.CloudGroepen["g-sm"], terug.CloudGroepen["g-sm"]);
        Assert.Equal(_d.Licenties, terug.Licenties);
        Assert.Equal(_d.Rooms, terug.Rooms);
        Assert.Equal(_d.PostvakRechten.Count, terug.PostvakRechten.Count);
        Assert.Equal(_d.Postvakken.Count, terug.Postvakken.Count);
        Assert.Equal(_d.ExoGroepen.Single(g => g.Smtp == "rl-gh@test.be").Leden, terug.ExoGroepen.Single(g => g.Smtp == "rl-gh@test.be").Leden);
    }

    [Fact]
    public void EnkelDeLiveOnderdelenWordenBewaard()
    {
        var map = InventarisSchrijver.Bewaar(_d, _map, InventarisOnderdeel.Rooms, DateTime.Now);
        Assert.Equal(new[] { InventarisLader.ExoRooms }, Directory.GetFiles(map).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------- gasten

    [Fact]
    public void GastUitnodigenEnVerwijderen()
    {
        var u = GastPlannen.Uitnodigen(_d, "jan@example.net", "Jan (Leverancier)", bericht: true);
        var w = Assert.Single(u.Plan);
        Assert.Equal(Actie.GraphGastUitnodigen, w.Actie);
        Assert.Equal("True", w.P("bericht"));

        Assert.NotEmpty(GastPlannen.Uitnodigen(_d, "geen-adres", "x", true).Fouten);
        Assert.Contains("bestaat al", Assert.Single(GastPlannen.Uitnodigen(_d, "an.peeters@test.be", "An", true).Fouten));

        var gast = _d.Personen.Single(p => p.IsGast);
        Assert.Equal(Actie.GraphGastVerwijderen, Assert.Single(GastPlannen.Verwijderen(_d, new[] { gast }).Plan).Actie);

        // Een medewerker wordt nooit verwijderd
        var an = _d.ZoekPersoon("ad:an.peeters")!;
        var weigering = GastPlannen.Verwijderen(_d, new[] { an });
        Assert.Empty(weigering.Plan);
        Assert.Contains("Uitstroom", Assert.Single(weigering.Fouten));
    }

    // ---------------------------------------------------------------- statistieken

    [Fact]
    public void StatistiekenUitDeTestgegevens()
    {
        var s = Statistiek.Bereken(_d, TestData.Config(), new DateTime(2026, 10, 23));
        Assert.Equal(1, s.Gasten);
        Assert.True(s.Actief > 0);
        Assert.Equal(s.Actief + s.Uitgeschakeld, s.Accounts);
        Assert.Equal(1, s.BevoorrechteLeden);   // an.peeters in Domain Admins
        Assert.Contains(s.Licenties, l => l.Naam == "SPB" && Statistiek.BijnaVol(l));
        Assert.Contains(s.Licenties, l => l.Naam == "DESKLESSPACK" && !Statistiek.BijnaVol(l));
        Assert.Equal(2, s.GedeeldePostvakken);
        Assert.Equal(1, s.ZonderToegangsgroep);
        Assert.Equal(1, s.RoomsMetAfwijking);
        Assert.Equal(2, s.Rechtenmodel.Sum(r => r.Aantal));
    }

    // ---------------------------------------------------------------- controle van de pc

    [Fact]
    public void ModulesBeoordelen()
    {
        var regels = SysteemControle.Beoordeel("PowerShell 7", "pwsh.exe", appGebruiktDeze: true, new[]
        {
            new SysteemControle.ModuleVersie("ExchangeOnlineManagement", "3.0.0"),
            new SysteemControle.ModuleVersie("Microsoft.Graph.Authentication", "2.20.0"),
        }).ToList();

        var exo = regels.Single(r => r.Onderdeel == "ExchangeOnlineManagement");
        Assert.Equal(ControleStatus.Fout, exo.Status);
        Assert.Equal("ExchangeOnlineManagement", exo.Module);
        Assert.Equal(ControleStatus.Goed, regels.Single(r => r.Onderdeel == "Microsoft.Graph.Authentication").Status);
        Assert.Equal(ControleStatus.Waarschuwing, regels.Single(r => r.Onderdeel == "Microsoft.Graph.Users").Status);

        // RSAT kan niet via Install-Module: geen knop, wel de opdracht
        var rsat = regels.Single(r => r.Onderdeel.StartsWith("ActiveDirectory", StringComparison.Ordinal));
        Assert.Null(rsat.Module);
        Assert.Contains("Add-WindowsCapability", rsat.Oplossing);

        // In de tweede PowerShell is een ontbrekende Exchange-module enkel een waarschuwing
        Assert.Equal(ControleStatus.Waarschuwing, SysteemControle.Beoordeel("5.1", "ps.exe", false, Array.Empty<SysteemControle.ModuleVersie>())
            .Single(r => r.Onderdeel == "ExchangeOnlineManagement").Status);
    }

    [Fact]
    public async Task ControleMetNepPowerShellEnNetwerk()
    {
        var starter = new JsonPowerShell("{\"PowerShell\":\"7.4.5\",\"Editie\":\"Core\",\"Modules\":[{\"Naam\":\"ExchangeOnlineManagement\",\"Versie\":\"3.7.2\"}],\"Installatie\":\"Gelukt\"}");
        var controle = new SysteemControle(_ => starter, () => new[] { ("PowerShell 7", "pwsh.exe") },
            (host, poort, _) => Task.FromResult(host != "outlook.office365.com" && poort != 636), _map);

        var regels = await controle.ControleerAsync(new VerbindingInstellingen { AdServer = "10.0.0.1" }, null, CancellationToken.None);

        Assert.Contains(regels, r => r.Onderdeel == "Versie" && r.Waarde == "7.4.5 (Core)");
        Assert.Equal(ControleStatus.Goed, regels.Single(r => r.Onderdeel == "ExchangeOnlineManagement").Status);
        Assert.Equal("bereikbaar (poort 389)", regels.Single(r => r.Onderdeel.StartsWith("Domeincontroller", StringComparison.Ordinal)).Waarde);
        Assert.Equal(ControleStatus.Fout, regels.Single(r => r.Onderdeel == "outlook.office365.com").Status);
        Assert.Equal(ControleStatus.Waarschuwing, regels.Single(r => r.Onderdeel == "App-registratie").Status);

        Assert.Equal("Gelukt", await controle.InstalleerAsync("pwsh.exe", "Microsoft.Graph.Users", CancellationToken.None));
        Assert.Contains("-Installeer", starter.Argumenten);
        Assert.StartsWith("Mislukt", await controle.InstalleerAsync("andere.exe", "x", CancellationToken.None));
    }

    private sealed class JsonPowerShell(string json) : IPowerShellStarter
    {
        public List<string> Argumenten { get; } = new();

        public Task<(int Code, string Fouten)> StartAsync(string script, IReadOnlyList<string> argumenten, IReadOnlyDictionary<string, string>? omgeving, CancellationToken annuleren)
        {
            Argumenten.AddRange(argumenten);
            File.WriteAllText(argumenten[argumenten.ToList().IndexOf("-Uitvoer") + 1], json);
            return Task.FromResult((0, string.Empty));
        }
    }

    [Fact]
    public void LdapFilterwaardeWordtOntsnapt()
    {
        Assert.Equal("jan\\2a\\28x\\29\\5c", AdLdapMapper.Filterwaarde("jan*(x)\\"));
        Assert.Equal("an.peeters", AdLdapMapper.Filterwaarde("an.peeters"));
    }
}
