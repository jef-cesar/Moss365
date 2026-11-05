using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Connections.Graph;
using Xunit;

namespace Moss365.Tests;

/// <summary>Conditional Access (0.6): reading from Graph, saving, explanation, scope, what if, check and
/// effect.</summary>
public sealed class ConditionalAccessTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kb-ca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static InventoryData Data(bool groupMembers = true) => TestData.AppEnvironment() with
    {
        ConditionalAccess = TestData.Ca(new DateTimeOffset(Now), groupMembers),
        Security = new SecurityData(
            new[]
            {
                new MfaRegistration("e-an", "an.peeters@test.be", true, true, new[] { "microsoftAuthenticatorPush" }, "push", true),
                new MfaRegistration("e-cas", "cas.vos@test.be", false, false, Array.Empty<string>(), "", false),
            },
            new[] { new RoleMember("Global Administrator", "e-an", "an peeters", "an.peeters@test.be", "user", CaData.RoleGlobalAdmin) }),
    };

    private static CaUser G(InventoryData d, string key) => CaUser.Of(d.SearchPerson(key)!, d)!;

    private static CaPolicy B(InventoryData d, string id) => d.ConditionalAccess!.Policies.Single(b => b.Id == id);

    // ---------------------------------------------------------------- reading and saving

    [Fact]
    public void ReadPolicyFromGraph()
    {
        var b = GraphConnection.ToCaPolicy(Json("""
            {"id":"p1","displayName":"MFA beheerders","state":"enabledForReportingButNotEnforced","modifiedDateTime":"2026-09-01T10:00:00Z",
             "conditions":{"clientAppTypes":["all"],
               "users":{"includeUsers":[],"excludeUsers":["u-nood"],"includeGroups":[],"excludeGroups":["g-x"],"includeRoles":["62e90394-69f5-4237-9190-012177145e10"],"excludeRoles":[],
                        "includeGuestsOrExternalUsers":null,"excludeGuestsOrExternalUsers":{"guestOrExternalUserTypes":"b2bCollaborationGuest"}},
               "applications":{"includeApplications":["MicrosoftAdminPortals"],"excludeApplications":[],"includeUserActions":[]},
               "platforms":null,"locations":{"includeLocations":["All"],"excludeLocations":["AllTrusted"]},
               "signInRiskLevels":[],"userRiskLevels":[],"authenticationFlows":{"transferMethods":"deviceCodeFlow"}},
             "grantControls":{"operator":"OR","builtInControls":[],"termsOfUse":[],"authenticationStrength":{"id":"00000000-0000-0000-0000-000000000004","displayName":"Phishing-resistant MFA"}},
             "sessionControls":{"signInFrequency":{"value":4,"type":"hours","isEnabled":true},"persistentBrowser":{"mode":"never","isEnabled":true}}}
            """));

        Assert.Equal(CaStatus.Report, b.Status);
        Assert.Equal(new[] { CaData.RoleGlobalAdmin }, b.RolesIn);
        Assert.Equal(new[] { "u-nood" }, b.UsersOff);
        Assert.True(b.GuestsOff);
        Assert.False(b.GuestsIn);
        Assert.Equal(new[] { "AllTrusted" }, b.LocationsOff);
        Assert.Equal(new[] { "deviceCodeFlow" }, b.Transfer);
        Assert.True(b.AsksMfa);
        Assert.True(b.AsksPhishingResistant);
        Assert.Equal("4 uur", b.SignInFrequency);
        Assert.Equal("nooit aangemeld blijven", b.PersistentBrowser);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), b.Changed);

        var ip = GraphConnection.ToLocation(Json("""{"@odata.type":"#microsoft.graph.ipNamedLocation","id":"l1","displayName":"Kantoor","isTrusted":true,"ipRanges":[{"cidrAddress":"203.0.113.0/24"}]}"""));
        Assert.True(ip.IsIp && ip.Trusted);
        Assert.Equal("203.0.113.0/24", Assert.Single(ip.Values));
        var country = GraphConnection.ToLocation(Json("""{"@odata.type":"#microsoft.graph.countryNamedLocation","id":"l2","displayName":"BE","countriesAndRegions":["BE","NL"],"includeUnknownCountriesAndRegions":false}"""));
        Assert.True(country.ContainsBelgium);
        Assert.False(country.IsIp);

        var a = GraphConnection.ToSignIn(Json("""{"createdDateTime":"2026-10-20T08:00:00Z","userId":"e-bo","status":{"errorCode":0},"appliedConditionalAccessPolicies":[{"id":"ca-mfa","displayName":"MFA","result":"success"},{"id":"ca-beheer","displayName":"B","result":"reportOnlyFailure"}]}"""));
        Assert.Equal(new[] { "success", "reportOnlyFailure" }, a.Ca!.Select(c => c.Result));
    }

    [Fact]
    public void SaveAndReadBack()
    {
        var d = Data();
        var folder = InventoryWriter.Save(d, _folder, InventoryComponent.Entra, Now);
        Assert.True(File.Exists(Path.Combine(folder, InventoryLoader.EntraCa)));

        var back = InventoryLoader.Load(new[] { folder });
        Assert.NotNull(back.ConditionalAccess);
        Assert.Equal(d.ConditionalAccess!.Policies.Select(b => b.Name), back.ConditionalAccess!.Policies.Select(b => b.Name));
        Assert.Equal(CaStatus.Report, back.ConditionalAccess.Policies.Single(b => b.Id == "ca-beheer").Status);
        Assert.Equal(new[] { "e-bo" }, back.ConditionalAccess.GroupMembers!["g-eigen"]);
        Assert.Contains(back.Sources, b => b.File == InventoryLoader.EntraCa && b.Rows == 4);
        Assert.Equal(CaData.RoleGlobalAdmin, back.Security!.Roles.Single().RoleTemplateId);

        // Broken file: simply not loaded
        File.WriteAllText(Path.Combine(folder, InventoryLoader.EntraCa), "{kapot");
        Assert.Null(InventoryLoader.Load(new[] { folder }).ConditionalAccess);
    }

    [Fact]
    public void NotReadableIsNotSaved()
    {
        var d = Data() with { ConditionalAccess = new CaData(Array.Empty<CaPolicy>(), Array.Empty<NamedLocation>(), Array.Empty<AuthenticationStrength>(), new Dictionary<string, string>(), null, "Niet leesbaar") };
        var folder = InventoryWriter.Save(d, _folder, InventoryComponent.Entra, Now);
        Assert.False(File.Exists(Path.Combine(folder, InventoryLoader.EntraCa)));
        Assert.Equal(CaVerdict.Problem, Assert.Single(CaCheck.Check(d, TestData.Config(), Now)).Verdict);
    }

    // ---------------------------------------------------------------- explanation and scope

    [Fact]
    public void ExplanationInPlainLanguage()
    {
        var d = Data();
        var name = CaReader.Names(d);
        var u = CaReader.Explanation(B(d, "ca-mfa"), d.ConditionalAccess!, name);
        Assert.Equal(new[] { "Alle gebruikers", "Behalve: Info", "Behalve leden van: WKG_Eigen" }, u.Who);
        Assert.Equal("Voor alle cloud-apps", Assert.Single(u.Apps));
        Assert.Equal("Toegang mits MFA", u.Consequence);

        var legacy = CaReader.Explanation(B(d, "ca-legacy"), d.ConditionalAccess!, name);
        Assert.Contains("Exchange ActiveSync", Assert.Single(legacy.Conditions));
        Assert.Equal("Toegang blokkeren", legacy.Consequence);

        var management = CaReader.Explanation(B(d, "ca-beheer"), d.ConditionalAccess!, name);
        Assert.Equal("Met de rol: Global Administrator", Assert.Single(management.Who));
        Assert.Equal("Toegang mits MFA met sterkte Phishingbestendige MFA", management.Consequence);

        var country = CaReader.Explanation(B(d, "ca-land"), d.ConditionalAccess!, name);
        Assert.Equal("Niet vanaf België", Assert.Single(country.Conditions));
    }

    [Fact]
    public void ScopePerUserWithExclusionsFirst()
    {
        var d = Data();
        var ca = d.ConditionalAccess!;
        var name = CaReader.Names(d);
        var mfa = B(d, "ca-mfa");

        Assert.Equal(CaApplies.Yes, CaReader.Scope(mfa, G(d, "ad:an.peeters"), name, ca).Applies);
        var bo = CaReader.Scope(mfa, G(d, "ad:bo.janssens"), name, ca);
        Assert.Equal(CaApplies.No, bo.Applies);
        Assert.Equal("uitgezonderd via de groep WKG_Eigen", bo.Reason);
        Assert.Equal("rechtstreeks uitgezonderd", CaReader.Scope(mfa, G(d, "entra:e-info"), name, ca).Reason);

        // Roles via the roleTemplateId
        var management = B(d, "ca-beheer");
        Assert.Equal("heeft de rol Global Administrator", CaReader.Scope(management, G(d, "ad:an.peeters"), name, ca).Reason);
        Assert.Equal(CaApplies.No, CaReader.Scope(management, G(d, "ad:cas.vos"), name, ca).Applies);

        // Without the full group members an excluded group is not certain
        var without = Data(groupMembers: false);
        Assert.Equal(CaApplies.Maybe, CaReader.Scope(mfa, G(without, "ad:an.peeters"), CaReader.Names(without), without.ConditionalAccess!).Applies);
        Assert.Equal(CaApplies.No, CaReader.Scope(mfa, G(without, "ad:bo.janssens"), CaReader.Names(without), without.ConditionalAccess!).Applies);

        // Who falls under it: everyone with Entra, except Info and Bo
        var under = CaReader.WhoFallsUnder(mfa, d).Select(x => x.Person.Entra!.Id).ToList();
        Assert.Contains("e-an", under);
        Assert.Contains("e-gast", under);
        Assert.DoesNotContain("e-bo", under);
        Assert.DoesNotContain("e-info", under);
        Assert.Null(CaUser.Of(d.SearchPerson("ad:dirk.intern")!, d));
    }

    // ---------------------------------------------------------------- what if

    [Fact]
    public void WhatIfScenario()
    {
        var d = Data();
        var ca = d.ConditionalAccess!;
        var name = CaReader.Names(d);
        var an = G(d, "ad:an.peeters");

        var home = CaReader.WhatIf(an, new CaScenario(CaApp.Office365, CaPlace.Belgium), ca, name);
        Assert.False(home.Blocked);
        Assert.Equal(new[] { "MFA" }, home.Requirements);
        Assert.Equal("Toegang mits MFA", home.Summary);
        Assert.Equal(CaApplies.No, home.Rules.Single(r => r.Policy.Id == "ca-land").Answer.Applies);
        Assert.Equal(CaApplies.Yes, Assert.Single(home.OnlyReport).Answer.Applies);

        var abroad = CaReader.WhatIf(an, new CaScenario(CaApp.Office365, CaPlace.Abroad), ca, name);
        Assert.True(abroad.Blocked);
        Assert.StartsWith("Geblokkeerd door Buitenland blokkeren", abroad.Summary);

        var imap = CaReader.WhatIf(an, new CaScenario(CaApp.Office365, CaPlace.Belgium, Client: CaClient.Outdated), ca, name);
        Assert.True(imap.Blocked);
        Assert.Equal(CaApplies.No, CaReader.WhatIf(an, new CaScenario(), ca, name).Rules.Single(r => r.Policy.Id == "ca-legacy").Answer.Applies);

        // Bo falls outside MFA: only a password
        var bo = CaReader.WhatIf(G(d, "ad:bo.janssens"), new CaScenario(), ca, name);
        Assert.Empty(bo.Requirements);
        Assert.StartsWith("Geen enkel beleid is zeker van toepassing", bo.Summary);
    }

    [Fact]
    public void WhatIfManagedDeviceAndLocations()
    {
        var d = Data();
        var baseline = d.ConditionalAccess!;
        var ca = baseline with
        {
            Policies = new[]
            {
                new CaPolicy
                {
                    Id = "x", Name = "MFA of toestel buiten kantoor", Status = CaStatus.On, UsersIn = new[] { "All" }, AppsIn = new[] { "Office365" },
                    LocationsIn = new[] { "All" }, LocationsOff = new[] { "AllTrusted" }, Checks = new[] { "mfa", "compliantDevice" },
                },
            },
        };
        var an = G(d, "ad:an.peeters");
        var name = CaReader.Names(d);

        Assert.Equal("Toegang mits MFA of een conform toestel (Intune)", CaReader.WhatIf(an, new CaScenario(), ca, name).Summary);
        Assert.Empty(CaReader.WhatIf(an, new CaScenario(ManagedDevice: true), ca, name).Requirements);
        Assert.Equal(CaApplies.No, CaReader.WhatIf(an, new CaScenario(Place: CaPlace.Office), ca, name).Rules.Single().Answer.Applies);
        Assert.Equal(CaApplies.No, CaReader.WhatIf(an, new CaScenario(App: CaApp.AdminPortals), ca, name).Rules.Single().Answer.Applies);
    }

    // ---------------------------------------------------------------- check and effect

    [Fact]
    public void CheckAgainstTheRecommendations()
    {
        var d = Data();
        var config = TestData.Config();
        config.EmergencyAccounts.Add("info@test.onmicrosoft.com");
        var findings = CaCheck.Check(d, config, Now);
        CaFinding About(string subject) => findings.First(b => b.Subject == subject);

        Assert.Equal(CaVerdict.InOrder, About("MFA voor iedereen").Verdict);
        Assert.Equal(CaVerdict.InOrder, About("Verouderde aanmelding geblokkeerd").Verdict);

        var outside = About("Medewerkers zonder MFA-beleid");
        Assert.Equal(CaVerdict.Attention, outside.Verdict);
        Assert.Equal(new[] { "e-bo" }, outside.Persons.Select(p => p.Entra!.Id));

        // Administrators: MFA via "everyone", the phishing-resistant policy is only in report
        Assert.Equal(CaVerdict.Attention, About("Beheerders").Verdict);
        var report = About("Lang op alleen rapport");
        Assert.Contains("60 dagen", report.Explanation);

        // Info is the emergency account, but falls under "Block abroad"
        var emergency = About("Noodaccount Info");
        Assert.Equal(CaVerdict.Problem, emergency.Verdict);
        Assert.Contains("Buitenland blokkeren", emergency.Explanation);
        Assert.Equal("ca-land", Assert.Single(emergency.Policies).Id);

        Assert.Equal(CaVerdict.Attention, About("Aanmelden met toestelcode").Verdict);
        Assert.Equal(CaVerdict.Attention, About("MFA registreren beschermd").Verdict);
        Assert.Equal(CaVerdict.InOrder, About("Gasten").Verdict);

        // Cas falls under MFA but has not registered anything yet
        Assert.Contains(About("MFA nog niet ingesteld").Persons, p => p.Entra!.Id == "e-cas");
    }

    [Fact]
    public void CheckWithoutEmergencyAccountsSuggestsCandidates()
    {
        var d = Data();
        var emergency = CaCheck.Check(d, TestData.Config(), Now).Single(b => b.Subject == "Noodaccounts");
        Assert.Equal(CaVerdict.Attention, emergency.Verdict);
        Assert.Contains("Organisatie > Noodaccounts", emergency.Advice);

        var empty = d with { ConditionalAccess = d.ConditionalAccess! with { Policies = Array.Empty<CaPolicy>() } };
        Assert.Contains(CaCheck.Check(empty, TestData.Config(), Now), b => b.Subject == "Bescherming" && b.Verdict == CaVerdict.Problem);
        Assert.Equal(CaVerdict.Info, Assert.Single(CaCheck.Check(d with { ConditionalAccess = null }, TestData.Config(), Now)).Verdict);
    }

    [Fact]
    public void EffectFromTheSignIns()
    {
        var d = Data();
        SignInEvent A(params CaApplication[] ca) => new(DateTimeOffset.Now, "e-an", "an", "An", "Office", "", "", 0, "", "Browser", ca);
        var effect = CaEffect.Compute(new[]
        {
            A(new CaApplication("ca-mfa", "MFA", "success"), new CaApplication("ca-beheer", "B", "reportOnlyFailure")),
            A(new CaApplication("ca-mfa", "MFA", "success"), new CaApplication("ca-beheer", "B", "reportOnlyInterrupted")),
            A(new CaApplication("ca-land", "L", "notApplied"), new CaApplication("onbekend", "?", "success")),
            new SignInEvent(DateTimeOffset.Now, "e-bo", "bo", "Bo", "Office", "", "", 0, "", "Browser"),
        }, d.ConditionalAccess!);

        var mfa = effect.Single(e => e.PolicyId == "ca-mfa");
        Assert.Equal(2, mfa.Applied);
        var management = effect.Single(e => e.PolicyId == "ca-beheer");
        Assert.Equal(2, management.WouldBlock);
        Assert.Equal(1, effect.Single(e => e.PolicyId == "ca-land").NotApplicable);
        Assert.Equal(0, effect.Single(e => e.PolicyId == "ca-legacy").Total);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
