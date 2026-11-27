using System.Text.Json;
using Moss365.Connections.Exchange;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Permissions;
using Moss365.Core.Security;
using CheckStatus = Moss365.Core.Security.CheckStatus;
using Xunit;

namespace Moss365.Tests;

/// <summary>R16: the advice for Exchange Online as plans, through the fixed commands of Exchange.ps1.</summary>
public class ExchangeAdviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.FromHours(1));

    public static ExchangeOrganisationReading Open() => new(false, new[] { "scanner@test.be" },
        new[] { new ForwardingPolicy("Default", "On", true), new ForwardingPolicy("Uitzondering", "Automatic", false) },
        false, false,
        new[]
        {
            new MailDomain("test.be", Array.Empty<string>(), new[] { "v=DMARC1; p=none; rua=mailto:dmarc@test.be" }, false,
                "selector1-test-be._domainkey.test.onmicrosoft.com", "selector2-test-be._domainkey.test.onmicrosoft.com"),
            new MailDomain("contoso.com", new[] { "v=spf1 include:spf.protection.outlook.com -all" }, new[] { "v=DMARC1; p=reject" }, true, string.Empty, string.Empty),
            new MailDomain("test.onmicrosoft.com", Array.Empty<string>(), Array.Empty<string>(), true, string.Empty, string.Empty),
        },
        new PresetReading(new[] { new PresetRule(ExchangeAdvice.StandardPreset, "Disabled") }, Array.Empty<PresetRule>(), false));

    private static LogEntry Log(Change c) => new(Now, "beheerder", "test", c.TargetSystem.ToString(), c.ChangeAction.ToString(), c.Target, c.TargetName,
        c.Description, c.Parameters, "Succeeded", string.Empty, Previous: c.Previous);

    [Fact]
    public void TheSettingsOfExchangeAreCheckedAndSolvedAsOnePlan()
    {
        var check = ExchangeAdvice.Check(Open(), new SmtpAuthUsage(Array.Empty<string>(), false));
        Assert.Equal(CheckStatus.Problem, check.Status);
        Assert.Equal(new[] { ExchangeAdvice.SmtpAuth, ExchangeAdvice.Forwarding, ExchangeAdvice.ExternalTag, ExchangeAdvice.Audit, ExchangeAdvice.Preset }, check.Rows.Select(r => r.Fix));
        Assert.DoesNotContain(check.Rows, r => r.Object == "Uitzondering"); // Automatic is off
        Assert.Contains("Defender for Office 365 zit niet in de licentie", check.Rows.Single(r => r.Fix == ExchangeAdvice.Preset).Detail, StringComparison.Ordinal);

        // SMTP AUTH is guided; the rest is one plan, each with its previous value
        Assert.DoesNotContain(SecurityAdvice.Solvable(check), r => r.Fix == ExchangeAdvice.SmtpAuth);
        var plan = SecurityAdvice.Solve(new InventoryData(), check).Plan;
        Assert.Equal(new[] { "Doorsturen", "ExternLabel", "Auditlog", "Preset" }, plan.Select(c => c.P("instelling")));
        Assert.Equal(("Default", "Off", "On"), (plan[0].P("identity"), plan[0].P("waarde"), plan[0].Previous!["waarde"]));
        Assert.Equal(ExchangeAdvice.StandardPreset, plan[3].P("identity"));
        Assert.All(plan, c => Assert.Equal((TargetSystem.Exchange, ChangeAction.ExchangeOrganisationSetting), (c.TargetSystem, c.ChangeAction)));
        Assert.Equal("On", Assert.Single(Undo.Plan(new[] { Log(plan[0]) }).Plan).P("waarde"));
        Assert.All(plan, c => Assert.NotNull(PermissionTable.Refusal(Role.Helpdesk, c)));

        Assert.Equal(CheckStatus.Unknown, ExchangeAdvice.Check(null, null).Status);
        var fine = Open() with { SmtpAuthOff = true, Forwarding = Array.Empty<ForwardingPolicy>(), ExternalTag = true, AuditLog = true,
            Presets = new PresetReading(new[] { new PresetRule(ExchangeAdvice.StandardPreset, "Enabled") }, Array.Empty<PresetRule>(), true) };
        Assert.Equal(CheckStatus.Ok, ExchangeAdvice.Check(fine, null).Status);
    }

    [Fact]
    public void SmtpAuthGoesOffWithAnExceptionForWhoStillUsesIt()
    {
        var used = ExchangeAdvice.Check(Open(), new SmtpAuthUsage(new[] { "printer@test.be", "scanner@test.be" }, false));
        var row = used.Rows.Single(r => r.Fix == ExchangeAdvice.SmtpAuth);
        Assert.True(SecurityAdvice.SolvableAlone(used, row));
        var plan = SecurityAdvice.SolveRow(new InventoryData(), row).Plan;

        // The printer keeps it (the scanner already has an exception); the organisation follows only when that worked
        Assert.Equal(new[] { "SmtpAuthPostvak", "SmtpAuth" }, plan.Select(c => c.P("instelling")));
        Assert.Equal(("printer@test.be", "True", ""), (plan[0].P("identity"), plan[0].P("waarde"), plan[0].Previous!["waarde"]));
        Assert.Equal(("False", "True"), (plan[1].P("waarde"), plan[1].Previous!["waarde"]));
        Assert.Equal(plan[0].Id, plan[1].DependentOf);
        Assert.Contains("printer@test.be", plan[1].Warning, StringComparison.Ordinal);
        Assert.Equal(new[] { "", "True" }, Undo.Plan(plan.Select(Log)).Plan.Select(c => c.P("waarde")).OrderBy(v => v, StringComparer.Ordinal));

        // Without the usage report, or with hidden names: first that
        Assert.NotEmpty(SecurityAdvice.SolveRow(new InventoryData(), ExchangeAdvice.Check(Open(), null).Rows.Single(r => r.Fix == ExchangeAdvice.SmtpAuth)).Errors);
        var hidden = ExchangeAdvice.Check(Open(), new SmtpAuthUsage(new[] { "0f3c9a" }, true)).Rows.Single(r => r.Fix == ExchangeAdvice.SmtpAuth);
        Assert.Contains("verbergt", Assert.Single(SecurityAdvice.SolveRow(new InventoryData(), hidden).Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void SpfDkimAndDmarcPerOwnDomain()
    {
        var check = ExchangeAdvice.Domains(Open());
        Assert.Equal(CheckStatus.Problem, check.Status);
        Assert.Equal(CheckStatus.Problem, check.Rows.Single(r => r.Object == "test.be (SPF)").Status);
        Assert.Contains("p=none", check.Rows.Single(r => r.Object == "test.be (DMARC)").Detail, StringComparison.Ordinal);
        var dkim = check.Rows.Single(r => r.Object == "test.be (DKIM)");
        Assert.Equal(ExchangeAdvice.Dkim, dkim.Fix);
        Assert.Contains("selector1-test-be._domainkey.test.onmicrosoft.com", dkim.Detail, StringComparison.Ordinal);
        Assert.All(check.Rows.Where(r => r.Object.StartsWith("contoso.com", StringComparison.Ordinal)), r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.DoesNotContain(check.Rows, r => r.Object.Contains("onmicrosoft", StringComparison.Ordinal));

        var on = Assert.Single(SecurityAdvice.SolveRow(new InventoryData(), dkim).Plan);
        Assert.Equal(("Dkim", "test.be", "True"), (on.P("instelling"), on.P("identity"), on.P("waarde")));
        Assert.Contains("selector2._domainkey.test.be", on.Warning, StringComparison.Ordinal);

        Assert.Equal("reject", ExchangeAdvice.Tag("v=DMARC1; p=Reject; rua=mailto:x@test.be", "p"));
        Assert.Null(ExchangeAdvice.Tag("v=DMARC1", "p"));
        var soft = ExchangeAdvice.Domains(Open() with { Domains = new[] { new MailDomain("test.be", new[] { "v=spf1 a ~all" }, new[] { "v=DMARC1; p=quarantine" }, true, "", "") } });
        Assert.Equal(CheckStatus.Attention, soft.Status);
        Assert.Equal(CheckStatus.Unknown, ExchangeAdvice.Domains(null).Status);
    }

    [Fact]
    public async Task TheOrganisationIsReadAndChangedThroughTheFixedCommands()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "organisatie", status = "Gelukt", melding = "",
                data = new
                {
                    SmtpAuthUit = false, SmtpAuthPostvakken = new[] { "scanner@test.be" },
                    Doorsturen = new[] { new { Name = "Default", Mode = "On", Default = true } },
                    ExternLabel = false, Auditlog = true,
                    Domeinen = new[] { new { Domain = "test.be", Spf = new[] { "v=spf1 -all" }, Dmarc = Array.Empty<string>(), DkimEnabled = (bool?)null, Selector1 = "", Selector2 = "" } },
                    Preset = new { Eop = new { Name = ExchangeAdvice.StandardPreset, State = "Enabled" }, Atp = Array.Empty<object>(), AtpBeschikbaar = true },
                },
            },
        });
        var starter = new OutputPowerShell(json);
        var r = await new ExchangeConnection("beheer@test.be", starter, Path.Combine(Path.GetTempPath(), "moss365-tests-" + Guid.NewGuid())).ReadOrganisationAsync(CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal((false, "scanner@test.be", "On", false, true), (r!.SmtpAuthOff, r.SmtpAuthMailboxes.Single(), r.Forwarding.Single().Mode, r.ExternalTag, r.AuditLog));
        var domain = r.Domains.Single();
        Assert.Equal(("v=spf1 -all", 0, (bool?)null), (domain.Spf.Single(), domain.Dmarc.Count, domain.DkimEnabled));
        Assert.Equal("Enabled", r.Presets!.Eop.Single().State); // one rule comes as an object, not a list
        Assert.Contains("\"op\":\"LeesOrganisatie\"", starter.Commands, StringComparison.Ordinal);

        // A change becomes the fixed command with its setting; an unknown setting never leaves the app
        var change = SecurityAdvice.Solve(new InventoryData(), ExchangeAdvice.Check(Open(), null)).Plan.First();
        var command = ExchangeConnection.ToCommand(change);
        Assert.Equal(("OrganisatieInstellen", "Doorsturen", "Off", "Default"), (command.Op, command.Params["Instelling"], command.Params["Waarde"], command.Params["Identity"]));
        var forged = new Change
        {
            TargetSystem = TargetSystem.Exchange, ChangeAction = ChangeAction.ExchangeOrganisationSetting, Target = "x", Description = "x",
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["instelling"] = "TransportRule", ["waarde"] = "True" },
        };
        Assert.Throws<InvalidOperationException>(() => ExchangeConnection.ToCommand(forged));
        Assert.NotNull(ExchangeAdvice.Refusal(forged));
        Assert.Null(ExchangeAdvice.Refusal(change));
    }

    private sealed class OutputPowerShell(string json) : IPowerShellStarter
    {
        public string Commands { get; private set; } = string.Empty;

        public Task<(int Code, string Errors)> StartAsync(string script, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel)
        {
            var list = arguments.ToList();
            Commands = File.ReadAllText(list[list.IndexOf("-Opdrachten") + 1]).Replace(" ", string.Empty, StringComparison.Ordinal);
            File.WriteAllText(list[list.IndexOf("-Uitvoer") + 1], json);
            return Task.FromResult((0, string.Empty));
        }
    }
}
