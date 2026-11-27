using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Permissions;
using Moss365.Core.Security;
using CheckStatus = Moss365.Core.Security.CheckStatus;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16: the advice for Active Directory carried out as plans; unconstrained delegation and Protected Users guided.</summary>
public class AdHardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(1));
    private const string Domain = "DC=contoso,DC=local";

    public static AdSecurityReading Reading() => new(
        new[] { "svc.sql", "svc.web" }, new[] { "SRV-APP$" }, Array.Empty<string>(), Array.Empty<string>(), Now.AddDays(-400), new[] { "oud.beheer" },
        Array.Empty<(string, string)>())
    {
        DomainDn = Domain,
        NoPreauth = new[] { new AdObject("scanner", "CN=scanner,OU=Service," + Domain) },
        SpnAccounts = new[]
        {
            new SpnAccount("svc.sql", "CN=svc.sql,OU=Service," + Domain, null, new[] { "MSSQLSvc/sql01:1433" }),
            new SpnAccount("svc.web", "CN=svc.web,OU=Service," + Domain, 0x18, new[] { "HTTP/web01" }),
        },
        Delegation = new[] { new DelegationAccount("SRV-APP$", "CN=SRV-APP,OU=Servers," + Domain, true, new[] { "HTTP/app01", "HOST/app01" }) },
        Orphans = new[] { new AdObject("oud.beheer", "CN=oud.beheer,OU=Users," + Domain) },
        MachineAccountQuota = 10,
        RecycleBin = false,
        WithoutLaps = new[] { "PC-1", "PC-2" },
        ProtectedUsersDn = "CN=Protected Users,CN=Users," + Domain,
        Admins = new[]
        {
            new AdminAccount("Administrator", "CN=Administrator,CN=Users," + Domain, true, false, false, Array.Empty<string>(), new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero), null),
            new AdminAccount("adm.an", "CN=adm.an,OU=Admins," + Domain, true, true, true, Array.Empty<string>(), null, null),
            new AdminAccount("svc.backup", "CN=svc.backup,OU=Service," + Domain, true, false, false, new[] { "backup/srv1" }, null, null),
            new AdminAccount("adm.weg", "CN=adm.weg,OU=Admins," + Domain, false, false, false, Array.Empty<string>(), null, null),
        },
        Groups = new[]
        {
            new PrivilegedGroup("512", "Domain Admins", "CN=Domain Admins,CN=Users," + Domain, new[]
            {
                new GroupMember("Administrator", "CN=Administrator,CN=Users," + Domain, "user", true),
                new GroupMember("adm.weg", "CN=adm.weg,OU=Admins," + Domain, "user", false),
                new GroupMember("GG-Tier0", "CN=GG-Tier0,OU=Admins," + Domain, "group", true),
            }),
            new PrivilegedGroup("519", "Enterprise Admins", "CN=Enterprise Admins,CN=Users," + Domain, new[] { new GroupMember("Administrator", "CN=Administrator,CN=Users," + Domain, "user", true) }),
            new PrivilegedGroup("551", "Backup Operators", "CN=Backup Operators,CN=Builtin," + Domain, new[] { new GroupMember("svc.backup", "CN=svc.backup,OU=Service," + Domain, "user", true) }),
            new PrivilegedGroup("548", "Account Operators", "CN=Account Operators,CN=Builtin," + Domain, Array.Empty<GroupMember>()),
        },
    };

    private static LogEntry Log(Change c) => new(Now, "beheerder", "test", c.TargetSystem.ToString(), c.ChangeAction.ToString(), c.Target, c.TargetName,
        c.Description, c.Parameters, "Succeeded", string.Empty, Previous: c.Previous);

    [Fact]
    public void TheAdSecurityCheckSolvesWhatHasLowRiskInOnePlan()
    {
        var check = SecurityChecks.AdSecurity(Reading(), Now);
        CheckRow Row(string name) => check.Rows.First(r => r.Object == name);

        Assert.Equal((AdHardening.AesOnly, "24", ""), (Row("svc.sql").Fix, Row("svc.sql").FixData!["nieuw"], Row("svc.sql").FixData!["was"]));
        Assert.Null(Row("svc.web").Fix); // AES only already: a gMSA is the advice
        Assert.Equal("TrustedForDelegation", Row("SRV-APP$").Fix);
        Assert.Contains("HTTP/app01", Row("SRV-APP$").Detail, StringComparison.Ordinal);
        Assert.Equal("DontRequirePreauth", Row("scanner").Fix);
        Assert.Equal(AdHardening.AdminCount, Row("oud.beheer").Fix);
        Assert.Equal("10", Row("ms-DS-MachineAccountQuota").FixData!["was"]);
        Assert.Equal(AdHardening.RecycleBin, Row("AD-prullenbak").Fix);
        Assert.Null(Row("PC-1").Fix); // LAPS is advice (Group Policy or Intune)
        Assert.Equal("script:Reset-KrbtgtPassword", Row("krbtgt").Fix);

        // Delegation is guided (one account per plan); the rest is one plan
        Assert.Equal(new[] { "svc.sql", "scanner", "oud.beheer", "ms-DS-MachineAccountQuota", "AD-prullenbak" }, SecurityAdvice.Solvable(check).Select(r => r.Object));
        Assert.True(SecurityAdvice.SolvableAlone(check, Row("SRV-APP$")));
        Assert.False(SecurityAdvice.SolvableAlone(check, Row("svc.sql")));

        var outcome = SecurityAdvice.Solve(new InventoryData(), check);
        Assert.Empty(outcome.Errors); // the DNs come from the reading
        Assert.Equal(new[] { ChangeAction.AdHardening, ChangeAction.AdSetAccountFlag, ChangeAction.AdHardening, ChangeAction.AdHardening, ChangeAction.AdHardening },
            outcome.Plan.Select(c => c.ChangeAction));
        var aes = outcome.Plan[0];
        Assert.Equal(("encryptionTypes", "24", ""), (aes.P("wat"), aes.P("waarde"), aes.Previous!["waarde"]));
        var preauth = outcome.Plan[1];
        Assert.Equal(("CN=scanner,OU=Service," + Domain, "DontRequirePreauth", "False", "True"), (preauth.Target, preauth.P("vlag"), preauth.P("aan"), preauth.Previous!["aan"]));
        Assert.Equal(("adminCount", "False", "True"), (outcome.Plan[2].P("wat"), outcome.Plan[2].P("beschermd"), outcome.Plan[2].Previous!["beschermd"]));
        Assert.Equal(("machineAccountQuota", "0", "10", Domain), (outcome.Plan[3].P("wat"), outcome.Plan[3].P("waarde"), outcome.Plan[3].Previous!["waarde"], outcome.Plan[3].Target));
        Assert.Equal("recycleBin", outcome.Plan[4].P("wat"));
        Assert.Contains("niet ongedaan", outcome.Plan[4].Warning, StringComparison.Ordinal);

        // Undo: every hardening goes back to its previous value; the Recycle Bin cannot be turned off
        var back = Undo.Plan(outcome.Plan.Take(4).Select(Log)).Plan;
        Assert.Contains(back, c => c.P("wat") == "encryptionTypes" && c.P("waarde") == "");
        Assert.Contains(back, c => c.P("wat") == "adminCount" && c.P("beschermd") == "True");
        Assert.Contains(back, c => c.P("wat") == "machineAccountQuota" && c.P("waarde") == "10");
        Assert.Contains(back, c => c.P("vlag") == "DontRequirePreauth" && c.P("aan") == "True");
        Assert.Equal("De AD-prullenbak kan niet meer uitgezet worden.", Undo.WhyNot(Log(outcome.Plan[4])));

        // Delegation: a plan for that account alone, with what stops working
        var delegation = Assert.Single(SecurityAdvice.SolveRow(new InventoryData(), Row("SRV-APP$")).Plan);
        Assert.Equal(("CN=SRV-APP,OU=Servers," + Domain, "TrustedForDelegation", "False"), (delegation.Target, delegation.P("vlag"), delegation.P("aan")));
        Assert.Contains("beperkte", delegation.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrivilegedGroupsAreCheckedAndAdministratorsProtectedOneByOne()
    {
        var check = AdHardening.Groups(Reading());
        Assert.Equal(CheckStatus.Problem, check.Status);
        var admin = check.Rows.Single(r => r.Object == "Administrator");
        Assert.Equal(AdHardening.Protect, admin.Fix);
        Assert.Contains("niet in Protected Users", admin.Detail, StringComparison.Ordinal);
        Assert.Contains("niet gemarkeerd als gevoelig", admin.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(check.Rows, r => r.Object == "adm.an"); // already protected
        var service = check.Rows.Single(r => r.Object == "svc.backup");
        Assert.Null(service.Fix); // runs a service: first a gMSA
        Assert.Contains("gMSA", service.Detail, StringComparison.Ordinal);

        var disabled = check.Rows.Single(r => r.Object == "adm.weg (Domain Admins)");
        Assert.Equal((CheckStatus.Problem, AdHardening.RemoveMember), (disabled.Status, disabled.Fix));
        Assert.Equal(CheckStatus.Attention, check.Rows.Single(r => r.Object == "Administrator (Enterprise Admins)").Status);
        Assert.Equal(CheckStatus.Attention, check.Rows.Single(r => r.Object == "svc.backup (Backup Operators)").Status);
        Assert.DoesNotContain(check.Rows, r => r.Object.StartsWith("GG-Tier0", StringComparison.Ordinal) || r.Object == "Administrator (Domain Admins)");

        // Removing members is one plan with undo; protecting is guided, per account
        var plan = SecurityAdvice.Solve(new InventoryData(), check).Plan;
        Assert.Equal(3, plan.Count);
        Assert.All(plan, c => Assert.Equal(ChangeAction.AdRemoveGroupMember, c.ChangeAction));
        var remove = plan.Single(c => c.TargetName == "adm.weg");
        Assert.Equal(("CN=adm.weg,OU=Admins," + Domain, "CN=Domain Admins,CN=Users," + Domain), (remove.Target, remove.P("groepDn")));
        var undone = Assert.Single(Undo.Plan(new[] { Log(remove) }).Plan);
        Assert.Equal((ChangeAction.AdAddGroupMember, "CN=Domain Admins,CN=Users," + Domain), (undone.ChangeAction, undone.P("groepDn")));

        Assert.True(SecurityAdvice.SolvableAlone(check, admin));
        var protect = SecurityAdvice.SolveRow(new InventoryData(), admin).Plan;
        Assert.Equal(new[] { ChangeAction.AdAddGroupMember, ChangeAction.AdSetAccountFlag }, protect.Select(c => c.ChangeAction));
        Assert.Equal("CN=Protected Users,CN=Users," + Domain, protect[0].P("groepDn"));
        Assert.Contains("NTLM", protect[0].Warning, StringComparison.Ordinal);
        Assert.Contains("2024-03-01", protect[0].Warning, StringComparison.Ordinal);
        Assert.Equal(("NotDelegated", "True", "False"), (protect[1].P("vlag"), protect[1].P("aan"), protect[1].Previous!["aan"]));

        // A domain without Protected Users (before 2012 R2): only "sensitive"
        var older = AdHardening.Groups(Reading() with { ProtectedUsersDn = null });
        var flagOnly = SecurityAdvice.SolveRow(new InventoryData(), older.Rows.Single(r => r.Object == "Administrator")).Plan;
        Assert.Equal(ChangeAction.AdSetAccountFlag, Assert.Single(flagOnly).ChangeAction);
        Assert.Equal(CheckStatus.Unknown, AdHardening.Groups(null).Status);
    }

    [Fact]
    public void CarryingOutAdviceNeedsAnAdministrator()
    {
        var plan = SecurityAdvice.Solve(new InventoryData(), SecurityChecks.AdSecurity(Reading(), Now)).Plan;
        foreach (var c in plan)
        {
            Assert.NotNull(PermissionTable.Refusal(Role.Auditor, c)); // sees everything, carries out nothing
            Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, c));
            Assert.Null(PermissionTable.Refusal(Role.Administrator, c));
        }

        Assert.Equal(new[] { "encryptionTypes", "adminCount", "machineAccountQuota", "recycleBin" }, AdHardening.Kinds);
    }
}
