using Moss365.Core.Scripts;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

/// <summary>Scripts (0.7): header and param(), library, last values, runs and scheduling.</summary>
public sealed class ScriptsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-scripts-" + Guid.NewGuid().ToString("N"));
    private ScriptFolders Folders => new(Path.Combine(_folder, "ingebouwd"), Path.Combine(_folder, "gegevens"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private const string WithHeader = """
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
    public void ReadHeader()
    {
        var header = ScriptText.Read(WithHeader, @"C:\x\Export-Agenda.ps1");
        Assert.True(header.FromBlock);
        Assert.Equal("Agenda-export", header.Name);
        Assert.Equal(ScriptConnection.Graph | ScriptConnection.Exchange, header.Connection);
        Assert.Equal("7", header.PowerShell);
        Assert.Equal(10, header.DurationMinutes);
        Assert.Equal(new[] { "Postvak", "Van", "Soort" }, header.Variables.Select(v => v.Name));
        Assert.True(header.Variables[0].Required);
        Assert.Equal(VariableKind.Mailbox, header.Variables[0].Kind);
        Assert.Equal("@werkjaar.van", header.Variables[1].Default);
        Assert.Equal(new[] { "CSV", "ICS" }, header.Variables[2].Options);
    }

    [Fact]
    public void WithoutHeaderDeriveFromParamAndHelp()
    {
        const string text = """
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
        var header = ScriptText.Read(text, "Compare-MailboxAttributes.ps1");
        Assert.False(header.FromBlock);
        Assert.Equal("Compare-MailboxAttributes", header.Name);
        Assert.Equal("Vergelijkt twee postvakken en toont de verschillen.", header.Description);
        Assert.Equal(ScriptConnection.Exchange, header.Connection);
        Assert.True(header.Writes);
        Assert.Equal(
            new[] { VariableKind.Text, VariableKind.Items, VariableKind.YesNo, VariableKind.Number, VariableKind.Choice },
            header.Variables.Select(v => v.Kind));
        Assert.Equal(new[] { "Reviewer", "LimitedDetails" }, header.Variables[4].Options);
    }

    [Fact]
    public void WriteHeaderAndParamAndReadBack()
    {
        // New script with help at the top: the block goes before it, the param() block after it
        const string text = "<#\n.SYNOPSIS\n    Test.\n#>\n$ErrorActionPreference = 'Stop'\nGet-Mailbox $Postvak\n";
        var header = new ScriptHeader
        {
            Name = "Test",
            Connection = ScriptConnection.Exchange,
            Writes = true,
            Variables = new[] { new ScriptVariable("Postvak", VariableKind.Mailbox, true, "Welk postvak?"), new ScriptVariable("Wie", VariableKind.Items) },
        };
        var newValue = ScriptText.Write(header, text);
        Assert.StartsWith("<#MOSS365", newValue);
        Assert.True(newValue.IndexOf(".SYNOPSIS", StringComparison.Ordinal) < newValue.IndexOf("param(", StringComparison.Ordinal));
        Assert.Contains("[CmdletBinding(SupportsShouldProcess = $true)]\r\nparam(\r\n    [string]$Postvak,\r\n    [string[]]$Wie\r\n)", newValue);
        Assert.Contains("Get-Mailbox $Postvak", newValue);

        var back = ScriptText.Read(newValue, "Test.ps1");
        Assert.Equal(header.Variables, back.Variables.Select(v => v with { Options = null }).ToList());
        Assert.True(back.Writes);

        // Writing again with another kind and one variable less: no second block, attributes stay
        const string withAttribute = "param([Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$Postvak, [int]$Weg)\n'x'";
        var modified = ScriptText.Write(header with { Writes = false, Variables = new[] { new ScriptVariable("Postvak", VariableKind.Items) } }, withAttribute);
        Assert.Contains("[Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string[]]$Postvak", modified);
        Assert.DoesNotContain("$Weg", modified);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ScriptText.Write(header, modified), "<#MOSS365"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ScriptText.Write(header, modified), @"param\("));
    }

    [Fact]
    public void WarningsOnStore()
    {
        var header = new ScriptHeader { Name = "x", Writes = true, Variables = new[] { new ScriptVariable("Twee woorden"), new ScriptVariable("a"), new ScriptVariable("A") } };
        var w = ScriptText.Warnings(header, "param([string]$a)\n$x = Read-Host 'wat'\nConnect-ExchangeOnline\nAdd-DistributionGroupMember -Member jan.peeters@test.be");
        Assert.Contains(w, x => x.Contains("Read-Host"));
        Assert.Contains(w, x => x.Contains("Connect-ExchangeOnline"));
        Assert.Contains(w, x => x.Contains("-WhatIf"));
        Assert.Contains(w, x => x.Contains("jan.peeters@test.be"));
        Assert.Contains(w, x => x.Contains("'Twee woorden'"));
        Assert.Contains(w, x => x.Contains("meer dan eens"));

        // The Get-ScriptWaarde block may use Read-Host
        Assert.Empty(ScriptText.Warnings(new ScriptHeader { Name = "x" }, "function Get-ScriptWaarde { Read-Host 'x' }"));
    }

    [Fact]
    public void LibraryLoadSaveImportAndRemove()
    {
        var m = Folders;
        Directory.CreateDirectory(Path.Combine(m.BuiltIn, "Exchange"));
        File.WriteAllText(Path.Combine(m.BuiltIn, "Exchange", "Export-Agenda.ps1"), WithHeader);

        var own = ScriptLibrary.Store(m, ScriptLibrary.NewScript("Mijn script"));
        Assert.Equal("Mijn-script", own.Key);
        Assert.False(ScriptLibrary.IsChangedOutsideApp(m, own));

        // An own script with the name of a built-in one gets another file name
        var copy = ScriptLibrary.Store(m, WithHeader);
        Assert.Equal("Agenda-export", copy.Key);
        var another = ScriptLibrary.Store(m, WithHeader);
        Assert.Equal("Agenda-export-2", another.Key);

        var all = ScriptLibrary.Load(m);
        Assert.Equal(4, all.Count);
        var builtIn = Assert.Single(all, s => s.BuiltIn);
        Assert.Equal("Exchange", builtIn.Folder);
        Assert.Throws<InvalidOperationException>(() => ScriptLibrary.Remove(m, builtIn));

        // Changed outside the app: approve again
        File.AppendAllText(own.FilePath, "\nRemove-Item C:\\");
        var changed = ScriptLibrary.ReadFile(own.FilePath, false);
        Assert.True(ScriptLibrary.IsChangedOutsideApp(m, changed));
        ScriptLibrary.Approve(m, changed);
        Assert.False(ScriptLibrary.IsChangedOutsideApp(m, changed));

        // Importing a script without a header: the header is added
        var source = Path.Combine(_folder, "Oud.ps1");
        File.WriteAllText(source, "param([string]$Groep)\nGet-DistributionGroup $Groep");
        var imported = ScriptLibrary.Import(m, source);
        Assert.True(imported.Header.FromBlock);
        Assert.Equal("Groep", Assert.Single(imported.Header.Variables).Name);
        Assert.Equal(ScriptConnection.Exchange, imported.Header.Connection);

        ScriptLibrary.Remove(m, imported);
        Assert.False(File.Exists(imported.FilePath));
        Assert.Equal("Mijn-script.ps1", ScriptLibrary.FileName("Mijn script"));
        Assert.Equal("a_b.ps1", ScriptLibrary.FileName("a/b"));
    }

    [Fact]
    public void BuiltInWithoutHeaderIsNotInTheLibrary()
    {
        var m = Folders;
        Directory.CreateDirectory(m.BuiltIn);
        File.WriteAllText(Path.Combine(m.BuiltIn, "Oud.ps1"), "param([string]$x)\n'x'");
        File.WriteAllText(Path.Combine(m.BuiltIn, "Met.ps1"), WithHeader);
        Assert.Equal("Agenda-export", Assert.Single(ScriptLibrary.Load(m)).Header.Name);
    }

    /// <summary>The built-in library in the repository: every variable in the header is a parameter of the
    /// script.</summary>
    [Fact]
    public void LibraryInTheRepositoryIsValid()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "scripts")))
        {
            folder = folder.Parent;
        }

        Assert.NotNull(folder);
        var scripts = Directory.EnumerateFiles(Path.Combine(folder!.FullName, "scripts"), "*.ps1", SearchOption.AllDirectories)
            .Select(p => (FilePath: p, Text: File.ReadAllText(p)))
            .Select(x => (x.FilePath, Header: ScriptText.Read(x.Text, x.FilePath), Parameters: ScriptText.ReadParameters(x.Text).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), x.Text))
            .Where(x => x.Header.FromBlock)
            .ToList();

        Assert.NotEmpty(scripts);
        foreach (var s in scripts)
        {
            foreach (var v in s.Header.Variables)
            {
                Assert.True(s.Parameters.Contains(v.Name), $"{Path.GetFileName(s.FilePath)}: variabele {v.Name} is geen parameter");
            }

            if (s.Header.Writes)
            {
                Assert.Matches(@"SupportsShouldProcess\s*=\s*\$true", s.Text);
            }

            // Read-Host does not work in the app: only in the Get-ScriptWaarde block or outside Moss365
            foreach (var rule in s.Text.Split('\n').Where(r => r.Contains("Read-Host") && !r.TrimStart().StartsWith('#')))
            {
                Assert.True(rule.Contains("$antwoord = Read-Host") || rule.Contains("MossUitvoer") || rule.Contains("-or (Read-Host"),
                    $"{Path.GetFileName(s.FilePath)}: Read-Host zonder uitzondering voor Moss365: {rule.Trim()}");
            }
        }
    }

    [Fact]
    public void LastValuesLikeGetScriptValue()
    {
        var m = Folders;
        Directory.CreateDirectory(m.Values);

        // As the block in the scripts writes it (a list as a JSON list)
        File.WriteAllText(Path.Combine(m.Values, "AddToSignatureGroup.json"), """{ "Groep": "Signature_All", "Gebruikers": ["a@test.be", "b@test.be"] }""");
        var w = ScriptValues.Read(m, "AddToSignatureGroup");
        Assert.Equal("Signature_All", w["groep"]);
        Assert.Equal("a@test.be\nb@test.be", w["Gebruikers"]);

        var header = new ScriptHeader { Variables = new[] { new ScriptVariable("Groep"), new ScriptVariable("Gebruikers", VariableKind.Items) } };
        ScriptValues.Save(m, "AddToSignatureGroup", header, new Dictionary<string, string> { ["Gebruikers"] = "c@test.be, d@test.be" });
        var json = File.ReadAllText(Path.Combine(m.Values, "AddToSignatureGroup.json"));
        Assert.Contains("\"c@test.be\"", json);
        Assert.Contains("\"Groep\": \"Signature_All\"", json);
        Assert.Equal("c@test.be\nd@test.be", ScriptValues.Read(m, "AddToSignatureGroup")["Gebruikers"]);

        File.WriteAllText(Path.Combine(m.Values, "kapot.json"), "{kapot");
        Assert.Empty(ScriptValues.Read(m, "kapot"));
    }

    [Theory]
    [InlineData("@werkjaar.van", "2026-09-01")]
    [InlineData("@werkjaar.tot", "2027-08-31")]
    [InlineData("@vorigemaand.van", "2026-09-01")]
    [InlineData("@vorigemaand.tot", "2026-09-30")]
    [InlineData("@dezemaand.tot", "2026-10-31")]
    [InlineData("@gisteren", "2026-10-29")]
    [InlineData("gewoon", "gewoon")]
    public void RelativeValues(string value, string expected)
        => Assert.Equal(expected, ScriptValues.Loose(value, new DateTime(2026, 10, 30, 9, 0, 0)));

    [Fact]
    public void CheckValues()
    {
        var header = ScriptText.Read(WithHeader, "x.ps1") with
        {
            Variables = new[]
            {
                new ScriptVariable("Postvak", VariableKind.Mailbox, true, "Welk postvak?"),
                new ScriptVariable("Aantal", VariableKind.Number),
                new ScriptVariable("Van", VariableKind.Date),
                new ScriptVariable("Soort", VariableKind.Choice, Options: new[] { "CSV", "ICS" }),
            },
        };
        var errors = ScriptValues.Check(header, new Dictionary<string, string> { ["Aantal"] = "drie", ["Van"] = "32/13/2026", ["Soort"] = "PDF" });
        Assert.Equal(4, errors.Count);
        Assert.Empty(ScriptValues.Check(header, new Dictionary<string, string> { ["Postvak"] = "x@test.be", ["Aantal"] = "3", ["Van"] = "@werkjaar.van", ["Soort"] = "ics" }));
    }

    [Fact]
    public void RunsSaveListAndCleanup()
    {
        var m = Folders;
        var now = DateTimeOffset.Now;
        var old = new ScriptRun { Id = "oud", Key = "A", Name = "A", Start = now.AddDays(-91), Result = RunResult.Succeeded, Folder = ScriptRuns.NewFolder(m, "A", "oud") };
        var newValue = new ScriptRun { Id = "nieuw", Key = "A", Name = "A", Start = now.AddDays(-1), Result = RunResult.Error, Folder = ScriptRuns.NewFolder(m, "A", "nieuw") };
        var other = new ScriptRun { Id = "x", Key = "B", Name = "B", Start = now, Preview = true, Result = RunResult.Succeeded, Folder = ScriptRuns.NewFolder(m, "B", "x") };
        foreach (var r in new[] { old, newValue, other })
        {
            ScriptRuns.Save(r);
        }

        Assert.True(Directory.Exists(Path.Combine(newValue.Folder, "bestanden")));
        Assert.Equal(new[] { "x", "nieuw", "oud" }, ScriptRuns.Items(m).Select(r => r.Id));
        Assert.Equal(new[] { "nieuw", "oud" }, ScriptRuns.Items(m, "A").Select(r => r.Id));
        Assert.Equal("voorbeeld: gelukt", ScriptRuns.Items(m, "B").Single().ResultText);

        Assert.Equal(1, ScriptRuns.Cleanup(m, now));
        Assert.Equal(new[] { "nieuw" }, ScriptRuns.Items(m, "A").Select(r => r.Id));
        Assert.NotEqual(newValue.Folder, ScriptRuns.NewFolder(m, "A", "nieuw"));
    }

    [Fact]
    public void ScheduleWithRecurrence()
    {
        var start = new DateTimeOffset(2026, 1, 31, 8, 0, 0, TimeSpan.FromHours(1));
        ScheduledAction With(Recurrence h) => ScheduledPlans.Script("Export-Agenda", true, "Agenda-export", new Dictionary<string, string> { ["Van"] = "@werkjaar.van" }, start, h, "beheerder");

        Assert.Null(With(Recurrence.Once).NextTime(start));
        Assert.Equal(start.AddDays(1), With(Recurrence.Daily).NextTime(start));
        Assert.Equal(start.AddDays(7), With(Recurrence.Weekly).NextTime(start));

        // 31 January, monthly: 28 February, 31 March (not "the 28th" forever)
        var month = With(Recurrence.Monthly);
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 8, 0, 0, start.Offset), month.NextTime(start));
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 8, 0, 0, start.Offset), month.NextTime(new DateTimeOffset(2026, 3, 1, 0, 0, 0, start.Offset)));

        // Weeks missed (app not open): the next one in the future, not all the missed ones
        Assert.Equal(start.AddDays(21), With(Recurrence.Weekly).NextTime(start.AddDays(15)));

        var storage = new ScheduledStorage(Path.Combine(_folder, "gepland.json"));
        storage.Add(month);
        var back = Assert.Single(storage.Read());
        Assert.Equal(ScheduledKind.Script, back.Kind);
        Assert.Equal(Recurrence.Monthly, back.Recurrence);
        Assert.Equal("@werkjaar.van", back.ScriptValues["Van"]);
    }
}
