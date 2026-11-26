using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

public enum ProfileItemKind
{
    Group,
    Mailbox,
    Licence,
    Attribute,
}

public enum ProfileStatus
{
    /// <summary>The person has it, as the profile says.</summary>
    Has,

    /// <summary>In the profile, the person does not have it yet.</summary>
    Missing,

    /// <summary>The person has it, the profile does not (more than the profile).</summary>
    Extra,

    /// <summary>The profile names something this tenant does not have (any more).</summary>
    NotFound,

    /// <summary>The app cannot give it (dynamic or privileged group, no AD object, ...).</summary>
    NotPossible,
}

/// <summary>One line of the comparison of a person with a job profile.</summary>
public sealed record ProfileItem(ProfileItemKind Kind, string Name, ProfileStatus Status, string Remark = "")
{
    public string KindText => Kind switch
    {
        ProfileItemKind.Group => Texts.T["word.group"],
        ProfileItemKind.Mailbox => Texts.T["code.job-profile-plans.shared-mailbox"],
        ProfileItemKind.Licence => Texts.T["licences.licence"],
        _ => Texts.T["code.job-profile-plans.attribute"],
    };

    public string StatusText => Status switch
    {
        ProfileStatus.Has => Texts.T["ca-check.verdict.ok"],
        ProfileStatus.Missing => Texts.T["job-profiles.missing"],
        ProfileStatus.Extra => Texts.T["licences.more-than-the-profile"],
        ProfileStatus.NotFound => Texts.T["routes.notfound"],
        _ => Texts.T["code.job-profile-plans.not-possible"],
    };
}

/// <summary>Who got which job profile: the version and when (stored per tenant).</summary>
public sealed record ProfileAssignment(string ProfileId, int Version, DateTimeOffset Applied, string By);

/// <summary>
/// Job profiles (R9, 0.22): compare a person with a profile, the plan that applies it, a profile made from a person,
/// and who holds a profile. Every change goes along the usual route (AD, Graph or Exchange); dynamic and privileged
/// groups are never given, and what is more than the profile is only proposed, never ticked.
/// </summary>
public static class JobProfilePlans
{
    /// <summary>The group with this name: display name, or name or sAMAccountName of an AD group.</summary>
    public static Group? FindGroup(InventoryData d, string name)
    {
        var n = name.Trim();
        return d.Index.Groups.FirstOrDefault(g => Same(g.Name, n))
               ?? d.Index.Groups.FirstOrDefault(g => Same(g.Ad?.SamAccountName, n) || Same(g.Ad?.Name, n) || Same(g.Exo?.DisplayName, n));
    }

    public static IReadOnlyList<ProfileItem> Compare(InventoryData d, OrganisationConfig config, JobProfile profile, Person p)
    {
        var index = d.Index;
        var items = new List<ProfileItem>();
        var groups = index.GroupsOf(p).GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var access = index.MailboxPermissionsOf(p).GroupBy(r => r.Mailbox.Smtp, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Mailbox, StringComparer.OrdinalIgnoreCase);

        // Access groups of mailboxes count as the mailbox, not as a group
        var accessGroups = profile.Mailboxes.Select(index.Mailbox).OfType<Mailbox>().Concat(access.Values)
            .Select(index.AccessGroupOf).OfType<Group>().Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inProfile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in profile.Groups.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var g = FindGroup(d, name);
            if (g is null)
            {
                items.Add(new(ProfileItemKind.Group, name, ProfileStatus.NotFound, Texts.T["code.job-profile-plans.this-group-does-not-exist"]));
                continue;
            }

            inProfile.Add(g.Key);
            if (groups.ContainsKey(g.Key))
            {
                items.Add(new(ProfileItemKind.Group, g.Name, ProfileStatus.Has));
            }
            else if (WhyNot(config, g, p) is { } reason)
            {
                items.Add(new(ProfileItemKind.Group, g.Name, ProfileStatus.NotPossible, reason));
            }
            else
            {
                items.Add(new(ProfileItemKind.Group, g.Name, ProfileStatus.Missing));
            }
        }

