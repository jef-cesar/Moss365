using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Setup;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R5: the wizard sets up a tenant without typing anything the app could find.</summary>
public sealed partial class PlanScreenTests
{
    private sealed partial class FakeFactory
    {
        public FakeSetup SetupConnection { get; } = new();

        public ITenantSetupConnection? Setup(string tenantId) => TenantLookup.IsTenantId(tenantId) ? SetupConnection : null;

        public Task<TenantLookupResult> FindTenantAsync(string domainOrId, CancellationToken cancel)
            => Task.FromResult(TenantLookup.Clean(domainOrId) == "contoso.com"
                ? new TenantLookupResult(SetupTestData.TenantId, null)
                : new TenantLookupResult(null, "Microsoft kent geen tenant met dat domein."));

        public LocalDomain? FindLocalDomain() => new("test.internal", "dc02.test.internal");
    }

    /// <summary>An empty tenant that is set up after the plan has run.</summary>
    private sealed class FakeSetup : ITenantSetupConnection
    {
        public TenantSetupReading Reading { get; set; } = SetupTestData.Empty();
        public List<Change> Executed { get; } = new();
        public string? SignedInAs { get; private set; }
        public TargetSystem TargetSystem => TargetSystem.Graph;

        public Task<TenantSetupReading> ReadAsync(IProgress<string>? progress, CancellationToken cancel)
        {
            SignedInAs = SetupTestData.Admin.Upn;
            return Task.FromResult(Reading);
        }

        public Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
        {
            Executed.AddRange(changes);
            Reading = SetupTestData.Complete();
            return Task.FromResult<IReadOnlyList<ChangeResult>>(changes.Select(c => ChangeResult.Ok(c)).ToList());
        }
    }

    [Fact]
    public void TheWizardSetsUpATenant()
    {
        State.Settings.TenantId = string.Empty;
        State.Settings.ClientId = string.Empty;
        State.Settings.ManagementUpn = string.Empty;
        var cut = RenderComponent<SetUpTenant>();

        // 1. A domain that does not exist, then the right one
        cut.Find("input[aria-label='Domein of tenant ID']").Change("nergens.example");
        cut.FindAll("button").First(b => b.TextContent == "Opzoeken").Click();
        Assert.Contains("Microsoft kent geen tenant", cut.Markup, StringComparison.Ordinal);
        cut.Find("input[aria-label='Domein of tenant ID']").Change("Jan@Contoso.com");
        cut.FindAll("button").First(b => b.TextContent == "Opzoeken").Click();
        Assert.Equal(SetupTestData.TenantId, State.Settings.TenantId);
        Assert.Equal(("contoso.com", SetupTestData.TenantId), (State.Tenant.Domain, State.Tenant.TenantId));

        // 2. Sign in and check: the plan of an empty tenant
        cut.FindAll("button").First(b => b.TextContent == "Aanmelden en nakijken").Click();
        Assert.Contains("Aangemeld als admin@contoso.com", cut.Markup, StringComparison.Ordinal);
        var plan = cut.Find("section.kb-plan");
        Assert.Contains("App-registratie Moss365 maken", plan.TextContent, StringComparison.Ordinal);
        Assert.Contains("GG-Moss365-Helpdesk", plan.TextContent, StringComparison.Ordinal);
        Assert.Empty(_factory.SetupConnection.Executed);

        // 3. Run it: everything with the sign-in of the wizard, in the change log; the client ID is filled in
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Equal(16, _factory.SetupConnection.Executed.Count));
        Assert.Empty(_factory.Executed);
        cut.WaitForAssertion(() => Assert.Equal(SetupTestData.AppId, State.Settings.ClientId));
        Assert.Contains(State.ChangeLog.Recent(50), r => r.ChangeAction == nameof(ChangeAction.GraphCreateApplication) && r.User == "admin@contoso.com");
        cut.WaitForAssertion(() => Assert.Contains("De app-registratie is in orde.", cut.Markup, StringComparison.Ordinal));

        // 4. The domain controller of this PC, 5. the Exchange admin
        cut.FindAll("button").First(b => b.TextContent == "Overnemen").Click();
        Assert.Equal("dc02.test.internal", State.Settings.AdServer);
        cut.FindAll("button").First(b => b.TextContent.Contains("als beheeraccount voor Exchange", StringComparison.Ordinal)).Click();
        Assert.Equal("admin@contoso.com", State.Settings.ManagementUpn);
    }

    [Fact]
    public void TheWizardSuggestsWhatIsMissingInTheOrganisation()
    {
        State.Config.Offices.Clear();
        State.Config.Departments.RemoveAll(d => d.Department == "gezinszorg");
        var cut = RenderComponent<SetUpTenant>();
        var row = cut.FindAll("tr").First(tr => tr.TextContent.Contains("gezinszorg", StringComparison.Ordinal));
        row.QuerySelector("input")!.Change(true);
        cut.FindAll("button").First(b => b.TextContent.StartsWith("Overnemen (1)", StringComparison.Ordinal)).Click();
        Assert.NotNull(State.Config.SearchDepartment("gezinszorg"));
        Assert.Contains("1 voorstel(len) overgenomen", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("tr"), tr => tr.TextContent.Contains("gezinszorg", StringComparison.Ordinal));
    }
}

public sealed partial class PlanScreenTests
{
    private sealed class InstallCheck : ISystemCheck
    {
        public Task<IReadOnlyList<CheckRule>> CheckAsync(ConnectionSettings settings, IProgress<string>? progress, CancellationToken cancel)
            => Task.FromResult<IReadOnlyList<CheckRule>>(new[]
            {
                new CheckRule("Deze pc", "PowerShell 7", CheckStatus.Warning, "niet geïnstalleerd", "x", "winget install --id Microsoft.PowerShell", "PowerShell7", "winget"),
                new CheckRule("PowerShell 7", "Microsoft.Graph.Users", CheckStatus.Warning, "niet geïnstalleerd", "x", "Install-Module", "Microsoft.Graph.Users", "pwsh.exe"),
            });

        public List<string> Installed { get; } = new();

        public Task<string> InstallAsync(string powerShell, string module, CancellationToken cancel)
        {
            Installed.Add(module);
            return Task.FromResult(module == "PowerShell7" ? "Mislukt: winget ontbreekt." : "Gelukt");
        }
    }

    [Fact]
    public void InstallingIsConfirmedOnceAndLogged()
    {
        var check = new InstallCheck();
        _factory.Check = check;
        var cut = RenderComponent<ThisComputer>();
        cut.FindAll("button").First(b => b.TextContent == "Controleren").Click();
        cut.WaitForAssertion(() => Assert.Contains("Alles installeren wat ontbreekt (2)", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(check.Installed);

        cut.FindAll("button").First(b => b.TextContent.StartsWith("Alles installeren", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent == "Ja, alles installeren").Click();
        cut.WaitForAssertion(() => Assert.Equal(new[] { "PowerShell7", "Microsoft.Graph.Users" }, check.Installed));
        var log = State.ChangeLog.Recent(10).Where(r => r.ChangeAction == "InstallComponent").ToList();
        Assert.Equal(2, log.Count);
        Assert.Contains(log, r => r.Target == "PowerShell7" && !r.Succeeded && r.Message.Contains("winget", StringComparison.Ordinal));
        Assert.Contains(log, r => r.Target == "Microsoft.Graph.Users" && r.Succeeded);
    }
}
