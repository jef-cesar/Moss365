using System.Globalization;
using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

public enum CleanupKind
{
    /// <summary>Active employees who have not signed in for a long time: disable (never delete).</summary>
    Staff,

    /// <summary>Guests who have not signed in for a long time: delete (can be restored in Entra for 30 days).</summary>
    Guests,
}

/// <summary>An account that qualifies for the cleanup round.</summary>
public sealed record CleanupCandidate(Person Person, DateTime? LastSignIn, DateTime? Created, bool Privileged)
{
    public string Reason => LastSignIn is { } l ? Texts.T.Format("code.cleanup.last-signed-in", l) : Texts.T["kpi.aanmelding:*"];
}

/// <summary>
/// Periodic cleanup: you tick who has not signed in for a long time and the app creates one plan. Employees are
/// disabled with the date in the description (as with offboarding: never delete), guests are deleted. A report with all
/// checked accounts and the decision serves as proof of the review.
/// </summary>
public static class CleanupPlans
{
    public static IReadOnlyList<CleanupCandidate> Candidates(InventoryData d, OrganisationConfig config, CleanupKind kind, int days, DateTime now)
    {
        var limit = now.AddDays(-days);
        var offboardingOu = config.Offboarding?.Ou;
        return d.Persons
            .Where(p => kind == CleanupKind.Guests ? p.IsGuest : !p.IsGuest && !d.Index.IsMailboxAccount(p))
            .Where(p => p.Enabled || kind == CleanupKind.Guests)
            .Where(p => string.IsNullOrWhiteSpace(offboardingOu) || p.Ad is null || !DnHelp.FallsUnder(p.Ad.OuPath, new[] { offboardingOu }))
            .Select(p => new CleanupCandidate(p, Statistic.LastSignIn(p), Created(p),
                d.AdGroupsOf(p).Any(MirrorUser.IsPrivileged)))
            .Where(k => k.LastSignIn is { } l ? l < limit : k.Created is { } a && a < limit)
            .OrderBy(k => k.LastSignIn ?? DateTime.MinValue)
            .ThenBy(k => k.Person.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static PlanOutcome Plan(InventoryData d, CleanupKind kind, IEnumerable<Person> selected, DateOnly date)
    {
        var list = selected.ToList();
        if (kind == CleanupKind.Guests)
        {
            return GuestPlans.Delete(d, list);
        }

        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var p in list)
        {
            if (p.Ad is { } ad)
            {
                var description = Texts.T.Format("code.cleanup.disabled-in-clean-up-round", date) + (string.IsNullOrWhiteSpace(ad.Description) ? string.Empty : " - " + ad.Description.Trim());
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdDisableAccount,
                    Target = ad.DistinguishedName,
                    TargetName = p.Name,
                    Description = Texts.T["code.cleanup.disable-ad-account-clean-up"],
                    CurrentValue = ad.Enabled ? "ingeschakeld" : Texts.T["code.plan-builder.already-disabled"],
                    NewValue = "uitgeschakeld",
                });
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdSetAttributes,
                    Target = ad.DistinguishedName,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["description"] = description },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["description"] = ad.Description },
                    Description = Texts.T["code.cleanup.put-the-reason-in-the"],
                    CurrentValue = ad.Description,
                    NewValue = description,
                });
            }
            else if (p.Entra is { OnPremisesSyncEnabled: false } e)
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetAttributes,
                    Target = e.Id,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = e.AccountEnabled ? "true" : "false" },
                    Description = Texts.T["code.cleanup.disable-cloud-account-block-sign"],
                    CurrentValue = e.AccountEnabled ? "ingeschakeld" : Texts.T["code.plan-builder.already-disabled"],
                    NewValue = "uitgeschakeld",
                });
            }
            else
            {
                errors.Add(Texts.T.Format("code.cleanup.synchronised-account-without-ad-data", p.Name));
            }
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>
    /// Report of the round: every checked account with the decision and, after running, the result. Contains personal
    /// data and stays local (export folder).
    /// </summary>
    public static string SaveReport(string folder, CleanupKind kind, int days, IReadOnlyList<CleanupCandidate> candidates,
        IReadOnlySet<string> selected, IReadOnlyList<Change> plan, IReadOnlyList<ChangeResult> results, string byValue, DateTime time)
    {
        var status = results.ToDictionary(r => r.ChangeId);
        string Result(Person p)
        {
            var own = plan.Where(w => w.Target == p.Ad?.DistinguishedName || w.Target == p.Entra?.Id)
                .Select(w => status.TryGetValue(w.Id, out var r) ? r : null)
                .ToList();
            if (own.Count == 0)
            {
                return selected.Contains(p.Key) ? Texts.T["cleanup.not-in-plan"] : string.Empty;
            }

            return own.All(r => r?.Status == ResultStatus.Succeeded) ? Texts.T["cleanup.succeeded"]
                : own.All(r => r is null) ? Texts.T["cleanup.not-run"]
                : Texts.T.Format("cleanup.failed", string.Join("; ", own.Where(r => r is not null && r.Status != ResultStatus.Succeeded).Select(r => r!.Message)));
        }

        var decision = kind == CleanupKind.Guests ? Texts.T["cleanup.remove"] : Texts.T["cleanup.disable"];
        var path = Path.Combine(folder, $"Opkuisronde-{kind}-{time:yyyyMMdd-HHmm}.csv");
        CsvWriter.Write(path,
            new[] { "cleanup.name", "UPN", "cleanup.source", "cleanup.last-sign-in", "cleanup.created", "cleanup.privileged", "cleanup.decision", "cleanup.result", "cleanup.threshold", "cleanup.checked-by", "cleanup.date" }.Select(h => h.Contains('.') ? Texts.T[h] : h).ToArray(),
            candidates.Select(k => new[]
            {
                k.Person.Name, k.Person.Upn, k.Person.Source,
                k.LastSignIn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? Texts.T["cleanup.never"],
                k.Created?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                k.Privileged ? Texts.T["cleanup.yes"] : string.Empty,
                selected.Contains(k.Person.Key) ? decision : Texts.T["cleanup.keep"],
                Result(k.Person),
                days.ToString(CultureInfo.InvariantCulture), byValue, time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            }),
            forExcel: true);
        return path;
    }

    private static DateTime? Created(Person p)
    {
        foreach (var t in new[] { p.Ad?.WhenCreated, p.Entra?.CreatedDateTime })
        {
            if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                return d;
            }
        }

        return null;
    }
}
