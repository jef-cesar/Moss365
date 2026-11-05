using Moss365.Core.Scripts;
using Moss365.Core.Wijzigingen;
using Xunit;

namespace Moss365.Tests;

/// <summary>Scripts (0.7): kop en param(), bibliotheek, laatste waarden, runs en inplannen.</summary>
public sealed class ScriptsTests : IDisposable
{
    private readonly string _map = Path.Combine(Path.GetTempPath(), "kb-scripts-" + Guid.NewGuid().ToString("N"));
    private ScriptMappen Mappen => new(Path.Combine(_map, "ingebouwd"), Path.Combine(_map, "gegevens"));

    public void Dispose()
    {
        if (Directory.Exists(_map))
        {
            Directory.Delete(_map, recursive: true);
        }
    }

    private const string MetKop = """
        <#MOSS365
        Naam: Agenda-export
        Omschrijving: Alle afspraken van een postvak
        Verbinding: Graph, Exchange
        Schrijft: nee
        PowerShell: 7
        Duur: 10
        Variabele: Postvak | Postvak | verplicht | Welk postvak? |  |
        Variabele: Van | Datum | optioneel | Eerste dag | @werkjaar.van |
        Variabele: Soort | Keuze | optioneel | Welke soort? | CSV | CSV;ICS
        #>
        [CmdletBinding()]
        param([string]$Postvak, [datetime]$Van, [ValidateSet('CSV','ICS')][string]$Soort = 'CSV')
        Write-Host 'hallo'
        """;

    [Fact]
    public void KopLezen()
    {
        var kop = ScriptTekst.Lees(MetKop, @"C:\x\Export-Agenda.ps1");
        Assert.True(kop.UitBlok);
        Assert.Equal("Agenda-export", kop.Naam);
        Assert.Equal(ScriptVerbinding.Graph | ScriptVerbinding.Exchange, kop.Verbinding);
        Assert.Equal("7", kop.PowerShell);
        Assert.Equal(10, kop.DuurMinuten);
        Assert.Equal(new[] { "Postvak", "Van", "Soort" }, kop.Variabelen.Select(v => v.Naam));
        Assert.True(kop.Variabelen[0].Verplicht);
        Assert.Equal(VariabeleSoort.Postvak, kop.Variabelen[0].Soort);
        Assert.Equal("@werkjaar.van", kop.Variabelen[1].Standaard);
        Assert.Equal(new[] { "CSV", "ICS" }, kop.Variabelen[2].Opties);
    }

    [Fact]
    public void ZonderKopAfleidenUitParamEnHelp()
    {
        const string tekst = """
            <#
            .SYNOPSIS
                Vergelijkt twee postvakken
                en toont de verschillen.

            .PARAMETER Mailbox1
                Eerste.
            #>
            [CmdletBinding(SupportsShouldProcess = $true)]
            param(
                [Parameter(Mandatory = $true)][string]$Mailbox1,
                [string[]]$Leden,
                [switch]$Export,
                [ValidateRange(1, 30)][int]$Dagen = 30,
                [ValidateSet('Reviewer', 'LimitedDetails')][string]$AccessRights = 'Reviewer'
            )
            Get-Mailbox $Mailbox1
            """;
        var kop = ScriptTekst.Lees(tekst, "Compare-MailboxAttributes.ps1");
        Assert.False(kop.UitBlok);
        Assert.Equal("Compare-MailboxAttributes", kop.Naam);
        Assert.Equal("Vergelijkt twee postvakken en toont de verschillen.", kop.Omschrijving);
        Assert.Equal(ScriptVerbinding.Exchange, kop.Verbinding);
        Assert.True(kop.Schrijft);
        Assert.Equal(
            new[] { VariabeleSoort.Tekst, VariabeleSoort.Lijst, VariabeleSoort.JaNee, VariabeleSoort.Getal, VariabeleSoort.Keuze },
            kop.Variabelen.Select(v => v.Soort));
        Assert.Equal(new[] { "Reviewer", "LimitedDetails" }, kop.Variabelen[4].Opties);
    }

