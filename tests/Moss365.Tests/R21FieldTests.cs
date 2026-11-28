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
}
