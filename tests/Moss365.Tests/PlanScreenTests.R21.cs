using Bunit;
using Moss365.Core.Changes;
using Moss365.UI.Pages;
using Xunit;

namespace Moss365.Tests;

/// <summary>R21: editing a user in sections, with more fields, checked while typing, as one plan with undo.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void AUserIsEditedInSectionsWithItsCurrentValues()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        var sections = cut.FindAll(".kb-sectie").Select(s => s.QuerySelector("h3")!.TextContent).ToList();
        Assert.Equal(new[] { "Identiteit", "Organisatie", "Contact en adres", "Toegang" }, sections);
        Assert.Contains("Personeelsnummer", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Gebruikslocatie", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Entra ID volgt na de synchronisatie", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".kb-sectie input")); // nothing is open until you choose a section
    }

    [Fact]
    public void ValuesAreCheckedWhileTypingAndTheUsageLocationGoesToEntra()
    {
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, "ad:cas.vos"));
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Contact en adres").QuerySelector("button")!.Click();

        // A usage location of one letter: explained at once, and no plan
        cut.Find("input[aria-label='Gebruikslocatie']").Input("b");
        Assert.Contains("Twee hoofdletters", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));

        // Two letters (made capital) and a company: the company in AD, the usage location in Entra (not synchronised)
        cut.Find("input[aria-label='Gebruikslocatie']").Input("be");
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Organisatie").QuerySelector("button")!.Click();
        cut.Find("input[aria-label='Bedrijf']").Input("Contoso Zorg");
        Assert.Contains("2 gewijzigd", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("nu: ", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();

        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("Bedrijf: Contoso Zorg", plan, StringComparison.Ordinal);
        Assert.Contains("Gebruikslocatie: BE", plan, StringComparison.Ordinal);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphSetAttributes && w.P("usageLocation") == "BE"));
        var ad = Assert.Single(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes);
        Assert.Equal("Contoso Zorg", ad.P("company"));
        Assert.False(ad.Parameters.ContainsKey("usageLocation"));
        Assert.True(ad.Previous!.ContainsKey("company")); // can be undone
    }

    [Fact]
    public void ADuplicateEmployeeIdIsAWarningNotABlock()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.EmployeeId), "1234567890123456"));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.EmployeeId), "12345678901234567"));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.Phone), "+32 2 abc"));
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.Phone), "+32 (0)2 123.45.67"));
        Assert.Null(AttributePlans.SameEmployeeId(State.Data, an, "999"));

        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, an.Key));
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Identiteit").QuerySelector("button")!.Click();
        cut.Find("input[aria-label='Personeelsnummer']").Input("E-1001");
        Assert.False(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));
    }
}

