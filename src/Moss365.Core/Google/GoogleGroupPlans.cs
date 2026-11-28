using System.Net.Mail;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Linking;
using Moss365.Core.Localization;
using Moss365.Core.Setup;
using Moss365.Core.Storage;

namespace Moss365.Core.Google;

/// <summary>The settings of a Google group as they have to become (G2, part "Google group settings"); null = do not
/// change.</summary>
public sealed record GoogleGroupSettingsDesired(string? WhoCanJoin = null, string? WhoCanPost = null, string? WhoCanViewMembership = null, bool? AllowExternalMembers = null);

/// <summary>A calendar resource as it has to become (G2); null = do not change.</summary>
public sealed record GoogleResourceDesired(string? Name = null, int? Capacity = null, string? Floor = null, string? Description = null, bool ClearCapacity = false);

/// <summary>
/// Plans for the groups, the calendar resources and the licences of Google Workspace (G2): the name and description
/// of a group, its members with their role, its settings (with the part), a resource, and licences (with the part).
/// Groups that follow from another platform (the source of truth, U33) keep their name and members there; their
/// settings belong to Google. Every change keeps what was before, so it can be undone; nothing is deleted.
/// </summary>
public static class GoogleGroupPlans
{
    public const string Member = "MEMBER";
    public const string Manager = "MANAGER";
    public const string Owner = "OWNER";

    public static IReadOnlyList<string> Roles { get; } = new[] { Member, Manager, Owner };

    public static IReadOnlyList<string> JoinOptions { get; } = new[] { "INVITED_CAN_JOIN", "CAN_REQUEST_TO_JOIN", "ALL_IN_DOMAIN_CAN_JOIN", "ANYONE_CAN_JOIN" };

    public static IReadOnlyList<string> PostOptions { get; } = new[]
        { "NONE_CAN_POST", "ALL_OWNERS_CAN_POST", "ALL_MANAGERS_CAN_POST", "ALL_MEMBERS_CAN_POST", "ALL_IN_DOMAIN_CAN_POST", "ANYONE_CAN_POST" };

    public static IReadOnlyList<string> ViewOptions { get; } = new[] { "ALL_OWNERS_CAN_VIEW", "ALL_MANAGERS_CAN_VIEW", "ALL_MEMBERS_CAN_VIEW", "ALL_IN_DOMAIN_CAN_VIEW" };

    /// <summary>The readable name of a role or a setting value.</summary>
    public static string Name(string value) => Texts.T.Text("google.value." + value.ToLowerInvariant().Replace('_', '-'), value);

    /// <summary>The platform the Google groups follow (their name and members), or null when Google keeps them itself.</summary>
    public static Platform? FollowsFrom(TenantSystems systems, InventoryData data, SourceSettings settings)
        => SourceOfTruth.FollowsFrom(Platform.GoogleWorkspace, TruthKind.Groups, systems, data, settings);

