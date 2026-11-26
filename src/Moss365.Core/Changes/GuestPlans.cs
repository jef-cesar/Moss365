using System.Net.Mail;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>
/// Invites and deletes guest users (B2B). Deleting is only possible for guests: an employee goes through offboarding
/// and is never deleted. A deleted guest stays among the deleted users in Entra for 30 more days.
/// </summary>
public static class GuestPlans
{
    public static PlanOutcome Invite(InventoryData d, string address, string name, bool message)
        => Invite(d, address, name, message, Array.Empty<Group>());

    /// <summary>
    /// Invites a guest and makes it a member of cloud groups in the same plan (0.31). AD groups cannot hold a guest;
    /// a mail group waits until Exchange knows the guest (add it later on the page of the guest); dynamic groups never.
    /// </summary>
    public static PlanOutcome Invite(InventoryData d, string address, string name, bool message, IEnumerable<Group> groups)
    {
        var errors = new List<string>();
        address = address.Trim();
        if (!MailAddress.TryCreate(address, out var mail) || mail.Address != address)
        {
            errors.Add(Texts.T.Format("code.helpdesk-tools.is-not-a-valid-e", address));
        }

        if (name.Trim().Length == 0)
        {
            errors.Add(Texts.T["code.guest-plans.enter-a-name"]);
        }

        // A guest has the UPN address_domain#EXT#@tenant; its mail is the address itself
        if (errors.Count == 0 && d.EntraUsers.FirstOrDefault(e => string.Equals(e.Mail, address, StringComparison.OrdinalIgnoreCase)
                                                                       || string.Equals(e.UserPrincipalName, address, StringComparison.OrdinalIgnoreCase)) is { } exists)
        {
            errors.Add(Texts.T.Format(exists.IsGuest ? "guest.exists.guest" : "guest.exists", address, exists.DisplayName));
        }

        if (errors.Count > 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        var invite = new Change
        {
            TargetSystem = TargetSystem.Graph,
            ChangeAction = ChangeAction.GraphInviteGuest,
            Target = address,
            TargetName = name.Trim(),
            Parameters = GroupPlans.P(("naam", name.Trim()), ("bericht", message ? "True" : "False")),
            Description = message ? Texts.T["code.guest-plans.invite-guest-with-an-invitation"] : "Gast uitnodigen (zonder e-mail; deel zelf de link https://myapps.microsoft.com)",
            NewValue = address,
        };
        var plan = new List<Change> { invite };
        var notes = new List<string>();
        foreach (var g in groups.DistinctBy(g => g.Key))
        {
            if (g.Dynamic)
            {
                notes.Add(Texts.T.Format("code.guest-plans.is-a-dynamic-group-the", g.Name));
            }
            else if (g.Ad is not null)
            {
                notes.Add(Texts.T.Format("code.guest-plans.is-an-ad-group-a", g.Name));
            }
            else if (g.Route == Analysis.WriteRoute.Exchange)
            {
                notes.Add(Texts.T.Format("code.guest-plans.is-a-mail-group-add", g.Name));
            }
            else if (g.Cloud is { } cloud)
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphAddGroupMember,
                    Target = address,
                    TargetName = name.Trim(),
                    Parameters = GroupPlans.P(("groepId", cloud.Id), ("groep", g.Name)),
                    Description = Texts.T.Format("code.plan-builder.make-member-of-cloud-group", g.Name),
                    DependentOf = invite.Id,
                });
            }
        }

        return PlanOutcome.Of(plan, notes);
    }

    public static PlanOutcome Delete(InventoryData d, IEnumerable<Person> guests)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var p in guests)
        {
            if (p.Entra is not { IsGuest: true } || p.Ad is not null)
            {
                errors.Add(Texts.T.Format("code.guest-plans.is-not-a-guest-account", p.Name));
                continue;
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphRemoveGuest,
                Target = p.Entra.Id,
                TargetName = p.Name,
                Description = Texts.T["code.guest-plans.remove-guest-account-can-be"],
                CurrentValue = p.Entra.Mail.Length > 0 ? p.Entra.Mail : p.Entra.UserPrincipalName,
                NewValue = Texts.T["code.guest-plans.removed"],
            });
        }

        return PlanOutcome.Of(plan, errors);
    }
}
