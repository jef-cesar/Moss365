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

    /// <summary>
    /// The details of a guest (R21): its name and company, and whether it counts as a member or a guest, as one change
    /// with the previous values (can be undone). A guest made member sees the directory as an employee does.
    /// </summary>
    public static PlanOutcome Edit(Person guest, string? name, string? company, bool? member)
    {
        if (guest.Entra is not { } e)
        {
            return PlanOutcome.Error(Texts.T["guest.edit.not-in-entra"]);
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = new List<string>();
        var becomes = new List<string>();
        void Field(string property, string label, string current, string? wanted)
        {
            if (wanted is null || string.Equals(current, wanted.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            parameters[property] = wanted.Trim();
            previous[property] = current;
            now.Add(label + ": " + (current.Length > 0 ? current : Texts.T["undo.empty"]));
            becomes.Add(label + ": " + (wanted.Trim().Length > 0 ? wanted.Trim() : Texts.T["undo.empty"]));
        }

        if (name is not null && name.Trim().Length == 0)
        {
            return PlanOutcome.Error(Texts.T["code.guest-plans.enter-a-name"]);
        }

        Field("displayName", Texts.T["attribute.displayname"], e.DisplayName, name);
        Field("companyName", Texts.T["attribute.company"], e.CompanyName, company);
        if (member is { } m)
        {
            Field("userType", Texts.T["guest.edit.kind"], e.UserType, m ? "Member" : "Guest");
        }

        if (parameters.Count == 0)
        {
            return PlanOutcome.Error(Texts.T["plan.nothing-changed"]);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphSetAttributes,
                Target = e.Id,
                TargetName = guest.Name,
                Parameters = parameters,
                Previous = previous,
                Description = Texts.T.Format("guest.edit.plan", parameters.Count),
                CurrentValue = string.Join("; ", now),
                NewValue = string.Join("; ", becomes),
                Warning = parameters.TryGetValue("userType", out var t) && t == "Member" ? Texts.T["guest.edit.member-warning"] : null,
            },
        });
    }

    /// <summary>
    /// Invites a guest again (R21): the redemption is reset, so the guest accepts a new invitation, for example with
    /// another account of the same address. The account and its groups stay; this cannot be undone.
    /// </summary>
    public static PlanOutcome InviteAgain(Person guest, bool message)
    {
        if (guest.Entra is not { IsGuest: true } e || e.Mail.Length == 0)
        {
            return PlanOutcome.Error(Texts.T["guest.again.no-address"]);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphInviteGuest,
                Target = e.Mail,
                TargetName = guest.Name,
                Parameters = GroupPlans.P(("naam", e.DisplayName), ("bericht", message ? "True" : "False"), ("opnieuw", "True"), ("id", e.Id)),
                Description = Texts.T["guest.again.plan"],
                NewValue = e.Mail,
            },
        });
    }
}
