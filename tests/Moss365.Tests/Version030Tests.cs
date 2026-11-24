using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Permissions;
using Xunit;

namespace Moss365.Tests;

/// <summary>0.30: the Conditional Access editor (every condition, a new policy, a copy, impact) and the roles mode.</summary>
public sealed class Version030Tests
{
    private const string Me = "an.peeters@test.be";

    private static CaPolicy WithJson(CaPolicy b, Action<JsonObject>? extra = null)
    {
        var node = JsonNode.Parse(CaJson.ToGraph(b))!.AsObject();
        node["id"] = b.Id;
        node["modifiedDateTime"] = "2026-02-01T00:00:00Z";
        extra?.Invoke(node);
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return CaJson.Read(doc.RootElement);
    }

    private static InventoryData Data()
    {
        var ca = TestData.Ca(DateTimeOffset.Now);
        return TestData.AppEnvironment() with
        {
            ConditionalAccess = ca with { Policies = ca.Policies.Select(p => WithJson(p)).ToList() },
            Security = new SecurityData(Array.Empty<MfaRegistration>(),
                new[] { new RoleMember("Global Administrator", "e-an", "an peeters", Me, "user", CaData.RoleGlobalAdmin) }),
        };
    }

    private static OrganisationConfig Config()
    {
        var c = TestData.Config();
        c.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        return c;
    }

    private static CaPolicy Policy(InventoryData d, string id) => d.ConditionalAccess!.Policies.Single(p => p.Id == id);

    [Fact]
    public void AnUnchangedPolicyGivesNoPatch()
    {
        var d = Data();
        foreach (var p in d.ConditionalAccess!.Policies)
        {
            Assert.Equal(string.Empty, CaEditor.Patch(p, CaEdit.Of(p)));
        }

        Assert.Equal("Er is niets gewijzigd.", Assert.Single(CaEditor.Update(Policy(d, "ca-mfa"), CaEdit.Of(Policy(d, "ca-mfa")), d, Config(), Me).Errors));
    }

