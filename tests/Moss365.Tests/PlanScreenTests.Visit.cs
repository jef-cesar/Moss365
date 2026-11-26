using System.Text.Json;
using Bunit;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Security;
using Moss365.UI.Components;
using Moss365.UI.Pages;
using Moss365.UI.Services;
using Xunit;

namespace Moss365.Tests;

/// <summary>R18: since your last visit, what changed outside Moss365.</summary>
public sealed partial class PlanScreenTests
{
    private static AuditEvent Audit(string id, string activity, string category, string service, string targetId, string targetName, double hoursAgo = 1, string result = "success")
        => new(id, DateTimeOffset.Now.AddHours(-hoursAgo), activity, category, service, "iemand@test.be", targetId, targetName, result);

    private static LogEntry Own(ChangeAction action, string target, string name, params (string Key, string Value)[] parameters)
        => new(DateTimeOffset.Now.AddHours(-1), "beheerder@test.be", "test", "Entra (Graph)", action.ToString(), target, name, "x",
            parameters.ToDictionary(p => p.Key, p => p.Value), nameof(ResultStatus.Succeeded), string.Empty);

    [Fact]
    public void SinceTheLastVisitShowsWhatChangedOutsideMoss365()
    {
        var since = DateTimeOffset.Now.AddDays(-1);
        var data = State.Data;
        var an = data.AdUsers.First(u => u.SamAccountName == "an.peeters");
        var domainAdmins = an.MemberOr.First(dn => dn.StartsWith("CN=Domain Admins,", StringComparison.OrdinalIgnoreCase));
        var before = data with
        {
            AdUsers = data.AdUsers.Select(u => u.SamAccountName == an.SamAccountName ? u with { MemberOr = u.MemberOr.Where(dn => dn != domainAdmins).ToList() } : u).ToList(),
        };
        var audits = new[]
        {
            Audit("r1", "Add member to role", "RoleManagement", "Core Directory", "u-x", "Cas Vos"),
            Audit("c1", "Update conditional access policy", "Policy", "Conditional Access", "p1", "Blokkeer oude protocollen"),
            Audit("c2", "Add named location", "Policy", "Conditional Access", "l1", "Kantoor"),
            Audit("g1", "Consent to application", "ApplicationManagement", "Core Directory", "sp1", "Onbekende app"),
            Audit("u1", "Update user", "UserManagement", "Core Directory", "u1", "An"),
            Audit("f1", "Add member to role", "RoleManagement", "Core Directory", "u-y", "Mislukt", result: "failure"),
            Audit("o1", "Add member to role", "RoleManagement", "Core Directory", "u-z", "Te oud", hoursAgo: 48),
        };
        var signIns = LocalLogs.ToKeep(new[] { KeptSignIn("n1", "u-nood", "nood@test.be") }, NoOne, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nood@test.be" });
        var own = new[] { Own(ChangeAction.GraphUpdateCaPolicy, "p1", "Blokkeer oude protocollen") };

        var changes = SinceLastVisit.Compute(since, before, data, audits, signIns, own);

        Assert.Equal(5, changes.Count);
        Assert.Contains(changes, c => c.Kind == "admin" && c.Time is null && c.What.EndsWith("zit nu in Domain Admins", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Kind == "admin" && c.What == "Add member to role: Cas Vos" && c.Who == "iemand@test.be");
        Assert.Contains(changes, c => c.Kind == "policy" && c.What.Contains("Kantoor", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Kind == "consent" && c.What.Contains("Onbekende app", StringComparison.Ordinal));
        Assert.Contains(changes, c => c.Kind == "emergency" && c.What.StartsWith("nood@test.be meldde zich aan", StringComparison.Ordinal));
        Assert.DoesNotContain(changes, c => c.What.Contains("Blokkeer oude protocollen", StringComparison.Ordinal)); // done by Moss365

        // What Moss365 added to a privileged group itself is in the change log, not here; without AD in the snapshot of
        // then, AD is not compared at all
        var added = Own(ChangeAction.AdAddGroupMember, an.DistinguishedName, an.DisplayName, ("groepDn", domainAdmins));
        Assert.DoesNotContain(SinceLastVisit.Compute(since, before, data, audits, signIns, own.Append(added)), c => c.Time is null);
        Assert.DoesNotContain(SinceLastVisit.Compute(since, new InventoryData(), data, audits, signIns, own), c => c.Time is null);
    }

    [Fact]
    public void TheVisitIsMarkedOncePerSessionAndTheStartScreenShowsTheChanges()
    {
        var previous = DateTimeOffset.Now.AddDays(-2);
        State.Database.SaveDocument("laatste-bezoek", JsonSerializer.Serialize(new VisitRecord(previous)));
        State.Database.KeepAudits(new[] { Audit("r1", "Add member to role", "RoleManagement", "Core Directory", "u-x", "Cas Vos") });

        var start = RenderComponent<Start>();
        Assert.Equal(previous, State.PreviousVisit);
        var card = start.Find("#sinds-bezoek");
        Assert.Contains("Sinds je laatste bezoek", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("Add member to role: Cas Vos", card.TextContent, StringComparison.Ordinal);
        Assert.Contains("nieuwe beheerder", card.TextContent, StringComparison.Ordinal);

        // The visit of now is kept for the next session; in this session the one before stays
        var kept = JsonSerializer.Deserialize<VisitRecord>(State.Database.ReadDocument("laatste-bezoek")!)!;
        Assert.True(kept.Last > previous.AddDays(1));
        State.BeginVisit();
        Assert.Equal(previous, State.PreviousVisit);

        // Nothing at all: the card says so
        State.Database.RemoveLocalLogs(DateTimeOffset.Now.AddDays(1));
        Assert.Contains("Niets buiten Moss365 veranderd", RenderComponent<SinceLastVisitCard>().Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstVisitShowsNoCard()
    {
        var start = RenderComponent<Start>();
        Assert.Null(State.PreviousVisit);
        Assert.Empty(start.FindAll("#sinds-bezoek"));
        Assert.NotNull(State.Database.ReadDocument("laatste-bezoek"));
    }
}
