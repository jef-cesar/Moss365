using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Xunit;

namespace Moss365.Tests;

public class CsvReaderTests
{
    [Fact]
    public void ReadsQuotesSeparatorsAndLineEndingsInAField()
    {
        var text = "﻿\"Naam\";\"Opmerking\";\"Getal\"\r\n\"Jan\";\"een; twee\";\"1\"\r\n\"Els\";\"regel 1\nregel 2 met \"\"citaat\"\"\";\"2\"\r\n";
        var rows = CsvReader.Read(text);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Jan", rows[0]["Naam"]);
        Assert.Equal("een; twee", rows[0]["Opmerking"]);
        Assert.Equal("regel 1\nregel 2 met \"citaat\"", rows[1]["Opmerking"]);
        Assert.Equal("2", rows[1]["getal"]);
    }

    [Fact]
    public void EmptyFileGivesNoRows()
    {
        Assert.Empty(CsvReader.Read(string.Empty));
        Assert.Empty(CsvReader.Read("\"Kop\"\r\n"));
    }

    [Fact]
    public void MissingFieldsBecomeEmpty()
    {
        var rows = CsvReader.Read("A;B;C\n1;2\n");
        Assert.Equal(string.Empty, rows[0]["C"]);
    }
}

public class DnHelpTests
{
    [Theory]
    [InlineData("CN=Jan,OU=TD,OU=Personeel,DC=x,DC=be", "Personeel/TD/Jan")]
    [InlineData("OU=Zorg\\, Welzijn,DC=x,DC=be", "Zorg, Welzijn")]
    [InlineData("", "")]
    public void OuPath(string dn, string expected) => Assert.Equal(expected, DnHelp.OuPath(dn));

    [Fact]
    public void ParentAndName()
    {
        Assert.Equal("OU=TD,DC=x,DC=be", DnHelp.Parent("CN=Jan,OU=TD,DC=x,DC=be"));
        Assert.Equal("TD", DnHelp.OuPathOfObject("CN=Jan,OU=TD,DC=x,DC=be"));
        Assert.Equal("gg_ICT", DnHelp.Name("CN=gg_ICT,OU=GROEPEN,DC=x"));
    }

    [Fact]
    public void IsUnderTakesSubOusIntoAccount()
    {
        var paths = new[] { "GEMEENTEBESTUUR/GEBRUIKERS" };
        Assert.True(DnHelp.FallsUnder("GEMEENTEBESTUUR/GEBRUIKERS", paths));
        Assert.True(DnHelp.FallsUnder("gemeentebestuur/gebruikers/Extra", paths));
        Assert.False(DnHelp.FallsUnder("GEMEENTEBESTUUR/GEBRUIKERSOUD", paths));
    }
}

