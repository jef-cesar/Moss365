using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

/// <summary>Core for 0.7: signing in with the account of a shared mailbox.</summary>
public sealed class Version07Tests
{
    /// <summary>The test environment with two shared mailboxes: a cloud account (Youth) and an account from AD
    /// (Culture, disabled).</summary>
    public static InventoryData WithMailboxAccounts() => InventoryLoader.Build(MailboxFiles());

    public static Dictionary<string, List<Dictionary<string, string>>> MailboxFiles()
    {
        var b = TestData.Files();
        b[InventoryLoader.ExoMailboxes].Add(TestData.Mailbox("Jeugd", "jeugd@test.be"));
        b[InventoryLoader.ExoMailboxes].Add(TestData.Mailbox("Cultuur", "cultuur@test.be"));
        b[InventoryLoader.EntraUsers].Add(TestData.EntraUser("e-jeugd", "jeugd@test.be", displayName: "Jeugd"));
        b[InventoryLoader.AdUsers].Add(TestData.AdUser("postvak.cultuur", TestData.Municipality, "cultuur@test.be", enabled: false, lastLogon: ""));
        return b;
    }

    [Fact]
    public void AccountOfAMailbox()
    {
        var d = WithMailboxAccounts();
        var youth = d.Index.Mailbox("jeugd@test.be")!;
        Assert.Equal("e-jeugd", d.Index.AccountOf(youth.Smtp, youth.Alias)!.Entra!.Id);
        Assert.True(d.Index.IsMailboxAccount(d.Index.AccountOf(youth.Smtp)!));
        Assert.Equal("postvak.cultuur", d.Index.AccountOf("CULTUUR@test.be")!.Ad!.SamAccountName);
        Assert.Null(d.Index.AccountOf("bestaat.niet@test.be"));
    }

    [Fact]
    public void BlockSignInInEntraOrAd()
    {
        var d = WithMailboxAccounts();

        // Cloud account: in Entra, reversible via Previous
        var block = Assert.Single(MailboxPlans.SignIn(d, d.Index.Mailbox("jeugd@test.be")!, allow: false).Plan);
        Assert.Equal(ChangeAction.GraphSetAttributes, block.ChangeAction);
        Assert.Equal("e-jeugd", block.Target);
        Assert.Equal("false", block.Parameters["accountEnabled"]);
        Assert.Equal("true", block.Previous!["accountEnabled"]);
        Assert.Equal("kan aanmelden", block.CurrentValue);

        // Account from AD: in AD (Entra follows), allow with a warning
        var toValue = Assert.Single(MailboxPlans.SignIn(d, d.Index.Mailbox("cultuur@test.be")!, allow: true).Plan);
        Assert.Equal(ChangeAction.AdEnableAccount, toValue.ChangeAction);
        Assert.StartsWith("CN=postvak.cultuur,", toValue.Target);
        Assert.Contains("Microsoft raadt aan", toValue.Warning);
        Assert.Equal(ChangeAction.AdDisableAccount, Assert.Single(MailboxPlans.SignIn(d, d.Index.Mailbox("cultuur@test.be")!, false).Plan).ChangeAction);

        // Without an account: a clear error
        var info = MailboxPlans.SignIn(d, d.Index.Mailbox("info@test.be")!, false);
        Assert.Empty(info.Plan);
        Assert.Contains("niet in de geladen gegevens", Assert.Single(info.Errors));
    }
}
