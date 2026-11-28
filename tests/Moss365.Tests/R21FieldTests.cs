using System.Globalization;
using Moss365.Connections.Ad;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Xunit;

namespace Moss365.Tests;

/// <summary>R21: the other phones and the expiry date of an account, read, stored and written as AD expects.</summary>
public sealed class R21FieldTests
{
    private static long Local(int y, int m, int d) => DateTime.SpecifyKind(new DateTime(y, m, d), DateTimeKind.Local).ToFileTime();

    [Fact]
    public void LdapReadsTheOtherPhonesAndTheExpiryDate()
    {
        var u = AdLdapMapper.ToUser(new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = new object?[] { "an.peeters" },
            ["homePhone"] = new object?[] { "03 000 00 09" },
            ["facsimileTelephoneNumber"] = new object?[] { "03 000 00 08" },
            ["ipPhone"] = new object?[] { "4711" },
            ["accountExpires"] = new object?[] { Local(2027, 1, 1) },
        });
        Assert.Equal("03 000 00 09", u.HomePhone);
        Assert.Equal("03 000 00 08", u.Fax);
        Assert.Equal("4711", u.IpPhone);
        Assert.Equal("2027-01-01 00:00:00", u.AccountExpires);
        Assert.Equal("2026-12-31", AttributePlans.ExpiryDay(u.AccountExpires)); // the end of the day before, as AD shows it
        foreach (var never in new object?[] { 0L, long.MaxValue })
        {
            var n = AdLdapMapper.ToUser(new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase) { ["accountExpires"] = new[] { never } });
            Assert.Equal(string.Empty, n.AccountExpires);
            Assert.Equal(string.Empty, AttributePlans.ExpiryDay(n.AccountExpires));
        }

        foreach (var name in new[] { "homePhone", "facsimileTelephoneNumber", "ipPhone", "accountExpires" })
        {
            Assert.Contains(name, AdLdapMapper.UserAttributes, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheLastDayBecomesTheStartOfTheNextDayAndNeverIsZero()
    {
        Assert.Equal(Local(2027, 1, 1).ToString(CultureInfo.InvariantCulture), AttributePlans.ExpiryValue("2026-12-31"));
        Assert.Equal("0", AttributePlans.ExpiryValue(string.Empty));
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.AccountExpires), "2026-12-31"));
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.AccountExpires), string.Empty));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.AccountExpires), "31/12/2026"));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.Upn), string.Empty)); // an account needs a sign-in name
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.DisplayName), " ".Trim()));
        Assert.True(AttributePlans.ExpiresBeforeToday(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Assert.False(AttributePlans.ExpiresBeforeToday(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void TheNewFieldsAreStoredAndReadBack()
    {
        var all = InventoryComponent.Ad | InventoryComponent.Entra | InventoryComponent.Rooms | InventoryComponent.Mailboxes;
        var d = InventoryLoader.Build(TestData.Files());
        var ad = d.AdUsers[0] with { HomePhone = "03 000 00 09", Fax = "03 000 00 08", IpPhone = "4711", AccountExpires = "2027-01-01 00:00:00" };
        var entra = d.EntraUsers[0] with { Fax = "03 000 00 07" };
        d = d with { AdUsers = d.AdUsers.Skip(1).Prepend(ad).ToList(), EntraUsers = d.EntraUsers.Skip(1).Prepend(entra).ToList() };

        var back = InventoryLoader.Build(Snapshots.Tables(InventoryWriter.Files(d, all)));
        var a = back.AdUsers.Single(u => u.SamAccountName == ad.SamAccountName);
        Assert.Equal(("03 000 00 09", "03 000 00 08", "4711", "2027-01-01 00:00:00"), (a.HomePhone, a.Fax, a.IpPhone, a.AccountExpires));
        Assert.Equal("03 000 00 07", back.EntraUsers.Single(u => u.Id == entra.Id).Fax);
    }

    [Fact]
    public void ThePrimaryAddressChangesAtTheSourceAndTheOldOneStaysAnAlias()
    {
        Assert.Equal(new[] { "SMTP:an.p@test.be", "smtp:an.peeters@test.be", "smtp:an@test.be", "X500:/o=Test/cn=an" },
            AttributePlans.WithPrimary(new[] { "SMTP:an.peeters@test.be", "smtp:an@test.be", "X500:/o=Test/cn=an" }, "an.p@test.be"));
        Assert.Equal(new[] { "SMTP:an@test.be", "smtp:an.peeters@test.be" }, AttributePlans.WithPrimary(new[] { "SMTP:an.peeters@test.be", "smtp:AN@test.be" }, "an@test.be"));

        var b = TestData.Files();
        b[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "an.peeters")["ProxyAddresses"] = "SMTP:an.peeters@test.be|smtp:an@test.be";
        var d = InventoryLoader.Build(b);
        var an = d.SearchPerson("ad:an.peeters")!;
        Assert.Equal("an.peeters@test.be", AttributePlans.PrimaryAddressOf(an));

        // From AD: mail and the whole list at once, with the list as it was to undo
        var w = Assert.Single(AttributePlans.User(d, TestData.Config(), an, new UserDesired { PrimaryAddress = "an.p@test.be" }).Plan);
        Assert.Equal(ChangeAction.AdSetProxyAddresses, w.ChangeAction);
        Assert.Equal("an.p@test.be", w.P("mail"));
        Assert.Equal("SMTP:an.p@test.be|smtp:an.peeters@test.be|smtp:an@test.be", w.P("proxyAddresses"));
        Assert.Equal("SMTP:an.peeters@test.be|smtp:an@test.be", w.Previous!["proxyAddresses"]);
        var log = new LogEntry(DateTimeOffset.Now, "beheerder", "test", w.TargetSystem.ToString(), w.ChangeAction.ToString(), w.Target, w.TargetName,
            w.Description, w.Parameters, "Succeeded", string.Empty, Previous: w.Previous);
        var back = Assert.Single(Undo.Plan(new[] { log }).Plan);
        Assert.Equal(("an.peeters@test.be", "SMTP:an.peeters@test.be|smtp:an@test.be"), (back.P("mail"), back.P("proxyAddresses")));

        // The address or alias of another person is refused; an alias of one's own becomes the primary address
        var bo = d.SearchPerson("ad:bo.janssens")!;
        Assert.Empty(AttributePlans.User(d, TestData.Config(), bo, new UserDesired { PrimaryAddress = "an@test.be" }).Plan);
        Assert.Empty(AttributePlans.User(d, TestData.Config(), bo, new UserDesired { PrimaryAddress = "an.peeters@test.be" }).Plan);
        Assert.Empty(AttributePlans.User(d, TestData.Config(), bo, new UserDesired { PrimaryAddress = string.Empty }).Plan);
        Assert.Single(AttributePlans.User(d, TestData.Config(), an, new UserDesired { PrimaryAddress = "an@test.be" }).Plan);

        // A cloud account: its mailbox in Exchange Online, found by the old address (which stays as an alias)
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        var x = Assert.Single(AttributePlans.User(d, TestData.Config(), info, new UserDesired { PrimaryAddress = "onthaal@test.be" }).Plan);
        Assert.Equal(ChangeAction.ExchangeUpdateMailbox, x.ChangeAction);
        Assert.Equal("info@test.onmicrosoft.com", x.Target);
        Assert.Equal("onthaal@test.be", x.P("WindowsEmailAddress"));
        Assert.Equal("info@test.onmicrosoft.com", x.Previous!["WindowsEmailAddress"]);
    }

    [Fact]
    public void MailContactsAreStoredAndReadAgainWithANewAddress()
    {
        var all = InventoryComponent.Ad | InventoryComponent.Entra | InventoryComponent.Rooms | InventoryComponent.Mailboxes;
        var d = InventoryLoader.Build(TestData.Files());
        var back = InventoryLoader.Build(Snapshots.Tables(InventoryWriter.Files(d, all)));
        Assert.Equal(d.Contacts, back.Contacts);
        Assert.Empty(d.WithoutMicrosoft365().Contacts);

        // Read again after a new external address: the alias finds the old entry, so there is no double
        var jan = d.Contacts.Single(c => c.Alias == "jan.extern");
        var renamed = jan with { External = "jan@contoso.com", Smtp = "jan@contoso.com" };
        var objects = new ExchangeObjects(Array.Empty<Room>(), new ExchangeData(Array.Empty<ExoGroup>(), Array.Empty<Mailbox>(), Array.Empty<MailboxPermission>(), new[] { renamed }), Array.Empty<string>());
        var after = ObjectRefresher.WithExchange(d, objects);
        Assert.Equal("jan@contoso.com", Assert.Single(after.Contacts, c => c.Alias == "jan.extern").Smtp);
        Assert.Equal(d.Contacts.Count, after.Contacts.Count);

        // Not found any more: gone; a reading without contacts (an older connection) keeps them
        Assert.DoesNotContain(ObjectRefresher.WithExchange(d, new ExchangeObjects(Array.Empty<Room>(), new ExchangeData(Array.Empty<ExoGroup>(), Array.Empty<Mailbox>(), Array.Empty<MailboxPermission>()), new[] { jan.Smtp })).Contacts, c => c.Alias == "jan.extern");
        Assert.Equal(d.Contacts.Count, LiveLoader.WithExchange(d, new ExchangeData(Array.Empty<ExoGroup>(), Array.Empty<Mailbox>(), Array.Empty<MailboxPermission>())).Contacts.Count);
    }

    [Fact]
    public void ExtensionAttributesAreReadStoredAndWrittenAtTheSource()
    {
        var u = AdLdapMapper.ToUser(new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["sAMAccountName"] = new object?[] { "an.peeters" },
            ["extensionAttribute3"] = new object?[] { "KOST-42" },
        });
        Assert.Equal(15, u.ExtensionAttributes.Count);
        Assert.Equal("KOST-42", u.ExtensionAttributes[2]);
        Assert.Empty(AdLdapMapper.ToUser(new Dictionary<string, object?[]>(StringComparer.OrdinalIgnoreCase)).ExtensionAttributes);

        // Stored and read back
        var all = InventoryComponent.Ad | InventoryComponent.Entra | InventoryComponent.Rooms | InventoryComponent.Mailboxes;
        var b = TestData.Files();
        b[InventoryLoader.AdUsers].Single(r => r["SamAccountName"] == "an.peeters")["extensionAttribute3"] = "KOST-42";
        b[InventoryLoader.EntraUsers].Single(r => r["Id"] == "e-info")["extensionAttribute15"] = "Onthaal";
        var d = InventoryLoader.Build(b);
        var back = InventoryLoader.Build(Snapshots.Tables(InventoryWriter.Files(d, all)));
        Assert.Equal("KOST-42", back.AdUsers.Single(x => x.SamAccountName == "an.peeters").ExtensionAttributes[2]);
        Assert.Equal("Onthaal", back.EntraUsers.Single(x => x.Id == "e-info").ExtensionAttributes[14]);

        // A synchronised account in AD, a cloud account in Entra (one nested object)
        var an = d.SearchPerson("ad:an.peeters")!;
        Assert.Equal("KOST-42", UserDesired.Of(an, d).ExtensionAttribute3);
        var ad = Assert.Single(AttributePlans.User(d, TestData.Config(), an, new UserDesired { ExtensionAttribute3 = "KOST-43" }).Plan);
        Assert.Equal((ChangeAction.AdSetAttributes, "KOST-43", "KOST-42"), (ad.ChangeAction, ad.P("extensionAttribute3"), ad.Previous!["extensionAttribute3"]));
        var info = d.Persons.Single(p => p.Entra?.Id == "e-info");
        var cloud = Assert.Single(AttributePlans.User(d, TestData.Config(), info, new UserDesired { ExtensionAttribute15 = string.Empty, ExtensionAttribute1 = "A" }).Plan);
        Assert.Equal(ChangeAction.GraphSetAttributes, cloud.ChangeAction);
        var json = System.Text.Json.JsonSerializer.Serialize(Moss365.Connections.Graph.GraphConnection.AttributesContent(cloud.Parameters));
        Assert.Equal("{\"onPremisesExtensionAttributes\":{\"extensionAttribute1\":\"A\",\"extensionAttribute15\":null}}", json);
        Assert.True(AttributePlans.HasExtensions(d, an));
    }
}
