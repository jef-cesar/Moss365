using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Devices;
using Microsoft.Extensions.DependencyInjection;
using Moss365.Core.Permissions;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R17 (U17): actions on Intune devices as plans, with safeguards in the planner and in the engine.</summary>
public sealed partial class PlanScreenTests
{
    private static IntuneDevice Laptop(string ownership = "company", string os = "Windows")
        => new("d1", "LAPTOP-1", "an.peeters@test.be", os, "10.0.26100", "compliant", DateTimeOffset.Now, ownership);

    [Fact]
    public void DeviceActionsHaveALevelAndSafeguards()
    {
        var sync = Assert.Single(DevicePlans.Action(Laptop(), "sync").Plan);
        Assert.Equal((TargetSystem.Graph, ChangeAction.IntuneDeviceAction, "d1"), (sync.TargetSystem, sync.ChangeAction, sync.Target));
        Assert.Equal(DeviceActionLevel.Safe, DevicePlans.LevelOf(sync));
        Assert.Null(sync.Warning);

        // Windows only, and unknown actions
        Assert.Contains("alleen voor Windows", Assert.Single(DevicePlans.Action(Laptop(os: "iOS"), "rotateLaps").Errors), StringComparison.Ordinal);
        Assert.NotEmpty(DevicePlans.Action(Laptop(), "nonsense").Errors);

        // A rename needs a valid Windows name, and keeps the old one for undo
        Assert.NotEmpty(DevicePlans.Action(Laptop(), "rename", "veel-te-lange-naam").Errors);
        Assert.NotEmpty(DevicePlans.Action(Laptop(), "rename", "12345").Errors);
        Assert.NotEmpty(DevicePlans.Action(Laptop(), "rename", "LAP TOP").Errors);
        var rename = Assert.Single(DevicePlans.Action(Laptop(), "rename", " LAPTOP-2 ").Plan);
        Assert.Equal(("LAPTOP-2", "LAPTOP-1"), (rename.Parameters["naam"], rename.Previous!["naam"]));
        Assert.Equal(DeviceActionLevel.Management, DevicePlans.LevelOf(rename));

        // Wiping: only with the name typed, never a personal device; retiring a personal device is fine
        Assert.NotEmpty(DevicePlans.Action(Laptop(), "wipe", typedName: "LAPTOP").Errors);
        var wipe = Assert.Single(DevicePlans.Action(Laptop(), "wipe", typedName: "laptop-1").Plan);
        Assert.Equal(DeviceActionLevel.Destructive, DevicePlans.LevelOf(wipe));
        Assert.Equal("Dit kan niet ongedaan gemaakt worden.", wipe.Warning);
        Assert.Contains(DevicePlans.Action(Laptop("personal"), "wipe", typedName: "LAPTOP-1").Errors, e => e.Contains("persoonlijk toestel", StringComparison.Ordinal));
        Assert.False(DevicePlans.Fits(Laptop("personal"), DevicePlans.Info("wipe")!));
        Assert.False(DevicePlans.Fits(Laptop(os: "iOS"), DevicePlans.Info("freshStart")!));
        Assert.Empty(DevicePlans.Action(Laptop("personal"), "retire").Errors);

        // The engine refuses a personal device, also when the plan was made by hand
        var forged = new Change
        {
            TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.IntuneDeviceAction, Target = "d2", Description = "x",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["actie"] = "wipe", ["eigendom"] = "personal" },
        };
        Assert.NotNull(DeviceSafety.Refusal(forged));
        Assert.Null(DeviceSafety.Refusal(wipe));
        Assert.Null(DeviceSafety.Refusal(sync));

