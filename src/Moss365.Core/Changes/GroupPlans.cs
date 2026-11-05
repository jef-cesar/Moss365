using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

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
            return PlanOutcome.Error($"{g.Name} is een dynamische groep: de leden volgen uit de regel, niet uit handmatige wijzigingen.");
        }

        if (add && g.Ad is { } adg && MirrorUser.IsPrivileged(adg))
        {
            return PlanOutcome.Error($"{g.Name} is een bevoorrechte groep; leden toevoegen doe je bewust buiten deze app.");
        }

        var verb = add ? "Lid maken van" : "Uit";
        switch (g.Route)
        {
            case WriteRoute.ActiveDirectory:
            {
                var dn = AdDn(data, member);
                if (dn is null)
                {
                    return PlanOutcome.Error($"{member.Name} heeft geen AD-object; {g.Name} is een AD-groep (ook in de cloud gaat het lidmaatschap via AD).");
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
                        Description = add ? $"Lid maken van AD-groep {g.Name}" : $"Uit AD-groep {g.Name} halen",
                        DependentOf = dependentOf,
                    },
                });
            }

            case WriteRoute.Graph:
            {
                var id = EntraId(data, member);
                if (id is null)
                {
                    return PlanOutcome.Error($"{member.Name} bestaat niet in Entra.");
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
                        Description = add ? $"Lid maken van cloudgroep {g.Name}" : $"Uit cloudgroep {g.Name} halen",
                        DependentOf = dependentOf,
                    },
                });
            }

            case WriteRoute.Exchange:
            {
                var address = Address(data, member);
                if (address is null)
                {
                    return PlanOutcome.Error($"{member.Name} heeft geen e-mailadres; alleen ontvangers kunnen lid zijn van een mailgroep.");
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
                        Description = $"{verb} {g.Name} (Exchange)" + (add ? string.Empty : " halen")
                                       + (add && g.Name.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase) ? ": toegang tot een gedeeld postvak" : string.Empty),
                        DependentOf = dependentOf,
                    },
                });
            }

            default:
                return PlanOutcome.Error($"Voor {g.Name} is geen schrijfweg bekend.");
        }
    }

    /// <summary>Display name, description and (mail groups) e-mail address; only what differs.</summary>
    public static PlanOutcome Attributes(Group g, GroupDesired desired)
    {
        var errors = new List<string>();
        var plan = new List<Change>();
        var name = New(g.Name, desired.DisplayName);
        var description = New(g.Description, desired.Description);
        var mail = New(g.Mail, desired.Mail);

        switch (g.Route)
        {
            case WriteRoute.ActiveDirectory:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (name is not null) p["displayName"] = name;
                if (description is not null) p["description"] = description;
                if (mail is not null) errors.Add("Het e-mailadres van een AD-groep wijzig je niet hier (het domein heeft geen Exchange-schema).");
                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = ChangeAction.AdSetAttributes,
                        Target = g.Ad!.DistinguishedName,
                        TargetName = g.Name,
                        Parameters = p,
                        Description = "Naam en/of omschrijving van de AD-groep aanpassen" + (g.Cloud is null ? string.Empty : " (Entra volgt na de synchronisatie)"),
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
                if (mail is not null) errors.Add("Het e-mailadres van een Microsoft 365-groep wijzig je in Exchange; dat zit nog niet in de app.");
                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Graph,
                        ChangeAction = ChangeAction.GraphSetGroupAttributes,
                        Target = g.Cloud!.Id,
                        TargetName = g.Name,
                        Parameters = p,
                        Description = "Naam en/of omschrijving van de cloudgroep aanpassen",
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
                if (description is not null) errors.Add("Een mailgroep heeft in Exchange geen omschrijving die de app kan zetten; de omschrijving blijft ongewijzigd.");
                if (p.Count > 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeUpdateGroup,
                        Target = ExoIdentity(g),
                        TargetName = g.Name,
                        Parameters = p,
                        Description = "Naam en/of e-mailadres van de mailgroep aanpassen",
                        CurrentValue = Text(("DisplayName", g.Name), ("PrimarySmtpAddress", g.Mail), p),
                        NewValue = Text(p),
                        Previous = Previous(p, ("DisplayName", g.Name), ("PrimarySmtpAddress", g.Mail)),
                    });
                }

                break;
            }

            default:
                errors.Add($"{g.Name} is dynamisch of heeft geen gekende schrijfweg.");
                break;
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>Add or remove a manager (AD managedBy, Exchange ManagedBy) or owner (cloud group).</summary>
    public static PlanOutcome Administrator(InventoryData data, Group g, ObjectRef who, bool add)
    {
        if (g.Ad is not null)
        {
            var dn = AdDn(data, who);
            if (add && dn is null)
            {
                return PlanOutcome.Error($"{who.Name} heeft geen AD-object en kan geen beheerder van een AD-groep zijn.");
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
                    Description = add ? $"{who.Name} beheerder maken (managedBy; een AD-groep heeft er maar één)" : "Beheerder (managedBy) weghalen",
                    CurrentValue = g.Ad.ManagedBy.Length > 0 ? DnHelp.Name(g.Ad.ManagedBy) : "(geen)",
                    NewValue = add ? who.Name : "(geen)",
                },
            });
        }

        if (g.Route == WriteRoute.Exchange)
        {
            var address = Address(data, who);
            if (address is null)
            {
                return PlanOutcome.Error($"{who.Name} heeft geen e-mailadres.");
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
                    Description = add ? $"{who.Name} beheerder maken" : $"{who.Name} als beheerder weghalen",
                    NewValue = who.Name,
                },
            });
        }

        if (g.Cloud is not null)
        {
            var id = EntraId(data, who);
            if (id is null)
            {
                return PlanOutcome.Error($"{who.Name} bestaat niet in Entra.");
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
                    Description = add ? $"{who.Name} eigenaar maken" : $"{who.Name} als eigenaar weghalen",
                    NewValue = who.Name,
                },
            });
        }

        return PlanOutcome.Error("Deze groep heeft geen beheerders die de app kan wijzigen.");
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
