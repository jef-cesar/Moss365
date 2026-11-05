using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using Moss365.UI.Pages;
using Moss365.UI.Components;
using Moss365.UI.Services;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Moss365.Tests;

/// <summary>Screen tests for the lists in 0.7: pages, filtering per column, selecting rows, and what is kept on Back
/// and Forward.</summary>
public sealed partial class PlanScreenTests
{
    private sealed record Row(string Name, string Department, int Count, DateTime Date);

    /// <summary>600 made-up rows: Row 001 to Row 600, three departments, a count and a date.</summary>
    private static List<Row> Rows(int count = 600)
        => Enumerable.Range(1, count)
            .Select(i => new Row($"Rij {i:000}", (i % 3) switch { 0 => "Bouw", 1 => "Cultuur", _ => "Jeugd" }, i, new DateTime(2026, 1, 1).AddDays(i)))
            .ToList();

    private static readonly IReadOnlyList<Column<Row>> RowColumns = new List<Column<Row>>
    {
        new() { Key = "naam", Title = "Naam", Cell = r => b => b.AddContent(0, r.Name), SortBy = r => r.Name },
        new() { Key = "dienst", Title = "Dienst", Cell = r => b => b.AddContent(0, r.Department), SortBy = r => r.Department },
        new() { Key = "aantal", Title = "Aantal", Number = true, Cell = r => b => b.AddContent(0, r.Count), SortBy = r => r.Count },
        new() { Key = "datum", Title = "Datum", Cell = r => b => b.AddContent(0, r.Date.ToString("yyyy-MM-dd")), SortBy = r => r.Date },
    };

    private IRenderedComponent<KbTable<Row>> RenderTable(List<Row>? rows = null, string id = "testlijst", bool selectable = false, Action<IReadOnlyCollection<string>>? selected = null)
        => RenderComponent<KbTable<Row>>(p =>
        {
            p.Add(x => x.Id, id).Add(x => x.Items, rows ?? Rows()).Add(x => x.Columns, RowColumns).Add(x => x.DefaultSorting, "naam");
            if (selectable)
            {
                p.Add(x => x.Selectable, true).Add(x => x.KeyOf, r => r.Name);
                if (selected is not null)
                {
                    p.Add(x => x.SelectionChanged, EventCallback.Factory.Create(this, selected));
                }
            }
        });

    private static List<string> Names(IRenderedComponent<KbTable<Row>> cut)
        => cut.FindAll("tbody tr").Select(tr => tr.QuerySelectorAll("td").First(td => !td.ClassList.Contains("kb-kies")).TextContent.Trim()).ToList();

    private static void Click(IRenderedComponent<KbTable<Row>> cut, string text)
        => cut.FindAll("button").First(b => b.TextContent.Trim().StartsWith(text, StringComparison.Ordinal)).Click();

