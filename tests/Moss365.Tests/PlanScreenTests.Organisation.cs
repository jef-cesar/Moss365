using Bunit;
using Moss365.Core.Configuration;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R9: on the organisation page a template, export and import, and the naming.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheOrganisationPageAppliesATemplateAndMovesSettings()
    {
        var cut = RenderComponent<Organisation>();
        Assert.Contains("Voorstellen uit de gegevens", cut.Markup, StringComparison.Ordinal);

        // Naming
        cut.Find("select[aria-label='Accountnaam']").Change(nameof(LocalPartPattern.InitialDotSurname));
        Assert.Equal(LocalPartPattern.InitialDotSurname, State.Config.Naming.LocalPart);

        // Template: only adds
        cut.Find("select[aria-label='Sjabloon']").Change("school");
        cut.FindAll("button").First(b => b.TextContent == "Toepassen").Click();
        Assert.Contains("afdeling Directie", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(State.Config.Divisions, d => d.Name == "Vrije tijd");
        cut.FindAll("button").First(b => b.TextContent == "Bewaren").Click();

        // Export, then import into the same tenant: the AD domain and the emergency accounts stay
        cut.FindAll("button").First(b => b.TextContent == "Exporteren").Click();
        var file = Assert.Single(Directory.GetFiles(State.ExportFolder, "Organisatie-*.json"));
        var exported = ConfigStorage.Parse(File.ReadAllText(file));
        Assert.Equal(LocalPartPattern.InitialDotSurname, exported.Naming.LocalPart);

        exported.Company = "Ander bedrijf";
        exported.AdDomain = "ander.local";
        exported.EmergencyAccounts = new() { "nood@fabrikam.com" };
        State.Config.EmergencyAccounts = new() { "breakglass@test.onmicrosoft.com" };
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText(ConfigStorage.To(exported), "organisatie.json"));
        cut.WaitForAssertion(() => Assert.Contains("Geïmporteerd uit organisatie.json", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("Ander bedrijf", State.Config.Company);
        Assert.Equal("TEST.INTERNAL", State.Config.AdDomain);
        Assert.Equal(new[] { "breakglass@test.onmicrosoft.com" }, State.Config.EmergencyAccounts);

        // Something that is not a settings file
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText("niet goed", "x.json"));
        cut.WaitForAssertion(() => Assert.Contains("Dit is geen bestand met instellingen", cut.Markup, StringComparison.Ordinal));
    }
}
