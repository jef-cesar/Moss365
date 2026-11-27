using Moss365.Core.Inventory;
using Moss365.Core.Linking;
using Xunit;

namespace Moss365.Tests;

/// <summary>G0, U32: linking persons over AD, Entra ID and Google Workspace.</summary>
public class PersonLinkTests
{
    private static Person AdEntra(string sam, string upn, string employee = "")
        => new() { Key = "ad:" + sam, Ad = new AdUser { SamAccountName = sam, UserPrincipalName = upn, EmailAddress = upn, DisplayName = sam, EmployeeId = employee, Enabled = true }, Entra = new EntraUser { Id = sam + "-id", UserPrincipalName = upn, Mail = upn, DisplayName = sam } };

    private static GoogleUser G(string id, string primary, string employee = "", params string[] aliases)
        => new() { Id = id, PrimaryEmail = primary, EmployeeId = employee, Aliases = aliases, Name = primary };

    [Fact]
    public void AGoogleAccountIsLinkedByAddressThenEmployeeIdThenAlias()
    {
        var persons = new[] { AdEntra("an", "an@contoso.com"), AdEntra("bo", "bo@contoso.com", "1002"), AdEntra("cas", "cas@contoso.com") };
        var google = new[] { G("g1", "an@contoso.com"), G("g2", "bo.peeters@contoso.org", "1002"), G("g3", "c@contoso.org", "", "cas@contoso.com") };

        var (linked, doubts) = PersonLinks.Attach(persons, google, new LinkSettings());

        Assert.Empty(doubts);
        Assert.Equal(3, linked.Count);
        Assert.Equal((LinkRule?)LinkRule.Address, linked.Single(p => p.Key == "ad:an").GoogleLink);
        Assert.Equal((LinkRule?)LinkRule.EmployeeId, linked.Single(p => p.Key == "ad:bo").GoogleLink);
        Assert.Equal((LinkRule?)LinkRule.Alias, linked.Single(p => p.Key == "ad:cas").GoogleLink);
        Assert.Equal("AD + Entra + Google", linked.Single(p => p.Key == "ad:an").Source);
    }

    [Fact]
    public void ALinkByHandWinsAndIsListedWhenARuleSaysOtherwise()
    {
        var persons = new[] { AdEntra("an", "an@contoso.com"), AdEntra("bo", "bo@contoso.com") };
        var google = new[] { G("g1", "an@contoso.com") };
        var settings = new LinkSettings { Links = new[] { new ManualLink("g1", "ad:bo", true, "admin", DateTimeOffset.Now) } };

        var (linked, doubts) = PersonLinks.Attach(persons, google, settings);

        Assert.Equal("g1", linked.Single(p => p.Key == "ad:bo").Google!.Id);
        Assert.Equal((LinkRule?)LinkRule.ByHand, linked.Single(p => p.Key == "ad:bo").GoogleLink);
        Assert.Null(linked.Single(p => p.Key == "ad:an").Google);
        Assert.Equal(DoubtKind.ContradictsRule, Assert.Single(doubts).Kind);
    }

    [Fact]
    public void ALinkUndoneByHandStaysUndoneAndTheAccountStandsAlone()
    {
        var persons = new[] { AdEntra("an", "an@contoso.com") };
        var google = new[] { G("g1", "an@contoso.com") };
        var undone = new LinkSettings
        {
            Links = new[]
            {
                new ManualLink("g1", "ad:an", true, "admin", DateTimeOffset.Now.AddDays(-1)),
                new ManualLink("g1", "ad:an", false, "admin", DateTimeOffset.Now),
            },
        };

        var (linked, doubts) = PersonLinks.Attach(persons, google, undone);

        Assert.Null(linked.Single(p => p.Key == "ad:an").Google);
        var alone = linked.Single(p => p.Key == "google:g1");
        Assert.Equal("Alleen Google", alone.Source);
        Assert.Equal(DoubtKind.NoCounterpart, Assert.Single(doubts).Kind);
    }

    [Fact]
    public void WhatFitsTwoWaysIsListedNotGuessed()
    {
        // Two persons with the same address (a shared mailbox address on two accounts): the Google account fits both
        var persons = new[] { AdEntra("an", "info@contoso.com"), AdEntra("bo", "info@contoso.com") };
        var (linked, doubts) = PersonLinks.Attach(persons, new[] { G("g1", "info@contoso.com") }, new LinkSettings());
        Assert.All(linked.Where(p => p.Ad is not null), p => Assert.Null(p.Google));
        Assert.Equal(DoubtKind.TwoCandidates, Assert.Single(doubts).Kind);

        // Two Google accounts for one person by the same rule: neither is linked
        var one = new[] { AdEntra("cas", "cas@contoso.com") };
        var (linked2, doubts2) = PersonLinks.Attach(one, new[] { G("g2", "x@contoso.org", "", "cas@contoso.com"), G("g3", "y@contoso.org", "", "cas@contoso.com") }, new LinkSettings());
        Assert.Null(linked2.Single(p => p.Key == "ad:cas").Google);
        Assert.Contains(doubts2, d => d.Kind == DoubtKind.TwoCandidates);

        // The stronger rule wins over a weaker one for the same person
        var (linked3, doubts3) = PersonLinks.Attach(one, new[] { G("g4", "x@contoso.org", "", "cas@contoso.com"), G("g5", "cas@contoso.com") }, new LinkSettings());
        Assert.Equal("g5", linked3.Single(p => p.Key == "ad:cas").Google!.Id);
        Assert.Contains(linked3, p => p.Key == "google:g4");
    }

    [Fact]
    public void AnAdAccountThatIsNotSynchronisedNextToACloudAccountWithItsAddressIsDoubtful()
    {
        var persons = new[]
        {
            new Person { Key = "ad:dirk", Ad = new AdUser { SamAccountName = "dirk", UserPrincipalName = "dirk@contoso.com", DisplayName = "Dirk" } },
            new Person { Key = "entra:x1", Entra = new EntraUser { Id = "x1", UserPrincipalName = "dirk@contoso.com", DisplayName = "Dirk (cloud)" } },
            new Person { Key = "entra:x2", Entra = new EntraUser { Id = "x2", UserPrincipalName = "gast_x#EXT#@contoso.onmicrosoft.com", Mail = "dirk@contoso.com", UserType = "Guest" } },
            AdEntra("an", "an@contoso.com"),
        };

        var doubt = Assert.Single(PersonLinks.AdEntraDoubts(persons));

        Assert.Equal(DoubtKind.SameAddressNotLinked, doubt.Kind);
        Assert.Equal(new[] { "ad:dirk", "entra:x1" }, doubt.Keys);
    }
}
