using Moss365.Core.Storage;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>
/// Tasks for a selection from a list (0.8, #10): always one plan with preview and confirmation. What is not possible
/// for one object (already a member, no access group, dynamic or privileged group) goes to the errors; the rest goes
/// ahead.
/// </summary>
public static class BulkPlans
{
    /// <summary>From this many changes on, the plan asks for an extra confirmation with the count.</summary>
    public const int LargePlan = 50;

    /// <summary>Makes the selected users members of a group (or removes them).</summary>
    public static PlanOutcome UsersInGroup(InventoryData data, IEnumerable<Person> persons, Group groupValue, bool add)
        => Together(persons.Select(p =>
        {
            var r = data.Index.Ref(p);
            var member = data.Index.UniqueMembersOf(groupValue).Any(l => l.Object.Kind == r.Kind && string.Equals(l.Object.Key, r.Key, StringComparison.OrdinalIgnoreCase));
            if (add == member)
            {
                return PlanOutcome.Error(member ? Texts.T.Format("code.bulk-plans.is-already-a-member-of", p.Name, groupValue.Name) : Texts.T.Format("code.bulk-plans.is-not-a-member-of", p.Name, groupValue.Name));
            }

            return add ? GroupPlans.AddMember(data, groupValue, r) : GroupPlans.RemoveMember(data, groupValue, r);
        }));

    /// <summary>Sets one attribute (e.g. office or department) to the same value for the selected users.</summary>
    public static PlanOutcome Attribute(InventoryData data, OrganisationConfig config, IEnumerable<Person> persons, string field, string value)
    {
        var property = typeof(UserDesired).GetProperty(LegacyNames.CurrentProperty(field)) ?? throw new ArgumentException(Texts.T.Format("code.bulk-plans.unknown-field", field), nameof(field));
        return Together(persons.Select(p =>
        {
            var g = new UserDesired();
            property.SetValue(g, value);
            var u = AttributePlans.User(data, config, p, g);
            return u.Plan.Count == 0 ? PlanOutcome.Error($"{p.Name}: {string.Join(" ", u.Errors)}") : u;
        }));
    }

    /// <summary>Makes one user a member of all selected groups.</summary>
    public static PlanOutcome UserInGroups(InventoryData data, IEnumerable<Group> groups, Person person)
        => Together(groups.Select(g => UsersInGroup(data, new[] { person }, g, add: true)));

    /// <summary>Gives someone access to all selected shared mailboxes, via their access group SG-SM-...-Full.</summary>
    public static PlanOutcome AccessToMailboxes(InventoryData data, IEnumerable<Mailbox> mailboxes, Person person)
        => Together(mailboxes.Select(p => data.Index.AccessGroupOf(p) is { } g
            ? UsersInGroup(data, new[] { person }, g, add: true)
            : PlanOutcome.Error(Texts.T.Format("code.bulk-plans.has-no-access-group-yet", p.DisplayName))));

    /// <summary>Hides the selected mailboxes in the address list (or shows them again).</summary>
    public static PlanOutcome Hide(IEnumerable<Mailbox> mailboxes, bool hide)
        => Together(mailboxes.Select(p => p.Hidden == hide
            ? PlanOutcome.Error(Texts.T.Format(hide ? "bulk.already-hidden" : "bulk.already-visible", p.DisplayName))
            : MailboxPlans.Settings(p, new MailboxDesired { Hidden = hide })));

    /// <summary>Blocks signing in with the account of the selected mailboxes (a mailbox needs no sign-in).</summary>
    public static PlanOutcome BlockSignIn(InventoryData data, IEnumerable<Mailbox> mailboxes)
        => Together(mailboxes.Select(p => data.Index.AccountOf(p.Smtp, p.Alias) is { Enabled: true }
            ? MailboxPlans.SignIn(data, p, allow: false)
            : PlanOutcome.Error(Texts.T.Format("code.bulk-plans.sign-in-is-already-blocked", p.DisplayName))));

    /// <summary>Makes one person the manager (AD, Exchange) or owner (cloud) of all selected groups.</summary>
    public static PlanOutcome AdministratorForGroups(InventoryData data, IEnumerable<Group> groups, Person person)
        => Together(groups.Select(g => GroupPlans.Administrator(data, g, data.Index.Ref(person), add: true)));

    /// <summary>
    /// Schedules the offboarding for the selected users: one scheduled action per person, with the default options and
    /// the chosen date. Nothing happens by itself: at the moment itself every plan is ready in Scheduled, with
    /// confirmation.
    /// </summary>
    public static IReadOnlyList<ScheduledAction> ScheduleOffboarding(IEnumerable<Person> persons, DateTimeOffset whenValue, string byValue)
        => persons.Where(p => !p.IsGuest)
            .Select(p => ScheduledPlans.Offboarding(p, new OffboardingOptions { Date = DateOnly.FromDateTime(whenValue.LocalDateTime) }, whenValue, byValue))
            .ToList();

    private static PlanOutcome Together(IEnumerable<PlanOutcome> outcomes)
    {
        var list = outcomes.ToList();
        return new PlanOutcome(list.SelectMany(u => u.Plan).ToList(), list.SelectMany(u => u.Errors).ToList());
    }
}
