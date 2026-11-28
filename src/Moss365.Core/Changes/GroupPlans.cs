using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>A plan or the reason why it is not possible.</summary>
public sealed record PlanOutcome(IReadOnlyList<Change> Plan, IReadOnlyList<string> Errors)
{
    public static PlanOutcome Error(string error) => new(Array.Empty<Change>(), new[] { error });

    public static PlanOutcome Of(IEnumerable<Change> plan, IEnumerable<string>? errors = null) => new(plan.ToList(), (errors ?? Array.Empty<string>()).ToList());
}

/// <summary>Desired attributes of a group; null = do not change.</summary>
public sealed class GroupDesired
{
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Mail { get; set; }
}

/// <summary>The settings of a group as they have to become (R21); null = do not change.</summary>
public sealed record GroupSettingsDesired(string? Visibility = null, bool? Hidden = null, bool? OnlyInternalSenders = null, string? JoinRestriction = null);

/// <summary>
/// Plans for groups: members, name and description, managers. The route follows from the group: an AD group (also when
/// it is synchronised to Entra) via AD, a cloud group via Graph, a mail group via Exchange. Dynamic groups are never
/// filled by hand.
/// </summary>
public static class GroupPlans
{
    public static PlanOutcome AddMember(InventoryData data, Group g, ObjectRef member, Guid? dependentOf = null)
        => Member(data, g, member, add: true, dependentOf);

    public static PlanOutcome RemoveMember(InventoryData data, Group g, ObjectRef member)
        => Member(data, g, member, add: false, null);

