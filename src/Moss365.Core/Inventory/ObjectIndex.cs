using Moss365.Core.Analysis;

namespace Moss365.Core.Inventory;

public enum ObjectKind
{
    User,
    Group,
    Mailbox,
    Room,
    Device,
    Contact,
    Unknown,
}

/// <summary>Reference to an object with its own page in the app (or a recognisable name when it is not
/// known).</summary>
public sealed record ObjectRef(ObjectKind Kind, string Key, string Name, string Detail = "")
{
    /// <summary>Relative path to the page, or null when the object has no page.</summary>
    public string? Route => Kind switch
    {
        ObjectKind.User => "gebruiker/" + Uri.EscapeDataString(Key),
        ObjectKind.Group => "groep/" + Uri.EscapeDataString(Key),
        ObjectKind.Mailbox => "postvak/" + Uri.EscapeDataString(Key),
        ObjectKind.Room => "room/" + Uri.EscapeDataString(Key),
        _ => null,
    };

    public string KindText => Kind switch
    {
        ObjectKind.User => "gebruiker",
        ObjectKind.Group => "groep",
        ObjectKind.Mailbox => "postvak",
        ObjectKind.Room => "room",
        ObjectKind.Device => "apparaat",
        ObjectKind.Contact => "contactpersoon",
        _ => "onbekend",
    };

    public bool IsSame(ObjectRef? other) => other is not null && other.Kind == Kind && string.Equals(other.Key, Key, StringComparison.OrdinalIgnoreCase);

    public static ObjectRef Unknown(string name, string detail = "") => new(ObjectKind.Unknown, string.Empty, name, detail);
}

/// <summary>
/// A group across the layers: the AD group, the group in Entra and/or in Exchange. A group synchronised from AD is one
/// group with an AD side and a cloud side.
/// </summary>
public sealed class Group
{
    public required string Key { get; init; }
    public AdGroup? Ad { get; internal set; }
    public CloudGroup? Cloud { get; internal set; }
    public ExoGroup? Exo { get; internal set; }

    public string Name => First(Cloud?.DisplayName, Exo?.DisplayName, Ad?.DisplayName, Ad?.Name);
    public string Mail => First(Cloud?.Mail, Exo?.Smtp, Ad?.Mail);
    public string Description => First(Ad?.Description, Cloud?.Description);
    public bool Dynamic => Cloud?.Dynamic == true;

    /// <summary>Along which route members and attributes are changed.</summary>
    public WriteRoute Route => Dynamic ? WriteRoute.No
        : Ad is not null ? WriteRoute.ActiveDirectory
        : Cloud is not null ? MirrorUser.RouteFor(Cloud)
        : Exo is not null ? WriteRoute.Exchange
        : WriteRoute.No;

    public string KindText
    {
        get
        {
            if (Ad is not null)
            {
                var kind = $"AD-{(string.Equals(Ad.Category, "Distribution", StringComparison.OrdinalIgnoreCase) ? "distributiegroep" : "beveiligingsgroep")}";
                return Cloud is not null ? kind + " (gesynchroniseerd naar Entra)" : kind;
            }

            if (Cloud is not null)
            {
                return Cloud.TypeText + (Dynamic ? " (dynamisch)" : string.Empty);
            }

            return Exo?.TypeText ?? "Groep";
        }
    }

    public string Layers => string.Join(" + ", new[] { Ad is null ? null : "AD", Cloud is null ? null : "Entra", Exo is null ? null : "Exchange" }.Where(l => l is not null));

    public ObjectRef Ref => new(ObjectKind.Group, Key, Name, KindText);

    private static string First(params string?[] w) => w.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}

/// <summary>A member or parent of a group, with the layer in which the relation exists.</summary>
public sealed record Relation(string Layer, ObjectRef Object);

/// <summary>A permission on a mailbox with the recognised trustee.</summary>
public sealed record PermissionRule(Mailbox Mailbox, string Permission, string Trustee, ObjectRef Who, ObjectRef? ViaGroup = null)
{
    /// <summary>Directly on a person instead of via a group.</summary>
    public bool Direct => Who.Kind != ObjectKind.Group;
}

