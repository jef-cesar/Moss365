using System.Net.Mail;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>
/// Invites and deletes guest users (B2B). Deleting is only possible for guests: an employee goes through offboarding
/// and is never deleted. A deleted guest stays among the deleted users in Entra for 30 more days.
/// </summary>
public static class GuestPlans
{
    public static PlanOutcome Invite(InventoryData d, string address, string name, bool message)
    {
        var errors = new List<string>();
        address = address.Trim();
        if (!MailAddress.TryCreate(address, out var mail) || mail.Address != address)
        {
            errors.Add($"'{address}' is geen geldig e-mailadres.");
        }

        if (name.Trim().Length == 0)
        {
            errors.Add("Vul een naam in.");
        }

        // A guest has the UPN address_domain#EXT#@tenant; its mail is the address itself
        if (errors.Count == 0 && d.EntraUsers.FirstOrDefault(e => string.Equals(e.Mail, address, StringComparison.OrdinalIgnoreCase)
                                                                       || string.Equals(e.UserPrincipalName, address, StringComparison.OrdinalIgnoreCase)) is { } exists)
        {
            errors.Add($"{address} bestaat al in Entra ({exists.DisplayName}{(exists.IsGuest ? ", gast" : string.Empty)}).");
        }

        if (errors.Count > 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphInviteGuest,
                Target = address,
                TargetName = name.Trim(),
                Parameters = GroupPlans.P(("naam", name.Trim()), ("bericht", message ? "True" : "False")),
                Description = message ? "Gast uitnodigen (met uitnodiging per e-mail)" : "Gast uitnodigen (zonder e-mail; deel zelf de link https://myapps.microsoft.com)",
                NewValue = address,
            },
        });
    }

    public static PlanOutcome Delete(InventoryData d, IEnumerable<Person> guests)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var p in guests)
        {
            if (p.Entra is not { IsGuest: true } || p.Ad is not null)
            {
                errors.Add($"{p.Name} is geen gastaccount en wordt niet verwijderd (gebruik Uitstroom).");
                continue;
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphRemoveGuest,
                Target = p.Entra.Id,
                TargetName = p.Name,
                Description = "Gastaccount verwijderen (30 dagen terug te zetten in Entra)",
                CurrentValue = p.Entra.Mail.Length > 0 ? p.Entra.Mail : p.Entra.UserPrincipalName,
                NewValue = "(verwijderd)",
            });
        }

        return PlanOutcome.Of(plan, errors);
    }
}
