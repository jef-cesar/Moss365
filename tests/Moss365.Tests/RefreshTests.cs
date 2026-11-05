using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.Connections.Ad;
using Moss365.Connections.Check;
using Moss365.Connections.Exchange;
using Xunit;

namespace Moss365.Tests;

/// <summary>Refresh objects, save the inventory, guests, statistics and the check of the PC.</summary>
public sealed class RefreshTests : IDisposable
{
    private readonly InventoryData _d = TestData.AppEnvironment();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-vernieuw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static string WifiDn => TestData.Dn("gg_wifiIntern", TestData.Groups);

    // ---------------------------------------------------------------- ObjectVernieuwer

    [Fact]
    public void AdUserAfterOffboarding()
    {
        var old = _d.AdUsers.Single(u => u.SamAccountName == "an.peeters");
        var dn = "CN=an.peeters,OU=disabled_users,OU=ARCHIVE,DC=test,DC=internal";
        var newValue = old with { Enabled = false, DistinguishedName = dn, OuPath = DnHelp.OuPathOfObject(dn), MemberOr = Array.Empty<string>() };

        var d = ObjectRefresher.WithAdUser(_d, "an.peeters", newValue);

        var an = d.SearchPerson("ad:an.peeters")!;
        Assert.False(an.Enabled);
        Assert.Equal("ARCHIVE/disabled_users", an.Ad!.OuPath);
        Assert.NotNull(an.Entra);   // the link with Entra stays
        Assert.DoesNotContain(old.DistinguishedName, d.AdGroupsByDn[WifiDn].Members);
        Assert.Contains(TestData.Dn("bo.janssens", TestData.Municipality), d.AdGroupsByDn[WifiDn].Members);
        Assert.NotSame(_d.Index, d.Index);
        Assert.Equal(_d.Mailboxes.Count, d.Mailboxes.Count);
    }

    [Fact]
    public void AdGroupUpdatesMemberOf()
    {
        var groupValue = _d.AdGroupsByDn[WifiDn];
        var cas = TestData.Dn("cas.vos", TestData.SocialWelfare);
        var d = ObjectRefresher.WithAdGroup(_d, "gg_wifiIntern", groupValue with { Members = new[] { cas } });

        Assert.Contains(WifiDn, d.SearchPerson("ad:cas.vos")!.Ad!.MemberOr);
        Assert.DoesNotContain(WifiDn, d.SearchPerson("ad:an.peeters")!.Ad!.MemberOr);

        var route = ObjectRefresher.WithAdGroup(_d, "gg_wifiIntern", null);
        Assert.False(route.AdGroupsByDn.ContainsKey(WifiDn));
    }

    [Fact]
    public void EntraAndCloudGroups()
    {
        var bo = _d.EntraUsers.Single(e => e.Id == "e-bo");
        var d = ObjectRefresher.WithEntraUser(_d, "e-bo", new GraphUserReading(bo with { AccountEnabled = false }, new[] { "g-sm" }));
        Assert.False(d.EntraUsers.Single(e => e.Id == "e-bo").AccountEnabled);
        Assert.Equal(new[] { "g-sm" }, d.CloudMembership["e-bo"]);

        var team = _d.CloudGroups["g-team"];
        var d2 = ObjectRefresher.WithCloudGroup(_d, "g-team", new GraphGroupReading(team, new[] { "e-cas" }));
        Assert.Contains("g-team", d2.CloudMembership["e-cas"]);
        Assert.DoesNotContain("g-team", d2.CloudMembership["e-an"]);
        Assert.Equal(1, d2.CloudGroups["g-team"].CountMembers);
    }