    private static PlanOutcome Member(InventoryData data, Group g, ObjectRef member, bool add, Guid? dependentOf)
    {
        if (g.Dynamic)
        {
            return PlanOutcome.Error(Texts.T.Format("code.group-plans.is-a-dynamic-group-the", g.Name));
        }

        if (add && g.Ad is { } adg && MirrorUser.IsPrivileged(adg))
        {
            return PlanOutcome.Error(Texts.T.Format("code.group-plans.is-a-privileged-group-you", g.Name));
        }

        switch (g.Route)
        {
            case WriteRoute.ActiveDirectory:
            {
                var dn = AdDn(data, member);
                if (dn is null)
                {
                    return PlanOutcome.Error(Texts.T.Format("code.group-plans.has-no-ad-object-is", member.Name, g.Name));
                }

                return PlanOutcome.Of(new[]
                {
                    new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = add ? ChangeAction.AdAddGroupMember : ChangeAction.AdRemoveGroupMember,
                        Target = dn,
                        TargetName = member.Name,
                        Parameters = P(("groepDn", g.Ad!.DistinguishedName), ("groep", g.Ad.Name)),
                        Description = add ? Texts.T.Format("code.plan-builder.make-member-of-ad-group", g.Name) : Texts.T.Format("code.plan-builder.remove-from-ad-group", g.Name),
                        DependentOf = dependentOf,
                    },
                });
            }

            case WriteRoute.Graph:
            {
                var id = EntraId(data, member);
                if (id is null)
                {
                    return PlanOutcome.Error(Texts.T.Format("code.group-plans.does-not-exist-in-entra", member.Name));
                }

                return PlanOutcome.Of(new[]
                {
                    new Change
                    {
                        TargetSystem = TargetSystem.Graph,
                        ChangeAction = add ? ChangeAction.GraphAddGroupMember : ChangeAction.GraphRemoveGroupMember,
                        Target = id,
                        TargetName = member.Name,
                        Parameters = P(("groepId", g.Cloud!.Id), ("groep", g.Name)),
                        Description = add ? Texts.T.Format("code.plan-builder.make-member-of-cloud-group", g.Name) : Texts.T.Format("code.plan-builder.remove-from-cloud-group", g.Name),
                        DependentOf = dependentOf,
                    },
                });
            }

            case WriteRoute.Exchange:
            {
                var address = Address(data, member);
                if (address is null)
                {
                    return PlanOutcome.Error(Texts.T.Format("code.group-plans.has-no-e-mail-address", member.Name));
                }

                return PlanOutcome.Of(new[]
                {
                    new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = add ? ChangeAction.ExchangeAddGroupMember : ChangeAction.ExchangeRemoveGroupMember,
                        Target = address,
                        TargetName = member.Name,
                        Parameters = P(("groep", ExoIdentity(g)), ("groepNaam", g.Name)),
                        Description = !add ? Texts.T.Format("plan.remove-exchange", g.Name)
                            : g.Name.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase) ? Texts.T.Format("plan.member-exchange.shared", g.Name) : Texts.T.Format("plan.member-exchange", g.Name),
                        DependentOf = dependentOf,
                    },
                });
            }

            default:
                return PlanOutcome.Error(Texts.T.Format("code.group-plans.no-write-route-is-known", g.Name));
        }
    }

    /// <summary>Display name, description and (mail groups) e-mail address; only what differs. With the data, an
    /// address another recipient has is refused (R21).</summary>
    public static PlanOutcome Attributes(Group g, GroupDesired desired, InventoryData? data = null)
    {
        var errors = new List<string>();
        var plan = new List<Change>();
        var name = New(g.Name, desired.DisplayName);
        var description = New(g.Description, desired.Description);
        var mail = New(g.Mail, desired.Mail);
        if (mail is { Length: > 0 } && !AttributePlans.ValidValue(nameof(UserDesired.Upn), mail))
        {
            return PlanOutcome.Error(Texts.T.Format("code.mailbox-plans.is-not-an-e-mail", mail));
        }

        if (mail is { Length: > 0 } && data is not null && MailboxPlans.AddressHolder(data, mail, g.Mail) is { } holder)
        {
            return PlanOutcome.Error(Texts.T.Format("mailbox.primary.taken", mail, holder));
        }

        switch (g.Route)
        {
            case WriteRoute.ActiveDirectory:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (name is not null) p["displayName"] = name;
                if (description is not null) p["description"] = description;
                if (mail is not null) errors.Add(Texts.T["code.group-plans.you-do-not-change-the"]);
                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = ChangeAction.AdSetAttributes,
                        Target = g.Ad!.DistinguishedName,
                        TargetName = g.Name,
                        Parameters = p,
                        Description = Texts.T.Format("code.group-plans.change-the-name-and-or", (g.Cloud is null ? string.Empty : (" " + Texts.T["code.group-plans.entra-follows-after-the-synchronisation"]))),
                        CurrentValue = Text(("displayName", g.Ad.DisplayName.Length > 0 ? g.Ad.DisplayName : g.Ad.Name), ("description", g.Ad.Description), p),
                        NewValue = Text(p),
                        Previous = Previous(p, ("displayName", g.Ad.DisplayName), ("description", g.Ad.Description)),
                    });
                }

                break;
            }

            case WriteRoute.Graph:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (name is not null) p["displayName"] = name;
                if (description is not null) p["description"] = description;
                if (mail is not null) errors.Add(Texts.T["code.group-plans.you-change-the-e-mail"]);
                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Graph,
                        ChangeAction = ChangeAction.GraphSetGroupAttributes,
                        Target = g.Cloud!.Id,
                        TargetName = g.Name,
                        Parameters = p,
                        Description = Texts.T["code.group-plans.change-the-name-and-or-2"],
                        CurrentValue = Text(("displayName", g.Cloud.DisplayName), ("description", g.Cloud.Description), p),
                        NewValue = Text(p),
                        Previous = Previous(p, ("displayName", g.Cloud.DisplayName), ("description", g.Cloud.Description)),
                    });
                }

                break;
            }

            case WriteRoute.Exchange:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (name is not null) p["DisplayName"] = name;
                if (mail is not null) p["PrimarySmtpAddress"] = mail;
                if (description is not null) errors.Add(Texts.T["code.group-plans.a-mail-group-has-no"]);
                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeUpdateGroup,
                        Target = ExoIdentity(g),
                        TargetName = g.Name,
                        Parameters = p,
                        Description = Texts.T["code.group-plans.change-the-name-and-or-3"],
                        CurrentValue = Text(("DisplayName", g.Name), ("PrimarySmtpAddress", g.Mail), p),
                        NewValue = Text(p),
                        Previous = Previous(p, ("DisplayName", g.Name), ("PrimarySmtpAddress", g.Mail)),
                    });
                }

                break;
            }

            default:
                errors.Add(Texts.T.Format("code.group-plans.is-dynamic-or-has-no", g.Name));
                break;
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>The ways to become a member of a mail group (MemberJoinRestriction).</summary>
    public static IReadOnlyList<string> JoinRestrictions { get; } = new[] { "Closed", "ApprovalRequired", "Open" };

    /// <summary>The settings a group has on its page (R21): privacy for a Microsoft 365 group made in the cloud, the
    /// mail settings for a mail group of Exchange Online that is not synchronised from AD.</summary>
    public static (bool Privacy, bool Mail) SettingsOf(Group g)
        => (g.Cloud is { Type: CloudGroupType.Microsoft365, FromAd: false },
            g.Exo is { IsDirSynced: false } && g.Ad is null && g.Route == WriteRoute.Exchange);

    /// <summary>
    /// The settings of a group (R21) as one change with the previous values (can be undone): Public or Private for a
    /// Microsoft 365 group (Graph), hidden from the address book, only senders inside the organisation and how to become
    /// a member for a mail group (the fixed command of Exchange.ps1).
    /// </summary>
    public static PlanOutcome Settings(Group g, GroupSettingsDesired desired)
    {
        var (privacy, mail) = SettingsOf(g);
        var plan = new List<Change>();
        var errors = new List<string>();
        if (desired.Visibility is { } visibility && g.Cloud is { } cloud && !string.Equals(visibility, cloud.Visibility, StringComparison.OrdinalIgnoreCase))
        {
            if (!privacy || visibility is not ("Public" or "Private"))
            {
                errors.Add(Texts.T["group.settings.no-privacy"]);
            }
            else
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetGroupAttributes,
                    Target = cloud.Id,
                    TargetName = g.Name,
                    Parameters = P(("visibility", visibility)),
                    Previous = P(("visibility", cloud.Visibility)),
                    Description = Texts.T["group.settings.privacy.plan"],
                    CurrentValue = cloud.Visibility,
                    NewValue = visibility,
                    Warning = visibility == "Public" ? Texts.T["group.settings.public-warning"] : null,
                });
            }
        }

        if (desired.Hidden is not null || desired.OnlyInternalSenders is not null || desired.JoinRestriction is not null)
        {
            if (!mail || g.Exo is not { } exo)
            {
                errors.Add(Texts.T["group.settings.no-mail"]);
            }
            else
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var now = new List<string>();
                var becomes = new List<string>();
                void Yes(string key, string label, bool? current, bool? wanted)
                {
                    if (wanted is { } w && w != current)
                    {
                        p[key] = w ? "True" : "False";
                        previous[key] = current is { } c ? (c ? "True" : "False") : string.Empty;
                        now.Add(label + ": " + (current is null ? "?" : current.Value ? Texts.T["word.yes"] : Texts.T["word.no"]));
                        becomes.Add(label + ": " + (w ? Texts.T["word.yes"] : Texts.T["word.no"]));
                    }
                }

                Yes("HiddenFromAddressListsEnabled", Texts.T["group.settings.hidden"], exo.Hidden, desired.Hidden);
                Yes("RequireSenderAuthenticationEnabled", Texts.T["group.settings.internal"], exo.OnlyInternalSenders, desired.OnlyInternalSenders);
                if (desired.JoinRestriction is { } join && !string.Equals(join, exo.MemberJoinRestriction, StringComparison.OrdinalIgnoreCase))
                {
                    if (!JoinRestrictions.Contains(join))
                    {
                        errors.Add(Texts.T["group.settings.no-mail"]);
                    }
                    else
                    {
                        p["MemberJoinRestriction"] = join;
                        previous["MemberJoinRestriction"] = exo.MemberJoinRestriction;
                        now.Add(Texts.T["group.settings.join"] + ": " + JoinText(exo.MemberJoinRestriction));
                        becomes.Add(Texts.T["group.settings.join"] + ": " + JoinText(join));
                    }
                }

                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeUpdateGroup,
                        Target = ExoIdentity(g),
                        TargetName = g.Name,
                        Parameters = p,
                        Previous = previous,
                        Description = Texts.T.Format("group.settings.mail.plan", p.Count),
                        CurrentValue = string.Join("; ", now),
                        NewValue = string.Join("; ", becomes),
                    });
                }
            }
        }

        if (plan.Count == 0 && errors.Count == 0)
        {
            errors.Add(Texts.T["plan.nothing-changed"]);
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>The words for a way to become a member.</summary>
    public static string JoinText(string restriction) => restriction switch
    {
        "Open" => Texts.T["group.settings.join.open"],
        "ApprovalRequired" => Texts.T["group.settings.join.approval"],
        "Closed" => Texts.T["group.settings.join.closed"],
        _ => restriction.Length > 0 ? restriction : "?",
    };

    /// <summary>Add or remove a manager (AD managedBy, Exchange ManagedBy) or owner (cloud group).</summary>
    public static PlanOutcome Administrator(InventoryData data, Group g, ObjectRef who, bool add)
    {
        if (g.Ad is not null)
        {
            var dn = AdDn(data, who);
            if (add && dn is null)
            {
                return PlanOutcome.Error(Texts.T.Format("code.group-plans.has-no-ad-object-and", who.Name));
            }

            return PlanOutcome.Of(new[]
            {
                new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdSetAttributes,
                    Target = g.Ad.DistinguishedName,
                    TargetName = g.Name,
                    Parameters = P(("managedBy", add ? dn! : string.Empty)),
                    Previous = P(("managedBy", g.Ad.ManagedBy)),
                    Description = add ? Texts.T.Format("code.group-plans.make-manager-managedby-an-ad", who.Name) : Texts.T["code.group-plans.remove-the-manager-managedby"],
                    CurrentValue = g.Ad.ManagedBy.Length > 0 ? DnHelp.Name(g.Ad.ManagedBy) : Texts.T["edit-user.none"],
                    NewValue = add ? who.Name : Texts.T["edit-user.none"],
                },
            });
        }

        if (g.Route == WriteRoute.Exchange)
        {
            var address = Address(data, who);
            if (address is null)
            {
                return PlanOutcome.Error(Texts.T.Format("code.group-plans.has-no-e-mail-address-2", who.Name));
            }

            return PlanOutcome.Of(new[]
            {
                new Change
                {
                    TargetSystem = TargetSystem.Exchange,
                    ChangeAction = ChangeAction.ExchangeGroupManager,
                    Target = ExoIdentity(g),
                    TargetName = g.Name,
                    Parameters = P((add ? "Toevoegen" : "Verwijderen", address)),
                    Description = add ? Texts.T.Format("code.group-plans.make-manager", who.Name) : Texts.T.Format("code.group-plans.remove-as-manager", who.Name),
                    NewValue = who.Name,
                },
            });
        }

        if (g.Cloud is not null)
        {
            var id = EntraId(data, who);
            if (id is null)
            {
                return PlanOutcome.Error(Texts.T.Format("code.group-plans.does-not-exist-in-entra", who.Name));
            }

            return PlanOutcome.Of(new[]
            {
                new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = add ? ChangeAction.GraphAddOwner : ChangeAction.GraphRemoveOwner,
                    Target = g.Cloud.Id,
                    TargetName = g.Name,
                    Parameters = P(("lid", id), ("lidNaam", who.Name)),
                    Description = add ? Texts.T.Format("code.group-plans.make-owner", who.Name) : Texts.T.Format("code.group-plans.remove-as-owner", who.Name),
                    NewValue = who.Name,
                },
            });
        }

        return PlanOutcome.Error(Texts.T["code.group-plans.this-group-has-no-managers"]);
    }

    // ---------------------------------------------------------------- helper functions (also for the mailbox plans)

    internal static string ExoIdentity(Group g) => FirstNonEmpty(g.Exo?.Smtp, g.Mail, g.Exo?.Alias, g.Name);

    /// <summary>DN of a user or group in AD.</summary>
    internal static string? AdDn(InventoryData data, ObjectRef r) => r.Kind switch
    {
        ObjectKind.User => data.SearchPerson(r.Key)?.Ad?.DistinguishedName,
        ObjectKind.Group => data.Index.Group(r.Key)?.Ad?.DistinguishedName,
        _ => null,
    };

    /// <summary>Entra id of a user, group, mailbox or room (the account behind the mailbox).</summary>
    internal static string? EntraId(InventoryData data, ObjectRef r) => r.Kind switch
    {
        ObjectKind.User => data.SearchPerson(r.Key)?.Entra?.Id,
        ObjectKind.Group => data.Index.Group(r.Key)?.Cloud?.Id,
        ObjectKind.Mailbox or ObjectKind.Room => data.EntraUsers.FirstOrDefault(e =>
            string.Equals(e.UserPrincipalName, r.Key, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Mail, r.Key, StringComparison.OrdinalIgnoreCase))?.Id,
        _ => null,
    };

    /// <summary>E-mail address (identity for Exchange).</summary>
    internal static string? Address(InventoryData data, ObjectRef r)
    {
        var address = r.Kind switch
        {
            ObjectKind.User when data.SearchPerson(r.Key) is { } p => FirstNonEmpty(p.Entra?.Mail, p.Ad?.EmailAddress, p.Upn),
            ObjectKind.Group => data.Index.Group(r.Key)?.Mail,
            ObjectKind.Mailbox or ObjectKind.Room => r.Key,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    /// <summary>The previous values of only the attributes that change in the plan (for undo).</summary>
    internal static Dictionary<string, string> Previous(IReadOnlyDictionary<string, string> newValue, params (string Name, string Value)[] current)
        => current.Where(h => newValue.ContainsKey(h.Name)).ToDictionary(h => h.Name, h => h.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string> P(params (string Name, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, w) in pairs)
        {
            d[n] = w;
        }

        return d;
    }

    /// <summary>The new value when it (trimmed) differs from the current one, otherwise null.</summary>
    internal static string? New(string current, string? desired)
    {
        if (desired is null)
        {
            return null;
        }

        var g = desired.Trim();
        return string.Equals(current.Trim(), g, StringComparison.Ordinal) ? null : g;
    }

    private static string Text(IReadOnlyDictionary<string, string> p) => string.Join(", ", p.Select(kv => $"{kv.Key}={kv.Value}"));

    private static string Text((string Name, string Value) a, (string Name, string Value) b, IReadOnlyDictionary<string, string> changed)
        => string.Join(", ", new[] { a, b }.Where(x => changed.ContainsKey(x.Name)).Select(x => $"{x.Name}={x.Value}"));

    internal static string FirstNonEmpty(params string?[] w) => w.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}
