using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>
/// Applies the AD changes since the previous read to that previous read. Changed objects (uSNChanged) come fresh from
/// AD; the rest stays, except what was deleted or moved (that follows from the list of all existing objects).
/// Memberships: a changed group has its fresh member list; the "member of" of users is adjusted to it.
/// </summary>
public static class AdDelta
{
    /// <param name="baseline">The previous read.</param>
    /// <param name="users">Users that changed since the previous read (read fresh, with memberOf).</param>
    /// <param name="groups">Groups that changed (read fresh, with their members).</param>
    /// <param name="existingUsers">All users that exist now: sAMAccountName to DN.</param>
    /// <param name="existingGroups">All groups that exist now: sAMAccountName to DN.</param>
    public static (List<AdUser> Users, List<AdGroup> Groups) Apply(
        AdBase baseline,
        IReadOnlyList<AdUser> users,
        IReadOnlyList<AdGroup> groups,
        IReadOnlyDictionary<string, string> existingUsers,
        IReadOnlyDictionary<string, string> existingGroups)
    {
        var ic = StringComparer.OrdinalIgnoreCase;

        // Moved or renamed (old DN to new) and deleted (old DN)
        var newDn = new Dictionary<string, string>(ic);
        var route = new HashSet<string>(ic);
        foreach (var (sam, dn) in baseline.Users.Select(u => (u.SamAccountName, u.DistinguishedName)).Concat(baseline.Groups.Select(g => (g.SamAccountName, g.DistinguishedName))))
        {
            var existing = existingUsers.TryGetValue(sam, out var u) ? u : existingGroups.TryGetValue(sam, out var g) ? g : null;
            if (existing is null)
            {
                route.Add(dn);
            }
            else if (!string.Equals(existing, dn, StringComparison.OrdinalIgnoreCase))
            {
                newDn[dn] = existing;
            }
        }

        string? Now(string dn) => route.Contains(dn) ? null : newDn.TryGetValue(dn, out var n) ? n : dn;
        List<string> Rewrite(IEnumerable<string> dns) => dns.Select(Now).Where(d => d is not null).Select(d => d!).Distinct(ic).ToList();

        // Groups
        var fresh = groups.GroupBy(g => g.SamAccountName, ic).ToDictionary(g => g.Key, g => g.First(), ic);
        var oldMembers = baseline.Groups.ToDictionary(g => g.SamAccountName, g => Rewrite(g.Members), ic);
        var newGroups = new List<AdGroup>();
        foreach (var g in baseline.Groups)
        {
            if (fresh.ContainsKey(g.SamAccountName) || !existingGroups.ContainsKey(g.SamAccountName))
            {
                continue;
            }

            var dn = Now(g.DistinguishedName)!;
            var members = oldMembers[g.SamAccountName];
            newGroups.Add(g with
            {
                DistinguishedName = dn,
                OuPath = DnHelp.OuPathOfObject(dn),
                ManagedBy = g.ManagedBy.Length == 0 ? g.ManagedBy : Now(g.ManagedBy) ?? string.Empty,
                Members = members,
                CountMembers = g.MembersKnown ? members.Count : g.CountMembers,
            });
        }

        newGroups.AddRange(fresh.Values);

        // Adjust "member of" for whoever was added to or removed from a changed group
        var added = new Dictionary<string, HashSet<string>>(ic);
        var removed = new Dictionary<string, HashSet<string>>(ic);
        void Note(Dictionary<string, HashSet<string>> d, string member, string groupValue)
        {
            if (!d.TryGetValue(member, out var s))
            {
                d[member] = s = new HashSet<string>(ic);
            }

            s.Add(groupValue);
        }

        foreach (var g in fresh.Values)
        {
            var old = oldMembers.TryGetValue(g.SamAccountName, out var o) ? o : new List<string>();
            foreach (var member in g.Members.Except(old, ic))
            {
                Note(added, member, g.DistinguishedName);
            }

            foreach (var member in old.Except(g.Members, ic))
            {
                Note(removed, member, g.DistinguishedName);
            }
        }

        // Users
        var freshUsers = users.GroupBy(u => u.SamAccountName, ic).ToDictionary(g => g.Key, g => g.First(), ic);
        var newUsers = new List<AdUser>();
        foreach (var u in baseline.Users)
        {
            if (freshUsers.ContainsKey(u.SamAccountName) || !existingUsers.ContainsKey(u.SamAccountName))
            {
                continue;
            }

            var dn = Now(u.DistinguishedName)!;
            var memberOf = Rewrite(u.MemberOr);
            if (removed.TryGetValue(dn, out var min))
            {
                memberOf.RemoveAll(min.Contains);
            }

            if (added.TryGetValue(dn, out var plus))
            {
                memberOf.AddRange(plus.Where(p => !memberOf.Contains(p, ic)));
            }

            newUsers.Add(u with { DistinguishedName = dn, OuPath = DnHelp.OuPathOfObject(dn), MemberOr = memberOf });
        }

        newUsers.AddRange(freshUsers.Values);
        return (newUsers, newGroups);
    }
}