public class InventoryLoaderTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "kb-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    private string Folder(string name, params (string File, string Content)[] files)
    {
        var folder = Path.Combine(_base, name);
        Directory.CreateDirectory(folder);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(folder, file), content);
        }

        return folder;
    }

    [Fact]
    public void TakesTheNewestFolderPerFile()
    {
        var old = Folder("Inventaris-20261015-1546",
            (InventoryLoader.EntraUsers, "\"Id\";\"UserPrincipalName\"\n\"oud\";\"a@example.com\"\n"),
            (InventoryLoader.AdUsers, "\"SamAccountName\"\n\"oud\"\n"));
        var newValue = Folder("Inventaris-20261016-1343",
            (InventoryLoader.AdUsers, "\"SamAccountName\"\n\"nieuw1\"\n\"nieuw2\"\n"));
        Folder("IetsAnders", (InventoryLoader.AdUsers, "\"SamAccountName\"\n\"fout\"\n"));

        var folders = InventoryLoader.SearchFolders(_base);
        Assert.Equal(new[] { newValue, old }, folders);

        var data = InventoryLoader.Load(folders);
        Assert.Equal(new[] { "nieuw1", "nieuw2" }, data.AdUsers.Select(a => a.SamAccountName));
        Assert.Single(data.EntraUsers);
        Assert.Equal(newValue, data.Sources.Single(b => b.File == InventoryLoader.AdUsers).Folder);
        Assert.Equal(old, data.Sources.Single(b => b.File == InventoryLoader.EntraUsers).Folder);
        Assert.Null(data.Sources.Single(b => b.File == InventoryLoader.EntraGroups).Folder);
    }

    [Fact]
    public void MissingBaseFolderGivesNoFolders() => Assert.Empty(InventoryLoader.SearchFolders(Path.Combine(_base, "bestaat-niet")));

    [Fact]
    public void LinksAdAndEntraBySamAccountName()
    {
        var data = TestData.AppEnvironment();
        var bo = data.SearchPerson("ad:bo.janssens")!;
        Assert.NotNull(bo.Entra);
        Assert.Equal("e-bo", bo.Entra!.Id);
        Assert.Equal("AD + Entra", bo.Source);

        Assert.Equal("Alleen AD", data.SearchPerson("ad:dirk.intern")!.Source);
        Assert.Equal("Alleen Entra", data.SearchPerson("entra:e-info")!.Source);
        Assert.True(data.SearchPerson("entra:e-gast")!.IsGuest);
    }

    [Fact]
    public void KeepsSpacesInAttributesButNotInKeys()
    {
        var data = TestData.AppEnvironment();
        Assert.Equal("cultuur ", data.SearchPerson("ad:bo.janssens")!.Ad!.Department);
        Assert.Equal("cultuur", data.SearchPerson("ad:bo.janssens")!.Department);
    }

    [Fact]
    public void GroupsOfAPerson()
    {
        var data = TestData.AppEnvironment();
        var an = data.SearchPerson("ad:an.peeters")!;
        Assert.Equal(new[] { "Domain Admins", "gg_rww_foxbeleid", "gg_wifiIntern" }, data.AdGroupsOf(an).Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(5, data.CloudGroupsOf(an).Count());
    }
}

public class AdEntraComparisonTests
{
    private static IReadOnlyList<Difference> Differences() => AdEntraComparison.Compare(TestData.AppEnvironment(), TestData.Config());

    [Fact]
    public void MissingInEntraWithReason()
    {
        var v = Differences().Where(x => x.Kind == DifferenceKind.MissingInEntra).ToList();
        var dirk = Assert.Single(v);
        Assert.Equal("dirk.intern", dirk.Person.Ad!.SamAccountName);
        Assert.Contains("niet routeerbaar", dirk.Explanation);
        Assert.Contains("nooit aangemeld", dirk.Explanation);
    }

    [Fact]
    public void OutsideTheSynchronisedOusNoMessage()
        => Assert.DoesNotContain(Differences(), x => x.Person.Ad?.SamAccountName == "vpn.leverancier");

    [Fact]
    public void DisabledInAdButActiveInEntra()
    {
        var v = Assert.Single(Differences(), x => x.Kind == DifferenceKind.StatusDiffers);
        Assert.Equal("oud.account", v.Person.Ad!.SamAccountName);
    }

    [Fact]
    public void TrailingSpaceIsNoDifferenceCaseIs()
    {
        var differences = Differences();
        Assert.DoesNotContain(differences, x => x.Person.Ad?.SamAccountName == "bo.janssens" && x.Attribute.StartsWith("Dienst"));
        var cas = Assert.Single(differences, x => x.Person.Ad?.SamAccountName == "cas.vos" && x.Attribute == "UPN");
        Assert.Equal("Alleen hoofdletters verschillen", cas.Explanation);
    }

    [Fact]
    public void WithoutAdOrEntraNoComparison()
        => Assert.Empty(AdEntraComparison.Compare(new InventoryData(), TestData.Config()));
}

public class MirrorUserTests
{
    private static IReadOnlyList<MirrorRule> Rules(string reference = "ad:an.peeters", string target = "ad:bo.janssens")
    {
        var data = TestData.AppEnvironment();
        return MirrorUser.Compute(data, TestData.Config(), data.SearchPerson(reference)!, data.SearchPerson(target)!);
    }

