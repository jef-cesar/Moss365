using Bunit;
using Moss365.Core.Inventaris;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>#20: de agenda van een room op zijn pagina.</summary>
public sealed partial class PlanSchermTests
{
    [Fact]
    public void DeAgendaVanEenRoomPerWeekEnInHetToegangslog()
    {
        var maandag = AgendaPeriode.Maandag(DateTime.Now);
        _fabriek.Agenda.Add(new Afspraak(maandag.AddHours(9), maandag.AddHours(10), "Overleg dienst", "Organisator Een", "een@test.be", false, false, "busy", false));
        _fabriek.Agenda.Add(new Afspraak(maandag.AddDays(1).AddHours(14), maandag.AddDays(1).AddHours(15), "Geheim", "Organisator Twee", "twee@test.be", false, true, "busy", false));
        _fabriek.Agenda.Add(new Afspraak(maandag.AddDays(8).AddHours(9), maandag.AddDays(8).AddHours(12), "Volgende week", "Organisator Een", "een@test.be", false, false, "tentative", false));

        var cut = RenderComponent<RoomDetail>(p => p.Add(x => x.Smtp, "rm-vz-a@test.be"));
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("2 afspraken van", cut.Markup));
        Assert.Contains("Overleg dienst", cut.Markup);
        Assert.Contains("Privé", cut.Markup);
        Assert.DoesNotContain("Geheim", cut.Markup); // een privéafspraak toont geen onderwerp
        Assert.DoesNotContain("Volgende week", cut.Markup.Replace(">Volgende week</button>", string.Empty));
        var (adres, van, tot) = Assert.Single(_fabriek.AgendaGelezen);
        Assert.Equal("rm-vz-a@test.be", adres);
        Assert.Equal(maandag, van);
        Assert.Equal(maandag.AddDays(7), tot);

        cut.FindAll("button").First(b => b.TextContent == "Volgende week").Click();
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("1 afspraken van", cut.Markup));
        Assert.Contains("voorlopig", cut.Markup);

        var toegang = State.Databank.LeesToegang(10);
        Assert.Equal(2, toegang.Count(t => t.Wat == "Agenda bekeken"));
        Assert.All(toegang.Where(t => t.Wat == "Agenda bekeken"), t => Assert.StartsWith("room rm-vz-a@test.be", t.Details));
    }

    [Fact]
    public void ZonderRechtOpDeAgendaZegtDeAppHoeHetWel()
    {
        _fabriek.AgendaGeenRecht = true;
        var cut = RenderComponent<RoomDetail>(p => p.Add(x => x.Smtp, "rm-vz-a@test.be"));
        cut.FindAll("button").First(b => b.TextContent == "Gekozen periode").Click();
        cut.FindAll("input[type=date]")[1].Change(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        Assert.Contains("einddatum ligt voor de begindatum", cut.Markup);
        Assert.Empty(_fabriek.AgendaGelezen);

        cut.FindAll("input[type=date]")[1].Change(DateTime.Today.AddDays(6).ToString("yyyy-MM-dd"));
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("mag de agenda van deze room niet lezen", cut.Markup));
        Assert.Contains("Reviewer", cut.Markup);
        Assert.Contains("Add-MailboxFolderPermission", cut.Markup);
    }
}