    [Fact]
    public void ExchangeObjectsReplaceAndRemove()
    {
        var info = _d.Mailboxes.Single(p => p.Smtp == "info@test.be");
        var o = new ExchangeObjects(Array.Empty<Room>(),
            new ExchangeData(Array.Empty<ExoGroup>(), new[] { info with { DisplayName = "Info (nieuw)" } }, new[] { new MailboxPermission("info@test.be", "FullAccess", "sg-sm-info-full@test.be") }),
            new[] { "boekhouding@test.be" });

        var d = ObjectRefresher.WithExchange(_d, o);

        Assert.Equal("Info (nieuw)", d.Mailboxes.Single(p => p.Smtp == "info@test.be").DisplayName);
        Assert.Single(d.MailboxPermissions, r => r.Mailbox == "info@test.be");
        Assert.DoesNotContain(d.Mailboxes, p => p.Smtp == "boekhouding@test.be");
        Assert.DoesNotContain(d.MailboxPermissions, r => r.Mailbox == "boekhouding@test.be");
        Assert.Equal(_d.Rooms.Count, d.Rooms.Count);
    }

    // ---------------------------------------------------------------- VernieuwDoelen

    [Fact]
    public void AfterOffboardingTheUserAndItsGroups()
    {
        var an = _d.SearchPerson("ad:an.peeters")!;
        var plan = PlanBuilder.Offboarding(_d, TestData.Config(), an, new OffboardingOptions { AdRemoveGroups = true });
        var results = plan.Where(w => w.Selected).Select(w => ChangeResult.Ok(w)).ToList();

        var targets = RefreshTargets.AfterPlan(_d, plan, results);

        Assert.Contains("an.peeters", targets.AdUsers);
        Assert.Contains("e-an", targets.EntraUsers);
        Assert.Contains("gg_wifiIntern", targets.AdGroups);
        Assert.Contains("user:ad:an.peeters", targets.Objects);

        // Not succeeded = do not refresh
        Assert.True(RefreshTargets.AfterPlan(_d, plan, Array.Empty<ChangeResult>()).Empty);
    }

    [Fact]
    public void NewObjectsAndGuests()
    {
        var newValue = new Change { TargetSystem = TargetSystem.ActiveDirectory, ChangeAction = ChangeAction.AdCreateUser, Target = "CN=x", Description = "x" };
        newValue.Parameters["sAMAccountName"] = "eva.desmet";
        var mailbox = new Change { TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeCreateSharedMailbox, Target = "jeugd@test.be", Description = "x" };
        var guest = new Change { TargetSystem = TargetSystem.Graph, ChangeAction = ChangeAction.GraphInviteGuest, Target = "x@fabrikam.com", Description = "x" };
        var id = Guid.NewGuid().ToString();

        var targets = RefreshTargets.AfterPlan(_d, new[] { newValue, mailbox, guest },
            new[] { ChangeResult.Ok(newValue), ChangeResult.Ok(mailbox), ChangeResult.Ok(guest, $"Uitgenodigd; id {id}") });

        Assert.Contains("eva.desmet", targets.AdUsers);
        Assert.Contains("jeugd@test.be", targets.ExchangeAddresses);
        Assert.Contains(id, targets.EntraUsers);
    }

    [Fact]
    public void ForAGroupAllSides()
    {
        var g = _d.Index.Group("cloud:g-sm")!;
        var targets = RefreshTargets.For(_d, new[] { g.Ref, _d.Index.Ref("rm-vz-a@test.be") });
        Assert.Contains("g-sm", targets.CloudGroups);
        Assert.Contains("sg-sm-info-full@test.be", targets.ExchangeAddresses);
        Assert.Contains("rm-vz-a@test.be", targets.ExchangeAddresses);
    }

    // ---------------------------------------------------------------- saving the inventory

