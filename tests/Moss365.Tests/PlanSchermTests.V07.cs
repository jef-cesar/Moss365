using Bunit;
using Moss365.Core.Scripts;
using Moss365.Core.Wijzigingen;
using Moss365.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Moss365.Tests;

/// <summary>Schermtests voor 0.7: scripts maken, starten (met voorbeeld), uitvoer bekijken en inplannen.</summary>
public sealed partial class PlanSchermTests
{
    /// <summary>Draait niets echt: meldt een paar regels en een rij, maakt een bestand, en onthoudt de opdrachten.</summary>
    private sealed class NepScripts : IScriptUitvoerder
    {
        public List<ScriptOpdracht> Opdrachten { get; } = new();
        public int Code { get; set; }

        public Task<int> VoerUitAsync(ScriptOpdracht opdracht, IProgress<ScriptGebeurtenis>? gebeurtenissen, CancellationToken annuleren)
        {
            lock (Opdrachten)
            {
                Opdrachten.Add(opdracht);
            }

            void Meld(ScriptGebeurtenis g, string json)
            {
                File.AppendAllText(Path.Combine(opdracht.RunMap, "uitvoer.jsonl"), json + "\n");
                gebeurtenissen?.Report(g);
            }

            var naam = opdracht.Waarden.TryGetValue("Postvak", out var p) ? p : "?";
            Meld(new ScriptGebeurtenis(GebeurtenisSoort.Tekst, (opdracht.Voorbeeld ? "What if: " : string.Empty) + "Bezig met " + naam), "{\"s\":\"tekst\",\"t\":\"" + (opdracht.Voorbeeld ? "What if: " : string.Empty) + "Bezig met " + naam + "\"}");
            Meld(new ScriptGebeurtenis(GebeurtenisSoort.Rij, string.Empty, Tabel: "Resultaat", Rij: new Dictionary<string, string> { ["Postvak"] = naam, ["Items"] = "42" }),
                "{\"s\":\"rij\",\"tabel\":\"Resultaat\",\"r\":{\"Postvak\":\"" + naam + "\",\"Items\":\"42\"}}");
            Directory.CreateDirectory(Path.Combine(opdracht.RunMap, "bestanden"));
            File.WriteAllText(Path.Combine(opdracht.RunMap, "bestanden", "export.csv"), "Onderwerp;Start\nOverleg;2026-09-01\n");
            return Task.FromResult(Code);
        }
    }

    private const string SchrijvendScript = """
        <#MOSS365
        Naam: Postvak opkuisen
        Omschrijving: Verwijdert oude items
        Verbinding: Exchange
        Schrijft: ja
        Variabele: Postvak | Postvak | verplicht | Welk postvak? |  |
        #>
        [CmdletBinding(SupportsShouldProcess = $true)]
        param([string]$Postvak)
        'x'
        """;

    private ScriptBestand IngebouwdScript(string bestandsnaam = "Postvak-opkuisen.ps1", string tekst = SchrijvendScript)
    {
        var map = Path.Combine(_basis, "scripts-ingebouwd", "Exchange");
        Directory.CreateDirectory(map);
        File.WriteAllText(Path.Combine(map, bestandsnaam), tekst);
        return State.ZoekScript(Path.GetFileNameWithoutExtension(bestandsnaam))!;
    }

    private void WachtOpScript()
    {
        var tot = DateTime.Now.AddSeconds(10);
        while (State.ScriptBezig && DateTime.Now < tot)
        {
            Thread.Sleep(20);
        }

        Assert.False(State.ScriptBezig);
    }

    [Fact]
    public void ScriptsOverzichtToontIngebouwdEnEigen()
    {
        IngebouwdScript();
        ScriptBibliotheek.Opslaan(State.ScriptMappen, ScriptBibliotheek.NieuwScript("Mijn export"));

        var cut = RenderComponent<ScriptsOverzicht>();
        Assert.Contains("Postvak opkuisen", cut.Markup);
        Assert.Contains("Mijn export", cut.Markup);
        Assert.Contains("wijzigt", cut.Markup);
        Assert.Contains("ingebouwd", cut.Markup);
        Assert.Contains("Kopie bewerken", cut.Markup);

        // Een eigen script verwijderen (met bevestiging)
        cut.FindAll("button").First(b => b.TextContent == "Verwijderen").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, verwijderen").Click();
        Assert.Contains("is verwijderd", cut.Markup);
        Assert.Single(State.LeesScripts());
    }

