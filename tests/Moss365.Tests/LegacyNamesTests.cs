using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>Data saved before 0.11 (Dutch property and enum names) still loads; saving writes the current names.</summary>
public sealed class LegacyNamesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "moss365-legacy-" + Guid.NewGuid().ToString("N"));

    public LegacyNamesTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void EnumsAreReadByTheirOldAndCurrentName()
    {
        Assert.True(LegacyNames.TryParseEnum<ChangeAction>("AdGroepLidToevoegen", out var old));
        Assert.Equal(ChangeAction.AdAddGroupMember, old);
        Assert.True(LegacyNames.TryParseEnum<ChangeAction>("AdAddGroupMember", out var current));
        Assert.Equal(ChangeAction.AdAddGroupMember, current);
        Assert.Equal(ResultStatus.Succeeded, LegacyNames.ParseEnum<ResultStatus>("Gelukt"));
        Assert.False(LegacyNames.TryParseEnum<ResultStatus>("Unknown", out _));
        Assert.False(LegacyNames.TryParseEnum<ResultStatus>("999", out _));
        Assert.Equal("Office", LegacyNames.CurrentProperty("Kantoor"));
    }

    [Fact]
    public void AnOrganisationWithDutchNamesLoads()
    {
        var path = Path.Combine(_folder, "organisatie.json");
        File.WriteAllText(path, """
            {
              "_uitleg": "oud",
              "bedrijf": "Contoso",
              "adDomein": "CONTOSO.LOCAL",
              "afdelingen": [ { "naam": "Finance", "groep": "GG_Finance" } ],
              "diensten": [ { "department": "accounting", "afdeling": "Finance" } ],
              "kantoren": [ "head office" ],
              "noodaccounts": [ "break.glass@contoso.com" ]
            }
            """);
        var (config, _) = ConfigStorage.Load(null, path);
        Assert.Equal("CONTOSO.LOCAL", config.AdDomain);
        Assert.Equal("GG_Finance", Assert.Single(config.Divisions).Group);
        Assert.Equal("Finance", Assert.Single(config.Departments).Division);
        Assert.Equal("head office", Assert.Single(config.Offices));
        Assert.Equal("break.glass@contoso.com", Assert.Single(config.EmergencyAccounts));

        // Saving writes the current names only
        var json = ConfigStorage.To(config);
        Assert.Contains("\"divisions\"", json);
        Assert.DoesNotContain("afdelingen", json);
    }

    [Fact]
    public void AChangeLogLineFromBefore011CanStillBeUndone()
    {
        File.WriteAllText(Path.Combine(_folder, "logboek-2026-01.jsonl"), """
            {"tijd":"2026-01-10T10:00:00+01:00","gebruiker":"admin@contoso.com","context":"test","systeem":"AD","actie":"AdGroepLidToevoegen","doel":"CN=an,DC=contoso,DC=local","doelNaam":"an","omschrijving":"Lid maken","parameters":{"groepDn":"CN=g,DC=contoso,DC=local","groep":"g"},"status":"Gelukt","melding":"ok"}
            """ + "\n");
        var entry = Assert.Single(new JsonlChangeLog(_folder).Recent(10));
        Assert.Equal("admin@contoso.com", entry.User);
        Assert.Equal("CN=an,DC=contoso,DC=local", entry.Target);
        Assert.True(entry.Succeeded);
        Assert.False(entry.Failed);
        Assert.Null(Undo.WhyNot(entry));
    }

    [Fact]
    public void AScheduledActionFromBefore011Loads()
    {
        var path = Path.Combine(_folder, "gepland.json");
        File.WriteAllText(path, """
            [ { "id": "6f0c2c1e-0000-4000-8000-000000000001", "soort": "Script", "wanneer": "2026-02-01T08:00:00+01:00", "titel": "Report",
                "scriptSleutel": "Get-Report.ps1", "scriptIngebouwd": true, "herhaling": "Wekelijks" } ]
            """);
        var a = Assert.Single(new ScheduledStorage(path).Read());
        Assert.Equal(ScheduledKind.Script, a.Kind);
        Assert.Equal("Report", a.Title);
        Assert.Equal(Recurrence.Weekly, a.Recurrence);
        Assert.Equal("Get-Report.ps1", a.ScriptKey);

        new ScheduledStorage(path).Save(new[] { a });
        var saved = File.ReadAllText(path);
        Assert.Contains("\"kind\": \"Script\"", saved);
        Assert.Contains("\"recurrence\": \"Weekly\"", saved);
    }
}