/// <summary>R21: a computer in AD, edited on the panel of the device.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheDetailsOfAComputerInAdAreAPlanWithUndo()
    {
        var pc = new Moss365.Core.Devices.AdComputer("PC-001", "CN=PC-001,OU=Werkplekken,OU=GEMEENTEBESTUUR,DC=contoso,DC=local", "Windows 11 Pro", "10.0 (26100)", DateTimeOffset.Now, true, null)
        {
            Description = "Oud toestel", Location = "",
        };
        var cut = RenderComponent<Moss365.UI.Components.ComputerEditor>(p => p.Add(x => x.Computer, pc));
        Assert.Contains("Oud toestel", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("GEMEENTEBESTUUR/Werkplekken", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("button").First(b => b.TextContent == "Bewerken").Click();
        cut.Find("input[aria-label='Omschrijving']").Input("Werkplek onthaal");
        cut.Find("input[aria-label='Locatie']").Input("Gelijkvloers");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes && w.Target == pc.DistinguishedName));
        var set = _factory.Executed.Single(w => w.Target == pc.DistinguishedName);
        Assert.Equal("Werkplek onthaal", set.P("description"));
        Assert.Equal("Gelijkvloers", set.P("location"));
        Assert.Equal("Oud toestel", set.Previous!["description"]);

        // A move to another OU is the last step
        var move = Moss365.Core.Devices.ComputerPlans.Edit(State.Data, State.Config, pc, new Moss365.Core.Devices.ComputerDesired(Ou: "GEMEENTEBESTUUR/Uit dienst"));
        var step = Assert.Single(move.Plan);
        Assert.Equal(ChangeAction.AdMove, step.ChangeAction);
        Assert.StartsWith("OU=Uit dienst,OU=GEMEENTEBESTUUR", step.P("doelOu"), StringComparison.Ordinal);
        Assert.Contains("niets gewijzigd", Assert.Single(Moss365.Core.Devices.ComputerPlans.Edit(State.Data, State.Config, pc, new Moss365.Core.Devices.ComputerDesired(Description: "Oud toestel")).Errors), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>R21: a guest, edited on its page and invited again.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void AGuestIsEditedAndInvitedAgain()
    {
        var guest = State.Data.Persons.First(p => p.IsGuest);
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, guest.Key));
        Assert.Contains("Gegevens van de gast", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Contact en adres", cut.Markup, StringComparison.Ordinal); // a guest has no user editor

        cut.Find("#gast").QuerySelectorAll("button").First(b => b.TextContent == "Bewerken").Click();
        cut.Find("input[aria-label='Bedrijf']").Input("Fabrikam");
        cut.Find("#gast").QuerySelectorAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphSetAttributes && w.Target == guest.Entra!.Id));
        var set = _factory.Executed.Single(w => w.Target == guest.Entra!.Id);
        Assert.Equal("Fabrikam", set.P("companyName"));
        Assert.True(set.Previous!.ContainsKey("companyName"));

        // Made member: with a warning on the plan
        var member = GuestPlans.Edit(guest, null, null, member: true);
        Assert.Equal("Member", Assert.Single(member.Plan).P("userType"));
        Assert.NotNull(member.Plan[0].Warning);

        // Invited again: the redemption is reset for the same account
        var again = Assert.Single(GuestPlans.InviteAgain(guest, message: false).Plan);
        Assert.Equal(ChangeAction.GraphInviteGuest, again.ChangeAction);
        Assert.Equal("True", again.P("opnieuw"));
        Assert.Equal(guest.Entra!.Id, again.P("id"));
    }
}

/// <summary>R21: the settings of groups: privacy of a Microsoft 365 group, mail settings of a mail group.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheSettingsOfAGroupArePlansWithUndo()
    {
        var exoInfo = new Moss365.Core.Inventory.ExoGroup { Type = "MailUniversalDistributionGroup", DisplayName = "Info", Smtp = "info@contoso.com", Hidden = false, OnlyInternalSenders = true, MemberJoinRestriction = "Closed" };
        var exoAd = exoInfo with { DisplayName = "Uit AD", Smtp = "ad@contoso.com", IsDirSynced = true };
        var data = new Moss365.Core.Inventory.InventoryData
        {
            CloudGroups = new Dictionary<string, Moss365.Core.Inventory.CloudGroup> { ["t1"] = new() { Id = "t1", DisplayName = "Team Onthaal", Type = Moss365.Core.Inventory.CloudGroupType.Microsoft365, Visibility = "Private" } },
            ExoGroups = new[] { exoInfo, exoAd },
        };
        var m365 = data.Index.Groups.Single(g => g.Name == "Team Onthaal");
        Assert.Equal((true, false), GroupPlans.SettingsOf(m365));
        var open = Assert.Single(GroupPlans.Settings(m365, new GroupSettingsDesired(Visibility: "Public")).Plan);
        Assert.Equal(ChangeAction.GraphSetGroupAttributes, open.ChangeAction);
        Assert.Equal("Public", open.P("visibility"));
        Assert.Equal("Private", open.Previous!["visibility"]);
        Assert.NotNull(open.Warning);
        Assert.NotEmpty(GroupPlans.Settings(m365, new GroupSettingsDesired(Visibility: "Iedereen")).Errors);

        var dl = data.Index.Groups.Single(g => g.Name == "Info");
        Assert.Equal((false, true), GroupPlans.SettingsOf(dl));
        var mail = Assert.Single(GroupPlans.Settings(dl, new GroupSettingsDesired(Hidden: true, OnlyInternalSenders: false, JoinRestriction: "ApprovalRequired")).Plan);
        Assert.Equal(ChangeAction.ExchangeUpdateGroup, mail.ChangeAction);
        Assert.Equal("info@contoso.com", mail.Target);
        Assert.Equal("True", mail.P("HiddenFromAddressListsEnabled"));
        Assert.Equal("False", mail.P("RequireSenderAuthenticationEnabled"));
        Assert.Equal("ApprovalRequired", mail.P("MemberJoinRestriction"));
        Assert.Equal("Closed", mail.Previous!["MemberJoinRestriction"]);
        Assert.Contains("niets gewijzigd", Assert.Single(GroupPlans.Settings(dl, new GroupSettingsDesired(Hidden: false)).Errors), StringComparison.Ordinal);

        // A mail group synchronised from AD changes in AD, not here
        var synced = data.Index.Groups.Single(g => g.Name == "Uit AD");
        Assert.Equal((false, false), GroupPlans.SettingsOf(synced));

        var cut = RenderComponent<Moss365.UI.Components.GroupSettingsEditor>(p => p.Add(x => x.Group, dl));
        Assert.Contains("Verborgen in het adresboek", cut.Markup, StringComparison.Ordinal);
        cut.FindAll("button").First(b => b.TextContent == "Bewerken").Click();
        cut.Find("input[aria-label='Verborgen in het adresboek']").Change(true);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Verborgen in het adresboek: ja", cut.Find("section.kb-plan").TextContent, StringComparison.Ordinal);
    }
}