    private static MirrorRule Rule(IReadOnlyList<MirrorRule> rules, string groupValue) => Assert.Single(rules, r => r.Group == groupValue);

    [Fact]
    public void AdGroups()
    {
        var r = Rules();
        Assert.Equal(MirrorStatus.AlreadyMember, Rule(r, "gg_wifiIntern").Status);
        var fox = Rule(r, "gg_rww_foxbeleid");
        Assert.Equal(MirrorStatus.Add, fox.Status);
        Assert.Equal(WriteRoute.ActiveDirectory, fox.Route);
        Assert.Equal(string.Empty, fox.Remark);
        Assert.Equal(MirrorStatus.OnlyAtTarget, Rule(r, "gg_eigen").Status);
    }

    [Fact]
    public void PrivilegedGroupIsNeverCopied()
    {
        var da = Rule(Rules(), "Domain Admins");
        Assert.Equal(MirrorStatus.DoNotCopy, da.Status);
        Assert.Equal(WriteRoute.No, da.Route);
    }

    [Fact]
    public void CloudGroupsGetTheRightRoute()
    {
        var r = Rules();
        Assert.Equal(MirrorStatus.AlreadyMember, Rule(r, "DNST_CULTUUR").Status);
        Assert.Equal(WriteRoute.Exchange, Rule(r, "SG-SM-info-Full").Route);
        Assert.Contains("gedeeld postvak", Rule(r, "SG-SM-info-Full").Remark);
        Assert.Equal(WriteRoute.Exchange, Rule(r, "Gemeentehuis").Route);
        Assert.Equal(WriteRoute.ActiveDirectory, Rule(r, "Gesynchroniseerd").Route);
        Assert.Equal(MirrorStatus.OnlyAtTarget, Rule(r, "WKG_Eigen").Status);
    }

    [Fact]
    public void DynamicGroupIsAutomatic()
    {
        var all = Rule(Rules("ad:an.peeters", "ad:cas.vos"), "Alle gebruikers");
        Assert.Equal(MirrorStatus.Automatic, all.Status);
        Assert.Equal(WriteRoute.No, all.Route);
    }

    [Fact]
    public void TargetWithoutEntraCannotGetCloudGroups()
    {
        var r = Rules("ad:an.peeters", "ad:dirk.intern");
        Assert.All(r.Where(x => x.Layer == "Cloud" && x.Status != MirrorStatus.Automatic),
            x => Assert.Equal(MirrorStatus.DoNotCopy, x.Status));
        Assert.Equal(MirrorStatus.Add, Rule(r, "gg_wifiIntern").Status);
    }
}

public class OrganisationConfigTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-cfg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void FindsDepartmentIgnoringSpacesAndCase()
    {
        var c = TestData.Config();
        Assert.Equal("cultuur", c.SearchDepartment("  Cultuur ")!.Department);
        Assert.Equal("Vrije tijd", c.DivisionFor("cultuur ")!.Name);
        Assert.Null(c.DivisionFor("gezinszorg"));
        Assert.Null(c.SearchDepartment(""));
    }

    [Fact]
    public void UserFileWinsAndSavingKeepsSpecialCharacters()
    {
        var defaultValue = Path.Combine(_folder, "standaard.json");
        var own = Path.Combine(_folder, "eigen", "organisatie.json");
        ConfigStorage.Save(TestData.Config(), defaultValue);

        var (c1, source1) = ConfigStorage.Load(defaultValue, own);
        Assert.Equal(defaultValue, source1);

        c1.Departments.Add(new DepartmentMapping { Department = "financiën", Division = "Burger en welzijn" });
        ConfigStorage.Save(c1, own);
        Assert.Contains("financiën", File.ReadAllText(own));

        var (c2, source2) = ConfigStorage.Load(defaultValue, own);
        Assert.Equal(own, source2);
        Assert.Equal("Burger en welzijn", c2.DivisionFor("Financiën")!.Name);
    }

    [Fact]
    public void WithoutFilesAnEmptyConfiguration()
    {
        var (c, source) = ConfigStorage.Load(Path.Combine(_folder, "x.json"), null);
        Assert.Null(source);
        Assert.Empty(c.Departments);
    }

    [Fact]
    public void TheBundledOrganisationIsValid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", "organisatie.json");
        var (c, source) = ConfigStorage.Load(path, null);
        Assert.Equal(path, source);
        Assert.Equal("Contoso", c.Company);
        Assert.NotEmpty(c.SynchronisedOus);
        Assert.Equal(4, c.Divisions.Count);
        Assert.All(c.Departments.Where(d => d.Division is not null),
            d => Assert.Contains(c.Divisions, a => a.Name == d.Division));
        Assert.DoesNotContain(c.Departments.GroupBy(d => OrganisationConfig.Normalise(d.Department)), g => g.Count() > 1);
        Assert.Equal("Social services", c.DivisionFor("home care")!.Name);
        Assert.Equal("IT", c.DivisionFor("Helpdesk ")!.Name);
    }
}