        foreach (var g in groups.Values.Where(g => !inProfile.Contains(g.Key) && !accessGroups.Contains(g.Key) && !Automatic(config, g))
                     .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            items.Add(new(ProfileItemKind.Group, g.Name, ProfileStatus.Extra, g.Ad is { } a && MirrorUser.IsPrivileged(a) ? Texts.T["code.job-profile-plans.privileged-group"] : string.Empty));
        }

        var mailboxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var smtp in profile.Mailboxes.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            mailboxes.Add(smtp.Trim());
            if (index.Mailbox(smtp.Trim()) is not { } mb)
            {
                items.Add(new(ProfileItemKind.Mailbox, smtp, ProfileStatus.NotFound, Texts.T["code.job-profile-plans.this-mailbox-does-not-exist"]));
            }
            else if (access.ContainsKey(mb.Smtp))
            {
                items.Add(new(ProfileItemKind.Mailbox, mb.DisplayName, ProfileStatus.Has));
            }
            else if (index.AccessGroupOf(mb) is null)
            {
                items.Add(new(ProfileItemKind.Mailbox, mb.DisplayName, ProfileStatus.NotPossible, Texts.T.Format("code.job-profile-plans.no-access-group-yet", ObjectIndex.ExpectedAccessGroup(mb))));
            }
            else
            {
                items.Add(new(ProfileItemKind.Mailbox, mb.DisplayName, ProfileStatus.Missing));
            }
        }

        foreach (var mb in access.Values.Where(m => !mailboxes.Contains(m.Smtp)).OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            items.Add(new(ProfileItemKind.Mailbox, mb.DisplayName, ProfileStatus.Extra));
        }

        var licences = p.Entra?.Licences ?? Array.Empty<string>();
        foreach (var sku in profile.Licences.Where(l => !string.IsNullOrWhiteSpace(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (p.Entra is null)
            {
                items.Add(new(ProfileItemKind.Licence, sku, ProfileStatus.NotPossible, Texts.T["code.job-profile-plans.no-entra-account-not-synchronised"]));
            }
            else if (licences.Contains(sku, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(new(ProfileItemKind.Licence, sku, ProfileStatus.Has));
            }
            else
            {
                items.Add(new(ProfileItemKind.Licence, sku, ProfileStatus.Missing));
            }
        }

        foreach (var sku in licences.Where(l => !profile.Licences.Contains(l, StringComparer.OrdinalIgnoreCase)))
        {
            items.Add(new(ProfileItemKind.Licence, sku, ProfileStatus.Extra, Texts.T["code.job-profile-plans.you-remove-it-at-the"]));
        }

        var current = UserDesired.Of(p);
        foreach (var (field, wanted, now, adOnly) in Attributes(profile, current))
        {
            var name = Texts.T["attribute." + field.ToLowerInvariant()];
            if (Same(Normalise(wanted), Normalise(now)))
            {
                items.Add(new(ProfileItemKind.Attribute, $"{name}: {wanted}", ProfileStatus.Has));
            }
            else if (adOnly && !AttributePlans.ViaAd(p))
            {
                items.Add(new(ProfileItemKind.Attribute, $"{name}: {wanted}", ProfileStatus.NotPossible, Texts.T["code.job-profile-plans.exists-only-in-ad"]));
            }
            else
            {
                items.Add(new(ProfileItemKind.Attribute, $"{name}: {wanted}", ProfileStatus.Missing, Texts.T.Format("plan.now.value", string.IsNullOrWhiteSpace(now) ? Texts.T["undo.empty"] : now)));
            }
        }

        return items;
    }

    /// <summary>
    /// The plan that gives the person what the profile says. What is more than the profile (groups, mailbox access) is
    /// added unticked when <paramref name="removeExtras"/> is on; licences are never removed here.
    /// </summary>
    public static PlanOutcome Apply(InventoryData d, OrganisationConfig config, JobProfile profile, Person p, bool removeExtras)
    {
        if (p.IsGuest)
        {
            return PlanOutcome.Error(Texts.T["code.job-profile-plans.a-guest-user-gets-no"]);
        }

        var index = d.Index;
        var who = index.Ref(p);
        var plan = new List<Change>();
        var errors = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = Compare(d, config, profile, p);

        foreach (var item in items.Where(i => i.Kind == ProfileItemKind.Group && i.Status == ProfileStatus.Missing))
        {
            if (FindGroup(d, item.Name) is { } g && added.Add(g.Key))
            {
                Collect(GroupPlans.AddMember(d, g, who), plan, errors);
            }
        }

        foreach (var smtp in profile.Mailboxes.Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (index.Mailbox(smtp) is { } mb && items.Any(i => i.Kind == ProfileItemKind.Mailbox && i.Status == ProfileStatus.Missing && i.Name == mb.DisplayName)
                && index.AccessGroupOf(mb) is { } g && added.Add(g.Key))
            {
                Collect(GroupPlans.AddMember(d, g, who), plan, errors);
            }
        }

        var missingLicences = items.Where(i => i.Kind == ProfileItemKind.Licence && i.Status == ProfileStatus.Missing).Select(i => i.Name).ToList();
        if (missingLicences.Count > 0 && p.Entra is { } entra)
        {
            Change? location = null;
            if (string.IsNullOrWhiteSpace(entra.UsageLocation))
            {
                var country = UsageLocation(d);
                location = new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetAttributes,
                    Target = entra.Id,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["usageLocation"] = country },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["usageLocation"] = string.Empty },
                    Description = Texts.T.Format("plan.usage-location", country),
                    NewValue = country,
                };
                plan.Add(location);
            }

            foreach (var sku in missingLicences)
            {
                var licence = d.Licences.FirstOrDefault(l => Same(l.SkuPartNumber, sku));
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphAssignLicence,
                    Target = entra.Id,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["sku"] = sku, ["skuId"] = licence?.SkuId ?? string.Empty },
                    Description = Texts.T.Format("code.plan-builder.assign-licence", sku),
                    NewValue = sku,
                    Warning = licence is { Free: 0 } ? Texts.T.Format("code.job-profile-plans.there-are-no-free-licences", sku, licence.Assigned, licence.Total) : null,
                    DependentOf = location?.Id,
                });
            }
        }

        var desired = new UserDesired();
        var any = false;
        var current = UserDesired.Of(p);
        foreach (var (name, wanted, now, adOnly) in Attributes(profile, current))
        {
            if (Same(Normalise(wanted), Normalise(now)) || (adOnly && !AttributePlans.ViaAd(p)))
            {
                continue;
            }

            any = true;
            switch (name)
            {
                case nameof(UserDesired.JobTitle): desired.JobTitle = wanted; break;
                case nameof(UserDesired.Department): desired.Department = wanted; break;
                case nameof(UserDesired.Office): desired.Office = wanted; break;
                default: desired.LogonScript = wanted; break;
            }
        }

        if (any)
        {
            Collect(AttributePlans.User(d, config, p, desired), plan, errors);
        }

        if (removeExtras)
        {
            foreach (var item in items.Where(i => i.Status == ProfileStatus.Extra))
            {
                var outcome = item.Kind switch
                {
                    ProfileItemKind.Group when index.GroupsOf(p).FirstOrDefault(g => g.Name == item.Name) is { } g => GroupPlans.RemoveMember(d, g, who),
                    ProfileItemKind.Mailbox when index.MailboxPermissionsOf(p).FirstOrDefault(r => r.Mailbox.DisplayName == item.Name) is { } r => MailboxPlans.RemoveAccess(d, r.Mailbox, p),
                    _ => null,
                };
                if (outcome is not null)
                {
                    foreach (var c in outcome.Plan)
                    {
                        c.Selected = false;
                        plan.Add(c);
                    }
                }
            }
        }

        if (plan.Count == 0)
        {
            return errors.Count > 0 ? PlanOutcome.Of(plan, errors) : PlanOutcome.Error(Texts.T.Format("code.job-profile-plans.already-has-everything-of", p.Name, profile.Name));
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>A new profile with what this person has: the start of a profile, to check and clean up.</summary>
    public static JobProfile FromPerson(InventoryData d, OrganisationConfig config, Person p, string name)
    {
        var index = d.Index;
        var mailboxes = index.MailboxPermissionsOf(p).Select(r => r.Mailbox).DistinctBy(m => m.Smtp, StringComparer.OrdinalIgnoreCase).ToList();
        var accessGroups = mailboxes.Select(index.AccessGroupOf).OfType<Group>().Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = UserDesired.Of(p);
        return new JobProfile
        {
            Name = name.Trim(),
            Description = Texts.T.Format("code.job-profile-plans.made-from", p.Name),
            Groups = index.GroupsOf(p)
                .Where(g => !Automatic(config, g) && !accessGroups.Contains(g.Key) && !(g.Ad is { } a && MirrorUser.IsPrivileged(a)))
                .Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Mailboxes = mailboxes.Select(m => m.Smtp).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList(),
            Licences = (p.Entra?.Licences ?? Array.Empty<string>()).ToList(),
            JobTitle = Empty(current.JobTitle),
            Department = Empty(current.Department),
            Office = Empty(current.Office),
            LogonScript = Empty(current.LogonScript),
        };
    }

    /// <summary>Who holds the profile: whoever got it in the app, and whoever has its job title.</summary>
    public static IReadOnlyList<Person> Holders(InventoryData d, JobProfile profile, IReadOnlyDictionary<string, ProfileAssignment> assigned)
        => d.Persons
            .Where(p => !p.IsGuest)
            .Where(p => (assigned.TryGetValue(p.Key, out var a) && a.ProfileId == profile.Id)
                        || (!string.IsNullOrWhiteSpace(profile.JobTitle) && Same(Normalise(UserDesired.Of(p).JobTitle), Normalise(profile.JobTitle))))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ assignments (a document per tenant)

    public static Dictionary<string, ProfileAssignment> ReadAssignments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, ProfileAssignment>>(json);
            return read is null ? new(StringComparer.OrdinalIgnoreCase) : new(read, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string WriteAssignments(IReadOnlyDictionary<string, ProfileAssignment> assignments) => JsonSerializer.Serialize(assignments);

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<(string Name, string Wanted, string? Now, bool AdOnly)> Attributes(JobProfile profile, UserDesired current)
    {
        if (!string.IsNullOrWhiteSpace(profile.JobTitle)) yield return (nameof(UserDesired.JobTitle), profile.JobTitle.Trim(), current.JobTitle, false);
        if (!string.IsNullOrWhiteSpace(profile.Department)) yield return (nameof(UserDesired.Department), profile.Department.Trim(), current.Department, false);
        if (!string.IsNullOrWhiteSpace(profile.Office)) yield return (nameof(UserDesired.Office), profile.Office.Trim(), current.Office, false);
        if (!string.IsNullOrWhiteSpace(profile.LogonScript)) yield return (nameof(UserDesired.LogonScript), profile.LogonScript.Trim(), current.LogonScript, true);
    }

    /// <summary>Why a group cannot be given to this person, or null.</summary>
    private static string? WhyNot(OrganisationConfig config, Group g, Person p)
    {
        if (Automatic(config, g))
        {
            return Texts.T["code.job-profile-plans.dynamic-group-the-members-follow"];
        }

        if (g.Ad is { } a && MirrorUser.IsPrivileged(a))
        {
            return Texts.T["code.job-profile-plans.privileged-group-you-add-members"];
        }

        if (g.Route == WriteRoute.ActiveDirectory && p.Ad is null)
        {
            return Texts.T["code.job-profile-plans.ad-group-and-this-account"];
        }

        if (g.Route == WriteRoute.Graph && p.Entra is null)
        {
            return Texts.T["code.job-profile-plans.cloud-group-and-this-account"];
        }

        return g.Route == WriteRoute.No ? Texts.T["code.job-profile-plans.no-write-route-is-known"] : null;
    }

    /// <summary>Groups whose members follow from a rule: dynamic groups and the division groups.</summary>
    private static bool Automatic(OrganisationConfig config, Group g)
        => g.Dynamic || config.IsDynamicGroup(g.Name) || config.Divisions.Any(a => Same(a.Group, g.Name));

    /// <summary>The usage location most licensed users have; BE when nobody has one.</summary>
    public static string UsageLocation(InventoryData d)
        => d.EntraUsers.Where(u => u.UsageLocation.Length > 0).GroupBy(u => u.UsageLocation, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key.ToUpperInvariant()).FirstOrDefault() ?? "BE";

    private static void Collect(PlanOutcome outcome, List<Change> plan, List<string> errors)
    {
        plan.AddRange(outcome.Plan);
        errors.AddRange(outcome.Errors.Where(e => e != Texts.T["plan.nothing-changed"]));
    }

    private static string Normalise(string? value) => (value ?? string.Empty).Trim();

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