/// <summary>R21: the copy of sent items and who sends on behalf of a shared mailbox.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheCopyOfSentItemsAndSendOnBehalfArePlansWithUndo()
    {
        var mailbox = new Moss365.Core.Inventory.Mailbox { Type = "SharedMailbox", DisplayName = "Info", Smtp = "info@contoso.com", SentItemsCopy = false, SendOnBehalf = new[] { "an@contoso.com" } };
        var outcome = MailboxPlans.Settings(mailbox, new MailboxDesired { SentItemsCopy = true, SendOnBehalf = new[] { "an@contoso.com", "bo@contoso.com" } });
        var step = Assert.Single(outcome.Plan);
        Assert.Equal(ChangeAction.ExchangeUpdateMailbox, step.ChangeAction);
        Assert.Equal("True", step.P("MessageCopyForSentAsEnabled"));
        Assert.Equal("True", step.P("MessageCopyForSendOnBehalfEnabled"));
        Assert.Equal("an@contoso.com|bo@contoso.com", step.P("GrantSendOnBehalfTo"));
        Assert.Equal("an@contoso.com", step.Previous!["GrantSendOnBehalfTo"]);
        Assert.Equal("False", step.Previous["MessageCopyForSentAsEnabled"]);

        // Nobody: an empty list; a name that is not an address is refused
        Assert.Equal(string.Empty, Assert.Single(MailboxPlans.Settings(mailbox, new MailboxDesired { SendOnBehalf = Array.Empty<string>() }).Plan).P("GrantSendOnBehalfTo"));
        Assert.NotEmpty(MailboxPlans.Settings(mailbox, new MailboxDesired { SendOnBehalf = new[] { "Jan" } }).Errors);

        // A list as Exchange names it (names, not addresses) cannot be put back: no previous value then
        var named = mailbox with { SendOnBehalf = new[] { "An Peeters" } };
        Assert.False(Assert.Single(MailboxPlans.Settings(named, new MailboxDesired { SendOnBehalf = new[] { "bo@contoso.com" } }).Plan).Previous!.ContainsKey("GrantSendOnBehalfTo"));
    }
}

