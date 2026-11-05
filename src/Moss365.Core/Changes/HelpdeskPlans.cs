using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

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
            errors.Add($"Het wachtwoord moet minstens {MinimumLength} tekens hebben.");
        }

        if (password.Trim() != password)
        {
            errors.Add("Het wachtwoord begint of eindigt met een spatie.");
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
                Description = "Nieuw wachtwoord instellen" + (changeAtSignIn ? " (wijzigen bij de volgende aanmelding)" : string.Empty),
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
        Description = "Account ontgrendelen",
        CurrentValue = ad.LockedOut ? "vergrendeld" : "niet vergrendeld (volgens de geladen gegevens)",
        NewValue = "ontgrendeld",
    };

    private static Change Enable(Person p, AdUser ad, bool selected) => new()
    {
        TargetSystem = TargetSystem.ActiveDirectory,
        ChangeAction = ChangeAction.AdEnableAccount,
        Target = ad.DistinguishedName,
        TargetName = p.Name,
        Description = "AD-account inschakelen",
        CurrentValue = ad.Enabled ? "al ingeschakeld" : "uitgeschakeld",
        NewValue = "ingeschakeld",
        Warning = ad.OuPath.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            ? "Dit account staat in de OU van uitgestroomde gebruikers. Is de persoon terug in dienst?"
            : null,
        Selected = selected,
    };

    private static string? Warning(InventoryData d, Person p)
        => d.AdGroupsOf(p).Where(MirrorUser.IsPrivileged).Select(g => g.Name).ToList() is { Count: > 0 } groups
            ? $"Bevoorrecht account (lid van {string.Join(", ", groups)}): controleer extra goed wie dit vraagt."
            : null;

    private static string NoAd(Person p)
        => p.Entra is not null
            ? $"{p.Name} heeft geen AD-account. Het wachtwoord van een cloudaccount stel je in via het Entra-portaal."
            : $"{p.Name} heeft geen AD-account.";
}