    public static PlanOutcome Edit(GoogleGroup group, string? name, string? description, Platform? followsFrom)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        void Field(string key, string label, string current, string? wanted, int max)
        {
            if (wanted is null || string.Equals(current, wanted.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            if (followsFrom is { } from)
            {
                errors.Add(Texts.T.Format("google.follows", label, Platforms.Name(from)));
            }
            else if (wanted.Trim().Length > max || (key == "name" && wanted.Trim().Length == 0))
            {
                errors.Add(Texts.T.Format("attribute.invalid", label));
            }
            else
            {
                parameters[key] = wanted.Trim();
                previous[key] = current;
            }
        }

        Field("name", Texts.T["google.group.name"], group.Name, name, 73);
        Field("description", Texts.T["google.group.description"], group.Description, description, 4096);
        if (errors.Count > 0 || parameters.Count == 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Google,
                ChangeAction = ChangeAction.GoogleUpdateGroup,
                Target = group.Id,
                TargetName = group.Email,
                Parameters = parameters,
                Previous = previous,
                Description = Texts.T.Format("google.plan.update-group", group.Email),
                CurrentValue = string.Join("; ", previous.Select(p => Shown(p.Value))),
                NewValue = string.Join("; ", parameters.Select(p => Shown(p.Value))),
            },
        });
    }

    /// <summary>The settings of a group (the part "Google group settings"): only what changes, with the values from
    /// before; opening a group to everyone on the internet comes with a warning.</summary>
    public static PlanOutcome Settings(GoogleGroup group, GoogleGroupSettingsDesired desired, IReadOnlyCollection<string> parts)
    {
        if (!parts.Contains(GoogleParts.GroupSettings))
        {
            return PlanOutcome.Error(Texts.T["google.group.settings-part-off"]);
        }

        if (group.WhoCanJoin.Length == 0)
        {
            return PlanOutcome.Error(Texts.T["google.group.settings-not-read"]);
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        void Setting(string key, string current, string? wanted, IReadOnlyList<string> options)
        {
            if (wanted is null || wanted == current)
            {
                return;
            }

            if (!options.Contains(wanted))
            {
                errors.Add(Texts.T.Format("attribute.invalid", Texts.T["google.group." + key.ToLowerInvariant()]));
                return;
            }

            parameters[key] = wanted;
            previous[key] = current;
        }

        Setting("whoCanJoin", group.WhoCanJoin, desired.WhoCanJoin, JoinOptions);
        Setting("whoCanPostMessage", group.WhoCanPost, desired.WhoCanPost, PostOptions);
        Setting("whoCanViewMembership", group.WhoCanViewMembership, desired.WhoCanViewMembership, ViewOptions);
        if (desired.AllowExternalMembers is { } external && external != group.AllowExternalMembers)
        {
            parameters["allowExternalMembers"] = external ? "True" : "False";
            previous["allowExternalMembers"] = group.AllowExternalMembers == true ? "True" : "False";
        }

        if (errors.Count > 0 || parameters.Count == 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        var open = parameters.GetValueOrDefault("whoCanJoin") == "ANYONE_CAN_JOIN" || parameters.GetValueOrDefault("whoCanPostMessage") == "ANYONE_CAN_POST"
                   || parameters.GetValueOrDefault("allowExternalMembers") == "True";
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Google,
                ChangeAction = ChangeAction.GoogleUpdateGroupSettings,
                Target = group.Email,
                TargetName = group.Name,
                Parameters = parameters,
                Previous = previous,
                Description = Texts.T.Format("google.plan.group-settings", group.Email),
                CurrentValue = string.Join("; ", previous.Select(p => Name(p.Value))),
                NewValue = string.Join("; ", parameters.Select(p => Name(p.Value))),
                Warning = open ? Texts.T["google.group.open-warning"] : null,
            },
        });
    }

    /// <summary>A new member of a group with a role; someone already in the group gets the role instead.</summary>
    public static PlanOutcome AddMember(InventoryData data, GoogleGroup group, string address, string role, IReadOnlyList<GoogleMember>? members,
        IReadOnlyList<string> domains, Platform? followsFrom)
    {
        if (followsFrom is { } from)
        {
            return PlanOutcome.Error(Texts.T.Format("google.group.members-follow", Platforms.Name(from)));
        }

        var email = address.Trim().ToLowerInvariant();
        if (!MailAddress.TryCreate(email, out var mail) || !string.Equals(mail.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            return PlanOutcome.Error(Texts.T.Format("google.alias.invalid", address));
        }

        if (!Roles.Contains(role))
        {
            return PlanOutcome.Error(Texts.T.Format("attribute.invalid", Texts.T["google.group.role"]));
        }

        if (string.Equals(email, group.Email, StringComparison.OrdinalIgnoreCase))
        {
            return PlanOutcome.Error(Texts.T["google.group.not-itself"]);
        }

        if (members?.FirstOrDefault(m => string.Equals(m.Email, email, StringComparison.OrdinalIgnoreCase)) is { } already)
        {
            return already.Role == role
                ? PlanOutcome.Error(Texts.T.Format("google.group.already", email, Name(role)))
                : PlanOutcome.Of(new[] { SetRole(group, already, role) });
        }

        var outside = !domains.Contains(email[(email.IndexOf('@') + 1)..]);
        var warning = outside && group.AllowExternalMembers == false
            ? Texts.T["google.group.external-refused"]
            : outside ? Texts.T["google.group.external"] : null;
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Google,
                ChangeAction = ChangeAction.GoogleAddGroupMember,
                Target = email,
                TargetName = GooglePlans.Holder(data, email) is { } holder && !string.Equals(holder, email, StringComparison.OrdinalIgnoreCase) ? holder : email,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groep"] = group.Email, ["rol"] = role },
                Description = Texts.T.Format("google.plan.add-member", email, group.Email, Name(role)),
                Warning = warning,
            },
        });
    }

    /// <summary>A member out of the group; the role goes along, so that undo puts it back as it was.</summary>
    public static PlanOutcome RemoveMember(GoogleGroup group, GoogleMember member, IReadOnlyList<GoogleMember>? members, Platform? followsFrom)
    {
        if (followsFrom is { } from)
        {
            return PlanOutcome.Error(Texts.T.Format("google.group.members-follow", Platforms.Name(from)));
        }

        var lastOwner = member.Role == Owner && members is not null && members.Count(m => m.Role == Owner) == 1;
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Google,
                ChangeAction = ChangeAction.GoogleRemoveGroupMember,
                Target = member.Email,
                TargetName = member.Email,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groep"] = group.Email, ["rol"] = member.Role },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["rol"] = member.Role },
                Description = Texts.T.Format("google.plan.remove-member", member.Email, group.Email),
                Warning = lastOwner ? Texts.T["google.group.last-owner"] : null,
            },
        });
    }

    public static Change SetRole(GoogleGroup group, GoogleMember member, string role) => new()
    {
        TargetSystem = TargetSystem.Google,
        ChangeAction = ChangeAction.GoogleSetGroupMemberRole,
        Target = member.Email,
        TargetName = member.Email,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groep"] = group.Email, ["rol"] = role },
        Previous = new(StringComparer.OrdinalIgnoreCase) { ["rol"] = member.Role },
        Description = Texts.T.Format("google.plan.member-role", member.Email, group.Email),
        CurrentValue = Name(member.Role),
        NewValue = Name(role),
    };

    /// <summary>A calendar resource: the name, the capacity, the floor and the description, with what was before.</summary>
    public static PlanOutcome EditResource(GoogleResource resource, GoogleResourceDesired desired)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        void Field(string key, string label, string current, string? wanted, int max)
        {
            if (wanted is null || string.Equals(current, wanted.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            if (wanted.Trim().Length > max || (key == "resourceName" && wanted.Trim().Length == 0))
            {
                errors.Add(Texts.T.Format("attribute.invalid", label));
                return;
            }

            parameters[key] = wanted.Trim();
            previous[key] = current;
        }

        Field("resourceName", Texts.T["google.resource.name"], resource.Name, desired.Name, 255);
        Field("floorName", Texts.T["google.resource.floor"], resource.Floor, desired.Floor, 255);
        Field("resourceDescription", Texts.T["google.resource.description"], resource.Description, desired.Description, 1024);
        var capacity = desired.ClearCapacity ? null : desired.Capacity ?? resource.Capacity;
        if (capacity is < 0 or > 10000)
        {
            errors.Add(Texts.T.Format("attribute.invalid", Texts.T["google.resource.capacity"]));
        }
        else if (capacity != resource.Capacity)
        {
            parameters["capacity"] = capacity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            previous["capacity"] = resource.Capacity?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (errors.Count > 0 || parameters.Count == 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Google,
                ChangeAction = ChangeAction.GoogleUpdateResource,
                Target = resource.Id,
                TargetName = resource.Name,
                Parameters = parameters,
                Previous = previous,
                Description = Texts.T.Format("google.plan.update-resource", resource.Name),
                CurrentValue = string.Join("; ", previous.Select(p => Shown(p.Value))),
                NewValue = string.Join("; ", parameters.Select(p => Shown(p.Value))),
            },
        });
    }

    /// <summary>The licences the tenant uses (product, SKU and name), the most used first: only those are offered.</summary>
    public static IReadOnlyList<GoogleLicence> Skus(InventoryData data)
        => data.GoogleLicences.GroupBy(l => (l.ProductId, l.SkuId)).OrderByDescending(g => g.Count())
            .Select(g => new GoogleLicence(string.Empty, g.Key.ProductId, g.Key.SkuId, g.First().SkuName)).ToList();

    public static PlanOutcome AssignLicence(InventoryData data, GoogleUser user, string productId, string skuId, IReadOnlyCollection<string> parts)
    {
        if (!parts.Contains(GoogleParts.Licences))
        {
            return PlanOutcome.Error(Texts.T["google.licences.part-off"]);
        }

        if (Skus(data).FirstOrDefault(s => s.ProductId == productId && s.SkuId == skuId) is not { } sku)
        {
            return PlanOutcome.Error(Texts.T["google.licence.unknown"]);
        }

        if (data.GoogleLicences.Any(l => Same(l.UserEmail, user.PrimaryEmail) && l.ProductId == productId))
        {
            return PlanOutcome.Error(Texts.T.Format("google.licence.has-product", user.PrimaryEmail));
        }

        return PlanOutcome.Of(new[] { Licence(user, ChangeAction.GoogleAssignLicence, sku, Texts.T.Format("google.plan.assign-licence", SkuName(sku), user.PrimaryEmail), null) });
    }

    public static Change RemoveLicence(GoogleUser user, GoogleLicence licence)
        => Licence(user, ChangeAction.GoogleRemoveLicence, licence, Texts.T.Format("google.plan.remove-licence", SkuName(licence), user.PrimaryEmail), Texts.T["google.licence.remove-warning"]);

    private static Change Licence(GoogleUser user, ChangeAction action, GoogleLicence sku, string description, string? warning) => new()
    {
        TargetSystem = TargetSystem.Google,
        ChangeAction = action,
        Target = user.PrimaryEmail,
        TargetName = user.PrimaryEmail,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["productId"] = sku.ProductId, ["skuId"] = sku.SkuId, ["sku"] = SkuName(sku) },
        Description = description,
        Warning = warning,
    };

    public static string SkuName(GoogleLicence l) => l.SkuName.Length > 0 ? l.SkuName : l.SkuId;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Shown(string value) => value.Length > 0 ? value : Texts.T["edit-user.empty"];
}