    [Fact]
    public void ListShowsPagesOf250AndPages()
    {
        var cut = RenderTable();
        Assert.Equal(250, cut.FindAll("tbody tr").Count);
        Assert.Contains("1–250 van 600", cut.Find(".kb-pagineren").TextContent);
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("Vorige")).HasAttribute("disabled"));

        Click(cut, "Volgende");
        Assert.Equal("Rij 251", Names(cut)[0]);
        Click(cut, "Volgende");
        Assert.Equal(100, cut.FindAll("tbody tr").Count);
        Assert.Contains("501–600 van 600", cut.Find(".kb-pagineren").TextContent);
        Assert.True(cut.FindAll("button").First(b => b.TextContent.Contains("Volgende")).HasAttribute("disabled"));

        // 500 per page, then everything; the choice is saved
        cut.Find(".kb-pagineren select").Change("500");
        Assert.Equal(500, cut.FindAll("tbody tr").Count);
        cut.Find(".kb-pagineren select").Change("0");
        Assert.Equal(600, cut.FindAll("tbody tr").Count);
        Assert.Empty(cut.FindAll(".kb-pagineren button"));
        Assert.Contains("\"PerPage\": 0", View());
        Assert.Equal(600, RenderTable().FindAll("tbody tr").Count);
    }

    [Fact]
    public void ListRemembersThePageWhileTheAppIsOpen()
    {
        var cut = RenderTable();
        Click(cut, "Volgende");
        Assert.Equal("Rij 251", Names(cut)[0]);

        // Open again (e.g. Back): the same page; another list starts on page 1
        Assert.Equal("Rij 251", Names(RenderTable())[0]);
        Assert.Equal("Rij 001", Names(RenderTable(id: "andere"))[0]);

        // Fewer rows (another search term on the screen): back to page 1
        cut.SetParametersAndRender(p => p.Add(x => x.Items, Rows(300)));
        Assert.Equal("Rij 001", Names(cut)[0]);
    }

    [Fact]
    public void ListFiltersPerColumnWithTextChoiceAndRange()
    {
        var cut = RenderTable();
        Click(cut, "Filteren");
        var filters = cut.FindAll("tr.kb-filterrij th");
        Assert.Equal(4, filters.Count);

        // Name: text; Department: choice (3 values); Count and Date: range
        Assert.NotNull(filters[0].QuerySelector("input[placeholder]"));
        Assert.NotNull(filters[1].QuerySelector("details.kb-keuzefilter"));
        Assert.Equal("number", filters[2].QuerySelector("input")!.GetAttribute("type"));
        Assert.Equal("date", filters[3].QuerySelector("input")!.GetAttribute("type"));

        cut.Find("tr.kb-filterrij input[placeholder]").Input("Rij 05");
        Assert.Equal(10, cut.FindAll("tbody tr").Count); // Row 050 to 059
        Assert.Contains("1 filter: 10 van 600", cut.Markup);
        Click(cut, "Filters wissen");
        Assert.Equal(250, cut.FindAll("tbody tr").Count);

        cut.FindAll("tr.kb-filterrij th")[1].QuerySelectorAll("label").First(l => l.TextContent.Contains("Bouw")).QuerySelector("input")!.Change(true);
        Assert.Contains("1 filter: 200 van 600", cut.Markup);
        Assert.All(cut.FindAll("tbody tr"), tr => Assert.Contains("Bouw", tr.TextContent));

        // Two filters together: Bouw and count 100 to 130
        var count = cut.FindAll("tr.kb-filterrij th")[2].QuerySelectorAll("input");
        count[0].Change("100");
        cut.FindAll("tr.kb-filterrij th")[2].QuerySelectorAll("input")[1].Change("130");
        Assert.Contains("2 filters: 10 van 600", cut.Markup); // 102, 105, ..., 129 (130 is not Bouw)

        // Date: from and to
        Click(cut, "Filters wissen");
        var date = cut.FindAll("tr.kb-filterrij th")[3].QuerySelectorAll("input");
        date[0].Change("2026-01-02");
        cut.FindAll("tr.kb-filterrij th")[3].QuerySelectorAll("input")[1].Change("2026-01-05");
        Assert.Equal(new[] { "Rij 001", "Rij 002", "Rij 003", "Rij 004" }, Names(cut));

        // No rows: a message with clear
        cut.Find("tr.kb-filterrij input[placeholder]").Input("bestaat niet");
        Assert.Contains("Geen rijen voor deze filters", cut.Markup);
    }

    [Fact]
    public void ListExportsFilteredAndSelectedRows()
    {
        var cut = RenderTable(selectable: true);
        Click(cut, "Filteren");
        cut.Find("tr.kb-filterrij input[placeholder]").Input("Rij 05");
        Click(cut, "Exporteren");
        var filtered = File.ReadAllLines(Assert.Single(Directory.GetFiles(ExportFolder, "Testlijst-*.csv")));
        Assert.Equal(11, filtered.Length); // header + 10

        Directory.Delete(ExportFolder, true);
        cut.FindAll("tbody td.kb-kies input")[0].Click();
        cut.FindAll("tbody td.kb-kies input")[3].Click();
        Assert.Contains("2 gekozen", cut.Markup);
        Click(cut, "Exporteren (gekozen)");
        var selected = File.ReadAllLines(Assert.Single(Directory.GetFiles(ExportFolder, "Testlijst-*.csv")));
        Assert.Equal(3, selected.Length);
        Assert.Contains("Rij 050", selected[1]);
        Assert.Contains("Rij 053", selected[2]);
    }

    [Fact]
    public void ListSelectsRowsWithShiftAndAcrossAllPages()
    {
        IReadOnlyCollection<string> last = Array.Empty<string>();
        var cut = RenderTable(selectable: true, selected: s => last = s);

        // Shift-click: everything between the previous click and this one
        cut.FindAll("tbody td.kb-kies input")[2].Click();
        cut.FindAll("tbody td.kb-kies input")[6].Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { ShiftKey = true });
        Assert.Equal(5, last.Count);
        Assert.Equal(5, cut.FindAll("tr.kb-rij-gekozen").Count);
        Assert.Contains("Rij 005", last);

        // The whole page, then all 600 across the pages
        cut.Find("thead th.kb-kies input").Change(true);
        Assert.Equal(250, last.Count);
        Assert.Contains("De 250 rijen op deze pagina zijn gekozen", cut.Markup);
        Click(cut, "Alle 600 rijen kiezen");
        Assert.Equal(600, last.Count);
        Assert.DoesNotContain("op deze pagina zijn gekozen", cut.Markup);

        // The next page is selected too; clearing empties everything
        Click(cut, "Volgende");
        Assert.Equal(250, cut.FindAll("tr.kb-rij-gekozen").Count);
        Click(cut, "Selectie wissen");
        Assert.Empty(last);
        Assert.Empty(cut.FindAll("tr.kb-rij-gekozen"));
    }

    [Fact]
    public void ListSelectsOnlyWhatIsPossibleInItsOwnSelection()
    {
        var own = new HashSet<string>();
        var cut = RenderComponent<KbTable<Row>>(p => p
            .Add(x => x.Id, "kankiezen").Add(x => x.Items, Rows(10)).Add(x => x.Columns, RowColumns).Add(x => x.DefaultSorting, "naam")
            .Add(x => x.Selectable, true).Add(x => x.KeyOf, r => r.Name).Add(x => x.Selected, own)
            .Add(x => x.CanChoose, r => r.Count % 2 == 0).Add(x => x.WhyNot, r => r.Count % 2 == 0 ? null : "oneven"));

        var boxes = cut.FindAll("tbody td.kb-kies input");
        Assert.Equal(5, boxes.Count(v => v.HasAttribute("disabled")));
        Assert.Equal("oneven", boxes[0].GetAttribute("title"));

        cut.Find("thead th.kb-kies input").Change(true);
        Assert.Equal(5, own.Count);
        Assert.All(own, k => Assert.True(int.Parse(k[4..]) % 2 == 0));
        Assert.Empty(State.TableSession("kankiezen").Selected); // the own selection, not the one of the session
    }

    [Fact]
    public void ListWithoutSelectionHasNoCheckBoxes()
    {
        var cut = RenderTable(Rows(10));
        Assert.Empty(cut.FindAll(".kb-kies"));
        Assert.Empty(cut.FindAll(".kb-pagineren button"));
        Assert.Empty(cut.FindAll(".kb-pagineren select")); // fewer than 250 rows: no choice needed
    }

    // ---------------------------------------------------------------- Back and Forward: what is kept

    [Fact]
    public void HistoryFollowsBackForwardAndSearchTerms()
    {
        var g = new History();
        g.Visited("app/");
        g.Visited("app/opkuis");
        g.Visited("app/opkuis?dagen=90"); // the same page, another search term: no new step
        Assert.Equal(new[] { "app/", "app/opkuis?dagen=90" }, g.Steps);

        Assert.Equal("app/", g.Back());
        Assert.True(g.Visited("app/"));
        Assert.True(g.CanForward);
        Assert.Equal("app/opkuis?dagen=90", g.Forward());
        Assert.True(g.Visited("app/opkuis?dagen=90"));

        // A, B, A (via a link): three steps; Forward from B goes to the third, not back to the first
        var h = new History();
        h.Visited("a");
        h.Visited("b");
        h.Visited("a");
        h.Back();
        h.Visited("b");
        Assert.Equal(1, h.Position);
        h.Forward();
        h.Visited("a");
        Assert.Equal(2, h.Position);
    }

    [Fact]
    public void SearchTermAndFiltersStayAfterBack()
    {
        var cut = RenderComponent<Users>();
        cut.Find("input.kb-breed").Input("jan");
        cut.Find("select.kb-invoer").Change("AD + Entra");
        var found = cut.FindAll("tbody tr").Count;
        Assert.True(found > 0);

        // To a user and back: the same list
        RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        var back = RenderComponent<Users>();
        Assert.Equal("jan", back.Find("input.kb-breed").GetAttribute("value"));
        Assert.Equal("AD + Entra", back.Find("select.kb-invoer").GetAttribute("value"));
        Assert.Equal(found, back.FindAll("tbody tr").Count);
    }

    [Fact]
    public void FormIsKeptButNeverThePassword()
    {
        var cut = RenderComponent<NewUser>();
        cut.FindAll("select")[0].Change(State.Config.Populations[0].Name);
        cut.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).Change("Testa");
        cut.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Achternaam")).Change("Verzonnen");
        cut.FindAll("button").First(b => b.TextContent == "Voorbeeld").Click();
        Assert.Contains("<h2>Voorstel</h2>", cut.Markup);

        // Away and back: the form is still there, the proposal (with the initial password) is not
        RenderComponent<Users>();
        var back = RenderComponent<NewUser>();
        Assert.Equal("Testa", back.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).GetAttribute("value"));
        Assert.Equal("Verzonnen", back.FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Achternaam")).GetAttribute("value"));
        Assert.DoesNotContain("<h2>Voorstel</h2>", back.Markup);
        Assert.Contains("Testa", SavedStatus());
        Assert.DoesNotContain("wachtwoord", SavedStatus(), StringComparison.OrdinalIgnoreCase);

        // Clearing forgets the form
        back.FindAll("button").First(b => b.TextContent == "Leegmaken").Click();
        Assert.Equal(string.Empty, RenderComponent<NewUser>().FindAll("input").First(i => i.ParentElement!.TextContent.StartsWith("Voornaam")).GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void PasswordResetIsNotInThePageState()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:bo.janssens"));
        cut.FindAll("button").First(b => b.TextContent == "Wachtwoord opnieuw instellen").Click();
        var password = cut.Find(".kb-kopieer input").GetAttribute("value")!;
        RenderComponent<Users>().Find("input.kb-breed").Input("bo");
        Assert.DoesNotContain(password, SavedStatus());
        Assert.DoesNotContain(State.PageState.Everything.Values, v => v.GetType().Name.Contains("Wachtwoord", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WithToken { public string Name { get; set; } = string.Empty; public string? GraphToken { get; set; } }
    private sealed class Nested { public List<WithToken> Items { get; } = new(); }
    private sealed class WithoutSecret { public string Name { get; set; } = string.Empty; public bool PasswordExpiresNever { get; set; } }

    [Fact]
    public void PageStateRefusesFieldsThatLookLikeASecret()
    {
        Assert.Throws<InvalidOperationException>(() => State.PageState.Of<WithToken>("x"));
        Assert.Throws<InvalidOperationException>(() => State.PageState.Of<Nested>("y"));
        Assert.NotNull(State.PageState.Of<WithoutSecret>("z")); // no text: no secret

        // All forms the app saves are in order
        foreach (var t in new[] { typeof(Moss365.Core.Changes.NewUserRequest), typeof(Moss365.Core.Changes.OffboardingOptions),
                     typeof(Moss365.Core.Changes.RoomDesired), typeof(Moss365.Core.Changes.GroupDesired), typeof(Moss365.Core.Changes.MailboxDesired) })
        {
            PageState.CheckType(t);
        }
    }

    [Fact]
    public void SelectedRoomsAndFormStayAfterBack()
    {
        var cut = RenderComponent<Rooms>();
        cut.FindAll("tbody td.kb-kies input")[0].Click();
        cut.FindAll("button").First(b => b.TextContent == "Standaard invullen").Click();

        var back = RenderComponent<Rooms>();
        Assert.Contains("Wijzigen (1 geselecteerd)", back.Markup);
        Assert.Single(back.FindAll("tr.kb-rij-gekozen"));
        back.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("BookingWindowInDays=180", back.Find("section.kb-plan").TextContent);
    }

    private string SavedStatus()
        => JsonSerializer.Serialize(State.PageState.Everything.Values.ToList(), new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles });
}
