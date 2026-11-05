using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>
/// Replaces one object that was read again in the data and updates the relations (memberOf of users, members of groups,
/// cloud membership, mailbox permissions). So after a change the app shows the new state right away without loading
/// everything again.
/// </summary>
public static class ObjectRefresher
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>Replaces an AD user (null = no longer exists); the member lists of the AD groups follow its
    /// memberOf.</summary>
    public static InventoryData WithAdUser(InventoryData d, string sam, AdUser? newValue)
    {
        var old = d.AdUsers.FirstOrDefault(u => Cmp.Equals(u.SamAccountName, sam));
        var users = d.AdUsers.Where(u => !Cmp.Equals(u.SamAccountName, sam)).ToList();
        if (newValue is not null)
        {
            users.Add(newValue);
        }

        var route = new[] { old?.DistinguishedName, newValue?.DistinguishedName }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToHashSet(Cmp);
        var memberOf = newValue?.MemberOr.ToHashSet(Cmp) ?? new HashSet<string>(Cmp);
        var groups = d.AdGroupsByDn.Values.Select(g =>
        {
            if (!g.MembersKnown)
            {
                return g;
            }

            var members = g.Members.Where(l => !route.Contains(l)).ToList();
            if (newValue is not null && memberOf.Contains(g.DistinguishedName))
            {
                members.Add(newValue.DistinguishedName);
            }

            return members.Count == g.Members.Count && members.SequenceEqual(g.Members, Cmp) ? g : g with { Members = members, CountMembers = members.Count };
        }).ToList();

        return Rebuild(d, adUsers: users, adGroups: groups);
    }

    /// <summary>Replaces an AD group (null = no longer exists); memberOf of the users follows the new member
    /// list.</summary>
    public static InventoryData WithAdGroup(InventoryData d, string sam, AdGroup? newValue)
    {
        var old = d.AdGroupsByDn.Values.FirstOrDefault(g => Cmp.Equals(g.SamAccountName, sam));
        var groups = d.AdGroupsByDn.Values.Where(g => !Cmp.Equals(g.SamAccountName, sam)).ToList();
        if (newValue is not null)
        {
            groups.Add(newValue);
        }

        var oldDns = new[] { old?.DistinguishedName, newValue?.DistinguishedName }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToHashSet(Cmp);
        var members = newValue?.Members.ToHashSet(Cmp) ?? new HashSet<string>(Cmp);
        var users = d.AdUsers.Select(u =>
        {
            var memberOr = u.MemberOr.Where(m => !oldDns.Contains(m)).ToList();
            if (newValue is not null && members.Contains(u.DistinguishedName))
            {
                memberOr.Add(newValue.DistinguishedName);
            }

            return memberOr.Count == u.MemberOr.Count && memberOr.SequenceEqual(u.MemberOr, Cmp) ? u : u with { MemberOr = memberOr };
        }).ToList();

        return Rebuild(d, adUsers: users, adGroups: groups);
    }

    /// <summary>Replaces or adds an Entra user, with its direct groups. Old = the id or UPN it was known by.</summary>
    public static InventoryData WithEntraUser(InventoryData d, string old, GraphUserReading? newValue)
    {
        var existing = d.EntraUsers.FirstOrDefault(e => Cmp.Equals(e.Id, old) || Cmp.Equals(e.UserPrincipalName, old));
        var route = new[] { existing?.Id, newValue?.User.Id }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToHashSet(Cmp);
        var users = d.EntraUsers.Where(e => !route.Contains(e.Id)).ToList();
        var membership = Copy(d.CloudMembership);
        foreach (var id in route)
        {
            membership.Remove(id);
        }

        if (newValue is not null)
        {
            users.Add(newValue.User);
            membership[newValue.User.Id] = newValue.GroupIds.ToHashSet(Cmp);
        }

        return Rebuild(d, entraUsers: users, membership: membership);
    }

    /// <summary>Replaces a cloud group (null = no longer exists), with the direct members.</summary>
    public static InventoryData WithCloudGroup(InventoryData d, string id, GraphGroupReading? newValue)
    {
        var groups = d.CloudGroups.Values.Where(g => !Cmp.Equals(g.Id, id)).ToList();
        var membership = Copy(d.CloudMembership);
        foreach (var set in membership.Values)
        {
            set.Remove(id);
        }

        if (newValue is not null)
        {
            groups.Add(newValue.Group with { CountMembers = newValue.MembersIds.Count });
            foreach (var member in newValue.MembersIds)
            {
                if (!membership.TryGetValue(member, out var set))
                {
                    membership[member] = set = new HashSet<string>(Cmp);
                }

                set.Add(newValue.Group.Id);
            }
        }

        return Rebuild(d, cloudGroups: groups, membership: membership);
    }

    /// <summary>Replaces rooms, mailboxes (with their permissions) and Exchange groups that were read again; not found
    /// = gone.</summary>
    public static InventoryData WithExchange(InventoryData d, ExchangeObjects o)
    {
        var route = o.NotFound.ToHashSet(Cmp);
        var newRooms = o.Rooms.ToDictionary(r => r.Smtp, Cmp);
        var newMailboxes = o.Data.Mailboxes.ToDictionary(p => p.Smtp, Cmp);
        var newGroups = o.Data.Groups.Where(g => g.Smtp.Length > 0).ToDictionary(g => g.Smtp, Cmp);

        var rooms = d.Rooms.Where(r => !route.Contains(r.Smtp) && !newRooms.ContainsKey(r.Smtp)).Concat(newRooms.Values)
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var mailboxes = d.Mailboxes.Where(p => !route.Contains(p.Smtp) && !newMailboxes.ContainsKey(p.Smtp)).Concat(newMailboxes.Values)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var permissions = d.MailboxPermissions.Where(r => !route.Contains(r.Mailbox) && !newMailboxes.ContainsKey(r.Mailbox)).Concat(o.Data.Permissions).ToList();
        var groups = d.ExoGroups.Where(g => !route.Contains(g.Smtp) && !newGroups.ContainsKey(g.Smtp)).Concat(newGroups.Values).ToList();

        return d with { Rooms = rooms, Mailboxes = mailboxes, MailboxPermissions = permissions, ExoGroups = groups };
    }

    private static Dictionary<string, HashSet<string>> Copy(IReadOnlyDictionary<string, IReadOnlySet<string>> source)
        => source.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value, Cmp), Cmp);

    /// <summary>Puts the data together again (linking persons) and keeps the Exchange data.</summary>
    private static InventoryData Rebuild(
        InventoryData d,
        IReadOnlyList<AdUser>? adUsers = null,
        IReadOnlyList<AdGroup>? adGroups = null,
        IReadOnlyList<EntraUser>? entraUsers = null,
        IReadOnlyList<CloudGroup>? cloudGroups = null,
        Dictionary<string, HashSet<string>>? membership = null)
        => InventoryLoader.Compose(
            adUsers ?? d.AdUsers,
            adGroups ?? d.AdGroupsByDn.Values.ToList(),
            entraUsers ?? d.EntraUsers,
            cloudGroups ?? d.CloudGroups.Values.ToList(),
            membership ?? Copy(d.CloudMembership),
            d.Rooms, d.Licences, d.Sources, d.Origin, d.DomainDn) with
        {
            CloudMemberNames = d.CloudMemberNames,
            ExoGroups = d.ExoGroups,
            Mailboxes = d.Mailboxes,
            MailboxPermissions = d.MailboxPermissions,
            Security = d.Security,
            ConditionalAccess = d.ConditionalAccess,
        };
}
