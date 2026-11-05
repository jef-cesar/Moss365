using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using Moss365.UI.Pages;
using Moss365.UI.Componenten;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor de lijsten in 0.7: pagina's, filteren per kolom, rijen kiezen, en wat bewaard blijft bij Terug en Vooruit.</summary>
public sealed partial class PlanSchermTests
{
    private sealed record Rij(string Naam, string Dienst, int Aantal, DateTime Datum);

    /// <summary>600 verzonnen rijen: Rij 001 tot Rij 600, drie diensten, een aantal en een datum.</summary>
    private static List<Rij> Rijen(int aantal = 600)
        => Enumerable.Range(1, aantal)
            .Select(i => new Rij($"Rij {i:000}", (i % 3) switch { 0 => "Bouw", 1 => "Cultuur", _ => "Jeugd" }, i, new DateTime(2026, 1, 1).AddDays(i)))
            .ToList();

    private static readonly IReadOnlyList<Kolom<Rij>> RijKolommen = new List<Kolom<Rij>>
    {
        new() { Sleutel = "naam", Titel = "Naam", Cel = r => b => b.AddContent(0, r.Naam), SorteerOp = r => r.Naam },
        new() { Sleutel = "dienst", Titel = "Dienst", Cel = r => b => b.AddContent(0, r.Dienst), SorteerOp = r => r.Dienst },
        new() { Sleutel = "aantal", Titel = "Aantal", Getal = true, Cel = r => b => b.AddContent(0, r.Aantal), SorteerOp = r => r.Aantal },
        new() { Sleutel = "datum", Titel = "Datum", Cel = r => b => b.AddContent(0, r.Datum.ToString("yyyy-MM-dd")), SorteerOp = r => r.Datum },
    };

    private IRenderedComponent<KbTabel<Rij>> RenderTabel(List<Rij>? rijen = null, string id = "testlijst", bool selecteerbaar = false, Action<IReadOnlyCollection<string>>? gekozen = null)
        => RenderComponent<KbTabel<Rij>>(p =>
        {
            p.Add(x => x.Id, id).Add(x => x.Items, rijen ?? Rijen()).Add(x => x.Kolommen, RijKolommen).Add(x => x.StandaardSortering, "naam");
            if (selecteerbaar)
            {
                p.Add(x => x.Selecteerbaar, true).Add(x => x.SleutelVan, r => r.Naam);
                if (gekozen is not null)
                {
                    p.Add(x => x.SelectieGewijzigd, EventCallback.Factory.Create(this, gekozen));
                }
            }
        });

    private static List<string> Namen(IRenderedComponent<KbTabel<Rij>> cut)
        => cut.FindAll("tbody tr").Select(tr => tr.QuerySelectorAll("td").First(td => !td.ClassList.Contains("kb-kies")).TextContent.Trim()).ToList();