        // ... and more than one device to wipe in one plan (not counting what is not ticked)
        var other = Assert.Single(DevicePlans.Action(Laptop() with { Id = "d9", Name = "PC-9" }, "wipe", typedName: "PC-9").Plan);
        Assert.NotNull(DeviceSafety.PlanRefusal(new[] { wipe, other }));
        Assert.Null(DeviceSafety.PlanRefusal(new[] { wipe, sync }));
        other.Selected = false;
        Assert.Null(DeviceSafety.PlanRefusal(new[] { wipe, other }));
    }

    [Fact]
    public void TheHelpdeskMayDoSafeDeviceActionsOnly()
    {
        var sync = Assert.Single(DevicePlans.Action(Laptop(), "sync").Plan);
        var retire = Assert.Single(DevicePlans.Action(Laptop(), "retire").Plan);
        var wipe = Assert.Single(DevicePlans.Action(Laptop(), "wipe", typedName: "LAPTOP-1").Plan);

        Assert.Null(PermissionTable.Refusal(Role.Helpdesk, sync));
        Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, retire));
        Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, wipe));
        Assert.NotNull(PermissionTable.Refusal(Role.Reader, sync));
        Assert.Null(PermissionTable.Refusal(Role.Administrator, wipe));
    }

    [Fact]
    public void ANewDeviceNameCanBeUndoneOtherActionsCannot()
    {
        var rename = Assert.Single(DevicePlans.Action(Laptop(), "rename", "LAPTOP-2").Plan);
        var entry = new LogEntry(DateTimeOffset.Now, "beheerder@test.be", "c", "Graph", nameof(ChangeAction.IntuneDeviceAction), rename.Target, rename.TargetName,
            rename.Description, rename.Parameters, nameof(ResultStatus.Succeeded), string.Empty, Previous: rename.Previous);
        Assert.Null(Undo.WhyNot(entry));
        var back = Assert.Single(Undo.Plan(new[] { entry }).Plan);
        Assert.Equal((TargetSystem.Graph, "rename", "LAPTOP-1"), (back.TargetSystem, back.P("actie"), back.P("naam")));
        Assert.Equal("LAPTOP-2", back.Previous!["naam"]);

        var sync = Assert.Single(DevicePlans.Action(Laptop(), "sync").Plan);
        Assert.Contains("Alleen een nieuwe naam", Undo.WhyNot(entry with { Parameters = sync.Parameters, Previous = null }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEngineWipesAtMostOneDevicePerPlan()
    {
        EntraFilled();
        var one = Assert.Single(DevicePlans.Action(Laptop(), "wipe", typedName: "LAPTOP-1").Plan);
        var two = Assert.Single(DevicePlans.Action(Laptop() with { Id = "d3", Name = "LAPTOP-OUD" }, "wipe", typedName: "LAPTOP-OUD").Plan);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => State.ExecuteAsync(new[] { one, two }, "test", null));
        Assert.Equal("Een plan wist hoogstens één toestel.", ex.Message);
        Assert.Empty(_factory.Executed);

        // A personal device is refused by the engine; the safe action in the same plan runs
        var forged = new Change
        {
            TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.IntuneDeviceAction, Target = "d2", Description = "x",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["actie"] = "wipe", ["eigendom"] = "personal" },
        };
        var sync = Assert.Single(DevicePlans.Action(Laptop(), "sync").Plan);
        var results = await State.ExecuteAsync(new[] { forged, sync }, "test", null);
        Assert.Equal(ResultStatus.Failed, results.Single(r => r.ChangeId == forged.Id).Status);
        Assert.Equal(sync.Id, Assert.Single(_factory.Executed).Id);
    }

    [Fact]
    public void TheDevicePanelMakesPlansPerLevel()
    {
        EntraFilled();
        var cut = RenderComponent<DevicesPage>();
        cut.FindAll("button").First(b => b.TextContent == "Toestellen lezen").Click();
        cut.WaitForAssertion(() => Assert.Contains("Eén lijst over AD, Entra en Intune", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        cut.Find("#een-lijst button[aria-label='LAPTOP-1 openen']").Click();
        Assert.Contains("Latitude 5440", cut.Find("#toestel").TextContent, StringComparison.Ordinal);
        Assert.Contains("SN-0001", cut.Find("#toestel").TextContent, StringComparison.Ordinal);
        cut.FindAll("#toestel button").First(b => b.TextContent == "Synchroniseren").Click();
        Assert.Contains("LAPTOP-1 synchroniseren met Intune", cut.Markup, StringComparison.Ordinal);

        // A rename with an invalid name explains why
        cut.Find("#toestel input[aria-label='Nieuwe naam']").Input("NAAM MET SPATIE");
        cut.FindAll("#toestel button").First(b => b.TextContent == "Naam wijzigen").Click();
        Assert.Contains("hoogstens 15 tekens", cut.Find("#toestel").TextContent, StringComparison.Ordinal);

        // Wiping only once the name is typed, and only after confirming in the plan
        Assert.True(WipeButton().HasAttribute("disabled"));
        cut.Find("#toestel input[aria-label='Typ de naam van het toestel']").Input("LAPTOP-1");
        Assert.False(WipeButton().HasAttribute("disabled"));
        WipeButton().Click();
        Assert.Contains("LAPTOP-1 wissen: terug naar de fabrieksinstellingen", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Dit kan niet ongedaan gemaakt worden.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(_factory.Executed);

        // A personal phone: only retire, never wipe; Windows-only actions are not possible
        cut.Find("#een-lijst button[aria-label='GSM-BO openen']").Click();
        Assert.Contains("Een persoonlijk toestel wordt nooit gewist", cut.Find("#toestel").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("#toestel button"), b => b.TextContent.StartsWith("Wissen", StringComparison.Ordinal));
        Assert.True(cut.FindAll("#toestel button").First(b => b.TextContent == "Nieuw LAPS-wachtwoord").HasAttribute("disabled"));
        Assert.Contains(cut.FindAll("#toestel button"), b => b.TextContent.StartsWith("Buiten gebruik stellen", StringComparison.Ordinal));

        AngleSharp.Dom.IElement WipeButton() => cut.FindAll("#toestel button").First(b => b.TextContent.StartsWith("Wissen", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHelpdeskSeesOnlySafeDeviceActions()
    {
        EntraFilled();
        State.Config.ProfilesRequired = true;
        _factory.AppRoles = new[] { "Moss365.Helpdesk" };
        var cut = RenderComponent<DevicesPage>();
        cut.FindAll("button").First(b => b.TextContent == "Toestellen lezen").Click();
        cut.WaitForAssertion(() => Assert.Contains("Eén lijst over AD, Entra en Intune", cut.Markup, StringComparison.Ordinal), TimeSpan.FromSeconds(5));

        cut.Find("#een-lijst button[aria-label='LAPTOP-1 openen']").Click();
        Assert.Contains(cut.FindAll("#toestel button"), b => b.TextContent == "Herstarten");
        Assert.DoesNotContain(cut.FindAll("#toestel button"), b => b.TextContent is "Naam wijzigen" || b.TextContent.StartsWith("Wissen", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewPrimaryUserIsAPlanThatCanBeUndone()
    {
        var an = State.Data.EntraUsers.First(u => u.UserPrincipalName == "an.peeters@test.be");
        var bo = State.Data.EntraUsers.First(u => u.UserPrincipalName == "bo.janssens@test.be");
        var change = Assert.Single(DevicePlans.PrimaryUser(Laptop(), bo, an.Id).Plan);
        Assert.Equal(("primaryUser", bo.Id, an.Id), (change.P("actie"), change.P("gebruiker"), change.Previous!["gebruiker"]));
        Assert.Equal(DeviceActionLevel.Management, DevicePlans.LevelOf(change));
        Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, change));
        Assert.NotEmpty(DevicePlans.PrimaryUser(Laptop(), an, an.Id).Errors); // already the primary user
        Assert.NotEmpty(DevicePlans.Action(Laptop(), "primaryUser").Errors); // only with a person

        var entry = new LogEntry(DateTimeOffset.Now, "beheerder@test.be", "c", "Graph", nameof(ChangeAction.IntuneDeviceAction), change.Target, change.TargetName,
            change.Description, change.Parameters, nameof(ResultStatus.Succeeded), string.Empty, Previous: change.Previous);
        Assert.Null(Undo.WhyNot(entry));
        Assert.Equal(an.Id, Assert.Single(Undo.Plan(new[] { entry }).Plan).P("gebruiker"));
    }

    [Fact]
    public void OffboardingRetiresPersonalDevicesAndAsksToReturnCompanyDevices()
    {
        var bo = State.Data.Persons.First(p => p.Entra?.UserPrincipalName == "bo.janssens@test.be");
        var devices = new[]
        {
            Laptop("personal") with { Id = "p1", Name = "GSM-BO", Owner = "BO.JANSSENS@test.be" },
            Laptop() with { Id = "c1", Name = "LAPTOP-BO", Owner = "bo.janssens@test.be" },
            Laptop() with { Id = "x1", Name = "LAPTOP-AN" },
        };
        var (plan, notes) = LeaverDevices.Plan(devices, bo);
        var retire = Assert.Single(plan);
        Assert.Equal(("p1", "retire"), (retire.Target, retire.P("actie")));
        Assert.Contains("LAPTOP-BO", Assert.Single(notes), StringComparison.Ordinal);
        Assert.DoesNotContain(plan, c => DevicePlans.LevelOf(c) == DeviceActionLevel.Destructive); // never a wipe in offboarding

        // On the offboarding screen, once the devices were read
        EntraFilled();
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("uitstroom/" + Uri.EscapeDataString(bo.Key));
        var cut = RenderComponent<Offboarding>(p => p.Add(x => x.Key, bo.Key));
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Lees eerst de toestellen in", cut.Markup.Replace("lees eerst", "Lees eerst", StringComparison.Ordinal), StringComparison.Ordinal);
        State.ReadDevicesAsync().GetAwaiter().GetResult();
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("GSM-BO buiten gebruik stellen", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserPageShowsTheDevicesOfThePerson()
    {
        EntraFilled();
        var an = State.Data.Persons.First(p => p.Entra?.UserPrincipalName == "an.peeters@test.be");
        var before = RenderComponent<UserDevicesCard>(p => p.Add(c => c.Person, an));
        Assert.Contains("Lees eerst de toestellen in", before.Markup, StringComparison.Ordinal);

        var devices = RenderComponent<DevicesPage>();
        devices.FindAll("button").First(b => b.TextContent == "Toestellen lezen").Click();
        devices.WaitForAssertion(() => Assert.NotNull(State.IntuneDevices), TimeSpan.FromSeconds(5));

        var card = RenderComponent<UserDevicesCard>(p => p.Add(c => c.Person, an));
        var row = Assert.Single(card.FindAll("tbody tr"));
        Assert.Contains("LAPTOP-1", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("organisatie", row.TextContent, StringComparison.Ordinal);

        var bo = State.Data.Persons.First(p => p.Entra?.UserPrincipalName == "bo.janssens@test.be");
        Assert.Contains("persoonlijk", Assert.Single(RenderComponent<UserDevicesCard>(p => p.Add(c => c.Person, bo)).FindAll("tbody tr")).TextContent, StringComparison.Ordinal);
        var cas = State.Data.Persons.First(p => p.Entra?.UserPrincipalName == "cas.vos@test.be");
        Assert.Contains("Geen toestellen in Intune", RenderComponent<UserDevicesCard>(p => p.Add(c => c.Person, cas)).Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void DevicesAreKeptInTheSnapshotsAndShownOnADate()
    {
        var now = DateTimeOffset.Now;
        var tables = DeviceSnapshot.Tables(
            new[] { new AdComputer("PC-1", "CN=PC-1,OU=Werkposten,DC=test,DC=internal", "Windows 11 Pro", "10.0 (26100)", now.AddDays(-200), true, now.AddYears(-2)) },
            new[] { new EntraDevice("o1", "dev-1", "PC-1", "Windows", "10.0.26100", true, now.AddDays(-1), "ServerAd") },
            new[] { Laptop() with { Encrypted = true, Serial = "SN-1" } }, now);
        Assert.Equal("True", tables[DeviceSnapshot.AdTable][0]["Stale"]);
        Assert.Equal("False", tables[DeviceSnapshot.EntraTable][0]["Stale"]);
        Assert.DoesNotContain(tables.Values.SelectMany(t => t).SelectMany(r => r.Values), v => v.Contains(now.ToString("HH:mm:ss"), StringComparison.Ordinal)); // no moment of last seen
        var (ad, entra, intune) = DeviceSnapshot.Read(tables, now);
        Assert.Null(Assert.Single(ad!).LastLogon); // not seen for 90 days
        Assert.Equal(now, Assert.Single(entra!).LastSignIn);
        Assert.Equal((true, "SN-1", "company"), (Assert.Single(intune!).Encrypted, intune![0].Serial, intune[0].Ownership));

        // Reading on Toestellen makes a snapshot; reading again without changes stores nothing new
        EntraFilled();
        State.ReadDevicesAsync().GetAwaiter().GetResult();
        var first = State.Database.SnapshotList().Last();
        Assert.Contains(DeviceSnapshot.IntuneTable, first.Tables);
        State.ReadDevicesAsync().GetAwaiter().GetResult();
        var second = State.Database.SnapshotList().Last();
        Assert.NotEqual(first.Id, second.Id);
        Assert.DoesNotContain(State.Database.Differences(first.Id, second.Id), d => d.Table.StartsWith("toestellen-", StringComparison.Ordinal));
        Assert.Contains("overzicht:devices-noncompliant", State.Database.ReadKpiSnapshot(DateOnly.FromDateTime(DateTime.Today)).Keys);

        // On the date of the first read the devices come from the snapshot, read only
        Assert.Null(State.ShowAsOf(first.Time));
        Assert.Contains(State.IntuneDevices!, d => d.Name == "GSM-BO");
        Assert.Contains(State.DeviceList, r => r.Name == "LAPTOP-1");
        var page = RenderComponent<DevicesPage>();
        Assert.True(page.FindAll("button").First(b => b.TextContent == "Toestellen lezen").HasAttribute("disabled"));
    }
}