public class OrganisationAnalysisTests
{
    [Fact]
    public void RecognisesSpellingVariantsAndUnknownDepartments()
    {
        var c = TestData.Config();
        var list = OrganisationAnalysis.DepartmentsInData(TestData.AppEnvironment(), c);

        var exact = Assert.Single(list, x => x.Value == "cultuur");
        Assert.True(exact.InConfig);
        Assert.False(exact.IsSpellingVariant);
        Assert.True(Assert.Single(list, x => x.Value == "cultuur ").IsSpellingVariant);

        c.Departments.RemoveAll(d => d.Department == "gezinszorg");
        Assert.False(Assert.Single(OrganisationAnalysis.DepartmentsInData(TestData.AppEnvironment(), c), x => x.Value == "gezinszorg").InConfig);
    }

    [Fact]
    public void CountsUsersPerDepartment()
    {
        var counts = OrganisationAnalysis.CountPerDepartment(TestData.AppEnvironment(), TestData.Config());
        Assert.Equal(2, counts["cultuur"]);
        Assert.Equal(1, counts["gezinszorg"]);
    }
}

public sealed class OldInstallationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "m365-oud-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, true);
        }
    }

    [Fact]
    public void APreviousInstallationIsRecognisedAndCopied()
    {
        var old = Path.Combine(_base, "OldApp");
        var newValue = Path.Combine(_base, "Moss365");
        Directory.CreateDirectory(Path.Combine(old, "Inventaris", "Inventaris-1"));
        Directory.CreateDirectory(Path.Combine(_base, "Iets anders"));
        File.WriteAllText(Path.Combine(old, Moss365.Core.Storage.Database.FileName), "db");
        File.WriteAllText(Path.Combine(old, "organisatie.json"), "{}");
        File.WriteAllText(Path.Combine(old, "Inventaris", "Inventaris-1", "ad-users.csv"), "x");
        File.WriteAllText(Path.Combine(_base, "Iets anders", Moss365.Core.Storage.Database.FileName), "geen organisatie.json");

        Assert.Equal(old, Moss365.Core.Storage.OldInstallation.Find(_base, newValue));
        Assert.Equal(old, Moss365.Core.Storage.OldInstallation.MigrateIfNeeded(_base, newValue));
        Assert.True(File.Exists(Path.Combine(newValue, "Inventaris", "Inventaris-1", "ad-users.csv")));
        Assert.True(File.Exists(Path.Combine(old, Moss365.Core.Storage.Database.FileName))); // the old one stays
        Assert.Contains(old, File.ReadAllText(Path.Combine(newValue, Moss365.Core.Storage.OldInstallation.Marker)));

        // A second time: the new folder already has a database, nothing to do
        Assert.Null(Moss365.Core.Storage.OldInstallation.MigrateIfNeeded(_base, newValue));
        Assert.Null(Moss365.Core.Storage.OldInstallation.Find(Path.Combine(_base, "bestaat-niet"), newValue));
    }
}
