using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Helpdesk;
using Moss365.Core.Inventory;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.33 (R10): helpdesk and daily work.</summary>
public sealed partial class PlanScreenTests
{
    private void EntraFilled()
    {
        State.Settings.TenantId = "tenant";
        State.Settings.ClientId = "client";
    }

    [Fact]
    public void ATemporaryAccessPassIsShownOnceAndNeverLogged()
    {
        EntraFilled();
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var plan = Assert.Single(HelpdeskTools.TemporaryAccessPass(cas, 480, true).Plan);
        Assert.Equal(ChangeAction.GraphCreateTemporaryAccessPass, plan.ChangeAction);
        Assert.Equal("e-cas", plan.Target);
        Assert.NotEmpty(HelpdeskTools.TemporaryAccessPass(cas, 10, true).Errors);
        Assert.NotEmpty(HelpdeskTools.TemporaryAccessPass(State.Data.Persons.First(p => p.IsGuest), 480, true).Errors);

        var cut = RenderComponent<Helpdesk>(p => p.Add(x => x.Person, cas));
        cut.FindAll("button").First(b => b.TextContent == "Pass maken (plan)").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains("Temporary Access Pass voor cas vos", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Tap-Test-1234", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(State.ChangeLog.Recent(10), r => r.Parameters.Values.Any(v => v.Contains("Tap-Test", StringComparison.Ordinal)) || r.Message.Contains("Tap-Test", StringComparison.Ordinal));
    }

    [Fact]
    public void BitLockerAndLapsAreLookedUpWithTheAccessLog()
    {
        EntraFilled();
        var cut = RenderComponent<Keys>();
        cut.Find("input[aria-label='Naam van het toestel']").Change("PC-1");
        cut.FindAll("button").First(b => b.TextContent == "Zoeken").Click();
        cut.WaitForAssertion(() => Assert.Contains("PC-100", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent == "BitLocker").Click();
        cut.WaitForAssertion(() => Assert.Contains("333333-444444", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").First(b => b.TextContent == "LAPS").Click();
        cut.WaitForAssertion(() => Assert.Contains("Windows LAPS in Entra", cut.Markup, StringComparison.Ordinal));

        var log = State.Database.ReadAccess(10);
        Assert.Contains(log, a => a.What == "BitLocker-herstelsleutel bekeken" && a.Details == "PC-100");
        Assert.Contains(log, a => a.What == "LAPS-wachtwoord bekeken");
        Assert.DoesNotContain(log, a => a.Details.Contains("333333", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AComputerInAdHasItsKeyAndLapsPasswordToo()
    {
        var (keys, _) = await State.BitLockerAsync(null, null, "PC-AD");
        Assert.Contains(keys, k => k.Key == "111111-222222");
        var (laps, _) = await State.LapsAsync(null, "PC-AD");
        Assert.Equal("Laps-AD-Secret", laps!.Password);
    }

    [Fact]
    public void PasswordsThatExpireSoonGetAReminder()
    {
        var d = State.Data;
        var now = new DateTime(2026, 11, 20);
        var ad = d.AdUsers.Select(a => a.SamAccountName switch
        {
            "an.peeters" => a with { PasswordLastSet = "2026-08-25 10:00:00" },   // 90 days: 23/11
            "bo.janssens" => a with { PasswordLastSet = "2026-11-01 10:00:00" },
            "cas.vos" => a with { PasswordLastSet = "2026-08-01 10:00:00", PasswordNeverExpires = true },
            _ => a,
        }).ToList();
        var data = d with { AdUsers = ad, Persons = InventoryLoader.LinkPersons(ad, d.EntraUsers) };
        var list = HelpdeskTools.ExpiringPasswords(data, 90, 14, now);
        var x = Assert.Single(list);
        Assert.Equal("an peeters", x.Person.Name);
        Assert.Equal(3, x.DaysLeft);
        Assert.Empty(HelpdeskTools.ExpiringPasswords(data, 0, 14, now));

        var mail = Assert.Single(HelpdeskTools.PasswordReminders(data, list, "Beste {naam}, op {datum} ({dagen} dagen).", false).Plan);
        Assert.Equal(ChangeAction.GraphSendMail, mail.ChangeAction);
        Assert.Equal("an.peeters@test.be", mail.Target);
        Assert.Equal("Beste an peeters, op 23/11/2026 (3 dagen).", mail.P("tekst"));
    }

    [Fact]
    public void AbsenceWithEndDatesAndTheEndOfForwardingInScheduled()
    {
        var bo = State.Data.Persons.First(p => p.Ad?.SamAccountName == "bo.janssens");
        var request = new AbsenceRequest
        {
            Start = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), End = new DateTimeOffset(2026, 12, 20, 18, 0, 0, TimeSpan.Zero),
            Internal = "Afwezig", ForwardTo = "collega@test.be", ForwardUntil = new DateOnly(2026, 12, 20),
        };
        var (outcome, end) = HelpdeskTools.Absence(bo, request);
        Assert.Equal(2, outcome.Plan.Count);
        Assert.Equal("Scheduled", outcome.Plan[0].P("AutoReplyState"));
        Assert.Equal("collega@test.be", outcome.Plan[1].P("ForwardingSmtpAddress"));
        Assert.Equal(string.Empty, Assert.Single(end).P("ForwardingSmtpAddress"));
        Assert.NotEmpty(HelpdeskTools.Absence(bo, new AbsenceRequest { Internal = "", AutoReply = true }).Outcome.Errors);

        var cut = RenderComponent<AbsenceCard>(p => p.Add(x => x.Person, bo));
        cut.Find("textarea[aria-label=\"Bericht voor collega's\"]").Change("Ik ben afwezig.");
        cut.Find("input[aria-label='Doorsturen naar']").Change("collega@test.be");
        cut.Find("input[aria-label='Doorsturen tot']").Change(DateTime.Today.AddDays(10).ToString("yyyy-MM-dd"));
        cut.FindAll("button").First(b => b.TextContent == "Afwezigheid plannen").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(State.Scheduled.Read(), a => a.Kind == ScheduledKind.Plan && a.Title.Contains("Einde doorsturen", StringComparison.Ordinal)));
        Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.ExchangeAutoReply && w.P("InternalMessage") == "Ik ben afwezig.");
    }

    [Fact]
    public void GroupsThroughOtherGroupsShowTheWay()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "an.peeters");
        var paths = HelpdeskTools.GroupsWithPaths(State.Data, an);
        var nested = Assert.Single(paths, p => p.Group.Ad?.SamAccountName == "gg_eigen");
        Assert.Single(nested.Via);
        Assert.Contains(paths, p => p.Via.Count == 0 && p.Group.Ad?.SamAccountName == "gg_wifiIntern");

        var cut = RenderComponent<NestedGroups>(p => p.Add(x => x.Person, an));
        Assert.Contains("via ", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMessageTraceShowsWhatWasNotDelivered()
    {
        var bo = State.Data.Persons.First(p => p.Ad?.SamAccountName == "bo.janssens");
        var cut = RenderComponent<MessageTraceCard>(p => p.Add(x => x.Person, bo));
        cut.FindAll("button").First(b => b.TextContent == "Opzoeken").Click();
        cut.WaitForAssertion(() => Assert.Contains("als spam tegengehouden", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("waarvan 1 niet afgeleverd", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(State.Database.ReadAccess(5), a => a.What == "Berichtspoor bekeken");
    }

    [Fact]
    public void StayingSignedInIsAChoiceThatCanBeUndone()
    {
        Assert.False(State.Settings.RememberSignIn);
        State.Settings.TokenCache!.Save(new byte[] { 1, 2, 3 });
        State.RememberSignIn(true);
        Assert.True(State.EffectiveSettings.RememberSignIn);
        Assert.NotNull(State.EffectiveSettings.TokenCache!.Load());
        State.RememberSignIn(false);
        Assert.Null(State.Settings.TokenCache.Load());
    }
}