/// <summary>R21: several users at once with the new fields, and the history of one field.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void SeveralUsersGetANewFieldInOnePlan()
    {
        var two = State.Data.Persons.Where(p => p.Ad is not null && !p.IsGuest).Take(2).ToList();
        var outcome = BulkPlans.Attribute(State.Data, State.Config, two, nameof(UserDesired.Company), "Contoso Zorg");
        Assert.Equal(2, outcome.Plan.Count(c => c.ChangeAction == ChangeAction.AdSetAttributes && c.P("company") == "Contoso Zorg"));

        // The usage location for several: written in Entra, checked like on the page of one user
        var location = BulkPlans.Attribute(State.Data, State.Config, two.Where(p => p.Entra is not null), nameof(UserDesired.UsageLocation), "BE");
        Assert.All(location.Plan, c => Assert.Equal(ChangeAction.GraphSetAttributes, c.ChangeAction));
        Assert.Empty(BulkPlans.Attribute(State.Data, State.Config, two, nameof(UserDesired.UsageLocation), "Belgie").Plan);

        // The last day for several (interns): the same file time for each, and a day as typed elsewhere is refused
        var expiry = BulkPlans.Attribute(State.Data, State.Config, two, nameof(UserDesired.AccountExpires), "2027-06-30");
        Assert.Equal(2, expiry.Plan.Count(c => c.P("accountExpires") == AttributePlans.ExpiryValue("2027-06-30")));
        Assert.Empty(BulkPlans.Attribute(State.Data, State.Config, two, nameof(UserDesired.AccountExpires), "30/06/2027").Plan);
    }

    [Fact]
    public void TheHistoryOfOneFieldLeavesOutFieldsReadForTheFirstTime()
    {
        var cas = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        Dictionary<string, List<Dictionary<string, string>>> Rows(params (string Key, string Value)[] values)
        {
            var row = new Dictionary<string, string> { ["SamAccountName"] = "cas.vos", ["Department"] = "Verkoop", ["Title"] = "Medewerker" };
            foreach (var (k, v) in values) row[k] = v;
            return new() { [Moss365.Core.Inventory.InventoryLoader.AdUsers] = new() { row } };
        }

        State.Database.SaveSnapshot(DateTimeOffset.Now.AddDays(-3), "test", Rows());
        State.Database.SaveSnapshot(DateTimeOffset.Now.AddDays(-2), "test", Rows(("Company", "Contoso")));
        State.Database.SaveSnapshot(DateTimeOffset.Now.AddDays(-1), "test", Rows(("Company", "Contoso"), ("Department", "Boekhouding")));

        var history = State.Timeline(cas).Where(t => t.What == "AD-account").ToList();
        Assert.Contains(history, t => t.Fields.SequenceEqual(new[] { "Department" }));
        Assert.DoesNotContain(history, t => t.Fields.Contains("Company"));

        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, cas.Key));
        cut.FindAll("button").First(b => b.TextContent == "Tijdlijn tonen").Click();
        cut.Find("select[aria-label='Veld']").Change("Department");
        Assert.Contains("Department: Verkoop → Boekhouding", cut.Markup, StringComparison.Ordinal);
    }
}

/// <summary>R21: the address and the equipment of a room, with undo.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheAddressAndEquipmentOfARoomAreAPlanWithUndo()
    {
        var room = new Moss365.Core.Inventory.Room { Smtp = "rm-onthaal@contoso.com", DisplayName = "Onthaal", Capacity = 6, City = "Gent", WheelchairAccessible = null };
        var plan = PlanBuilder.Room(room, new RoomDesired { City = "Brugge", Street = "Markt 1", VideoDevice = "Camera", WheelchairAccessible = true });
        var place = Assert.Single(plan, c => c.ChangeAction == ChangeAction.ExchangeRoomPlace);
        Assert.Equal("Brugge", place.P("City"));
        Assert.Equal("Markt 1", place.P("Street"));
        Assert.Equal("True", place.P("IsWheelChairAccessible"));
        Assert.Equal("Gent", place.Previous!["City"]);
        Assert.Equal(string.Empty, place.Previous["Street"]);
        Assert.False(place.Previous.ContainsKey("IsWheelChairAccessible")); // unknown before: cannot be put back
        var entry = new Moss365.Core.Changes.LogEntry(DateTimeOffset.Now, "admin", "test", "Exchange", nameof(ChangeAction.ExchangeRoomPlace), room.Smtp, room.DisplayName, "x",
            place.Parameters, nameof(ResultStatus.Succeeded), string.Empty, Previous: place.Previous);
        Assert.Null(Undo.WhyNot(entry));
    }
}

