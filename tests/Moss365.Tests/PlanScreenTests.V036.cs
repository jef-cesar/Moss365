using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Devices;
using Moss365.Core.Inventory;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.36 (R13): devices, Teams and SharePoint.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void StaleComputersSystemsAndADisablePlan()
    {
        var now = DateTimeOffset.Now;
        var computers = new[]
        {
            new AdComputer("A", "CN=A,OU=Pc,OU=Gemeente,DC=test,DC=internal", "Windows 11 Pro", "10.0", now.AddDays(-1), true, null),
            new AdComputer("B", "CN=B,OU=Pc,DC=test,DC=internal", "Windows 7 Professional", "6.1", now.AddDays(-120), true, null),
            new AdComputer("C", "CN=C,CN=Computers,DC=test,DC=internal", "", "", null, true, null),
            new AdComputer("D", "CN=D,OU=Pc,DC=test,DC=internal", "Windows 11 Pro", "10.0", now.AddDays(-500), false, null),
        };
        Assert.Equal("Gemeente/Pc", computers[0].OuPath);
        Assert.Equal(string.Empty, computers[2].OuPath);
        Assert.Equal(new[] { "C", "B" }, DeviceAnalysis.StaleComputers(computers, now).Select(c => c.Name));
        Assert.True(DeviceAnalysis.Outdated("Windows 7 Professional"));
        Assert.False(DeviceAnalysis.Outdated("Windows 11 Pro"));
        Assert.Contains(DeviceAnalysis.Systems(computers), s => s.System == "Windows 11 Pro 10.0" && s.Count == 1);

        var plan = DeviceAnalysis.DisableComputers(computers, DateOnly.FromDateTime(DateTime.Today)).Plan;
        Assert.Equal(3, plan.Count); // the disabled one is left out
        Assert.All(plan, c => Assert.Equal(ChangeAction.AdDisableAccount, c.ChangeAction));
        Assert.DoesNotContain(plan, c => c.ChangeAction.ToString().Contains("Delete", StringComparison.Ordinal));
    }

    [Fact]
    public void IntuneDevicesNotCompliantOrNotSeen()
    {
        var now = DateTimeOffset.Now;
        var devices = new[]
        {
            new IntuneDevice("1", "A", "", "Windows", "10", "compliant", now, "company"),
            new IntuneDevice("2", "B", "", "iOS", "16", "noncompliant", now, "personal"),
            new IntuneDevice("3", "C", "", "Android", "12", "inGracePeriod", now.AddDays(-45), "personal"),
        };
        Assert.Equal(new[] { "B", "C" }, DeviceAnalysis.NotCompliant(devices).Select(d => d.Name));
        Assert.Equal(new[] { "C" }, DeviceAnalysis.NotSeen(devices, now).Select(d => d.Name));
    }

    [Fact]
    public void GroupsWithoutOwnerWithGuestsOrUnused()
    {
        var d = State.Data;
        var membership = d.CloudMembership.ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
        membership["e-gast"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "g-afd" };
        d = d with { CloudMembership = membership };

        // Without a reading: only the guests (owners and activity are unknown)
        var before = DeviceAnalysis.Groups(d, null, DateTimeOffset.Now);
        Assert.Contains(before, g => g.Id == "g-afd" && g.Guests == 1 && g.Owners == -1);
        Assert.DoesNotContain(before, g => g.Id == "g-sm"); // only Microsoft 365 groups

        var reading = new CollaborationReading(
            new[] { new GroupActivity("g-team", "DNST_CULTUUR", true, 0, DateTimeOffset.Now.AddDays(-5)), new GroupActivity("g-afd", "AFD_VRIJETIJD", false, 2, null) },
            Array.Empty<SiteUsage>(), "disabled", null);
        var after = DeviceAnalysis.Groups(d, reading, DateTimeOffset.Now);
        var team = Assert.Single(after, g => g.Id == "g-team");
        Assert.True(team.IsTeam);
        Assert.Contains("geen eigenaar", team.Points);
        var afd = Assert.Single(after, g => g.Id == "g-afd");
        Assert.Contains(afd.Points, p => p.StartsWith("geen activiteit", StringComparison.Ordinal));
        Assert.Contains(afd.Points, p => p.Contains("gast", StringComparison.Ordinal));
    }

    [Fact]
    public void SharingSettingAndUnusedSites()
    {
        Assert.True(DeviceAnalysis.Sharing("externalUserAndGuestSharing").Attention);
        Assert.False(DeviceAnalysis.Sharing("disabled").Attention);
        var reading = new CollaborationReading(Array.Empty<GroupActivity>(), new[]
        {
            new SiteUsage("https://contoso.sharepoint.com/sites/a", "", 10, 1, DateTimeOffset.Now.AddDays(-100)),
            new SiteUsage("https://contoso.sharepoint.com/sites/b", "", 10, 1, DateTimeOffset.Now),
        }, null, null);
        Assert.Equal("https://contoso.sharepoint.com/sites/a", Assert.Single(DeviceAnalysis.UnusedSites(reading, DateTimeOffset.Now)).Url);
        Assert.Equal("3.0 GB", DeviceAnalysis.Size(3L << 30).Replace(',', '.'));
    }

    [Fact]
    public void TheDevicesScreenReadsAndPlansDisabling()
    {
        EntraFilled();
        var cut = RenderComponent<DevicesPage>();
        cut.FindAll("button").First(b => b.TextContent == "Toestellen lezen").Click();
        cut.WaitForAssertion(() => Assert.Contains("PC-OUD", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Contains("niet meer ondersteund", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("GSM-BO", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PC-NIEUW</td><td>Windows 11 Pro</td><td class=\"kb-zacht\">Werkposten</td><td>", cut.Markup, StringComparison.Ordinal);
        cut.Find("input[aria-label='PC-OUD']").Change(true);
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Computers uitschakelen", StringComparison.Ordinal)).Click();
        Assert.Contains("Computer PC-OUD uitschakelen", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCollaborationScreenShowsOwnersAndSharing()
    {
        EntraFilled();
        var cut = RenderComponent<CollaborationPage>();
        cut.FindAll("button").First(b => b.TextContent == "Teams en SharePoint lezen").Click();
        cut.WaitForAssertion(() => Assert.Contains("geen eigenaar", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Contains("ook met een link zonder aanmelding", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("sites/oud", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewPagesFitTheTenant()
    {
        Assert.False(Access.FitsTenant("samenwerking", true, false));
        Assert.True(Access.FitsTenant("toestellen", true, false));
        Assert.True(Access.FitsTenant("toestellen", false, true));
        Assert.Contains(MenuModel.AllPages, p => p.Href == "toestellen");
        Assert.Contains(MenuModel.AllPages, p => p.Href == "samenwerking");
    }
}
