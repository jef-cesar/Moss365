using Bunit;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using CheckStatus = Moss365.Core.Security.CheckStatus;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16 on Controles: the advice for AD as plans; guided per account, krbtgt with the library script.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void AdAdviceIsGuidedPerAccountAndAdIsReadOncePerRound()
    {
        EntraFilled();
        _factory.AdSecurity = AdHardeningTests.Reading();
        // The library has the krbtgt script (Update library puts it there)
        var folder = Path.Combine(_base, "scripts-ingebouwd", "Security");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(ScriptLibraryRepoTests.Scripts, "Security", "Reset-KrbtgtPassword.ps1"), Path.Combine(folder, "Reset-KrbtgtPassword.ps1"), true);

        var cut = RenderComponent<SecurityChecksPage>();
        cut.FindAll("button").First(b => b.TextContent == "Alles nakijken").Click();
        cut.WaitForAssertion(() => Assert.Equal(State.SecurityCheckList().Count, State.SecurityCheckResults.Count), TimeSpan.FromSeconds(5));
        Assert.Single(_factory.WasRead, w => w == "ad-security"); // two checks, one read
        Assert.Equal(CheckStatus.Problem, State.SecurityCheckResults["ad-groepen"].Status);

        AngleSharp.Dom.IElement CheckRow(string title) => cut.FindAll("tr").First(tr => tr.TextContent.Contains(title, StringComparison.Ordinal) && tr.QuerySelector("button[aria-label='Uitleg']") is not null);

        // krbtgt: the script, with its preview, instead of a plan
        CheckRow("AD-beveiliging").QuerySelectorAll("button").First(b => b.TextContent.StartsWith("Details", StringComparison.Ordinal)).Click();
        Assert.Equal("scripts/starten/Reset-KrbtgtPassword", cut.FindAll("a").First(a => a.TextContent == "Met het script").GetAttribute("href"));

        // Protecting an administrator: guided, one account, with what changes for it
        CheckRow("Bevoorrechte AD-groepen").QuerySelectorAll("button").First(b => b.TextContent.StartsWith("Details", StringComparison.Ordinal)).Click();
        cut.Find("button[aria-label='Begeleid oplossen voor Administrator']").Click();
        Assert.Contains("Administrator toevoegen aan Protected Users", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("geen NTLM", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("section.kb-plan button").First(b => b.TextContent.StartsWith("Uitvoeren (", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Ja, uitvoeren", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _factory.Executed.Count), TimeSpan.FromSeconds(5));
        Assert.Equal("CN=Protected Users,CN=Users,DC=contoso,DC=local", _factory.Executed.Single(c => c.ChangeAction == ChangeAction.AdAddGroupMember).P("groepDn"));
        Assert.Equal("NotDelegated", _factory.Executed.Single(c => c.ChangeAction == ChangeAction.AdSetAccountFlag).P("vlag"));
    }

    [Fact]
    public void ReadersAndTheAuditorSeeAdviceButCarryOutNothing()
    {
        EntraFilled();
        _factory.AdSecurity = AdHardeningTests.Reading();
        State.Config.ProfilesRequired = true;
        _factory.AppRoles = new[] { "Moss365.Auditor" };
        var cut = RenderComponent<SecurityChecksPage>();
        cut.FindAll("button").First(b => b.TextContent == "Alles nakijken").Click();
        cut.WaitForAssertion(() => Assert.Equal(State.SecurityCheckList().Count, State.SecurityCheckResults.Count), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.StartsWith("Oplossen als plan", StringComparison.Ordinal) || b.TextContent == "Begeleid");
        Assert.NotEmpty(State.SecurityCheckResults["ad-groepen"].Rows);
    }
}