/// <summary>R21: the sign-in name (UPN), checked while typing and confirmed, as a plan with undo.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void ANewSignInNameIsCheckedConfirmedAndCanBeUndone()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var other = State.Data.Persons.First(p => p.Key != an.Key && p.Ad?.UserPrincipalName is { Length: > 0 });
        var domain = an.Ad!.UserPrincipalName[(an.Ad.UserPrincipalName.IndexOf('@') + 1)..];
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.Upn), "cas vos@" + domain));
        Assert.False(AttributePlans.ValidValue(nameof(UserDesired.Upn), "cas.vos@"));
        Assert.True(AttributePlans.ValidValue(nameof(UserDesired.Upn), "c.vos@" + domain));
        Assert.Equal(other.Name, AttributePlans.Taken(State.Data, an, nameof(UserDesired.Upn), other.Ad!.UserPrincipalName.ToUpperInvariant()));
        Assert.Contains(domain, AttributePlans.UpnDomains(State.Data));

        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, an.Key));
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Toegang").QuerySelector("button")!.Click();
        Button MakePlan() => new(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));

        // Taken by another person: refused while typing
        cut.Find("input[aria-label='Aanmeldnaam (UPN)']").Input(other.Ad.UserPrincipalName);
        Assert.Contains("heeft dit al", cut.Markup, StringComparison.Ordinal);
        Assert.True(MakePlan().Disabled);

        // A domain no account uses yet: only a warning, but the change of sign-in needs the confirmation first
        cut.Find("input[aria-label='Aanmeldnaam (UPN)']").Input("c.vos@nieuw.example.com");
        Assert.Contains("nieuw.example.com", cut.Markup, StringComparison.Ordinal);
        Assert.True(MakePlan().Disabled);
        cut.Find("input[aria-label='Aanmeldnaam (UPN)']").Input("c.vos@" + domain);
        Assert.True(MakePlan().Disabled);
        cut.Find("section.kb-bewerken input[type='checkbox']").Change(true);
        Assert.False(MakePlan().Disabled);
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();

        var plan = cut.Find("section.kb-plan").TextContent;
        Assert.Contains("meldt zich daarna aan met de nieuwe naam", plan, StringComparison.Ordinal);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes));
        var ad = Assert.Single(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes);
        Assert.Equal("c.vos@" + domain, ad.P("userPrincipalName"));
        Assert.Equal(an.Ad.UserPrincipalName, ad.Previous!["userPrincipalName"]); // can be undone
    }

    private readonly record struct Button(bool Disabled);
}

/// <summary>R21: the expiry date of an account and the other phones, on the page of the user.</summary>
public sealed partial class PlanScreenTests
{
    [Fact]
    public void TheExpiryDateIsAFileTimeInAdAndCanBeUndone()
    {
        var an = State.Data.Persons.First(p => p.Ad?.SamAccountName == "cas.vos");
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, an.Key));
        Assert.Contains("vervalt nooit", cut.Markup, StringComparison.Ordinal);
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Toegang").QuerySelector("button")!.Click();

        // A day in the past: a warning, not a block
        var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        cut.Find("input[aria-label='Account werkt tot en met']").Change(yesterday);
        Assert.Contains("ligt voor vandaag", cut.Markup, StringComparison.Ordinal);
        Assert.False(cut.FindAll("button").First(b => b.TextContent == "Plan maken").HasAttribute("disabled"));

        var day = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        cut.Find("input[aria-label='Account werkt tot en met']").Change(day);
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Contact en adres").QuerySelector("button")!.Click();
        cut.Find("input[aria-label='Telefoon thuis']").Input("03 000 00 09");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Assert.Contains("Account werkt tot en met: " + day, cut.Find("section.kb-plan").TextContent, StringComparison.Ordinal);
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes));
        var ad = Assert.Single(_factory.Executed, w => w.ChangeAction == ChangeAction.AdSetAttributes);
        Assert.Equal(AttributePlans.ExpiryValue(day), ad.P("accountExpires"));
        Assert.Equal("0", ad.Previous!["accountExpires"]); // undo: never again
        Assert.Equal("03 000 00 09", ad.P("homePhone"));
    }

    [Fact]
    public void ACloudAccountHasTheFaxButNotTheFieldsOnlyAdHas()
    {
        var cloud = State.Data.Persons.First(p => p.Ad is null && p.Entra is { IsGuest: false });
        var cut = RenderComponent<UserDetail>(p => p.Add(x => x.Key, cloud.Key));
        cut.FindAll(".kb-sectie").First(s => s.QuerySelector("h3")!.TextContent == "Contact en adres").QuerySelector("button")!.Click();
        Assert.Empty(cut.FindAll("input[aria-label='Telefoon thuis']"));
        Assert.Empty(cut.FindAll("input[aria-label='Account werkt tot en met']"));
        cut.Find("input[aria-label='Fax']").Input("03 000 00 08");
        cut.FindAll("button").First(b => b.TextContent == "Plan maken").Click();
        Confirm(cut);
        cut.WaitForAssertion(() => Assert.Contains(_factory.Executed, w => w.ChangeAction == ChangeAction.GraphSetAttributes && w.P("faxNumber") == "03 000 00 08"));
    }
}