    [Fact]
    public void ScriptMakenMetVariabelenEnOpslaan()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("scripts/bewerken");
        var cut = RenderComponent<ScriptBewerken>();

        // Zonder Monaco (bUnit): het tekstvak
        Assert.NotNull(cut.Find("textarea.kb-code"));
        Veld(cut, "Naam").Change("Agenda export");
        cut.FindAll("button").First(b => b.TextContent == "+ Variabele").Click();
        cut.FindAll("input[aria-label=Naam]").Last().Change("Van");
        cut.FindAll("select[aria-label=Soort]").Last().Change(nameof(VariabeleSoort.Datum));
        cut.FindAll("input[aria-label=Standaard]").Last().Change("@werkjaar.van");

        cut.FindAll("button").First(b => b.TextContent == "Opslaan").Click();
        cut.WaitForAssertion(() => Assert.Contains("Opgeslagen als Agenda-export.ps1", cut.Markup));

        var script = State.ZoekScript("Agenda-export")!;
        Assert.Equal(new[] { "Postvak", "Van" }, script.Kop.Variabelen.Select(v => v.Naam));
        Assert.Equal("@werkjaar.van", script.Kop.Variabelen[1].Standaard);
        var tekst = File.ReadAllText(script.Pad);
        Assert.Contains("[datetime]$Van", tekst);
        Assert.False(ScriptBibliotheek.IsGewijzigdBuitenApp(State.ScriptMappen, script));
    }

    [Fact]
    public void ScriptDatWijzigtEerstVoorbeeldDanUitvoeren()
    {
        var script = IngebouwdScript();
        var cut = RenderComponent<ScriptStarten>(p => p.Add(x => x.Sleutel, script.Sleutel));

        // Zonder voorbeeld geen Uitvoeren
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Uitvoeren");
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Voorbeeld", StringComparison.Ordinal)).Click();
        Assert.Contains("Welk postvak?: verplicht", cut.Markup);
        Assert.Empty(_fabriek.NepScripts.Opdrachten);

        cut.Find("input[list=kb-keuzes-postvak]").Change("info@test.be");
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Voorbeeld", StringComparison.Ordinal)).Click();
        WachtOpScript();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"), b => b.TextContent == "Uitvoeren"));
        Assert.True(Assert.Single(_fabriek.NepScripts.Opdrachten).Voorbeeld);
        Assert.Contains("What if: Bezig met info@test.be", cut.Markup);

        // Een andere waarde: opnieuw een voorbeeld nodig
        cut.Find("input[list=kb-keuzes-postvak]").Change("ander@test.be");
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent == "Uitvoeren");
        cut.Find("input[list=kb-keuzes-postvak]").Change("info@test.be");

        cut.FindAll("button").First(b => b.TextContent == "Uitvoeren").Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, uitvoeren").Click();
        WachtOpScript();
        cut.WaitForAssertion(() => Assert.Contains("gelukt", cut.Markup));
        Assert.False(_fabriek.NepScripts.Opdrachten[1].Voorbeeld);

        // Resultaat: tabel uit de rijen en de CSV, en het logboek
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Tabellen", StringComparison.Ordinal)).Click();
        Assert.Contains("42", cut.Markup);
        Assert.Contains("export.csv", cut.Markup);
        var log = State.Logboek.Recent(5);
        Assert.Contains(log, r => r.Actie == "ScriptVoorbeeld");
        Assert.Contains(log, r => r.Actie == "ScriptUitvoeren" && r.Status == "Gelukt" && r.Parameters["Postvak"] == "info@test.be");

        // De laatste waarden zijn onthouden (zelfde bestand als Get-ScriptWaarde)
        Assert.Equal("info@test.be", ScriptWaarden.Lees(State.ScriptMappen, script.Sleutel)["Postvak"]);
        Assert.Equal(2, ScriptRuns.Lijst(State.ScriptMappen, script.Sleutel).Count);
    }

    [Fact]
    public void ScriptRunTerugzienEnOpnieuwStarten()
    {
        var script = IngebouwdScript("Lees.ps1", SchrijvendScript.Replace("Schrijft: ja", "Schrijft: nee").Replace("Postvak opkuisen", "Lees"));
        var (status, fout) = State.StartScript(script, new Dictionary<string, string> { ["Postvak"] = "info@test.be" }, voorbeeld: false);
        Assert.Null(fout);
        WachtOpScript();

        var cut = RenderComponent<ScriptRunDetail>(p => p.Add(x => x.Sleutel, script.Sleutel).Add(x => x.Id, Path.GetFileName(status!.Run.Map)));
        Assert.Contains("Bezig met info@test.be", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Opnieuw starten met dezelfde waarden").Click();
        Assert.Equal("info@test.be", State.ScriptVoorinvulling!["Postvak"]);
        Assert.EndsWith("scripts/starten/Lees", Services.GetRequiredService<NavigationManager>().Uri);

        // Een tweede script tegelijk kan niet; een fout script geeft "mislukt"
        _fabriek.NepScripts.Code = 1;
        State.StartScript(script, new Dictionary<string, string> { ["Postvak"] = "x@test.be" }, false);
        WachtOpScript();
        Assert.Equal(RunResultaat.Fout, State.ScriptRun!.Run.Resultaat);
    }

    [Fact]
    public void ScriptInplannenEnStartenVanuitGepland()
    {
        var script = IngebouwdScript();
        var cut = RenderComponent<ScriptStarten>(p => p.Add(x => x.Sleutel, script.Sleutel));
        cut.Find("input[list=kb-keuzes-postvak]").Change("info@test.be");
        cut.Find("select").Change(nameof(Herhaling.Maandelijks));
        cut.FindAll("button").First(b => b.TextContent == "Inplannen").Click();
        Assert.Contains("Ingepland voor", cut.Markup);

        var actie = Assert.Single(State.Gepland.Lees());
        Assert.Equal(GeplandSoort.Script, actie.Soort);
        Assert.Equal(Herhaling.Maandelijks, actie.Herhaling);
        Assert.Equal("info@test.be", actie.ScriptWaarden["Postvak"]);

        var gepland = RenderComponent<Gepland>();
        Assert.Contains($"scripts/starten/{script.Sleutel}?gepland={actie.Id}", gepland.Markup);
        Assert.Contains("(elke maand)", gepland.Markup);
    }

    [Fact]
    public void GedeeldPostvakAanmeldenZienEnBlokkeren()
    {
        TestData.SchrijfInventaris(Path.Combine(_basis, "Inventaris", "Inventaris-20261030-1000"), Versie07Tests.PostvakBestanden());
        State.LaadInventaris();
        var lijst = RenderComponent<Postvakken>();
        Assert.Contains("Kan aanmelden", lijst.Markup);
        Assert.Contains("Laatste aanmelding", lijst.Markup);
        Assert.Contains("kunnen aanmelden", lijst.Markup);
        lijst.FindAll("button.kb-tegel").First(b => b.TextContent.Contains("kunnen aanmelden")).Click();
        Assert.Contains("Jeugd", lijst.Markup);
        Assert.DoesNotContain(">Cultuur<", lijst.Markup);

        var cut = RenderComponent<PostvakDetail>(p => p.Add(x => x.Smtp, "jeugd@test.be"));
        Assert.Contains("Aanmelden met het account", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent == "Aanmelden blokkeren").Click();
        Assert.Contains("Aanmelden blokkeren in Entra", cut.Markup);
        Bevestig(cut);
        cut.WaitForAssertion(() => Assert.Contains(_fabriek.Uitgevoerd, w => w.Actie == Actie.GraphKenmerkenZetten && w.Doel == "e-jeugd"));
    }
}
