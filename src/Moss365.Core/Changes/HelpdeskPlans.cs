using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>
/// Daily helpdesk actions on an AD account: reset the password, unlock and enable again. The password is only in
/// <see cref="Change.Secret"/> and never goes to the change log.
/// </summary>
public static class HelpdeskPlans
{
    public const int MinimumLength = 12;

    public static PlanOutcome ConfigurePassword(InventoryData d, Person p, string password, bool changeAtSignIn, bool unlock)
    {
        if (p.Ad is not { } ad)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { NoAd(p) });
        }

        var errors = new List<string>();
        if (password.Length < MinimumLength)
        {
            errors.Add(Texts.T.Format("code.helpdesk-plans.the-password-must-have-at", MinimumLength));
        }

        if (password.Trim() != password)
        {
            errors.Add(Texts.T["code.helpdesk-plans.the-password-starts-or-ends"]);
        }

        if (errors.Count > 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        var plan = new List<Change>
        {
            new()
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdSetPassword,
                Target = ad.DistinguishedName,
                TargetName = p.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wijzigenBijAanmelding"] = changeAtSignIn ? "True" : "False" },
                Description = Texts.T.Format("code.helpdesk-plans.set-a-new-password", (changeAtSignIn ? (" " + Texts.T["code.helpdesk-plans.change-at-the-next-sign"]) : string.Empty)),
                Secret = password,
                Warning = Warning(d, p),
            },
        };

        if (unlock)
        {
            plan.Add(Unlock(p, ad));
        }

        if (!ad.Enabled)
        {
            plan.Add(Enable(p, ad, selected: false));
        }

        return PlanOutcome.Of(plan, Array.Empty<string>());
    }

    public static PlanOutcome Unlock(Person p)
        => p.Ad is { } ad
            ? PlanOutcome.Of(new[] { Unlock(p, ad) }, Array.Empty<string>())
            : PlanOutcome.Of(Array.Empty<Change>(), new[] { NoAd(p) });

    public static PlanOutcome Enable(Person p)
        => p.Ad is { } ad
            ? PlanOutcome.Of(new[] { Enable(p, ad, selected: true) }, Array.Empty<string>())
            : PlanOutcome.Of(Array.Empty<Change>(), new[] { NoAd(p) });

    private static Change Unlock(Person p, AdUser ad) => new()
    {
        TargetSystem = TargetSystem.ActiveDirectory,
        ChangeAction = ChangeAction.AdUnlockAccount,
        Target = ad.DistinguishedName,
        TargetName = p.Name,
        Description = Texts.T["code.helpdesk-plans.unlock-account"],
        CurrentValue = ad.LockedOut ? "vergrendeld" : Texts.T["code.helpdesk-plans.not-locked-according-to-the"],
        NewValue = "ontgrendeld",
    };

    private static Change Enable(Person p, AdUser ad, bool selected) => new()
    {
        TargetSystem = TargetSystem.ActiveDirectory,
        ChangeAction = ChangeAction.AdEnableAccount,
        Target = ad.DistinguishedName,
        TargetName = p.Name,
        Description = Texts.T["code.helpdesk-plans.enable-ad-account"],
        CurrentValue = ad.Enabled ? Texts.T["code.helpdesk-plans.already-enabled"] : "uitgeschakeld",
        NewValue = "ingeschakeld",
        Warning = ad.OuPath.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            ? Texts.T["code.helpdesk-plans.this-account-is-in-the"]
            : null,
        Selected = selected,
    };

    private static string? Warning(InventoryData d, Person p)
        => d.AdGroupsOf(p).Where(MirrorUser.IsPrivileged).Select(g => g.Name).ToList() is { Count: > 0 } groups
            ? Texts.T.Format("helpdesk.privileged", string.Join(", ", groups))
            : null;

    private static string NoAd(Person p)
        => p.Entra is not null
            ? Texts.T.Format("code.helpdesk-plans.has-no-ad-account-you", p.Name)
            : Texts.T.Format("code.helpdesk-plans.has-no-ad-account", p.Name);
}
