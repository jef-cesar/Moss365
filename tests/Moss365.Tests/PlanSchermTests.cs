using AngleSharp.Dom;
using Bunit;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Componenten;
using Microsoft.JSInterop;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>
/// Schermtests voor de schrijfacties: plan maken, bevestigen, uitvoeren via nepverbindingen, logboek en opvolging.
/// Er wordt nergens echt verbonden.
/// </summary>
public sealed partial class PlanSchermTests : TestContext, IDisposable
{
    private readonly string _basis = Path.Combine(Path.GetTempPath(), "kb-plan-" + Guid.NewGuid().ToString("N"));
    private readonly NepFabriek _fabriek = new();

    public PlanSchermTests()
    {
        TestData.SchrijfInventaris(Path.Combine(_basis, "Inventaris", "Inventaris-20261016-1343"));
        var standaard = Path.Combine(_basis, "standaard.json");
        ConfigOpslag.Bewaar(TestData.Config(), standaard);
        Services.AddSingleton<IOmgeving>(new VasteOmgeving(Path.Combine(_basis, "Inventaris"), standaard, Path.Combine(_basis, "eigen", "organisatie.json"), Scripts: Path.Combine(_basis, "scripts-ingebouwd")));
        Services.AddSingleton<IVerbindingsFabriek>(_fabriek);
        Services.AddSingleton<BeheerState>();

        var state = Services.GetRequiredService<BeheerState>();
        state.Instellingen.AdServer = "dc.test.internal";
        state.Instellingen.TenantId = "tenant";
        state.Instellingen.ClientId = "client";
        state.BewaarInstellingen(); // anders staan de verbindingsinstellingen als "niet bewaard"
    }

    private BeheerState State => Services.GetRequiredService<BeheerState>();