    [Fact]
    public void OnlyTheChangedPartGoesToGraphWithTheOldValuesForUndo()
    {
        var d = Data();
        var mfa = Policy(d, "ca-mfa");
        var e = CaEdit.Of(mfa);
        e.PlatformsIn = new() { "android", "iOS" };
        e.FrequencyType = "hours";
        e.FrequencyValue = 12;

        var plan = CaEditor.Update(mfa, e, d, Config(), Me);
        var change = Assert.Single(plan.Plan);
        var patch = JsonNode.Parse(change.P("json"))!.AsObject();
        Assert.Equal(new[] { "conditions", "sessionControls" }, patch.Select(p => p.Key).OrderBy(k => k).ToArray());
        Assert.Equal(new[] { "platforms" }, patch["conditions"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal(12, patch["sessionControls"]!["signInFrequency"]!["value"]!.GetValue<int>());
        Assert.NotNull(change.Previous);
        Assert.Contains("platforms", change.Previous!["json"], StringComparison.Ordinal);
        Assert.Equal(ChangeAction.GraphUpdateCaPolicy, change.ChangeAction);

        var after = CaJson.AfterPatch(mfa, change.P("json"));
        Assert.Equal(new[] { "android", "iOS" }, after.PlatformsIn);
        Assert.Equal("12 uur", after.SignInFrequency);
    }

    [Fact]
    public void FieldsTheEditorDoesNotKnowStay()
    {
        var d = Data();
        var withContext = WithJson(new CaPolicy { Id = "ca-ctx", Name = "Context", Status = CaStatus.Report, UsersIn = new[] { "All" }, UsersOff = new[] { "e-info" }, AppsIn = Array.Empty<string>(), Checks = new[] { "mfa" } },
            n =>
            {
                n["conditions"]!["applications"]!["includeAuthenticationContextClassReferences"] = new JsonArray("c1");
                n["conditions"]!["servicePrincipalRiskLevels"] = new JsonArray("high");
            });
        var e = CaEdit.Of(withContext);
        Assert.Equal(CaAppsMode.Other, e.AppsMode);
        e.Checks = new() { "mfa", "compliantDevice" };

        var json = JsonNode.Parse(CaEditor.ToJson(e, withContext))!;
        Assert.Equal("c1", json["conditions"]!["applications"]!["includeAuthenticationContextClassReferences"]![0]!.GetValue<string>());
        Assert.Equal("high", json["conditions"]!["servicePrincipalRiskLevels"]![0]!.GetValue<string>());
        var patch = JsonNode.Parse(CaEditor.Patch(withContext, e))!.AsObject();
        Assert.Equal(new[] { "grantControls" }, patch.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void TheSafetyCheckStillStopsAnEnabledPolicyWithoutEmergencyAccounts()
    {
        var d = Data();
        var mfa = Policy(d, "ca-mfa");
        var e = CaEdit.Of(mfa);
        e.UsersOff.Remove("e-info");
        var outcome = CaEditor.Update(mfa, e, d, Config(), Me);
        Assert.Empty(outcome.Plan);
        Assert.Contains(outcome.Errors, f => f.Contains("noodaccount", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(1, CaEditor.ExcludeEmergencyAccounts(e, d, Config()));
        Assert.Equal("Er is niets gewijzigd.", Assert.Single(CaEditor.Update(mfa, e, d, Config(), Me).Errors)); // back as it was
    }

    [Fact]
    public void BlockingYourselfIsRefused()
    {
        var d = Data();
        var land = Policy(d, "ca-land");
        var e = CaEdit.Of(land);
        e.LocationsOff.Clear(); // all locations, including Belgium
        e.UsersOff.Add("e-info");
        var outcome = CaEditor.Update(land, e, d, Config(), Me);
        Assert.Empty(outcome.Plan);
        Assert.Contains(outcome.Errors, f => f.Contains("jouw eigen account", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewPolicyNeverStartsSwitchedOnAndIsCheckedFirst()
    {
        var d = Data();
        var e = CaEditor.New(d, Config());
        Assert.Contains("e-info", e.UsersOff);
        e.Name = "MFA voor iedereen"; // exists already
        Assert.Contains(CaEditor.Validate(e, d.ConditionalAccess), f => f.Contains("bestaat al", StringComparison.Ordinal));

        e.Name = "MFA buiten kantoor";
        e.LocationsIn = new() { "All" };
        e.LocationsOff = new() { "AllTrusted" };
        e.Status = CaStatus.On;
        Assert.Contains(CaEditor.Create(e, d, Config(), Me).Errors, f => f.Contains("alleen rapport", StringComparison.Ordinal));

        e.Status = CaStatus.Report;
        var change = Assert.Single(CaEditor.Create(e, d, Config(), Me).Plan);
        Assert.Equal(ChangeAction.GraphCreateCaPolicy, change.ChangeAction);
        var json = JsonNode.Parse(change.P("json"))!;
        Assert.Equal("enabledForReportingButNotEnforced", json["state"]!.GetValue<string>());
        Assert.Equal("AllTrusted", json["conditions"]!["locations"]!["excludeLocations"]![0]!.GetValue<string>());
        Assert.Null(CaSafety.Refusal(change, d, Config(), Me));
    }

    [Fact]
    public void ACopyStartsInReportOnlyWithAnotherName()
    {
        var d = Data();
        var copy = CaEditor.Duplicate(Policy(d, "ca-legacy"));
        Assert.Equal("Kopie van Verouderd blokkeren", copy.Name);
        Assert.Equal(CaStatus.Report, copy.Status);
        Assert.True(copy.Block);
        Assert.Equal(new[] { "exchangeActiveSync", "other" }, copy.ClientApps);
        Assert.Single(CaEditor.Create(copy, d, Config(), Me).Plan);
    }

    [Fact]
    public void ValidationExplainsWhatIsMissing()
    {
        var e = new CaEdit { Name = " ", UsersMode = CaUsersMode.Selected, AppsMode = CaAppsMode.Selected, Checks = new() { "mfa" }, StrengthId = "x", FrequencyType = "hours", FrequencyValue = 40 };
        var errors = CaEditor.Validate(e);
        Assert.Contains(errors, f => f.Contains("naam", StringComparison.Ordinal));
        Assert.Contains(errors, f => f.Contains("wie onder het beleid", StringComparison.Ordinal));
        Assert.Contains(errors, f => f.Contains("minstens één app", StringComparison.Ordinal));
        Assert.Contains(errors, f => f.Contains("authenticatiesterkte", StringComparison.Ordinal));
        Assert.Contains(errors, f => f.Contains("tussen 1 en 23", StringComparison.Ordinal));

        var nothing = new CaEdit { Name = "x", Checks = new() };
        Assert.Contains(CaEditor.Validate(nothing), f => f.Contains("Kies wat er gebeurt", StringComparison.Ordinal));
    }

    [Fact]
    public void TheImpactShowsWhoComesUnderThePolicyAndWhoNoLonger()
    {
        var d = Data();
        var mfa = Policy(d, "ca-mfa");
        var e = CaEdit.Of(mfa);
        e.GroupsOff.Clear(); // bo (member of g-eigen) comes under the policy again
        e.UsersOff.Add("e-cas"); // cas no longer
        var after = CaEditor.Preview(e, mfa);
        var (added, removed, count) = CaEditor.Impact(mfa, after, d);
        Assert.Contains(added, p => p.Entra?.Id == "e-bo");
        Assert.Contains(removed, p => p.Entra?.Id == "e-cas");
        Assert.True(count > 0);
        Assert.Contains(CaEditor.Differences(mfa, after, d), x => x.Contains("uitgezonderde groepen", StringComparison.Ordinal));
    }

    [Fact]
    public void GuestsAndRolesRoundTrip()
    {
        var d = Data();
        var e = CaEditor.New(d, Config());
        e.Name = "Gasten en beheerders";
        e.UsersMode = CaUsersMode.Selected;
        e.GuestsIn = true;
        e.RolesIn = new() { CaData.RoleGlobalAdmin };
        e.Block = false;
        e.Checks = new();
        e.StrengthId = CaData.StrengthPhishingResistant;
        var p = CaEditor.Preview(e, null);
        Assert.True(p.GuestsIn);
        Assert.Contains(CaData.RoleGlobalAdmin, p.RolesIn);
        Assert.Equal(CaData.StrengthPhishingResistant, p.StrengthId);
        var back = CaEdit.Of(p with { Json = CaEditor.ToJson(e) });
        Assert.Equal(CaUsersMode.Selected, back.UsersMode);
        Assert.True(back.GuestsIn);
    }

    [Fact]
    public void TheRolesModeDecidesWhatSomeoneWithoutARoleMay()
    {
        Assert.Equal(Role.Everything, PermissionTable.Determine(null, RolesMode.Off));
        Assert.Equal(Role.Everything, PermissionTable.Determine(new[] { "Moss365.Reader" }, RolesMode.Off));
        Assert.Equal(Role.Everything, PermissionTable.Determine(Array.Empty<string>(), RolesMode.Transition));
        Assert.Equal(Role.Reader, PermissionTable.Determine(Array.Empty<string>(), RolesMode.Required));
        Assert.Equal(Role.Reader, PermissionTable.Determine(new[] { "Moss365.Reader" }, RolesMode.Transition));

        var c = new OrganisationConfig { ProfilesRequired = true };
        Assert.Equal(RolesMode.Required, c.RolesModeEffective);
        c.SetRoles(RolesMode.Off);
        Assert.Equal(RolesMode.Off, c.RolesModeEffective);
        Assert.False(c.ProfilesRequired);
        var back = ConfigStorage.Parse(ConfigStorage.To(c));
        Assert.Equal(RolesMode.Off, back.RolesModeEffective);
    }
}
