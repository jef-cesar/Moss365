using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Reporting;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>R15 (0.38.5): the texts that the core builds (KPIs, plans, checks, reports) follow the language too.</summary>
public sealed class CoreLanguageTests
{
    [Fact]
    public void KpisWithAParameterHaveTheirNameInTheLanguage()
    {
        using (Texts.Scope("en"))
        {
            Assert.Equal("active employees", Kpis.Active.Label);
            Assert.Equal("signed in in the last 30 days", Kpis.SignInEvent("0-30").Label);
            Assert.Equal("never signed in (or unknown)", Kpis.SignInEvent("onbekend").Label);
            Assert.Equal("guests from contoso.com", Kpis.GuestDomainKpi("contoso.com").Label);
            Assert.Equal("division (no division)", Kpis.DivisionAll(Statistic.NoDivision).Label);
            Assert.Equal("Only through a group", Kpis.PermissionModel("Alleen via groep").Label);
            // A value from AD or Microsoft 365 stays as it is
            Assert.Equal("Finance", Kpis.Division("Finance").Label);
        }

        using (Texts.Scope("de"))
        {
            Assert.Equal("mit MFA", Kpis.MfaStatus("ja").Label);
        }

        Assert.Equal("gasten van contoso.com", Kpis.GuestDomainKpi("contoso.com").Label);
    }

    [Fact]
    public void ThePlanOfTheTenantSetUpFollowsTheLanguage()
    {
        using (Texts.Scope("en"))
        {
            var a = TenantSetup.Assess(SetupTestData.Empty());
            Assert.Contains(a.Items, i => i.Step == "App registration");
            Assert.Contains(a.Plan, w => w.Description.StartsWith("Create app registration Moss365", StringComparison.Ordinal));
        }

        using (Texts.Scope("fr"))
        {
            Assert.Contains(TenantSetup.Assess(SetupTestData.Empty()).Items, i => i.Step == "Profils");
        }
    }

    [Fact]
    public void UndoKnowsAnAlreadyDoneResultInEveryLanguage()
    {
        LogEntry Entry(string message) => new(DateTimeOffset.Now, "admin", "test", "Graph", nameof(ChangeAction.GraphAddGroupMember), "id", "Anna",
            "Lid maken", new Dictionary<string, string> { ["groepId"] = "g" }, "Succeeded", message);
        using (Texts.Scope("en"))
        {
            Assert.NotNull(Undo.WhyNot(Entry("Was already a member.")));
            Assert.NotNull(Undo.WhyNot(Entry("Était déjà membre.")));
            Assert.NotNull(Undo.WhyNot(Entry("Was al lid.")));
            Assert.Null(Undo.WhyNot(Entry("Done.")));
            Assert.StartsWith("nothing changed", Undo.WhyNot(Entry("Was already a member."))!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePeriodReportIsInTheLanguageOfTheInterface()
    {
        using (Texts.Scope("en"))
        {
            var report = new PeriodReport("Contoso", ReportPeriod.Month(2026, 10), DateTimeOffset.Now, "admin@contoso.com",
                Array.Empty<KpiRow>(), Array.Empty<ChangeRow>(), Array.Empty<QualityRow>(), Array.Empty<StandardRow>(), Array.Empty<string>());
            var html = report.ToHtml();
            Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
            Assert.Contains("Report October 2026", html, StringComparison.Ordinal);
            Assert.Contains("No changes in this period.", html, StringComparison.Ordinal);
            Assert.StartsWith("Report;October 2026", report.ToCsv(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void KindsOfGroupsAndMfaMethodsHaveAFixedKey()
    {
        var d = TestData.AppEnvironment();
        var group = d.Index.Groups.First(g => g.Ad is not null);
        var kpi = Kpis.GroupKind(group.KindKey);
        var count = Kpis.Groups(kpi, new KpiSource(d, TestData.Config(), DateTime.Now)).Count;
        Assert.True(count > 0);
        using (Texts.Scope("en"))
        {
            Assert.StartsWith("AD ", group.KindText, StringComparison.Ordinal);
            Assert.Equal(group.KindText, Kpis.Find(kpi.Key)!.Label);
            Assert.Equal("Code from an app", Kpis.Method("softwareOneTimePasscode").Label);
            Assert.Equal("Security key or passkey", SecurityStatistics.MethodName("passKeyDeviceBound"));
            Assert.Equal("Department", AdEntraComparison.AttributeText("Department"));
        }

        // The key is the same in every language, so a link keeps working after a change of language
        using (Texts.Scope("de"))
        {
            Assert.Equal(count, Kpis.Groups(Kpis.Find(kpi.Key)!, new KpiSource(d, TestData.Config(), DateTime.Now)).Count);
        }

        Assert.Equal("Dienst (Department)", AdEntraComparison.AttributeText("Department"));
    }

    [Fact]
    public void TheDefaultMailsKeepTheirPlaceholders()
    {
        using (Texts.Scope("pt-BR"))
        {
            Assert.Contains("{naam}", Core.Helpdesk.HelpdeskTools.DefaultReminder, StringComparison.Ordinal);
            Assert.Contains("{datum}", Core.Helpdesk.HelpdeskTools.DefaultReminder, StringComparison.Ordinal);
            Assert.Contains("{gasten}", GuestRenewal.DefaultQuestion, StringComparison.Ordinal);
            Assert.StartsWith("Olá", GuestRenewal.DefaultQuestion, StringComparison.Ordinal);
        }
    }
}
