using Bunit;
using Moss365.Core.Inventory;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>#20: the calendar of a room on its page.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheCalendarOfARoomPerWeekAndInTheAccessLog()
    {
        var monday = CalendarPeriod.Monday(DateTime.Now);
        _factory.Calendar.Add(new Appointment(monday.AddHours(9), monday.AddHours(10), "Overleg dienst", "Organisator Een", "een@test.be", false, false, "busy", false));
        _factory.Calendar.Add(new Appointment(monday.AddDays(1).AddHours(14), monday.AddDays(1).AddHours(15), "Geheim", "Organisator Twee", "twee@test.be", false, true, "busy", false));
        _factory.Calendar.Add(new Appointment(monday.AddDays(8).AddHours(9), monday.AddDays(8).AddHours(12), "Volgende week", "Organisator Een", "een@test.be", false, false, "tentative", false));

        var cut = RenderComponent<RoomDetail>(p => p.Add(x => x.Smtp, "rm-vz-a@test.be"));
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("2 afspraken van", cut.Markup));
        Assert.Contains("Overleg dienst", cut.Markup);
        Assert.Contains("Privé", cut.Markup);
        Assert.DoesNotContain("Geheim", cut.Markup); // a private appointment shows no subject
        Assert.DoesNotContain("Volgende week", cut.Markup.Replace(">Volgende week</button>", string.Empty));
        var (address, start, until) = Assert.Single(_factory.CalendarRead);
        Assert.Equal("rm-vz-a@test.be", address);
        Assert.Equal(monday, start);
        Assert.Equal(monday.AddDays(7), until);

        cut.FindAll("button").First(b => b.TextContent == "Volgende week").Click();
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("1 afspraken van", cut.Markup));
        Assert.Contains("voorlopig", cut.Markup);

        var access = State.Database.ReadAccess(10);
        Assert.Equal(2, access.Count(t => t.What == "Agenda bekeken"));
        Assert.All(access.Where(t => t.What == "Agenda bekeken"), t => Assert.StartsWith("room rm-vz-a@test.be", t.Details));
    }

    [Fact]
    public void WithoutCalendarPermissionTheAppSaysHowTo()
    {
        _factory.CalendarNoPermission = true;
        var cut = RenderComponent<RoomDetail>(p => p.Add(x => x.Smtp, "rm-vz-a@test.be"));
        cut.FindAll("button").First(b => b.TextContent == "Gekozen periode").Click();
        cut.FindAll("input[type=date]")[1].Change(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"));
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        Assert.Contains("einddatum ligt voor de begindatum", cut.Markup);
        Assert.Empty(_factory.CalendarRead);

        cut.FindAll("input[type=date]")[1].Change(DateTime.Today.AddDays(6).ToString("yyyy-MM-dd"));
        cut.FindAll("button").First(b => b.TextContent == "Agenda laden").Click();
        cut.WaitForAssertion(() => Assert.Contains("mag de agenda van deze room niet lezen", cut.Markup));
        Assert.Contains("Reviewer", cut.Markup);
        Assert.Contains("Add-MailboxFolderPermission", cut.Markup);
    }
}