    void IDisposable.Dispose()
    {
        base.Dispose();

        // Een achtergrondtaak (vernieuwen na een plan) kan nog net iets schrijven: even opnieuw proberen
        for (var poging = 0; poging < 10 && Directory.Exists(_basis); poging++)
        {
            try
            {
                Directory.Delete(_basis, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void Bevestig(IRenderedFragment cut)
    {
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, uitvoeren").Click();
    }

    private static IElement Veld(IRenderedFragment cut, string label, string soort = "input")
        => cut.FindAll("label").First(l => l.TextContent.TrimStart().StartsWith(label, StringComparison.Ordinal)).QuerySelector(soort)!;

    [Fact]
    public void SpiegelToepassenNaBevestiging()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        var cut = RenderComponent<Spiegel>();

        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("gg_rww_foxbeleid", cut.Find("section.kb-plan").TextContent);
        Assert.DoesNotContain("Domain Admins", cut.Find("section.kb-plan").TextContent);

        // Eerst uitvoeren, dan bevestigen: pas dan gebeurt er iets
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        Assert.Empty(_fabriek.Uitgevoerd);
        cut.FindAll("button").First(b => b.TextContent == "Ja, uitvoeren").Click();

        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        // De gesynchroniseerde groep via AD (één keer), de mailgroepen via Exchange
        Assert.Equal(
            new[] { "AdGroepLidToevoegen gg_rww_foxbeleid", "ExchangeGroepLidToevoegen gemeentehuis@test.be", "ExchangeGroepLidToevoegen sg-sm-info-full@test.be" },
            _fabriek.Uitgevoerd.Select(w => $"{w.Actie} {w.P("groep")}").OrderBy(t => t, StringComparer.Ordinal));

        var log = State.Logboek.Recent(10);
        Assert.Equal(3, log.Count);
        Assert.All(log, r => Assert.Equal("Gelukt", r.Status));
        Assert.All(log, r => Assert.Contains("Spiegel", r.Context));
    }

    [Fact]
    public void PlanZonderVerbindingWaarschuwt()
    {
        State.Instellingen.AdServer = string.Empty;
        Services.GetRequiredService<NavigationManager>().NavigateTo("spiegel?referentie=ad%3Aan.peeters&doel=ad%3Abo.janssens");
        var cut = RenderComponent<Spiegel>();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Geen verbinding ingesteld voor: AD", cut.Markup);
    }

    [Fact]
    public void UitgevinktWordtNietUitgevoerd()
    {
        var plan = new List<Wijziging>
        {
            new() { Systeem = Systeem.ActiveDirectory, Actie = Actie.AdKenmerkenZetten, Doel = "CN=a", Omschrijving = "een" },
            new() { Systeem = Systeem.ActiveDirectory, Actie = Actie.AdKenmerkenZetten, Doel = "CN=b", Omschrijving = "twee" },
        };
        var cut = RenderComponent<PlanWeergave>(p => p.Add(x => x.Plan, plan).Add(x => x.Context, "test"));
        cut.Find("input[aria-label=twee]").Change(false);
        Assert.Contains("Uitvoeren (1)", cut.Markup);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.Equal("CN=a", Assert.Single(_fabriek.Uitgevoerd).Doel);
    }

    [Fact]
    public void NieuweGebruikerAanmakenToontWachtwoordEnBewaartOpvolging()
    {
        var cut = RenderComponent<NieuweGebruiker>();
        Veld(cut, "Soort", "select").Change("Gemeente");
        Veld(cut, "Voornaam").Change("Eva");
        Veld(cut, "Achternaam").Change("De Smet");
        Veld(cut, "Dienst", "select").Change("cultuur");
        Veld(cut, "Kantoor", "select").Change("gemeentehuis");
        Veld(cut, "Licentie", "select").Change("DESKLESSPACK");
        cut.FindAll("button").First(b => b.TextContent == "Voorbeeld").Click();

        Assert.Contains("eva.desmet@test.be", cut.Markup);
        Assert.Contains("cultuur.bat", cut.Markup);
        Assert.Contains("Licentie DESKLESSPACK toekennen", cut.Markup);
        Assert.Contains("Standaardgroep bestaat_niet niet gevonden", cut.Markup);
        Assert.DoesNotContain("Startwachtwoord", cut.Markup);

        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("Startwachtwoord", cut.Markup));

        var aanmaken = _fabriek.Uitgevoerd.First(w => w.Actie == Actie.AdGebruikerAanmaken);
        Assert.Equal(aanmaken.Geheim, cut.Find("code.kb-wachtwoord").TextContent);
        Assert.Equal("cultuur", aanmaken.P("department"));
        Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.AdGroepLidToevoegen && w.P("groep") == "gg_wifiIntern");

        // Het wachtwoord staat nergens in het logboek of de opvolging
        var bestanden = Directory.GetFiles(Path.Combine(_basis, "eigen"), "*", SearchOption.AllDirectories);
        Assert.All(bestanden, f => Assert.DoesNotContain(aanmaken.Geheim!, LeesGedeeld(f)));

        var taak = Assert.Single(State.Opvolging.Lees());
        Assert.Equal("eva.desmet@test.be", taak.Upn);
        Assert.Contains(taak.Wijzigingen, w => w.Actie == Actie.GraphLicentieToekennen);
        Assert.Contains(taak.Wijzigingen, w => w.Actie == Actie.GraphGroepLidToevoegen && w.P("groep") == "AFD_VRIJETIJD");
    }

    [Fact]
    public void OpvolgingWachtTotDeGebruikerInEntraBestaat()
    {
        State.Opvolging.Voegtoe(new OpvolgTaak
        {
            Upn = "eva.desmet@test.be",
            Omschrijving = "Nieuwe gebruiker Eva De Smet",
            Wijzigingen = new() { new Wijziging { Systeem = Systeem.Graph, Actie = Actie.GraphLicentieToekennen, Doel = "eva.desmet@test.be", Omschrijving = "Licentie" } },
        });

        var cut = RenderComponent<Opvolging>();
        cut.FindAll("button").First(b => b.TextContent == "Afwerken").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, afwerken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Nog niet in Entra", cut.Markup));
        Assert.Empty(_fabriek.Uitgevoerd);

        _fabriek.GraphId = "e-eva";
        cut.FindAll("button").First(b => b.TextContent == "Afwerken").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, afwerken").Click();
        cut.WaitForAssertion(() => Assert.Contains("Alles afgewerkt", cut.Markup));
        Assert.Equal(Actie.GraphLicentieToekennen, Assert.Single(_fabriek.Uitgevoerd).Actie);
        Assert.Empty(State.Opvolging.Lees());
    }

    [Fact]
    public void UitstroomVerplaatstEnHaaltBevoorrechteGroepenWeg()
    {
        var cut = RenderComponent<Uitstroom>(p => p.Add(x => x.Sleutel, "ad:an.peeters"));
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("Verplaatsen naar ARCHIVE/disabled_users", plan);
        Assert.Contains("Uit AD-groep Domain Admins halen (bevoorrecht)", plan);
        Assert.DoesNotContain("verwijderen", plan, StringComparison.OrdinalIgnoreCase);

        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.Equal(Actie.AdVerplaatsen, _fabriek.Uitgevoerd.Last(w => w.Systeem == Systeem.ActiveDirectory).Actie);
        Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.AdGroepLidVerwijderen && w.P("groep") == "Domain Admins");
        Assert.DoesNotContain(_fabriek.Uitgevoerd, w => w.Actie == Actie.AdGroepLidVerwijderen && w.P("groep") == "gg_wifiIntern");
    }