/// <summary>
/// Finds objects by name, address, DN or id and links users, groups, mailboxes and rooms. Built once per data set (see
/// <see cref="InventoryData.Index"/>).
/// </summary>
public sealed class ObjectIndex
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    private readonly InventoryData _data;
    private readonly Dictionary<string, ObjectRef> _strong = new(Cmp);
    private readonly Dictionary<string, ObjectRef> _weak = new(Cmp);
    private readonly Dictionary<string, Group> _groups = new(Cmp);
    private readonly Dictionary<string, Mailbox> _mailboxes = new(Cmp);
    private readonly Dictionary<string, Room> _rooms = new(Cmp);
    private readonly Dictionary<string, List<string>> _cloudMembers = new(Cmp);
    private readonly Dictionary<string, List<Group>> _adParents = new(Cmp);
    private readonly Dictionary<string, List<Group>> _exoParents = new(Cmp);
    private readonly List<PermissionRule> _permissions = new();

    public ObjectIndex(InventoryData data)
    {
        _data = data;
        BuildGroups();
        BuildLookupTable();
        BuildRelations();
    }

    public IReadOnlyCollection<Group> Groups => _groups.Values;
    public IReadOnlyList<PermissionRule> Permissions => _permissions;

    public Group? Group(string key) => _groups.GetValueOrDefault(key);
    public Mailbox? Mailbox(string smtp) => _mailboxes.GetValueOrDefault(smtp);
    public Room? Room(string smtp) => _rooms.GetValueOrDefault(smtp);

    /// <summary>
    /// The object ID in Entra (Azure) of a user, cloud group, or the account of a mailbox or room; null when the object
    /// is not in the loaded Entra data (e.g. a group that exists only in AD or Exchange).
    /// </summary>
    public string? EntraIdOf(ObjectRef r)
    {
        var id = r.Kind switch
        {
            ObjectKind.User => _data.SearchPerson(r.Key)?.Entra?.Id,
            ObjectKind.Group => Group(r.Key)?.Cloud?.Id,
            ObjectKind.Mailbox => Mailbox(r.Key) is { } p ? AccountOf(p.Smtp, p.Alias)?.Entra?.Id : null,
            ObjectKind.Room => Room(r.Key) is { } k ? AccountOf(k.Smtp, k.Alias)?.Entra?.Id : null,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private HashSet<string>? _mailboxAliases;

    /// <summary>
    /// Is this account actually a mailbox (shared mailbox, room, scheduling mailbox)? Such accounts also have a user in
    /// Entra (and sometimes in AD), but nobody signs in with them: they do not belong in the numbers about employees,
    /// sign-ins or MFA. Recognised by the address or the alias.
    /// </summary>
    public bool IsMailboxAccount(Person p)
    {
        foreach (var address in new[] { p.Entra?.Mail, p.Entra?.UserPrincipalName, p.Ad?.EmailAddress, p.Ad?.UserPrincipalName })
        {
            if (!string.IsNullOrWhiteSpace(address) && (_mailboxes.ContainsKey(address.Trim()) || _rooms.ContainsKey(address.Trim())))
            {
                return true;
            }
        }

        _mailboxAliases ??= _data.Mailboxes.Select(x => x.Alias).Concat(_data.Rooms.Select(r => r.Alias))
            .Where(a => !string.IsNullOrWhiteSpace(a)).ToHashSet(Cmp);
        return p.Entra?.MailNickname is { Length: > 0 } alias && _mailboxAliases.Contains(alias);
    }

    /// <summary>
    /// The account (AD and/or Entra) behind a mailbox or room: the account with that address, or with the same alias.
    /// Null when the account is not loaded.
    /// </summary>
    public Person? AccountOf(string smtp, string? alias = null)
    {
        _accountOnAddress ??= BuildAccountsByAddress();
        if (_accountOnAddress.TryGetValue(smtp.Trim(), out var p))
        {
            return p;
        }

        return alias is { Length: > 0 } a
            ? _data.Persons.FirstOrDefault(x => string.Equals(x.Entra?.MailNickname, a, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private Dictionary<string, Person>? _accountOnAddress;

    private Dictionary<string, Person> BuildAccountsByAddress()
    {
        var output = new Dictionary<string, Person>(Cmp);
        foreach (var p in _data.Persons)
        {
            foreach (var address in new[] { p.Entra?.Mail, p.Entra?.UserPrincipalName, p.Ad?.EmailAddress, p.Ad?.UserPrincipalName })
            {
                if (!string.IsNullOrWhiteSpace(address))
                {
                    output.TryAdd(address.Trim(), p);
                }
            }
        }

        return output;
    }

    public Group? GroupOf(AdGroup g) => Find(g.DistinguishedName) is { Kind: ObjectKind.Group } r ? Group(r.Key) : null;
    public Group? GroupOf(CloudGroup g) => Find(g.Id) is { Kind: ObjectKind.Group } r ? Group(r.Key) : null;

    /// <summary>Recognises a name, address, DN, id or DOMAIN\name. Addresses and DNs win over display names.</summary>
    public ObjectRef? Find(string? identity)
    {
        var s = identity?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        if (_strong.TryGetValue(s, out var r) || _weak.TryGetValue(s, out r))
        {
            return r;
        }

        var slash = s.IndexOf('\\');
        if (slash > 0 && _strong.TryGetValue(s[(slash + 1)..], out r))
        {
            return r;
        }

        return null;
    }

    /// <summary>Like <see cref="Find"/>, but always a reference: unknown values stay readable.</summary>
    public ObjectRef Ref(string identity, string detail = "")
        => Find(identity) ?? ObjectRef.Unknown(identity,
            detail.Length > 0 ? detail : identity.StartsWith("S-1-5-", StringComparison.OrdinalIgnoreCase) ? "verwijderd account (SID)" : "niet gevonden");

    public ObjectRef Ref(Person p) => new(ObjectKind.User, p.Key, p.Name, p.Upn);

    // ------------------------------------------------------------------ relations of a group

    public IReadOnlyList<Relation> MembersOf(Group g)
    {
        var output = new List<Relation>();
        if (g.Ad is { } ad)
        {
            var dns = ad.MembersKnown
                ? ad.Members
                : _data.AdUsers.Where(u => u.MemberOr.Contains(ad.DistinguishedName, Cmp)).Select(u => u.DistinguishedName).ToList();
            output.AddRange(dns.Distinct(Cmp).Select(dn => new Relation("AD", Find(dn) ?? ObjectRef.Unknown(DnHelp.Name(dn), DnHelp.OuPathOfObject(dn)))));
        }

        if (g.Cloud is { } cloud && _cloudMembers.TryGetValue(cloud.Id, out var ids))
        {
            output.AddRange(ids.Distinct(Cmp).Select(id => new Relation("Entra", CloudMember(id))));
        }

        if (g.Exo is { } exo)
        {
            output.AddRange(exo.Members.Select(l => new Relation("Exchange", Find(l.Smtp) ?? Find(l.Name) ?? ObjectRef.Unknown(First(l.Name, l.Smtp), l.Smtp))));
        }

        return Sort(output);
    }

    /// <summary>Like <see cref="MembersOf"/>, but a member that is in several layers (e.g. AD and Entra) once, with all
    /// layers.</summary>
    public IReadOnlyList<Relation> UniqueMembersOf(Group g)
        => MembersOf(g)
            .GroupBy(r => r.Object.Kind == ObjectKind.Unknown ? "?" + r.Object.Name : r.Object.Kind + ":" + r.Object.Key, Cmp)
            .Select(x => new Relation(string.Join(", ", x.Select(r => r.Layer).Distinct()), x.First().Object))
            .ToList();

    /// <summary>The groups this group itself is a member of (nested groups).</summary>
    public IReadOnlyList<Relation> MemberOf(Group g)
    {
        var output = new List<Relation>();
        if (g.Ad is { } ad && _adParents.TryGetValue(ad.DistinguishedName, out var adParents))
        {
            output.AddRange(adParents.Select(o => new Relation("AD", o.Ref)));
        }

        if (g.Cloud is { } cloud && _data.CloudMembership.TryGetValue(cloud.Id, out var cloudParents))
        {
            output.AddRange(cloudParents.Select(id => Find(id)).OfType<ObjectRef>().Select(r => new Relation("Entra", r)));
        }

        if (g.Mail.Length > 0 && _exoParents.TryGetValue(g.Mail, out var exoParents))
        {
            output.AddRange(exoParents.Select(o => new Relation("Exchange", o.Ref)));
        }

        return Sort(output);
    }

    /// <summary>Managers: managedBy in AD and ManagedBy in Exchange. Owners of cloud groups only come live.</summary>
    public IReadOnlyList<Relation> AdministratorsOf(Group g)
    {
        var output = new List<Relation>();
        if (g.Ad is { ManagedBy.Length: > 0 } ad)
        {
            output.Add(new Relation("AD", Ref(ad.ManagedBy)));
        }

        if (g.Exo is { } exo)
        {
            output.AddRange(exo.ManagedBy.Select(m => new Relation("Exchange", Ref(m))));
        }

        return output;
    }

    public IReadOnlyList<PermissionRule> PermissionsOf(ObjectRef who) => _permissions.Where(r => r.Who.IsSame(who)).ToList();

    // ------------------------------------------------------------------ relations of a mailbox

    public IReadOnlyList<PermissionRule> PermissionsOn(Mailbox p)
        => _permissions.Where(r => r.Mailbox == p).OrderBy(r => r.Who.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Permission).ToList();

    /// <summary>The group that gives access: an existing SG-SM group with permissions, otherwise the group with the
    /// expected name.</summary>
    public Group? AccessGroupOf(Mailbox p)
    {
        var withPermissions = PermissionsOn(p).Where(r => r.Who.Kind == ObjectKind.Group).Select(r => Group(r.Who.Key)).OfType<Group>().ToList();
        var sgSm = withPermissions.FirstOrDefault(g => g.Name.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase));
        if (sgSm is not null)
        {
            return sgSm;
        }

        if (Find(ExpectedAccessGroup(p)) is { Kind: ObjectKind.Group } expected)
        {
            return Group(expected.Key);
        }

        return withPermissions.FirstOrDefault();
    }

    public static string ExpectedAccessGroup(Mailbox p) => $"SG-SM-{p.LocalPart}-Full";

    /// <summary>Groups the mailbox (or room) is a member of, in Exchange and in Entra.</summary>
    public IReadOnlyList<Relation> GroupsOfAddress(string smtp)
    {
        var output = new List<Relation>();
        if (_exoParents.TryGetValue(smtp, out var exo))
        {
            output.AddRange(exo.Select(g => new Relation("Exchange", g.Ref)));
        }

        var entra = _data.EntraUsers.FirstOrDefault(e => Cmp.Equals(e.UserPrincipalName, smtp) || Cmp.Equals(e.Mail, smtp));
        if (entra is not null && _data.CloudMembership.TryGetValue(entra.Id, out var ids))
        {
            output.AddRange(ids.Select(id => Find(id)).OfType<ObjectRef>().Select(r => new Relation("Entra", r)));
        }

        return Sort(output.GroupBy(r => r.Object.Key, Cmp).Select(g => g.First()).ToList());
    }

    // ------------------------------------------------------------------ relations of a person

    /// <summary>Mailbox permissions of a person: direct and via the groups it is a member of.</summary>
    public IReadOnlyList<PermissionRule> MailboxPermissionsOf(Person p)
    {
        var me = Ref(p);
        var groups = GroupsOf(p).ToDictionary(g => g.Key, Cmp);
        var output = new List<PermissionRule>();
        foreach (var r in _permissions)
        {
            if (r.Who.IsSame(me))
            {
                output.Add(r);
            }
            else if (r.Who.Kind == ObjectKind.Group && groups.TryGetValue(r.Who.Key, out var via))
            {
                output.Add(r with { ViaGroup = via.Ref });
            }
        }

        return output.OrderBy(r => r.Mailbox.DisplayName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Permission).ToList();
    }

    /// <summary>All groups of a person (AD, Entra and Exchange by address), direct member.</summary>
    public IEnumerable<Group> GroupsOf(Person p)
    {
        var seen = new HashSet<string>(Cmp);
        foreach (var g in _data.AdGroupsOf(p))
        {
            if (Find(g.DistinguishedName) is { Kind: ObjectKind.Group } r && seen.Add(r.Key) && Group(r.Key) is { } groupValue)
            {
                yield return groupValue;
            }
        }

        foreach (var g in _data.CloudGroupsOf(p))
        {
            if (GroupOf(g) is { } groupValue && seen.Add(groupValue.Key))
            {
                yield return groupValue;
            }
        }

        foreach (var address in new[] { p.Upn, p.Entra?.Mail, p.Ad?.EmailAddress }.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(Cmp))
        {
            if (_exoParents.TryGetValue(address!, out var exo))
            {
                foreach (var g in exo.Where(g => seen.Add(g.Key)))
                {
                    yield return g;
                }
            }
        }
    }

    // ------------------------------------------------------------------ construction

    private void BuildGroups()
    {
        foreach (var ad in _data.AdGroupsByDn.Values)
        {
            var key = "ad:" + (ad.SamAccountName.Length > 0 ? ad.SamAccountName : ad.DistinguishedName).ToLowerInvariant();
            _groups.TryAdd(key, new Group { Key = key, Ad = ad });
        }

        var adOnSam = _groups.Values.Where(g => g.Ad!.SamAccountName.Length > 0).ToDictionary(g => g.Ad!.SamAccountName, Cmp);
        foreach (var cloud in _data.CloudGroups.Values)
        {
            if (cloud.FromAd && cloud.OnPremisesSamAccountName.Length > 0 && adOnSam.TryGetValue(cloud.OnPremisesSamAccountName, out var ad) && ad.Cloud is null)
            {
                ad.Cloud = cloud;
            }
            else
            {
                var key = "cloud:" + cloud.Id.ToLowerInvariant();
                _groups.TryAdd(key, new Group { Key = key, Cloud = cloud });
            }
        }

        var onMail = _groups.Values.Where(g => g.Mail.Length > 0).GroupBy(g => g.Mail, Cmp).ToDictionary(g => g.Key, g => g.First(), Cmp);
        foreach (var exo in _data.ExoGroups)
        {
            if (exo.Smtp.Length > 0 && onMail.TryGetValue(exo.Smtp, out var g) && g.Exo is null)
            {
                g.Exo = exo;
            }
            else
            {
                var key = "exo:" + First(exo.Smtp, exo.Alias, exo.DisplayName).ToLowerInvariant();
                _groups.TryAdd(key, new Group { Key = key, Exo = exo });
            }
        }
    }

    private void BuildLookupTable()
    {
        void Strong(string? k, ObjectRef r)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                _strong.TryAdd(k.Trim(), r);
            }
        }

        void Weak(string? k, ObjectRef r)
        {
            if (!string.IsNullOrWhiteSpace(k))
            {
                _weak.TryAdd(k.Trim(), r);
            }
        }

        // Mailboxes and rooms first: their cloud account is also a "user", but the mailbox page is more useful
        foreach (var p in _data.Mailboxes.Where(p => p.Smtp.Length > 0))
        {
            _mailboxes.TryAdd(p.Smtp, p);
            var r = new ObjectRef(ObjectKind.Mailbox, p.Smtp, First(p.DisplayName, p.Smtp), p.TypeText);
            Strong(p.Smtp, r);
            Strong(p.Alias, r);
            Weak(p.DisplayName, r);
        }

        foreach (var room in _data.Rooms.Where(r => r.Smtp.Length > 0))
        {
            _rooms.TryAdd(room.Smtp, room);
            var r = new ObjectRef(ObjectKind.Room, room.Smtp, First(room.DisplayName, room.Smtp), "room");
            Strong(room.Smtp, r);
            Strong(room.Alias, r);
            Weak(room.DisplayName, r);
        }

        foreach (var e in _data.EntraUsers)
        {
            // The Entra account behind a mailbox or room points to that page
            if (Find(e.UserPrincipalName) is { Kind: ObjectKind.Mailbox or ObjectKind.Room } behind)
            {
                Strong(e.Id, behind);
            }
            else if (Find(e.Mail) is { Kind: ObjectKind.Mailbox or ObjectKind.Room } behind2)
            {
                Strong(e.Id, behind2);
            }
        }

        foreach (var g in _groups.Values)
        {
            var r = g.Ref;
            Strong(g.Key, r);
            Strong(g.Ad?.DistinguishedName, r);
            Strong(g.Ad?.SamAccountName, r);
            Strong(g.Cloud?.Id, r);
            Strong(g.Mail, r);
            Strong(g.Exo?.Smtp, r);
            Strong(g.Exo?.Alias, r);
            Strong(g.Exo?.Name, r);
            Weak(g.Name, r);
            Weak(g.Ad?.Name, r);
            Weak(g.Exo?.DisplayName, r);
        }

        foreach (var p in _data.Persons)
        {
            var r = Ref(p);
            Strong(p.Key, r);
            Strong(p.Ad?.DistinguishedName, r);
            Strong(p.Ad?.SamAccountName, r);
            Strong(p.Ad?.UserPrincipalName, r);
            Strong(p.Ad?.EmailAddress, r);
            Strong(p.Entra?.Id, r);
            Strong(p.Entra?.UserPrincipalName, r);
            Strong(p.Entra?.Mail, r);
            Weak(p.Name, r);
        }

        foreach (var (id, (kind, name)) in _data.CloudMemberNames)
        {
            var objKind = kind.Contains("device", StringComparison.OrdinalIgnoreCase) ? ObjectKind.Device
                : kind.Contains("contact", StringComparison.OrdinalIgnoreCase) ? ObjectKind.Contact
                : ObjectKind.Unknown;
            Strong(id, new ObjectRef(objKind, string.Empty, First(name, id), kind));
        }
    }

    private void BuildRelations()
    {
        foreach (var (member, groupIds) in _data.CloudMembership)
        {
            foreach (var g in groupIds)
            {
                if (!_cloudMembers.TryGetValue(g, out var list))
                {
                    _cloudMembers[g] = list = new List<string>();
                }

                list.Add(member);
            }
        }

        foreach (var g in _groups.Values)
        {
            if (g.Ad is { MembersKnown: true } ad)
            {
                foreach (var dn in ad.Members)
                {
                    Add(_adParents, dn, g);
                }
            }

            if (g.Exo is { } exo)
            {
                foreach (var l in exo.Members)
                {
                    Add(_exoParents, l.Smtp, g);
                }
            }
        }

        foreach (var r in _data.MailboxPermissions)
        {
            if (_mailboxes.TryGetValue(r.Mailbox, out var p))
            {
                _permissions.Add(new PermissionRule(p, r.Permission, r.Trustee, Ref(r.Trustee)));
            }
        }
    }

    private ObjectRef CloudMember(string id) => Find(id) ?? ObjectRef.Unknown(id, "onbekend lid");

    private static void Add(Dictionary<string, List<Group>> d, string key, Group g)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (!d.TryGetValue(key, out var list))
        {
            d[key] = list = new List<Group>();
        }

        if (!list.Contains(g))
        {
            list.Add(g);
        }
    }

    private static IReadOnlyList<Relation> Sort(List<Relation> list)
        => list.OrderBy(r => r.Layer == "AD" ? 0 : r.Layer == "Entra" ? 1 : 2)
            .ThenBy(r => r.Object.Kind)
            .ThenBy(r => r.Object.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string First(params string?[] w) => w.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}