    private static void Klik(IRenderedComponent<KbTabel<Rij>> cut, string tekst)
        => cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith(tekst, StringComparison.Ordinal)).Click();

    [Fact]
    public void LijstToontPaginasVan250EnBladert()
    {
        var cut = RenderTabel();
        Assert.Equal(250, cut.FindAll("tbody tr").Count);
        Assert.Contains("1–250 van 600", cut.Find(".kb-pagineren").TextContent);
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("Vorige")).HasAttribute("disabled"));

        Klik(cut, "Volgende");
        Assert.Equal("Rij 251", Namen(cut)[0]);
        Klik(cut, "Volgende");
        Assert.Equal(100, cut.FindAll("tbody tr").Count);
        Assert.Contains("501–600 van 600", cut.Find(".kb-pagineren").TextContent);
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("Volgende")).HasAttribute("disabled"));

        // 500 per pagina, dan alles; de keuze wordt bewaard
        cut.Find(".kb-pagineren select").Change("500");
        Assert.Equal(500, cut.FindAll("tbody tr").Count);
        cut.Find(".kb-pagineren select").Change("0");
        Assert.Equal(600, cut.FindAll("tbody tr").Count);
        Assert.Empty(cut.FindAll(".kb-pagineren button"));
        Assert.Contains("\"PerPagina\": 0", Weergave());
        Assert.Equal(600, RenderTabel().FindAll("tbody tr").Count);
    }

    [Fact]
    public void LijstOnthoudtDePaginaZolangDeAppOpenIs()
    {
        var cut = RenderTabel();
        Klik(cut, "Volgende");
        Assert.Equal("Rij 251", Namen(cut)[0]);

        // Opnieuw openen (bv. Terug): zelfde pagina; een andere lijst begint op pagina 1
        Assert.Equal("Rij 251", Namen(RenderTabel())[0]);
        Assert.Equal("Rij 001", Namen(RenderTabel(id: "andere"))[0]);

        // Minder rijen (andere zoekterm op het scherm): terug naar pagina 1
        cut.SetParametersAndRender(p => p.Add(x => x.Items, Rijen(300)));
        Assert.Equal("Rij 001", Namen(cut)[0]);
    }

    [Fact]
    public void LijstFiltertPerKolomMetTekstKeuzeEnBereik()
    {
        var cut = RenderTabel();
        Klik(cut, "Filteren");
        var filters = cut.FindAll("tr.kb-filterrij th");
        Assert.Equal(4, filters.Count);

        // Naam: tekst; Dienst: keuze (3 waarden); Aantal en Datum: bereik
        Assert.NotNull(filters[0].QuerySelector("input[placeholder]"));
        Assert.NotNull(filters[1].QuerySelector("details.kb-keuzefilter"));
        Assert.Equal("number", filters[2].QuerySelector("input")!.GetAttribute("type"));
        Assert.Equal("date", filters[3].QuerySelector("input")!.GetAttribute("type"));

        cut.Find("tr.kb-filterrij input[placeholder]").Input("Rij 05");
        Assert.Equal(10, cut.FindAll("tbody tr").Count); // Rij 050 tot 059
        Assert.Contains("1 filter: 10 van 600", cut.Markup);
        Klik(cut, "Filters wissen");
        Assert.Equal(250, cut.FindAll("tbody tr").Count);

        cut.FindAll("tr.kb-filterrij th")[1].QuerySelectorAll("label").First(l => l.TextContent.Contains("Bouw")).QuerySelector("input")!.Change(true);
        Assert.Contains("1 filter: 200 van 600", cut.Markup);
        Assert.All(cut.FindAll("tbody tr"), tr => Assert.Contains("Bouw", tr.TextContent));

        // Twee filters samen: Bouw en aantal 100 tot 130
        var aantal = cut.FindAll("tr.kb-filterrij th")[2].QuerySelectorAll("input");
        aantal[0].Change("100");
        cut.FindAll("tr.kb-filterrij th")[2].QuerySelectorAll("input")[1].Change("130");
        Assert.Contains("2 filters: 10 van 600", cut.Markup); // 102, 105, ..., 129 (130 is geen Bouw)

        // Datum: van en tot
        Klik(cut, "Filters wissen");
        var datum = cut.FindAll("tr.kb-filterrij th")[3].QuerySelectorAll("input");
        datum[0].Change("2026-01-02");
        cut.FindAll("tr.kb-filterrij th")[3].QuerySelectorAll("input")[1].Change("2026-01-05");
        Assert.Equal(new[] { "Rij 001", "Rij 002", "Rij 003", "Rij 004" }, Namen(cut));

        // Geen rijen: melding met wissen
        cut.Find("tr.kb-filterrij input[placeholder]").Input("bestaat niet");
        Assert.Contains("Geen rijen voor deze filters", cut.Markup);
    }

    [Fact]
    public void LijstExporteertGefilterdeEnGekozenRijen()
    {
        var cut = RenderTabel(selecteerbaar: true);
        Klik(cut, "Filteren");
        cut.Find("tr.kb-filterrij input[placeholder]").Input("Rij 05");
        Klik(cut, "Exporteren");
        var gefilterd = File.ReadAllLines(Assert.Single(Directory.GetFiles(ExportMap, "Testlijst-*.csv")));
        Assert.Equal(11, gefilterd.Length); // kop + 10

        Directory.Delete(ExportMap, true);
        cut.FindAll("tbody td.kb-kies input")[0].Click();
        cut.FindAll("tbody td.kb-kies input")[3].Click();
        Assert.Contains("2 gekozen", cut.Markup);
        Klik(cut, "Exporteren (gekozen)");
        var gekozen = File.ReadAllLines(Assert.Single(Directory.GetFiles(ExportMap, "Testlijst-*.csv")));
        Assert.Equal(3, gekozen.Length);
        Assert.Contains("Rij 050", gekozen[1]);
        Assert.Contains("Rij 053", gekozen[2]);
    }

    [Fact]
    public void LijstKiestRijenMetShiftEnOverAllePaginas()
    {
        IReadOnlyCollection<string> laatst = Array.Empty<string>();
        var cut = RenderTabel(selecteerbaar: true, gekozen: s => laatst = s);

        // Shift-klik: alles tussen de vorige klik en deze
        cut.FindAll("tbody td.kb-kies input")[2].Click();
        cut.FindAll("tbody td.kb-kies input")[6].Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { ShiftKey = true });
        Assert.Equal(5, laatst.Count);
        Assert.Equal(5, cut.FindAll("tr.kb-rij-gekozen").Count);
        Assert.Contains("Rij 005", laatst);

        // Hele pagina, dan alle 600 over de pagina's heen
        cut.Find("thead th.kb-kies input").Change(true);
        Assert.Equal(250, laatst.Count);
        Assert.Contains("De 250 rijen op deze pagina zijn gekozen", cut.Markup);
        Klik(cut, "Alle 600 rijen kiezen");
        Assert.Equal(600, laatst.Count);
        Assert.DoesNotContain("op deze pagina zijn gekozen", cut.Markup);

        // Volgende pagina is ook gekozen; wissen maakt alles leeg
        Klik(cut, "Volgende");
        Assert.Equal(250, cut.FindAll("tr.kb-rij-gekozen").Count);
        Klik(cut, "Selectie wissen");
        Assert.Empty(laatst);
        Assert.Empty(cut.FindAll("tr.kb-rij-gekozen"));
    }

    [Fact]
    public void LijstKiestEnkelWatKanEnInEenEigenSelectie()
    {
        var eigen = new HashSet<string>();
        var cut = RenderComponent<KbTabel<Rij>>(p => p
            .Add(x => x.Id, "kankiezen").Add(x => x.Items, Rijen(10)).Add(x => x.Kolommen, RijKolommen).Add(x => x.StandaardSortering, "naam")
            .Add(x => x.Selecteerbaar, true).Add(x => x.SleutelVan, r => r.Naam).Add(x => x.Gekozen, eigen)
            .Add(x => x.KanKiezen, r => r.Aantal % 2 == 0).Add(x => x.WaaromNiet, r => r.Aantal % 2 == 0 ? null : "oneven"));

        var vakjes = cut.FindAll("tbody td.kb-kies input");
        Assert.Equal(5, vakjes.Count(v => v.HasAttribute("disabled")));
        Assert.Equal("oneven", vakjes[0].GetAttribute("title"));

        cut.Find("thead th.kb-kies input").Change(true);
        Assert.Equal(5, eigen.Count);
        Assert.All(eigen, k => Assert.True(int.Parse(k[4..]) % 2 == 0));
        Assert.Empty(State.TabelSessie("kankiezen").Gekozen); // de eigen selectie, niet die van de sessie
    }

    [Fact]
    public void LijstZonderKiezenHeeftGeenVakjes()
    {
        var cut = RenderTabel(Rijen(10));
        Assert.Empty(cut.FindAll(".kb-kies"));
        Assert.Empty(cut.FindAll(".kb-pagineren button"));
        Assert.Empty(cut.FindAll(".kb-pagineren select")); // minder dan 250 rijen: geen keuze nodig
    }

    // ---------------------------------------------------------------- Terug en Vooruit: wat bewaard blijft

    [Fact]
    public void GeschiedenisVolgtTerugVooruitEnZoektermen()
    {
        var g = new Geschiedenis();
        g.Bezocht("app/");
        g.Bezocht("app/opkuis");
        g.Bezocht("app/opkuis?dagen=90"); // zelfde pagina, andere zoekterm: geen nieuwe stap
        Assert.Equal(new[] { "app/", "app/opkuis?dagen=90" }, g.Stappen);

        Assert.Equal("app/", g.Terug());
        Assert.True(g.Bezocht("app/"));
        Assert.True(g.KanVooruit);
        Assert.Equal("app/opkuis?dagen=90", g.Vooruit());
        Assert.True(g.Bezocht("app/opkuis?dagen=90"));

        // A, B, A (via een link): drie stappen; Vooruit vanaf B gaat naar de derde, niet terug naar de eerste
        var h = new Geschiedenis();
        h.Bezocht("a");
        h.Bezocht("b");
        h.Bezocht("a");
        h.Terug();
        h.Bezocht("b");
        Assert.Equal(1, h.Positie);
        h.Vooruit();
        h.Bezocht("a");
        Assert.Equal(2, h.Positie);
    }

    [Fact]
    public void ZoektermEnFiltersBlijvenNaTerug()
    {
        var cut = RenderComponent<Gebruikers>();
        cut.Find("input.kb-breed").Input("jan");
        cut.Find("select.kb-invoer").Change("AD + Entra");
        var gevonden = cut.FindAll("tbody tr").Count;
        Assert.True(gevonden > 0);

        // Naar een gebruiker en terug: dezelfde lijst
        RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        var terug = RenderComponent<Gebruikers>();
        Assert.Equal("jan", terug.Find("input.kb-breed").GetAttribute("value"));
        Assert.Equal("AD + Entra", terug.Find("select.kb-invoer").GetAttribute("value"));
        Assert.Equal(gevonden, terug.FindAll("tbody tr").Count);
    }

    [Fact]
    public void FormulierBlijftBewaardMaarNooitHetWachtwoord()
    {
        var cut = RenderComponent<NieuweGebruiker>();
        cut.FindAll("select")[0].Change(State.Config.Populaties[0].Naam);
        cut.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).Change("Testa");
        cut.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Achternaam")).Change("Verzonnen");
        cut.FindAll("button").First(b => b.TextContent == "Voorbeeld").Click();
        Assert.Contains("<h2>Voorstel</h2>", cut.Markup);

        // Weg en terug: het formulier staat er nog, het voorstel (met het startwachtwoord) niet
        RenderComponent<Gebruikers>();
        var terug = RenderComponent<NieuweGebruiker>();
        Assert.Equal("Testa", terug.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).GetAttribute("value"));
        Assert.Equal("Verzonnen", terug.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Achternaam")).GetAttribute("value"));
        Assert.DoesNotContain("<h2>Voorstel</h2>", terug.Markup);
        Assert.Contains("Testa", BewaardeStatus());
        Assert.DoesNotContain("wachtwoord", BewaardeStatus(), StringComparison.OrdinalIgnoreCase);

        // Leegmaken vergeet het formulier
        terug.FindAll("button").First(b => b.TextContent == "Leegmaken").Click();
        Assert.Equal(string.Empty, RenderComponent<NieuweGebruiker>().FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void WachtwoordResetKomtNietInDePaginastatus()
    {
        var cut = RenderComponent<GebruikerDetail>(p => p.Add(x => x.Sleutel, "ad:bo.janssens"));
        cut.FindAll("button").First(b => b.TextContent == "Wachtwoord opnieuw instellen").Click();
        var wachtwoord = cut.Find(".kb-kopieer input").GetAttribute("value")!;
        RenderComponent<Gebruikers>().Find("input.kb-breed").Input("bo");
        Assert.DoesNotContain(wachtwoord, BewaardeStatus());
        Assert.DoesNotContain(State.Paginastatus.Alles.Values, v => v.GetType().Name.Contains("Wachtwoord", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class MetToken { public string Naam { get; set; } = string.Empty; public string? GraphToken { get; set; } }
    private sealed class Genest { public List<MetToken> Lijst { get; } = new(); }
    private sealed class ZonderGeheim { public string Naam { get; set; } = string.Empty; public bool WachtwoordVerlooptNooit { get; set; } }

    [Fact]
    public void PaginastatusWeigertVeldenDieOpEenGeheimLijken()
    {
        Assert.Throws<InvalidOperationException>(() => State.Paginastatus.Van<MetToken>("x"));
        Assert.Throws<InvalidOperationException>(() => State.Paginastatus.Van<Genest>("y"));
        Assert.NotNull(State.Paginastatus.Van<ZonderGeheim>("z")); // geen tekst: geen geheim

        // Alle formulieren die de app bewaart, zijn in orde
        foreach (var t in new[] { typeof(Moss365.Core.Wijzigingen.NieuweGebruikerAanvraag), typeof(Moss365.Core.Wijzigingen.UitstroomOpties),
                     typeof(Moss365.Core.Wijzigingen.RoomGewenst), typeof(Moss365.Core.Wijzigingen.GroepGewenst), typeof(Moss365.Core.Wijzigingen.PostvakGewenst) })
        {
            Paginastatus.ControleerType(t);
        }
    }

    [Fact]
    public void GekozenRoomsEnFormulierBlijvenNaTerug()
    {
        var cut = RenderComponent<Rooms>();
        cut.FindAll("tbody td.kb-kies input")[0].Click();
        cut.FindAll("button").First(b => b.TextContent == "Standaard invullen").Click();

        var terug = RenderComponent<Rooms>();
        Assert.Contains("Wijzigen (1 geselecteerd)", terug.Markup);
        Assert.Single(terug.FindAll("tr.kb-rij-gekozen"));
        terug.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("BookingWindowInDays=180", terug.Find("section.kb-plan").TextContent);
    }

    private string BewaardeStatus()
        => JsonSerializer.Serialize(State.Paginastatus.Alles.Values.ToList(), new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
}