    [Fact]
    public void RoomsNaarDeStandaardBrengen()
    {
        var cut = RenderComponent<Rooms>();
        Assert.Contains("Boekingsvenster: 600 (standaard 180)", cut.Markup);
        cut.Find("thead th.kb-kies input").Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Standaard invullen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();

        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("BookingWindowInDays=180", plan);
        Assert.DoesNotContain("Vergaderzaal B", plan);

        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.All(_fabriek.Uitgevoerd, w => Assert.Equal(Systeem.Exchange, w.Systeem));
        Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.ExchangeAgendaRecht && w.P("AccessRights") == "Reviewer");
    }

    [Fact]
    public void LiveLadenVervangtDeGegevens()
    {
        var cut = RenderComponent<UI.Pages.Verbindingen>();
        Assert.DoesNotContain("enkel beschikbaar in de Windows-app", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Live laden (AD en Entra)").Click();
        WachtOpTaak();
        cut.WaitForAssertion(() => Assert.Contains("Bewaren als inventaris", cut.Markup));
        Assert.Equal("Live", State.Data.Herkomst);
        Assert.Equal("DC=test,DC=internal", State.Data.DomeinDn);

        // Het wachtwoord wordt nooit bewaard
        State.AdWachtwoord = "Geheim123!";
        State.BewaarInstellingen();
        Assert.Contains("dc.test.internal", State.Databank.LeesDocument("verbinding"));
        Assert.DoesNotContain("Geheim123!", DatabankOpSchijf());
    }

    [Fact]
    public void LogboekToontUitgevoerdeWijzigingen()
    {
        State.Logboek.Schrijf(new LogRegel(DateTimeOffset.Now, "beheerder", "test", "AD", "AdKenmerkenZetten", "CN=x", "Xavier",
            "Omschrijving aanpassen", new Dictionary<string, string>(), "Mislukt", "geen rechten"));
        var cut = RenderComponent<Logboek>();
        Assert.Contains("geen rechten", cut.Markup);
        Assert.Contains("kb-ontbreekt", cut.Markup);
    }

    private static void Kies(IRenderedFragment cut, string label, string zoek, string resultaat)
    {
        var kiezer = cut.FindAll(".kb-kiezer").First(k => k.QuerySelector("label")?.TextContent.Trim() == label);
        kiezer.QuerySelector("input")!.Input(zoek);
        cut.FindAll(".kb-kiezer").First(k => k.QuerySelector("label")?.TextContent.Trim() == label)
            .QuerySelectorAll("button.kb-link").First(b => b.TextContent.Contains(resultaat, StringComparison.OrdinalIgnoreCase)).Click();
    }

    [Fact]
    public void GroepenLijstLinktNaarDeGroep()
    {
        var cut = RenderComponent<Groepen>();
        Assert.Contains("href=\"groep/ad%3Agg_eigen\"", cut.Markup);
        cut.Find("input[aria-label=Zoeken]").Input("SG-SM");
        Assert.Single(cut.FindAll("tbody tr"));
    }

    [Fact]
    public void GroepToontRelatiesEnVoegtLidToe()
    {
        var cut = RenderComponent<GroepDetail>(p => p.Add(x => x.Sleutel, "ad:gg_eigen"));
        Assert.Contains("href=\"groep/ad%3Agg_rww_foxbeleid\"", cut.Markup);   // geneste groep als lid
        Assert.Contains("href=\"gebruiker/ad%3Abo.janssens\"", cut.Markup);

        Kies(cut, "Lid toevoegen", "cas", "cas vos");
        Assert.Contains("Lid maken van AD-groep gg_eigen", cut.Find("section.kb-plan").TextContent);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        var w = Assert.Single(_fabriek.Uitgevoerd);
        Assert.Equal(Actie.AdGroepLidToevoegen, w.Actie);
        Assert.Equal(TestData.Dn("cas.vos", TestData.Ocmw), w.Doel);
    }

    [Fact]
    public void GroepLidVanEnPostvakRechten()
    {
        var fox = RenderComponent<GroepDetail>(p => p.Add(x => x.Sleutel, "ad:gg_rww_foxbeleid"));
        Assert.Contains("gg_eigen", fox.FindAll("section.kb-kaart").First(s => s.TextContent.Contains("Lid van")).TextContent);

        var sg = RenderComponent<GroepDetail>(p => p.Add(x => x.Sleutel, "cloud:g-sm"));
        Assert.Contains("href=\"postvak/info%40test.be\"", sg.Markup);
        sg.FindAll("button").First(b => b.TextContent == "Gekozen leden verwijderen (0)");
    }

    [Fact]
    public void CloudEigenaarsLiveLaden()
    {
        var cut = RenderComponent<GroepDetail>(p => p.Add(x => x.Sleutel, "cloud:g-team"));
        cut.FindAll("button").First(b => b.TextContent == "Eigenaars live laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("eigenaar in Entra", cut.Markup));
    }

    [Fact]
    public void PostvakRechtstreekseRechtenOpruimen()
    {
        var cut = RenderComponent<PostvakDetail>(p => p.Add(x => x.Smtp, "info@test.be"));
        Assert.Contains("SG-SM-info-Full", cut.Markup);
        Assert.Contains("rechtstreeks", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent == "Rechtstreekse rechten opruimen").Click();
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.ExchangeGroepLidToevoegen && w.Doel == "bo.janssens@test.be");
        Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.ExchangePostvakRechtVerwijderen && w.P("Trustee") == "bo.janssens@test.be");
        Assert.DoesNotContain(_fabriek.Uitgevoerd, w => w.P("Trustee").StartsWith("S-1-5", StringComparison.Ordinal));
    }

    [Fact]
    public void PostvakZonderGroepEnToegangGeven()
    {
        var cut = RenderComponent<PostvakDetail>(p => p.Add(x => x.Smtp, "boekhouding@test.be"));
        Assert.Contains("SG-SM-boekhouding-Full", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Toegangsgroep aanmaken").Click();
        Assert.Contains("Mail-beveiligingsgroep SG-SM-boekhouding-Full aanmaken", cut.Find("section.kb-plan").TextContent);

        var info = RenderComponent<PostvakDetail>(p => p.Add(x => x.Smtp, "info@test.be"));
        Kies(info, "Toegang geven aan", "cas", "cas vos");
        Assert.Contains("SG-SM-info-Full", info.Find("section.kb-plan").TextContent);
    }

    [Fact]
    public void NieuwGedeeldPostvak()
    {
        var cut = RenderComponent<NieuwPostvak>();
        Veld(cut, "Weergavenaam").Change("Jeugddienst");
        Veld(cut, "Adres").Change("jeugddienst");
        Kies(cut, "Wie krijgt toegang?", "cas", "cas vos");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Equal(5, cut.FindAll("section.kb-plan tbody tr").Count);
        Assert.Contains("jeugddienst@test.be", cut.Markup);
    }

    [Fact]
    public void RoomPaginaMetRelaties()
    {
        var cut = RenderComponent<RoomDetail>(p => p.Add(x => x.Smtp, "rm-vz-a@test.be"));
        Assert.Contains("Wijkt af van de standaard", cut.Markup);
        Assert.Contains("rl-gh", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Standaard invullen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("BookingWindowInDays=180", cut.Find("section.kb-plan").TextContent);
    }

    [Fact]
    public void GebruikerToontPostvakkenEnLinks()
    {
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:an.peeters"));
        Assert.Contains("href=\"postvak/boekhouding%40test.be\"", cut.Markup);
        Assert.Contains("href=\"postvak/info%40test.be\"", cut.Markup);
        Assert.Contains("href=\"groep/ad%3Agg_wifiintern\"", cut.Markup);
    }

    [Fact]
    public void AanmeldBalkToontLinkOmTeKopieren()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = RenderComponent<AanmeldBalk>();
        Assert.Empty(cut.Markup.Trim());

        using var cts = new CancellationTokenSource();
        State.Aanmelding.Toon(new Uri("https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize?client_id=x"), cts);
        cut.WaitForAssertion(() => Assert.Contains("login.microsoftonline.com", cut.Find("input").GetAttribute("value")));

        cut.FindAll("button").First(b => b.TextContent == "Kopiëren").Click();
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "moss365.kopieer");

        cut.FindAll("button").First(b => b.TextContent == "Annuleren").Click();
        Assert.True(cts.IsCancellationRequested);

        State.Aanmelding.Klaar();
        cut.WaitForAssertion(() => Assert.Empty(cut.Markup.Trim()));
    }

    [Fact]
    public void PostvakkenLiveLaden()
    {
        var cut = RenderComponent<UI.Pages.Verbindingen>();
        cut.FindAll("button").First(b => b.TextContent == "Postvakken en groepen laden uit Exchange").Click();
        WachtOpTaak();
        Assert.Single(State.Data.Postvakken);
        Assert.Contains(State.Data.Bronnen, b => b.Bestand.StartsWith("Postvakken", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 0.4: tabellen, terug, achtergrond, vernieuwen

    /// <summary>De weergavekeuzes zoals bewaard in de databank (sinds 0.8).</summary>
    private string Weergave() => State.Databank.LeesDocument("weergave") ?? string.Empty;

    /// <summary>Een bestand lezen dat de app nog open heeft (databank, technisch log): Windows weigert anders.</summary>
    private static string LeesGedeeld(string pad)
    {
        using var f = new FileStream(pad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(f, System.Text.Encoding.Latin1);
        return r.ReadToEnd();
    }

    /// <summary>Alles wat de databank op schijf heeft (ook het WAL-bestand), als tekst: om te bewijzen dat een geheim er niet in staat.</summary>
    private string DatabankOpSchijf()
    {
        var tekst = new System.Text.StringBuilder();
        foreach (var pad in new[] { State.Databank.Pad, State.Databank.Pad + "-wal" }.Where(File.Exists))
        {
            using var f = new FileStream(pad, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f, System.Text.Encoding.Latin1);
            tekst.Append(r.ReadToEnd());
        }

        return tekst.ToString();
    }

    [Fact]
    public void TabelSorterenVerbergenEnVerplaatsen()
    {
        var cut = RenderComponent<Groepen>();
        string Eerste() => cut.Find("tbody tr td:not(.kb-kies)").TextContent.Trim();
        var oplopend = Eerste();

        cut.FindAll("th").First(t => t.TextContent.Trim().StartsWith("Groep", StringComparison.Ordinal)).Click();
        Assert.NotEqual(oplopend, Eerste());
        Assert.Contains("descending", cut.FindAll("th").First(t => t.TextContent.Contains("Groep")).GetAttribute("aria-sort"));

        cut.FindAll("button").First(b => b.TextContent == "Kolommen").Click();
        cut.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("Soort")).QuerySelector("input")!.Change(false);
        Assert.DoesNotContain(cut.FindAll("thead th"), t => t.TextContent.Trim() == "Soort");

        // Standaard verborgen kolom tonen en naar voren schuiven
        cut.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("sAMAccountName")).QuerySelector("input")!.Change(true);
        Assert.Contains(cut.FindAll("thead th"), t => t.TextContent.Trim() == "sAMAccountName");
        cut.FindAll(".kb-kolompaneel li").First(l => l.TextContent.Contains("Waar")).QuerySelectorAll("button")[0].Click();
        Assert.Equal("Waar", cut.FindAll("thead th:not(.kb-kies)")[0].TextContent.Trim());

        // Bewaard voor de volgende keer
        var json = Weergave();
        Assert.Contains("\"groepen\"", json);
        Assert.Contains("soort", json);
        var opnieuw = RenderComponent<Groepen>();
        Assert.Equal("Waar", opnieuw.FindAll("thead th:not(.kb-kies)")[0].TextContent.Trim());

        cut.FindAll("button").First(b => b.TextContent == "Standaard").Click();
        Assert.Equal("Groep ▲", cut.FindAll("thead th:not(.kb-kies)")[0].TextContent.Trim());
    }

    [Fact]
    public void TerugEnVooruitInDeApp()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var nav = Services.GetRequiredService<NavigationManager>();
        var cut = RenderComponent<Navigatie>();
        IElement Knop(string tekst) => cut.FindAll("button").First(b => b.TextContent.Contains(tekst));
        Assert.True(Knop("Terug").HasAttribute("disabled"));
        Assert.True(Knop("Vooruit").HasAttribute("disabled"));

        nav.NavigateTo("groepen");
        nav.NavigateTo("rooms");
        cut.WaitForAssertion(() => Assert.False(Knop("Terug").HasAttribute("disabled")));
        Knop("Terug").Click();
        Assert.EndsWith("/groepen", nav.Uri);
        Assert.False(Knop("Vooruit").HasAttribute("disabled"));
        Knop("Vooruit").Click();
        Assert.EndsWith("/rooms", nav.Uri);
        Assert.True(Knop("Vooruit").HasAttribute("disabled"));

        // Na Terug een nieuwe pagina: de stappen erna vervallen
        Knop("Terug").Click();
        nav.NavigateTo("postvakken");
        cut.WaitForAssertion(() => Assert.True(Knop("Vooruit").HasAttribute("disabled")));
        Knop("Terug").Click();
        Assert.EndsWith("/groepen", nav.Uri);
        Knop("Terug").Click();
        Assert.True(Knop("Terug").HasAttribute("disabled"));

        // Muisknoppen en Alt+pijl lopen via het script naar dezelfde knoppen
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "moss365.navigatie");
    }

    [Fact]
    public void AllesLadenOpDeAchtergrondEnBewaren()
    {
        var layout = RenderComponent<Moss365.UI.Layout.MainLayout>(p => p.Add(x => x.Body, b => b.AddMarkupContent(0, "<p>inhoud</p>")));
        Assert.Contains("Niet live", layout.Markup);

        var cut = RenderComponent<UI.Pages.Verbindingen>();
        cut.FindAll("button").First(b => b.TextContent == "Alles live laden").Click();
        WachtOpTaak();

        var taak = State.Taak!;
        Assert.Equal(new[] { "AD lezen", "Entra lezen", "Beveiliging lezen (MFA en beheerrollen)", "Conditional Access lezen", "Rooms, postvakken en groepen lezen (Exchange)", "Bewaren als inventaris" },
            taak.Stappen.Select(s => s.Naam));
        Assert.All(taak.Stappen, s => Assert.Equal(StapStatus.Klaar, s.Status));
        Assert.Contains(taak.Log, r => r.Contains("AD gelezen"));
        Assert.Equal(new[] { "AD", "Entra", "Rooms", "Postvakken" }, State.LiveOp.Keys.OrderBy(k => k switch { "AD" => 0, "Entra" => 1, "Rooms" => 2, _ => 3 }));

        // Bewaard als inventaris, en de app vindt die map
        Assert.NotNull(State.BewaardeInventaris);
        Assert.True(File.Exists(Path.Combine(State.BewaardeInventaris!, InventarisLader.AdGebruikers)));
        Assert.Contains(State.Mappen, m => m.EndsWith("-live", StringComparison.Ordinal));

        // Weg en terug naar het scherm: de voortgang staat er nog; het menu toont de status
        var terug = RenderComponent<UI.Pages.Verbindingen>();
        Assert.Contains("Bewaren als inventaris", terug.Markup);
        Assert.Contains("Tekstlog", terug.Markup);
        layout.WaitForAssertion(() => Assert.Contains("AD: live", layout.Markup));
        Assert.DoesNotContain("Niet live", layout.Markup);
        Assert.Contains("Alles live laden: klaar", layout.Markup);
    }

    [Fact]
    public void ObjectVernieuwenMetDeKnop()
    {
        _fabriek.AdNaLezen = u => u with { Enabled = false };
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:cas.vos"));
        Assert.Contains("uit de inventaris", cut.Find(".kb-objectstatus").TextContent);

        cut.FindAll("button").First(b => b.TextContent.Contains("Vernieuwen")).Click();
        cut.WaitForAssertion(() => Assert.Contains("live gelezen om", cut.Find(".kb-objectstatus").TextContent));

        // De status tekent zichzelf; de pagina rekent een tel later opnieuw (AD en Entra lopen tegelijk)
        cut.WaitForAssertion(() => Assert.Contains("uitgeschakeld", cut.Markup));
        Assert.Contains("ad:cas.vos", _fabriek.Gelezen);
        Assert.Contains("entra:e-cas", _fabriek.Gelezen);
    }

    [Fact]
    public void NaEenUitstroomWordtDeGebruikerVernieuwd()
    {
        _fabriek.AdNaLezen = u => u.SamAccountName == "an.peeters" ? u with { Enabled = false } : u;
        var cut = RenderComponent<Uitstroom>(p => p.Add(x => x.Sleutel, "ad:an.peeters"));
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));

        WachtOpTaak();
        Assert.StartsWith("Vernieuwen na wijziging", State.Taak!.Naam);
        Assert.False(State.Data.ZoekPersoon("ad:an.peeters")!.Ingeschakeld);
        Assert.Equal(0, State.AantalGewijzigd);
        Assert.Contains("live gelezen om", State.HerkomstVan(State.Data.Index.Ref(State.Data.ZoekPersoon("ad:an.peeters")!)));
    }

    [Fact]
    public void ZonderVerbindingBlijftHetObjectGemarkeerd()
    {
        _fabriek.LezenMislukt = true;
        var cut = RenderComponent<GroepDetail>(p => p.Add(x => x.Sleutel, "cloud:g-team"));
        Kies(cut, "Lid toevoegen", "cas", "cas vos");
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        WachtOpTaak();

        // Entra lezen lukt niet: vernieuwen mislukt en de groep blijft "gewijzigd, nog niet vernieuwd"
        Assert.Contains(State.Taak!.Stappen, s => s.Status == StapStatus.Fout);
        Assert.True(State.IsGewijzigd(State.Data.Index.Groep("cloud:g-team")!.Ref));
        Assert.Contains("gewijzigd, nog niet vernieuwd", cut.Markup);
    }

    [Fact]
    public void GastgebruikersUitnodigenEnVerwijderen()
    {
        var cut = RenderComponent<Gasten>();
        Assert.Contains("1 gasten", cut.Markup);
        Veld(cut, "E-mailadres").Change("jan@example.net");
        Veld(cut, "Naam").Change("Jan (Leverancier)");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Gast uitnodigen", cut.Find("section.kb-plan").TextContent);

        cut.Find("tbody td.kb-kies input").Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Gekozen gasten verwijderen", StringComparison.Ordinal)).Click();
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains("gelukt,", cut.Markup));
        Assert.Equal(Actie.GraphGastVerwijderen, Assert.Single(_fabriek.Uitgevoerd).Actie);
    }

    [Fact]
    public void StatistiekenTonen()
    {
        var cut = RenderComponent<Statistieken>();
        Assert.Contains("bijna vol", cut.Markup);
        Assert.Contains("10 / 10", cut.Markup);
        Assert.Contains($"href=\"{Moss365.Core.Analyse.Kpis.Gasten.Link}\"", cut.Markup);
    }

    [Fact]
    public void ControleVanDePcMetInstalleren()
    {
        var nep = new NepControle();
        _fabriek.Controle = nep;
        var cut = RenderComponent<Controle>();
        cut.FindAll("button").First(b => b.TextContent == "Controleren").Click();
        cut.WaitForAssertion(() => Assert.Contains("ExchangeOnlineManagement", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent == "Installeren").Click();
        Assert.Empty(nep.Geinstalleerd);   // eerst bevestigen
        cut.FindAll("button").First(b => b.TextContent == "Ja, installeren").Click();
        cut.WaitForAssertion(() => Assert.Equal("ExchangeOnlineManagement", Assert.Single(nep.Geinstalleerd)));
    }

    private sealed class NepControle : ISysteemControle
    {
        public List<string> Geinstalleerd { get; } = new();

        public Task<IReadOnlyList<ControleRegel>> ControleerAsync(VerbindingInstellingen instellingen, IProgress<string>? voortgang, CancellationToken annuleren)
            => Task.FromResult<IReadOnlyList<ControleRegel>>(new[]
            {
                new ControleRegel("PowerShell 7", "ExchangeOnlineManagement", Geinstalleerd.Count == 0 ? ControleStatus.Fout : ControleStatus.Goed,
                    Geinstalleerd.Count == 0 ? "niet geïnstalleerd" : "3.7.2", "Nodig voor Exchange.", "Install-Module ExchangeOnlineManagement", "ExchangeOnlineManagement", "pwsh.exe"),
                new ControleRegel("Netwerk", "graph.microsoft.com", ControleStatus.Goed, "bereikbaar", "Entra."),
            });

        public Task<string> InstalleerAsync(string powerShell, string module, CancellationToken annuleren)
        {
            Geinstalleerd.Add(module);
            return Task.FromResult("Gelukt");
        }
    }

    /// <summary>Nepverbindingen: onthouden wat er uitgevoerd werd en melden altijd succes.</summary>
    private sealed class NepFabriek : IVerbindingsFabriek
    {
        public List<Wijziging> Uitgevoerd { get; } = new();
        public string? GraphId { get; set; }

        /// <summary>Hoe een opnieuw gelezen AD-gebruiker eruitziet (bv. uitgeschakeld na een uitstroom).</summary>
        public Func<AdGebruiker, AdGebruiker?> AdNaLezen { get; set; } = u => u;

        public List<string> Gelezen { get; } = new();

        public ISysteemControle? Controle { get; set; }

        /// <summary>Opnieuw lezen mislukt (bv. de verbinding valt weg na de wijziging).</summary>
        public bool LezenMislukt { get; set; }

        /// <summary>Zo lang duurt elke volledige lezing (AD, Entra, rooms, postvakken), om parallel laden te testen.</summary>
        public TimeSpan Vertraging { get; set; }

        /// <summary>De app-rollen in de aanmelding (profielen, 0.8); null = niet aangemeld.</summary>
        public IReadOnlyList<string>? AppRollen { get; set; }
        public bool AdLezenMislukt { get; set; }

        // AD, Entra en Exchange lezen tegelijk: de lijst van gelezen objecten met een slot bijwerken
        public void Lees(string sleutel)
        {
            lock (Gelezen)
            {
                Gelezen.Add(sleutel);
            }
        }

        public void LeesAlle(IEnumerable<string> sleutels)
        {
            lock (Gelezen)
            {
                Gelezen.AddRange(sleutels);
            }
        }

        /// <summary>De vorige lezing die de app meegaf (snel laden), per keer.</summary>
        public List<AdBasis?> AdBases { get; } = new();
        public List<GraphBasis?> GraphBases { get; } = new();

        public BeveiligingGegevens? Beveiliging { get; set; }

        /// <summary>De agenda van een room (#20); met AgendaGeenRecht faalt het lezen zoals bij een 403.</summary>
        public List<Afspraak> Agenda { get; } = new();
        public bool AgendaGeenRecht { get; set; }
        public List<(string Adres, DateTime Van, DateTime Tot)> AgendaGelezen { get; } = new();
        public List<Aanmelding> Aanmeldingen { get; } = new();
        public CaGegevens? Ca { get; set; }
        public NepScripts NepScripts { get; } = new();

        public Core.Scripts.IScriptUitvoerder? Scripts(VerbindingInstellingen instellingen, string? adWachtwoord) => NepScripts;

        public IAdVerbinding? Ad(VerbindingInstellingen instellingen, string? wachtwoord)
            => instellingen.AdIngevuld ? new NepAd(this) : null;

        public IGraphVerbinding? Graph(VerbindingInstellingen instellingen)
            => instellingen.GraphIngevuld ? new NepGraphVerbinding(this) : null;

        public IExchangeVerbinding? Exchange(VerbindingInstellingen instellingen) => new NepExchange(this);

        public IReadOnlyList<IUitvoerder> Uitvoerders(VerbindingInstellingen instellingen, string? adWachtwoord)
            => new IUitvoerder[] { new NepAd(this), new NepGraphVerbinding(this), new NepExchange(this) };

        public Task<IReadOnlyList<WijzigingResultaat>> Voer(IReadOnlyList<Wijziging> wijzigingen)
        {
            Uitgevoerd.AddRange(wijzigingen);
            return Task.FromResult<IReadOnlyList<WijzigingResultaat>>(wijzigingen.Select(w => WijzigingResultaat.Ok(w)).ToList());
        }
    }

    private sealed class NepAd(NepFabriek f) : IAdVerbinding, IUitvoerder
    {
        public Systeem Systeem => Systeem.ActiveDirectory;

        public Task<AdGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren) => LeesAsync(null, voortgang, annuleren);

        public async Task<AdGegevens> LeesAsync(AdBasis? basis, IProgress<string>? voortgang, CancellationToken annuleren)
        {
            f.AdBases.Add(basis);
            await Task.Delay(f.Vertraging, annuleren);
            if (f.AdLezenMislukt)
            {
                throw new InvalidOperationException("De domeincontroller antwoordt niet.");
            }

            var data = TestData.Omgeving();
            voortgang?.Report("AD gelezen");
            return (new AdGegevens(data.AdGebruikers, data.AdGroepenOpDn.Values.ToList(), "DC=test,DC=internal",
                new AdStand("CN=NTDS Settings,CN=DC1", 1000 + f.AdBases.Count), Snel: basis is not null));
        }

        public Task<AdGebruiker?> LeesGebruikerAsync(string samAccountName, CancellationToken annuleren)
        {
            f.Lees("ad:" + samAccountName);
            var u = TestData.Omgeving().AdGebruikers.FirstOrDefault(x => x.SamAccountName == samAccountName);
            return Task.FromResult(u is null ? null : f.AdNaLezen(u));
        }

        public Task<AdGroep?> LeesGroepAsync(string samAccountName, CancellationToken annuleren)
        {
            f.Lees("adgroep:" + samAccountName);
            return Task.FromResult(TestData.Omgeving().AdGroepenOpDn.Values.FirstOrDefault(g => g.SamAccountName == samAccountName));
        }

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren) => f.Voer(wijzigingen);
    }

    private sealed class NepGraphVerbinding(NepFabriek f) : IGraphVerbinding, IUitvoerder
    {
        public Systeem Systeem => Systeem.Graph;

        public string? AangemeldAls => "beheerder@test.be";

        public IReadOnlyList<string>? AppRollen => f.AppRollen;

        public Task<GraphGegevens> LeesAsync(IProgress<string>? voortgang, CancellationToken annuleren) => LeesAsync(null, voortgang, annuleren);

        public async Task<GraphGegevens> LeesAsync(GraphBasis? basis, IProgress<string>? voortgang, CancellationToken annuleren)
        {
            f.GraphBases.Add(basis);
            await Task.Delay(f.Vertraging, annuleren);
            var data = TestData.Omgeving();
            return (new GraphGegevens(data.EntraGebruikers, data.CloudGroepen.Values.ToList(),
                data.CloudLidmaatschap.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value), StringComparer.OrdinalIgnoreCase), data.Licenties,
                $"https://graph.microsoft.com/v1.0/groups/delta?$deltatoken=t{f.GraphBases.Count}", Snel: basis is not null));
        }

        public Task<BeveiligingGegevens?> LeesBeveiligingAsync(IProgress<string>? voortgang, CancellationToken annuleren) => Task.FromResult(f.Beveiliging);

        public Task<IReadOnlyList<Aanmelding>> LeesAanmeldingenAsync(string? gebruikerId, int dagen, bool alleenMislukt, CancellationToken annuleren)
            => Task.FromResult<IReadOnlyList<Aanmelding>>(f.Aanmeldingen.Where(a => (gebruikerId is null || a.GebruikerId == gebruikerId) && (!alleenMislukt || !a.Gelukt)).ToList());

        public Task<IReadOnlyList<Aanmelding>> LeesAanmeldingenAsync(int dagen, int max, CancellationToken annuleren)
            => Task.FromResult<IReadOnlyList<Aanmelding>>(f.Aanmeldingen.Take(max).ToList());

        public Task<CaGegevens?> LeesConditionalAccessAsync(IProgress<string>? voortgang, CancellationToken annuleren) => Task.FromResult(f.Ca);

        public Task<IReadOnlyList<Afspraak>> LeesAgendaAsync(string adres, DateTime van, DateTime tot, CancellationToken annuleren)
        {
            f.AgendaGelezen.Add((adres, van, tot));
            return f.AgendaGeenRecht
                ? throw new GeenRechtFout("Access is denied. Check credentials and try again.")
                : Task.FromResult<IReadOnlyList<Afspraak>>(f.Agenda.Where(a => a.Begin < tot && a.Einde > van).ToList());
        }

        public Task<string?> ZoekGebruikerIdAsync(string upn, CancellationToken annuleren) => Task.FromResult(f.GraphId);

        public Task<GraphGebruikerLezing?> LeesGebruikerAsync(string idOfUpn, CancellationToken annuleren)
        {
            f.Lees("entra:" + idOfUpn);
            var d = TestData.Omgeving();
            var e = d.EntraGebruikers.FirstOrDefault(x => x.Id == idOfUpn || x.UserPrincipalName == idOfUpn);
            return Task.FromResult(e is null ? null : new GraphGebruikerLezing(e, d.CloudLidmaatschap.GetValueOrDefault(e.Id)?.ToList() ?? new List<string>()));
        }

        public Task<GraphGroepLezing?> LeesGroepAsync(string id, CancellationToken annuleren)
        {
            if (f.LezenMislukt)
            {
                throw new HttpRequestException("Graph is niet bereikbaar.");
            }

            f.Lees("cloud:" + id);
            var d = TestData.Omgeving();
            return Task.FromResult(d.CloudGroepen.TryGetValue(id, out var g)
                ? new GraphGroepLezing(g, d.CloudLidmaatschap.Where(kv => kv.Value.Contains(id)).Select(kv => kv.Key).ToList())
                : null);
        }

        public Task<IReadOnlyList<GraphEigenaar>> LeesEigenaarsAsync(string groepId, CancellationToken annuleren)
            => Task.FromResult<IReadOnlyList<GraphEigenaar>>(new[] { new GraphEigenaar("e-bo", "bo janssens", "bo.janssens@test.be") });

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren) => f.Voer(wijzigingen);
    }

    private sealed class NepExchange(NepFabriek f) : IExchangeVerbinding, IUitvoerder
    {
        public Systeem Systeem => Systeem.Exchange;

        public async Task<IReadOnlyList<Room>> LeesRoomsAsync(IProgress<string>? voortgang, CancellationToken annuleren)
        {
            await Task.Delay(f.Vertraging, annuleren);
            return TestData.Omgeving().Rooms;
        }

        public async Task<ExchangeGegevens> LeesPostvakkenAsync(IProgress<string>? voortgang, CancellationToken annuleren)
        {
            await Task.Delay(f.Vertraging, annuleren);
            var d = TestData.Omgeving();
            return (new ExchangeGegevens(d.ExoGroepen, d.Postvakken.Take(1).ToList(), d.PostvakRechten.Take(1).ToList()));
        }

        public Task<ExchangeObjecten> LeesObjectenAsync(IReadOnlyList<string> adressen, CancellationToken annuleren)
        {
            f.LeesAlle(adressen.Select(a => "exo:" + a));
            var d = TestData.Omgeving();
            var set = adressen.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var postvakken = d.Postvakken.Where(p => set.Contains(p.Smtp)).ToList();
            var groepen = d.ExoGroepen.Where(g => set.Contains(g.Smtp)).ToList();
            var rooms = d.Rooms.Where(r => set.Contains(r.Smtp)).ToList();
            var gevonden = postvakken.Select(p => p.Smtp).Concat(groepen.Select(g => g.Smtp)).Concat(rooms.Select(r => r.Smtp)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ExchangeObjecten(rooms,
                new ExchangeGegevens(groepen, postvakken, d.PostvakRechten.Where(r => set.Contains(r.Postvak)).ToList()),
                adressen.Where(a => !gevonden.Contains(a)).ToList()));
        }

        public Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren) => f.Voer(wijzigingen);
    }
}
