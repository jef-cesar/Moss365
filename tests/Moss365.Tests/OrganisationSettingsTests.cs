using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R9: the settings of an organisation, suggested from its data: naming, OUs, company, licences, templates.</summary>
public sealed class OrganisationSettingsTests
{
    [Theory]
    [InlineData(LocalPartPattern.GivenDotSurname, "an.vandenbroeck")]
    [InlineData(LocalPartPattern.GivenSurname, "anvandenbroeck")]
    [InlineData(LocalPartPattern.InitialDotSurname, "a.vandenbroeck")]
    [InlineData(LocalPartPattern.InitialSurname, "avandenbroeck")]
    [InlineData(LocalPartPattern.SurnameDotGiven, "vandenbroeck.an")]
    [InlineData(LocalPartPattern.SurnameInitial, "vandenbroecka")]
    public void TheAccountNameFollowsThePattern(LocalPartPattern pattern, string expected)
        => Assert.Equal(expected, NamingSettings.Local("Ân", "Van den Broeck", pattern));

    [Theory]
    [InlineData(DisplayNamePattern.GivenSurname, "An Van den Broeck")]
    [InlineData(DisplayNamePattern.SurnameGiven, "Van den Broeck An")]
    [InlineData(DisplayNamePattern.SurnameUpperGiven, "VAN DEN BROECK An")]
    [InlineData(DisplayNamePattern.SurnameCommaGiven, "Van den Broeck, An")]
    public void TheDisplayNameFollowsThePattern(DisplayNamePattern pattern, string expected)
        => Assert.Equal(expected, NamingSettings.Display(" An ", "Van den Broeck", pattern));

    private static InventoryData WithNames(Func<string, string, string> upn, Func<string, string, string> display)
    {
        var files = TestData.Files();
        var names = new[] { ("an.peeters", "An", "Peeters"), ("bo.janssens", "Bo", "Janssens"), ("cas.vos", "Cas", "Vos") };
        foreach (var (sam, given, surname) in names)
        {
            var row = files[InventoryLoader.AdUsers].Single(r => r["SamAccountName"].Equals(sam, StringComparison.OrdinalIgnoreCase));
            row["GivenName"] = given;
            row["Surname"] = surname;
            row["UserPrincipalName"] = upn(given, surname) + "@test.be";
            row["DisplayName"] = display(given, surname);
        }

        return InventoryLoader.Build(files);
    }

    [Fact]
    public void TheNamingIsDetectedAndSuggested()
    {
        var d = WithNames((g, s) => (g[..1] + s).ToLowerInvariant(), (g, s) => $"{s.ToUpperInvariant()} {g}");
        var found = NamingSettings.Detect(d)!.Value;
        Assert.Equal((LocalPartPattern.InitialSurname, DisplayNamePattern.SurnameUpperGiven), (found.Local, found.Display));

        var c = TestData.Config();
        var s = OrganisationSuggestions.For(d, c);
        var local = Assert.Single(s, x => x.Kind == SuggestionKind.LocalPart);
        Assert.Contains("apeeters@", local.Explanation, StringComparison.Ordinal);
        Assert.Single(s, x => x.Kind == SuggestionKind.DisplayName);
        OrganisationSuggestions.Apply(c, s.Where(x => x.Kind is SuggestionKind.LocalPart or SuggestionKind.DisplayName));
        Assert.Equal((LocalPartPattern.InitialSurname, DisplayNamePattern.SurnameUpperGiven), (c.Naming.LocalPart, c.Naming.DisplayName));

        // A new user follows it
        var v = PlanBuilder.NewUser(d, c, new NewUserRequest { Population = "Gemeente", GivenName = "Dirk", Surname = "De Smet", Department = "cultuur" }, "Geheim!123");
        Assert.Equal("ddesmet@test.be", v.Upn);
        Assert.Equal("DE SMET Dirk", v.ViewName);
    }

    [Fact]
    public void AMixedOrganisationGetsNoNamingSuggestion()
    {
        var i = 0;
        var d = WithNames((g, s) => i++ % 2 == 0 ? $"{g}.{s}".ToLowerInvariant() : (s + g[..1]).ToLowerInvariant(), (g, s) => $"{g} {s}");
        Assert.DoesNotContain(OrganisationSuggestions.For(d, TestData.Config()), x => x.Kind == SuggestionKind.LocalPart);
    }

    [Fact]
    public void CompanyOusAndLicencesAreSuggested()
    {
        var files = TestData.Files();
        foreach (var row in files[InventoryLoader.AdUsers])
        {
            row["Company"] = "Gemeente Voorbeeld";
        }

        var d = InventoryLoader.Build(files);
        var c = TestData.Config();
        c.Populations.Clear();
        c.Offboarding = null;
        c.GroupsOu = null;
        c.Licences.Clear();

        var s = OrganisationSuggestions.For(d, c);
        Assert.Equal("Gemeente Voorbeeld", Assert.Single(s, x => x.Kind == SuggestionKind.Company).Value);
        var ou = Assert.Single(s, x => x.Kind == SuggestionKind.NewUsersOu);
        Assert.Contains("test.be", ou.Explanation, StringComparison.Ordinal);
        Assert.Single(s, x => x.Kind == SuggestionKind.GroupsOu);
        Assert.Contains(s, x => x.Kind == SuggestionKind.LicenceName && x.Value == "SPB");

        OrganisationSuggestions.Apply(c, s);
        Assert.Equal("Gemeente Voorbeeld", c.Company);
        Assert.Equal("test.be", Assert.Single(c.Populations).UpnDomain);
        Assert.Equal("Microsoft 365 Business Premium", c.Licences.Single(l => l.Sku == "SPB").Name);
        Assert.Equal("Office 365 F3", c.Licences.Single(l => l.Sku == "DESKLESSPACK").Name);
        Assert.NotNull(c.GroupsOu);
        Assert.DoesNotContain(OrganisationSuggestions.For(d, c), x => x.Kind is SuggestionKind.Company or SuggestionKind.NewUsersOu or SuggestionKind.GroupsOu or SuggestionKind.LicenceName);
        Assert.Equal("FOO BAR", LicenceNames.For("FOO_BAR"));
    }

    [Fact]
    public void ATemplateOnlyAddsWhatIsMissing()
    {
        var c = TestData.Config();
        var school = OrganisationTemplates.All.Single(t => t.Key == "school");
        var added = OrganisationTemplates.Apply(c, school);
        Assert.Contains("afdeling Directie", added);
        Assert.Contains("voorvoegsel KLAS_", added);
        Assert.Contains(c.Divisions, x => x.Name == "Vrije tijd"); // stays
        Assert.Empty(OrganisationTemplates.Apply(c, school));
        Assert.Equal(3, OrganisationTemplates.All.Count);
    }

    [Fact]
    public void SettingsGoFromOneTenantToAnother()
    {
        var c = TestData.Config();
        c.Naming.LocalPart = LocalPartPattern.SurnameDotGiven;
        var json = ConfigStorage.To(c);
        var back = ConfigStorage.Parse("﻿" + json);
        Assert.Equal(LocalPartPattern.SurnameDotGiven, back.Naming.LocalPart);
        Assert.Contains("\"SurnameDotGiven\"", json, StringComparison.Ordinal);
        Assert.Equal(c.Divisions.Select(x => x.Name), back.Divisions.Select(x => x.Name));

        var target = new OrganisationConfig { AdDomain = "andere.local" };
        ConfigStorage.CopyInto(back, target);
        Assert.Equal(c.Company, target.Company);
        Assert.Throws<System.Text.Json.JsonException>(() => ConfigStorage.Parse("geen json"));
    }
}
