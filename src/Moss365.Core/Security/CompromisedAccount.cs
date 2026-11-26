using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Security;

/// <summary>What to look at next to the plan of a compromised account (read only).</summary>
public sealed record CompromisedFindings(IReadOnlyList<string> MfaMethods, IReadOnlyList<string> AdminRoles, bool Synced);

/// <summary>
/// A compromised account (R16): one plan that blocks the account in AD and in Entra, sets a new random password in AD
/// (change at the next sign-in; the password is kept nowhere and shown nowhere), revokes every sign-in session and
/// removes the account from privileged AD groups. Nothing is deleted; you choose the steps in the preview, and blocking
/// and the groups can be undone from the change log.
/// </summary>
public static class CompromisedAccount
{
    public static PlanOutcome Plan(InventoryData data, Person person, string newPassword)
    {
        var plan = new List<Change>();
        var name = person.Name;
        if (person.Ad is { } ad)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdDisableAccount,
                Target = ad.DistinguishedName,
                TargetName = name,
                Description = Texts.T["code.plan-builder.disable-ad-account"],
                CurrentValue = ad.Enabled ? Texts.T["device.enabled"] : Texts.T["code.plan-builder.already-disabled"],
                NewValue = Texts.T["device.disabled"],
                Selected = ad.Enabled,
            });
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdSetPassword,
                Target = ad.DistinguishedName,
                TargetName = name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wijzigenBijAanmelding"] = "True" },
                Description = Texts.T["compromised.password"],
                Secret = newPassword,
            });
            foreach (var g in data.AdGroupsOf(person).Where(MirrorUser.IsPrivileged).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdRemoveGroupMember,
                    Target = ad.DistinguishedName,
                    TargetName = name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = g.DistinguishedName, ["groep"] = g.Name },
                    Description = Texts.T.Format("code.plan-builder.remove-from-ad-group", g.Name) + " " + Texts.T["code.plan-builder.privileged"],
                });
            }
        }

        if (person.Entra is { } entra)
        {
            // A synchronised account follows AD (blocked above, in Entra after the next synchronisation); a cloud account
            // is blocked in Entra itself
            if (!entra.OnPremisesSyncEnabled)
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetAttributes,
                    Target = entra.Id,
                    TargetName = name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = entra.AccountEnabled ? "true" : "false" },
                    Description = Texts.T["code.plan-builder.disable-cloud-account-block-sign"],
                    CurrentValue = entra.AccountEnabled ? Texts.T["device.enabled"] : Texts.T["code.plan-builder.already-disabled"],
                    NewValue = Texts.T["device.disabled"],
                    Selected = entra.AccountEnabled,
                });
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphRevokeSessions,
                Target = entra.Id,
                TargetName = name,
                Description = Texts.T["code.plan-builder.revoke-all-sign-in-sessions"],
            });
        }

        return PlanOutcome.Of(plan, plan.Count == 0 ? new[] { Texts.T["compromised.nothing"] } : Array.Empty<string>());
    }

    /// <summary>The MFA methods and admin roles of the person, from what was read: to check after blocking.</summary>
    public static CompromisedFindings Findings(InventoryData data, Person person)
    {
        var methods = data.Security?.MfaOf(person.Entra?.Id)?.Methods.Where(m => !m.Equals("password", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();
        var roles = data.Security?.RolesOf(person.Entra?.Id).Select(r => r.Role).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        return new CompromisedFindings(methods, roles, person.Entra?.OnPremisesSyncEnabled ?? false);
    }
}
