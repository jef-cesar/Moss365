using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Linking;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>G0, U33: the source of truth per tenant, and changes that go to the source.</summary>
public class SourceOfTruthTests
{
    private static readonly InventoryData Synced = new()
    {
        EntraUsers = new[] { new EntraUser { Id = "e1", OnPremisesSyncEnabled = true } },
        CloudGroups = new Dictionary<string, CloudGroup> { ["g1"] = new CloudGroup { Id = "g1", FromAd = true } },
    };

    private static IReadOnlyList<string> Text(IReadOnlyList<IReadOnlyList<Platform>> chains)
        => chains.Select(c => string.Join(" > ", c)).ToList();

    [Fact]
    public void AdAndEntraFormOneChainWhenEntraConnectSynchronises()
    {
        Assert.Equal(new[] { "ActiveDirectory > EntraId" }, Text(SourceOfTruth.Chains(TruthKind.Users, TenantSystems.Both, Synced, new SourceSettings())));
        Assert.Equal(new[] { "ActiveDirectory", "EntraId" }, Text(SourceOfTruth.Chains(TruthKind.Users, TenantSystems.Both, new InventoryData(), new SourceSettings())));
        Assert.Equal(Platform.ActiveDirectory, SourceOfTruth.FollowsFrom(Platform.EntraId, TruthKind.Users, TenantSystems.Both, Synced, new SourceSettings()));
        Assert.Null(SourceOfTruth.FollowsFrom(Platform.ActiveDirectory, TruthKind.Users, TenantSystems.Both, Synced, new SourceSettings()));
    }

    [Fact]
    public void GoogleFollowsWhatTheTenantSaysAndIsASourceUntilThen()
    {
        var all = TenantSystems.Both | TenantSystems.GoogleWorkspace;
        Assert.Equal(new[] { "ActiveDirectory > EntraId", "GoogleWorkspace" }, Text(SourceOfTruth.Chains(TruthKind.Users, all, Synced, new SourceSettings())));
        Assert.Equal(new[] { TruthKind.Users, TruthKind.Groups }, SourceOfTruth.OpenQuestions(all, new SourceSettings()));

        var fromEntra = new SourceSettings { GoogleUsers = GoogleFeed.FromEntra, GoogleGroups = GoogleFeed.Itself };
        Assert.Equal(new[] { "ActiveDirectory > EntraId > GoogleWorkspace" }, Text(SourceOfTruth.Chains(TruthKind.Users, all, Synced, fromEntra)));
        Assert.Equal(new[] { "ActiveDirectory > EntraId", "GoogleWorkspace" }, Text(SourceOfTruth.Chains(TruthKind.Groups, all, Synced, fromEntra)));
        Assert.Equal(Platform.ActiveDirectory, SourceOfTruth.FollowsFrom(Platform.GoogleWorkspace, TruthKind.Users, all, Synced, fromEntra));
        Assert.Empty(SourceOfTruth.OpenQuestions(all, fromEntra));

        // Directory Sync from AD without Microsoft 365
        var adGoogle = TenantSystems.ActiveDirectory | TenantSystems.GoogleWorkspace;
        Assert.Equal(new[] { "ActiveDirectory > GoogleWorkspace" }, Text(SourceOfTruth.Chains(TruthKind.Users, adGoogle, new InventoryData(), new SourceSettings { GoogleUsers = GoogleFeed.FromAd })));
        Assert.DoesNotContain(GoogleFeed.FromEntra, SourceOfTruth.Feeds(adGoogle));
        // From Entra while the tenant has no Microsoft 365 cannot be: Google stays a source
        Assert.Equal(new[] { "ActiveDirectory", "GoogleWorkspace" }, Text(SourceOfTruth.Chains(TruthKind.Users, adGoogle, new InventoryData(), new SourceSettings { GoogleUsers = GoogleFeed.FromEntra })));
        Assert.Empty(SourceOfTruth.OpenQuestions(TenantSystems.GoogleWorkspace, new SourceSettings()));
    }

    [Fact]
    public void AChangeToASynchronisedAccountIsWrittenInAdNeverInEntra()
    {
        var person = new Person
        {
            Key = "ad:an",
            Ad = new AdUser { SamAccountName = "an", DisplayName = "An", Department = "Verkoop", DistinguishedName = "CN=An,OU=Users,DC=contoso,DC=com", Enabled = true },
            Entra = new EntraUser { Id = "e-an", DisplayName = "An", Department = "Verkoop", OnPremisesSyncEnabled = true },
        };
        var data = new InventoryData { AdUsers = new[] { person.Ad }, EntraUsers = new[] { person.Entra }, Persons = new[] { person } };
        var desired = UserDesired.Of(person);
        desired.Department = "Boekhouding";

        var outcome = AttributePlans.User(data, new OrganisationConfig(), person, desired);

        Assert.NotEmpty(outcome.Plan);
        Assert.All(outcome.Plan, c => Assert.Equal(TargetSystem.ActiveDirectory, c.TargetSystem));
        Assert.Equal(Platform.ActiveDirectory, SourceOfTruth.FollowsFrom(Platform.EntraId, TruthKind.Users, TenantSystems.Both, data, new SourceSettings()));
    }
}