    [Fact]
    public void SaveAndReadBackGivesTheSameData()
    {
        var all = InventoryComponent.Ad | InventoryComponent.Entra | InventoryComponent.Rooms | InventoryComponent.Mailboxes;
        var folder = InventoryWriter.Save(_d, _folder, all, new DateTime(2026, 10, 23, 10, 42, 0));
        Assert.EndsWith("Inventaris-20261023-1042-live", folder);

        var back = InventoryLoader.Load(InventoryLoader.SearchFolders(_folder));
        Assert.Equal(_d.AdUsers.Count, back.AdUsers.Count);
        Assert.Equal(_d.SearchPerson("ad:an.peeters")!.Ad!.MemberOr, back.SearchPerson("ad:an.peeters")!.Ad!.MemberOr);
        Assert.Equal(_d.AdGroupsByDn[WifiDn].Members, back.AdGroupsByDn[WifiDn].Members);
        Assert.True(back.AdGroupsByDn[WifiDn].MembersKnown);
        Assert.Equal(_d.EntraUsers.Count, back.EntraUsers.Count);
        Assert.Equal(_d.CloudMembership["e-an"].OrderBy(x => x), back.CloudMembership["e-an"].OrderBy(x => x));
        Assert.Equal(_d.CloudGroups["g-sm"], back.CloudGroups["g-sm"]);
        Assert.Equal(_d.Licences, back.Licences);
        Assert.Equal(_d.Rooms, back.Rooms);
        Assert.Equal(_d.MailboxPermissions.Count, back.MailboxPermissions.Count);
        Assert.Equal(_d.Mailboxes.Count, back.Mailboxes.Count);
        Assert.Equal(_d.ExoGroups.Single(g => g.Smtp == "rl-gh@test.be").Members, back.ExoGroups.Single(g => g.Smtp == "rl-gh@test.be").Members);
    }