    [Fact]
    public void KopEnParamSchrijvenEnTerugLezen()
    {
        // Nieuw script met help bovenaan: het blok komt ervoor, het param()-blok erna
        const string tekst = "<#\n.SYNOPSIS\n    Test.\n#>\n$ErrorActionPreference = 'Stop'\nGet-Mailbox $Postvak\n";
        var kop = new ScriptKop
        {
            Naam = "Test",
            Verbinding = ScriptVerbinding.Exchange,
            Schrijft = true,
            Variabelen = new[] { new ScriptVariabele("Postvak", VariabeleSoort.Postvak, true, "Welk postvak?"), new ScriptVariabele("Wie", VariabeleSoort.Lijst) },
        };
        var nieuw = ScriptTekst.Schrijf(kop, tekst);
        Assert.StartsWith("<#MOSS365", nieuw);
        Assert.True(nieuw.IndexOf(".SYNOPSIS", StringComparison.Ordinal) < nieuw.IndexOf("param(", StringComparison.Ordinal));
        Assert.Contains("[CmdletBinding(SupportsShouldProcess = $true)]\r\nparam(\r\n    [string]$Postvak,\r\n    [string[]]$Wie\r\n)", nieuw);
        Assert.Contains("Get-Mailbox $Postvak", nieuw);

        var terug = ScriptTekst.Lees(nieuw, "Test.ps1");
        Assert.Equal(kop.Variabelen, terug.Variabelen.Select(v => v with { Opties = null }).ToList());
        Assert.True(terug.Schrijft);

        // Opnieuw schrijven met een andere soort en een variabele minder: geen tweede blok, attributen blijven
        const string metAttribuut = "param([Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Postvak, [int]$Weg)\n'x'";
        var aangepast = ScriptTekst.Schrijf(kop with { Schrijft = false, Variabelen = new[] { new ScriptVariabele("Postvak", VariabeleSoort.Lijst) } }, metAttribuut);
        Assert.Contains("[Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string[]]$Postvak", aangepast);
        Assert.DoesNotContain("$Weg", aangepast);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ScriptTekst.Schrijf(kop, aangepast), "<#MOSS365"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ScriptTekst.Schrijf(kop, aangepast), @"param\("));
    }

    [Fact]
    public void WaarschuwingenBijOpslaan()
    {
        var kop = new ScriptKop { Naam = "x", Schrijft = true, Variabelen = new[] { new ScriptVariabele("Twee woorden"), new ScriptVariabele("a"), new ScriptVariabele("A") } };
        var w = ScriptTekst.Waarschuwingen(kop, "param([string]$a)\n$x = Read-Host 'wat'\nConnect-ExchangeOnline\nAdd-DistributionGroupMember -Member jan.peeters@test.be");
        Assert.Contains(w, x => x.Contains("Read-Host"));
        Assert.Contains(w, x => x.Contains("Connect-ExchangeOnline"));
        Assert.Contains(w, x => x.Contains("-WhatIf"));
        Assert.Contains(w, x => x.Contains("jan.peeters@test.be"));
        Assert.Contains(w, x => x.Contains("'Twee woorden'"));
        Assert.Contains(w, x => x.Contains("meer dan eens"));

        // Het blok Get-ScriptWaarde mag Read-Host gebruiken
        Assert.Empty(ScriptTekst.Waarschuwingen(new ScriptKop { Naam = "x" }, "function Get-ScriptWaarde { Read-Host 'x' }"));
    }

    [Fact]
    public void BibliotheekLadenOpslaanImporterenEnVerwijderen()
    {
        var m = Mappen;
        Directory.CreateDirectory(Path.Combine(m.Ingebouwd, "Exchange"));
        File.WriteAllText(Path.Combine(m.Ingebouwd, "Exchange", "Export-Agenda.ps1"), MetKop);

        var eigen = ScriptBibliotheek.Opslaan(m, ScriptBibliotheek.NieuwScript("Mijn script"));
        Assert.Equal("Mijn-script", eigen.Sleutel);
        Assert.False(ScriptBibliotheek.IsGewijzigdBuitenApp(m, eigen));

        // Een eigen script met de naam van een ingebouwd krijgt een andere bestandsnaam
        var kopie = ScriptBibliotheek.Opslaan(m, MetKop);
        Assert.Equal("Agenda-export", kopie.Sleutel);
        var nogEen = ScriptBibliotheek.Opslaan(m, MetKop);
        Assert.Equal("Agenda-export-2", nogEen.Sleutel);

        var alles = ScriptBibliotheek.Laad(m);
        Assert.Equal(4, alles.Count);
        var ingebouwd = Assert.Single(alles, s => s.Ingebouwd);
        Assert.Equal("Exchange", ingebouwd.Map);
        Assert.Throws<InvalidOperationException>(() => ScriptBibliotheek.Verwijder(m, ingebouwd));

        // Buiten de app gewijzigd: opnieuw goedkeuren
        File.AppendAllText(eigen.Pad, "\nRemove-Item C:\\");
        var gewijzigd = ScriptBibliotheek.LeesBestand(eigen.Pad, false);
        Assert.True(ScriptBibliotheek.IsGewijzigdBuitenApp(m, gewijzigd));
        ScriptBibliotheek.Keur(m, gewijzigd);
        Assert.False(ScriptBibliotheek.IsGewijzigdBuitenApp(m, gewijzigd));

        // Importeren van een script zonder kop: de kop wordt erbij gezet
        var bron = Path.Combine(_map, "Oud.ps1");
        File.WriteAllText(bron, "param([string]$Groep)\nGet-DistributionGroup $Groep");
        var geimporteerd = ScriptBibliotheek.Importeer(m, bron);
        Assert.True(geimporteerd.Kop.UitBlok);
        Assert.Equal("Groep", Assert.Single(geimporteerd.Kop.Variabelen).Naam);
        Assert.Equal(ScriptVerbinding.Exchange, geimporteerd.Kop.Verbinding);

        ScriptBibliotheek.Verwijder(m, geimporteerd);
        Assert.False(File.Exists(geimporteerd.Pad));
        Assert.Equal("Mijn-script.ps1", ScriptBibliotheek.Bestandsnaam("Mijn script"));
        Assert.Equal("a_b.ps1", ScriptBibliotheek.Bestandsnaam("a/b"));
    }

    [Fact]
    public void IngebouwdZonderKopStaatNietInDeBibliotheek()
    {
        var m = Mappen;
        Directory.CreateDirectory(m.Ingebouwd);
        File.WriteAllText(Path.Combine(m.Ingebouwd, "Oud.ps1"), "param([string]$x)\n'x'");
        File.WriteAllText(Path.Combine(m.Ingebouwd, "Met.ps1"), MetKop);
        Assert.Equal("Agenda-export", Assert.Single(ScriptBibliotheek.Laad(m)).Kop.Naam);
    }

    /// <summary>De ingebouwde bibliotheek in de repo: elke variabele in de kop is een parameter van het script.</summary>
    [Fact]
    public void BibliotheekInDeRepoKlopt()
    {
        var map = new DirectoryInfo(AppContext.BaseDirectory);
        while (map is not null && !Directory.Exists(Path.Combine(map.FullName, "scripts")))
        {
            map = map.Parent;
        }

        Assert.NotNull(map);
        var scripts = Directory.EnumerateFiles(Path.Combine(map!.FullName, "scripts"), "*.ps1", SearchOption.AllDirectories)
            .Select(p => (Pad: p, Tekst: File.ReadAllText(p)))
            .Select(x => (x.Pad, Kop: ScriptTekst.Lees(x.Tekst, x.Pad), Parameters: ScriptTekst.LeesParameters(x.Tekst).Select(p => p.Naam).ToHashSet(StringComparer.OrdinalIgnoreCase), x.Tekst))
            .Where(x => x.Kop.UitBlok)
            .ToList();

        Assert.NotEmpty(scripts);
        foreach (var s in scripts)
        {
            foreach (var v in s.Kop.Variabelen)
            {
                Assert.True(s.Parameters.Contains(v.Naam), $"{Path.GetFileName(s.Pad)}: variabele {v.Naam} is geen parameter");
            }

            if (s.Kop.Schrijft)
            {
                Assert.Matches(@"SupportsShouldProcess\s*=\s*\$true", s.Tekst);
            }

            // In de app werkt Read-Host niet: enkel in het blok Get-ScriptWaarde of buiten Moss365
            foreach (var regel in s.Tekst.Split('\n').Where(r => r.Contains("Read-Host") && !r.TrimStart().StartsWith('#')))
            {
                Assert.True(regel.Contains("$antwoord = Read-Host") || regel.Contains("MossUitvoer") || regel.Contains("-or (Read-Host"),
                    $"{Path.GetFileName(s.Pad)}: Read-Host zonder uitzondering voor Moss365: {regel.Trim()}");
            }
        }
    }

    [Fact]
    public void LaatsteWaardenZoalsGetScriptWaarde()
    {
        var m = Mappen;
        Directory.CreateDirectory(m.Waarden);

        // Zoals het blok in de scripts het schrijft (lijst als JSON-lijst)
        File.WriteAllText(Path.Combine(m.Waarden, "AddToSignatureGroup.json"), """{ "Groep": "Signature_All", "Gebruikers": ["a@test.be", "b@test.be"] }""");
        var w = ScriptWaarden.Lees(m, "AddToSignatureGroup");
        Assert.Equal("Signature_All", w["groep"]);
        Assert.Equal("a@test.be\nb@test.be", w["Gebruikers"]);

        var kop = new ScriptKop { Variabelen = new[] { new ScriptVariabele("Groep"), new ScriptVariabele("Gebruikers", VariabeleSoort.Lijst) } };
        ScriptWaarden.Bewaar(m, "AddToSignatureGroup", kop, new Dictionary<string, string> { ["Gebruikers"] = "c@test.be, d@test.be" });
        var json = File.ReadAllText(Path.Combine(m.Waarden, "AddToSignatureGroup.json"));
        Assert.Contains("\"c@test.be\"", json);
        Assert.Contains("\"Groep\": \"Signature_All\"", json);
        Assert.Equal("c@test.be\nd@test.be", ScriptWaarden.Lees(m, "AddToSignatureGroup")["Gebruikers"]);

        File.WriteAllText(Path.Combine(m.Waarden, "kapot.json"), "{kapot");
        Assert.Empty(ScriptWaarden.Lees(m, "kapot"));
    }

    [Theory]
    [InlineData("@werkjaar.van", "2026-09-01")]
    [InlineData("@werkjaar.tot", "2027-08-31")]
    [InlineData("@vorigemaand.van", "2026-09-01")]
    [InlineData("@vorigemaand.tot", "2026-09-30")]
    [InlineData("@dezemaand.tot", "2026-10-31")]
    [InlineData("@gisteren", "2026-10-29")]
    [InlineData("gewoon", "gewoon")]
    public void RelatieveWaarden(string waarde, string verwacht)
        => Assert.Equal(verwacht, ScriptWaarden.Los(waarde, new DateTime(2026, 10, 30, 9, 0, 0)));

    [Fact]
    public void WaardenControleren()
    {
        var kop = ScriptTekst.Lees(MetKop, "x.ps1") with
        {
            Variabelen = new[]
            {
                new ScriptVariabele("Postvak", VariabeleSoort.Postvak, true, "Welk postvak?"),
                new ScriptVariabele("Aantal", VariabeleSoort.Getal),
                new ScriptVariabele("Van", VariabeleSoort.Datum),
                new ScriptVariabele("Soort", VariabeleSoort.Keuze, Opties: new[] { "CSV", "ICS" }),
            },
        };
        var fouten = ScriptWaarden.Controleer(kop, new Dictionary<string, string> { ["Aantal"] = "drie", ["Van"] = "32/13/2026", ["Soort"] = "PDF" });
        Assert.Equal(4, fouten.Count);
        Assert.Empty(ScriptWaarden.Controleer(kop, new Dictionary<string, string> { ["Postvak"] = "x@test.be", ["Aantal"] = "3", ["Van"] = "@werkjaar.van", ["Soort"] = "ics" }));
    }

    [Fact]
    public void RunsBewarenLijstenEnOpruimen()
    {
        var m = Mappen;
        var nu = DateTimeOffset.Now;
        var oud = new ScriptRun { Id = "oud", Sleutel = "A", Naam = "A", Start = nu.AddDays(-91), Resultaat = RunResultaat.Gelukt, Map = ScriptRuns.NieuweMap(m, "A", "oud") };
        var nieuw = new ScriptRun { Id = "nieuw", Sleutel = "A", Naam = "A", Start = nu.AddDays(-1), Resultaat = RunResultaat.Fout, Map = ScriptRuns.NieuweMap(m, "A", "nieuw") };
        var ander = new ScriptRun { Id = "x", Sleutel = "B", Naam = "B", Start = nu, Voorbeeld = true, Resultaat = RunResultaat.Gelukt, Map = ScriptRuns.NieuweMap(m, "B", "x") };
        foreach (var r in new[] { oud, nieuw, ander })
        {
            ScriptRuns.Bewaar(r);
        }

        Assert.True(Directory.Exists(Path.Combine(nieuw.Map, "bestanden")));
        Assert.Equal(new[] { "x", "nieuw", "oud" }, ScriptRuns.Lijst(m).Select(r => r.Id));
        Assert.Equal(new[] { "nieuw", "oud" }, ScriptRuns.Lijst(m, "A").Select(r => r.Id));
        Assert.Equal("voorbeeld: gelukt", ScriptRuns.Lijst(m, "B").Single().ResultaatTekst);

        Assert.Equal(1, ScriptRuns.Opruimen(m, nu));
        Assert.Equal(new[] { "nieuw" }, ScriptRuns.Lijst(m, "A").Select(r => r.Id));
        Assert.NotEqual(nieuw.Map, ScriptRuns.NieuweMap(m, "A", "nieuw"));
    }

    [Fact]
    public void InplannenMetHerhaling()
    {
        var start = new DateTimeOffset(2026, 1, 31, 8, 0, 0, TimeSpan.FromHours(1));
        GeplandeActie Met(Herhaling h) => GeplandPlannen.Script("Export-Agenda", true, "Agenda-export", new Dictionary<string, string> { ["Van"] = "@werkjaar.van" }, start, h, "beheerder");

        Assert.Null(Met(Herhaling.Eenmalig).VolgendeKeer(start));
        Assert.Equal(start.AddDays(1), Met(Herhaling.Dagelijks).VolgendeKeer(start));
        Assert.Equal(start.AddDays(7), Met(Herhaling.Wekelijks).VolgendeKeer(start));

        // 31 januari, maandelijks: 28 februari, 31 maart (niet "de 28ste" voor altijd)
        var maand = Met(Herhaling.Maandelijks);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 8, 0, 0, start.Offset), maand.VolgendeKeer(start));
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 8, 0, 0, start.Offset), maand.VolgendeKeer(new DateTimeOffset(2026, 3, 1, 0, 0, 0, start.Offset)));

        // Weken gemist (app niet open): de volgende in de toekomst, niet alle gemiste
        Assert.Equal(start.AddDays(21), Met(Herhaling.Wekelijks).VolgendeKeer(start.AddDays(15)));

        var opslag = new GeplandOpslag(Path.Combine(_map, "gepland.json"));
        opslag.Voegtoe(maand);
        var terug = Assert.Single(opslag.Lees());
        Assert.Equal(GeplandSoort.Script, terug.Soort);
        Assert.Equal(Herhaling.Maandelijks, terug.Herhaling);
        Assert.Equal("@werkjaar.van", terug.ScriptWaarden["Van"]);
    }
}
