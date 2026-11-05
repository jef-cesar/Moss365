using System.Diagnostics;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.7 (#12): AD, Entra en Exchange tegelijk lezen; rooms en postvakken in één Exchange-sessie.</summary>
public sealed partial class PlanSchermTests
{
    [Fact]
    public void AllesLadenLeestAdEntraEnExchangeTegelijk()
    {
        var vertraging = TimeSpan.FromSeconds(1);
        _fabriek.Vertraging = vertraging;
        var klok = Stopwatch.StartNew();
        Assert.True(State.StartLaden(LaadSoort.Alles));

        // Na een korte tijd lopen de drie sporen tegelijk; het menu toont "3 bezig"
        var tot = DateTime.Now.AddSeconds(5);
        while (State.Taak!.Stappen.Count(s => s.Status == StapStatus.Bezig) < 3 && DateTime.Now < tot)
        {
            Thread.Sleep(10);
        }

        Assert.StartsWith("3 bezig:", State.Taak.Samenvatting);
        WachtOpTaak();
        klok.Stop();

        // Na elkaar: AD + Entra + rooms + postvakken = 4 keer de vertraging; tegelijk: ongeveer 2 (Exchange doet er twee)
        Assert.True(klok.Elapsed < vertraging * 3.5, $"duurde {klok.Elapsed.TotalMilliseconds} ms");
        Assert.All(State.Taak.Stappen, s => Assert.Equal(StapStatus.Klaar, s.Status));

        // Alles samengevoegd, niets overschreven
        Assert.Equal(new[] { "AD", "Entra", "Postvakken", "Rooms" }, State.LiveOp.Keys.Order());
        Assert.NotEmpty(State.Data.AdGebruikers);
        Assert.NotEmpty(State.Data.EntraGebruikers);
        Assert.NotEmpty(State.Data.Rooms);
        Assert.Single(State.Data.Postvakken); // de nep-Exchange geeft er één terug

        // Bewaren als inventaris pas als alle sporen klaar zijn
        var bewaren = State.Taak.Stappen.Single(s => s.Naam == "Bewaren als inventaris");
        Assert.All(State.Taak.Stappen.Where(s => s != bewaren), s => Assert.True(s.Einde <= bewaren.Start));
    }

    [Fact]
    public void EenFoutInAdLaatEntraEnExchangeKlaarKomen()
    {
        _fabriek.AdLezenMislukt = true;
        Assert.True(State.StartLaden(LaadSoort.Alles));
        WachtOpTaak();

        var taak = State.Taak!;
        Assert.Equal(StapStatus.Fout, taak.Stappen.Single(s => s.Naam == "AD lezen").Status);
        Assert.Contains("domeincontroller", taak.Stappen.Single(s => s.Naam == "AD lezen").Melding);
        Assert.All(taak.Stappen.Where(s => s.Naam != "AD lezen"), s => Assert.Equal(StapStatus.Klaar, s.Status));
        Assert.DoesNotContain("AD", State.LiveOp.Keys);
        Assert.Contains("Entra", State.LiveOp.Keys);
        Assert.Contains("Postvakken", State.LiveOp.Keys);
        Assert.Contains(taak.Log, r => r.Contains("AD lezen: MISLUKT"));
    }

    [Fact]
    public void AnnulerenStoptAlleSporen()
    {
        _fabriek.Vertraging = TimeSpan.FromSeconds(10);
        Assert.True(State.StartLaden(LaadSoort.Alles));
        Thread.Sleep(100);
        State.AnnuleerTaak();
        WachtOpTaak();

        Assert.True(State.Taak!.Geannuleerd);
        Assert.All(State.Taak.Stappen, s => Assert.Equal(StapStatus.Geannuleerd, s.Status));
        Assert.Empty(State.LiveOp);
    }
}