/// <summary>One changed cloud group from Graph delta.</summary>
/// <param name="Id">Id of the group.</param>
/// <param name="Removed">The group was deleted.</param>
/// <param name="Update">Puts the changed attributes on the previous version (null = new group); null when only the
/// members changed.</param>
/// <param name="MembersAdded">Users who became members.</param>
/// <param name="MembersRemoved">Users who are no longer members.</param>
public sealed record GroupDeltaItem(
    string Id,
    bool Removed,
    Func<CloudGroup?, CloudGroup>? Update,
    IReadOnlyList<string> MembersAdded,
    IReadOnlyList<string> MembersRemoved);

/// <summary>Applies the changes to cloud groups (Graph /groups/delta) to the previous read.</summary>
public static class GraphDelta
{
    public static (List<CloudGroup> Groups, Dictionary<string, HashSet<string>> Membership) Apply(GraphBase baseline, IEnumerable<GroupDeltaItem> items)
    {
        var ic = StringComparer.OrdinalIgnoreCase;
        var groups = new Dictionary<string, CloudGroup>(baseline.Groups, ic);

        // The members per group (the reverse of the membership per user)
        var members = groups.Keys.ToDictionary(id => id, _ => new HashSet<string>(ic), ic);
        foreach (var (user, ids) in baseline.Membership)
        {
            foreach (var id in ids)
            {
                if (!members.TryGetValue(id, out var s))
                {
                    members[id] = s = new HashSet<string>(ic);
                }

                s.Add(user);
            }
        }

        var changed = new HashSet<string>(ic);
        foreach (var item in items)
        {
            if (item.Removed)
            {
                groups.Remove(item.Id);
                members.Remove(item.Id);
                continue;
            }

            groups.TryGetValue(item.Id, out var existing);
            if (item.Update is not null)
            {
                groups[item.Id] = item.Update(existing) with { Id = item.Id };
            }
            else if (existing is null)
            {
                // Only members of a group we do not know: without attributes the app cannot show it
                continue;
            }

            if (!members.TryGetValue(item.Id, out var set))
            {
                members[item.Id] = set = new HashSet<string>(ic);
            }

            set.UnionWith(item.MembersAdded);
            set.ExceptWith(item.MembersRemoved);
            changed.Add(item.Id);
        }

        foreach (var id in changed.Where(groups.ContainsKey))
        {
            groups[id] = groups[id] with { CountMembers = members[id].Count };
        }

        var membership = new Dictionary<string, HashSet<string>>(ic);
        foreach (var (id, s) in members.Where(l => groups.ContainsKey(l.Key)))
        {
            foreach (var user in s)
            {
                if (!membership.TryGetValue(user, out var g))
                {
                    membership[user] = g = new HashSet<string>(ic);
                }

                g.Add(id);
            }
        }

        return (groups.Values.ToList(), membership);
    }
}