    [Fact]
    public void OnlyTheLivePartsAreSaved()
    {
        var folder = InventoryWriter.Save(_d, _folder, InventoryComponent.Rooms, DateTime.Now);
        Assert.Equal(new[] { InventoryLoader.ExoRooms }, Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    // ---------------------------------------------------------------- guests

    [Fact]
    public void InviteAndRemoveGuest()
    {
        var u = GuestPlans.Invite(_d, "jan@example.net", "Jan (Leverancier)", message: true);
        var w = Assert.Single(u.Plan);
        Assert.Equal(ChangeAction.GraphInviteGuest, w.ChangeAction);
        Assert.Equal("True", w.P("bericht"));

        Assert.NotEmpty(GuestPlans.Invite(_d, "geen-adres", "x", true).Errors);
        Assert.Contains("bestaat al", Assert.Single(GuestPlans.Invite(_d, "an.peeters@test.be", "An", true).Errors));

        var guest = _d.Persons.Single(p => p.IsGuest);
        Assert.Equal(ChangeAction.GraphRemoveGuest, Assert.Single(GuestPlans.Delete(_d, new[] { guest }).Plan).ChangeAction);

        // An employee is never deleted
        var an = _d.SearchPerson("ad:an.peeters")!;
        var refusal = GuestPlans.Delete(_d, new[] { an });
        Assert.Empty(refusal.Plan);
        Assert.Contains("Uitstroom", Assert.Single(refusal.Errors));
    }

    // ---------------------------------------------------------------- statistics

    [Fact]
    public void StatisticsFromTheTestData()
    {
        var s = Statistic.Compute(_d, TestData.Config(), new DateTime(2026, 10, 23));
        Assert.Equal(1, s.Guests);
        Assert.True(s.Active > 0);
        Assert.Equal(s.Active + s.Disabled, s.Accounts);
        Assert.Equal(1, s.PrivilegedMembers);   // an.peeters in Domain Admins
        Assert.Contains(s.Licences, l => l.Name == "SPB" && Statistic.AlmostFull(l));
        Assert.Contains(s.Licences, l => l.Name == "DESKLESSPACK" && !Statistic.AlmostFull(l));
        Assert.Equal(2, s.SharedMailboxes);
        Assert.Equal(1, s.WithoutAccessGroup);
        Assert.Equal(1, s.RoomsWithDeviation);
        Assert.Equal(2, s.PermissionModel.Sum(r => r.Count));
    }

    // ---------------------------------------------------------------- check of the PC

    [Fact]
    public void ModulesAssess()
    {
        var rules = SystemCheck.Assess("PowerShell 7", "pwsh.exe", appUsedThis: true, new[]
        {
            new SystemCheck.ModuleVersion("ExchangeOnlineManagement", "3.0.0"),
            new SystemCheck.ModuleVersion("Microsoft.Graph.Authentication", "2.20.0"),
        }).ToList();

        var exo = rules.Single(r => r.Section == "ExchangeOnlineManagement");
        Assert.Equal(CheckStatus.Error, exo.Status);
        Assert.Equal("ExchangeOnlineManagement", exo.Module);
        Assert.Equal(CheckStatus.Good, rules.Single(r => r.Section == "Microsoft.Graph.Authentication").Status);
        Assert.Equal(CheckStatus.Warning, rules.Single(r => r.Section == "Microsoft.Graph.Users").Status);

        // RSAT cannot be installed with Install-Module: no button, but the command
        var rsat = rules.Single(r => r.Section.StartsWith("ActiveDirectory", StringComparison.Ordinal));
        Assert.Null(rsat.Module);
        Assert.Contains("Add-WindowsCapability", rsat.Solution);

        // In the second PowerShell a missing Exchange module is only a warning
        Assert.Equal(CheckStatus.Warning, SystemCheck.Assess("5.1", "ps.exe", false, Array.Empty<SystemCheck.ModuleVersion>())
            .Single(r => r.Section == "ExchangeOnlineManagement").Status);
    }

    [Fact]
    public async Task CheckWithFakePowerShellAndNetwork()
    {
        var starter = new JsonPowerShell("{\"PowerShell\":\"7.4.5\",\"Editie\":\"Core\",\"Modules\":[{\"Naam\":\"ExchangeOnlineManagement\",\"Versie\":\"3.7.2\"}],\"Installatie\":\"Gelukt\"}");
        var check = new SystemCheck(_ => starter, () => new[] { ("PowerShell 7", "pwsh.exe") },
            (host, port, _) => Task.FromResult(host != "outlook.office365.com" && port != 636), _folder);

        var rules = await check.CheckAsync(new ConnectionSettings { AdServer = "10.0.0.1" }, null, CancellationToken.None);

        Assert.Contains(rules, r => r.Section == "Versie" && r.Value == "7.4.5 (Core)");
        Assert.Equal(CheckStatus.Good, rules.Single(r => r.Section == "ExchangeOnlineManagement").Status);
        Assert.Equal("bereikbaar (poort 389)", rules.Single(r => r.Section.StartsWith("Domeincontroller", StringComparison.Ordinal)).Value);
        Assert.Equal(CheckStatus.Error, rules.Single(r => r.Section == "outlook.office365.com").Status);
        Assert.Equal(CheckStatus.Warning, rules.Single(r => r.Section == "App-registratie").Status);

        Assert.Equal("Gelukt", await check.InstallAsync("pwsh.exe", "Microsoft.Graph.Users", CancellationToken.None));
        Assert.Contains("-Installeer", starter.Arguments);
        Assert.StartsWith("Mislukt", await check.InstallAsync("andere.exe", "x", CancellationToken.None));
    }

    private sealed class JsonPowerShell(string json) : IPowerShellStarter
    {
        public List<string> Arguments { get; } = new();

        public Task<(int Code, string Errors)> StartAsync(string script, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel)
        {
            Arguments.AddRange(arguments);
            File.WriteAllText(arguments[arguments.ToList().IndexOf("-Uitvoer") + 1], json);
            return Task.FromResult((0, string.Empty));
        }
    }

    [Fact]
    public void LdapFilterValueIsEscaped()
    {
        Assert.Equal("jan\\2a\\28x\\29\\5c", AdLdapMapper.FilterValue("jan*(x)\\"));
        Assert.Equal("an.peeters", AdLdapMapper.FilterValue("an.peeters"));
    }
}
