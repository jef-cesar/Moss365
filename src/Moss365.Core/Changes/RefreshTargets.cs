using System.Text.RegularExpressions;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>
/// Which objects have to be read again: after a plan was run (the targets and the groups in the changes) or at the
/// request of the administrator (Refresh button). Per system the key to read by.
/// </summary>
public sealed partial class RefreshTargets
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>sAMAccountName of AD users (stays valid after a move).</summary>
    public HashSet<string> AdUsers { get; } = new(Cmp);

    public HashSet<string> AdGroups { get; } = new(Cmp);

    /// <summary>Id or UPN of Entra users.</summary>
    public HashSet<string> EntraUsers { get; } = new(Cmp);

    public HashSet<string> CloudGroups { get; } = new(Cmp);

    /// <summary>Addresses of rooms, mailboxes and Exchange groups.</summary>
    public HashSet<string> ExchangeAddresses { get; } = new(Cmp);

    /// <summary>Keys (<see cref="Key"/>) of the objects concerned, to mark them as changed.</summary>
    public HashSet<string> Objects { get; } = new(Cmp);

    /// <summary>Read Conditional Access again (after a change to a policy or a named location).</summary>
    public bool ConditionalAccess { get; set; }

    public bool Empty => !ConditionalAccess && AdUsers.Count + AdGroups.Count + EntraUsers.Count + CloudGroups.Count + ExchangeAddresses.Count == 0;

    public int Count => (ConditionalAccess ? 1 : 0) + AdUsers.Count + AdGroups.Count + EntraUsers.Count + CloudGroups.Count + ExchangeAddresses.Count;

    /// <summary>Key of an object for the status "read at" and "changed".</summary>
    public static string Key(ObjectRef r) => $"{r.Kind}:{r.Key}".ToLowerInvariant();

    public static RefreshTargets For(InventoryData d, IEnumerable<ObjectRef> objects)
    {
        var targets = new RefreshTargets();
        foreach (var r in objects)
        {
            targets.Add(d, r);
        }

        return targets;
    }

    /// <summary>The objects a plan that was run touched (only the changes that succeeded).</summary>
    public static RefreshTargets AfterPlan(InventoryData d, IEnumerable<Change> plan, IEnumerable<ChangeResult> results)
    {
        var succeeded = results.Where(r => r.Status == ResultStatus.Succeeded).ToDictionary(r => r.ChangeId);
        var targets = new RefreshTargets();
        foreach (var w in plan.Where(w => succeeded.ContainsKey(w.Id)))
        {
            switch (w.ChangeAction)
            {
                case ChangeAction.AdCreateUser:
                    // New account: not in the data yet, read by sAMAccountName
                    var sam = w.P("sAMAccountName");
                    if (sam.Length > 0)
                    {
                        targets.AdUsers.Add(sam);
                        targets.Objects.Add($"gebruiker:ad:{sam}".ToLowerInvariant());
                    }

                    break;
                case ChangeAction.GraphInviteGuest:
                    // The invitation returns the id of the new guest
                    if (Guid().Match(succeeded[w.Id].Message) is { Success: true } id)
                    {
                        targets.EntraUsers.Add(id.Value);
                    }

                    break;
                case ChangeAction.ExchangeCreateSharedMailbox:
                case ChangeAction.ExchangeCreateGroup:
                    targets.ExchangeAddresses.Add(w.Target);
                    break;
                case ChangeAction.AdCreateGroup:
                    // New group: read by sAMAccountName (with the members that were added right away)
                    if (w.P("sAMAccountName") is { Length: > 0 } groupSam)
                    {
                        targets.AdGroups.Add(groupSam);
                    }

                    break;
                case ChangeAction.GraphCreateGroup:
                    // Graph returns the id of the new group
                    if (Guid().Match(succeeded[w.Id].Message) is { Success: true } groupId)
                    {
                        targets.CloudGroups.Add(groupId.Value);
                    }

                    break;
                case ChangeAction.GraphCreateCaPolicy:
                case ChangeAction.GraphUpdateCaPolicy:
                case ChangeAction.GraphCreateCaLocation:
                case ChangeAction.GraphUpdateCaLocation:
                    targets.ConditionalAccess = true;
                    continue;
                default:
                    targets.AddId(d, w.Target);
                    break;
            }

            foreach (var p in new[] { "groepDn", "groepId", "groep" })
            {
                if (w.Parameters.TryGetValue(p, out var groupValue) && groupValue.Length > 0)
                {
                    targets.AddId(d, groupValue);
                }
            }
        }

        return targets;
    }

    private void AddId(InventoryData d, string id)
    {
        if (d.Index.Find(id) is { } r)
        {
            Add(d, r);
        }
        else if (id.Contains('@'))
        {
            // Unknown address (e.g. just created): Exchange looks it up itself
            ExchangeAddresses.Add(id);
        }
    }

    private void Add(InventoryData d, ObjectRef r)
    {
        switch (r.Kind)
        {
            case ObjectKind.User when d.SearchPerson(r.Key) is { } p:
                if (p.Ad is not null)
                {
                    AdUsers.Add(p.Ad.SamAccountName);
                }

                if (p.Entra is not null)
                {
                    EntraUsers.Add(p.Entra.Id);
                }
                else if (p.Upn.Length > 0)
                {
                    // After the synchronisation it may already exist in Entra
                    EntraUsers.Add(p.Upn);
                }

                break;
            case ObjectKind.Group when d.Index.Group(r.Key) is { } g:
                if (g.Ad is not null)
                {
                    AdGroups.Add(g.Ad.SamAccountName);
                }

                if (g.Cloud is not null)
                {
                    CloudGroups.Add(g.Cloud.Id);
                }

                if (g.Exo is not null)
                {
                    ExchangeAddresses.Add(g.Exo.Smtp);
                }

                break;
            case ObjectKind.Mailbox:
            case ObjectKind.Room:
                ExchangeAddresses.Add(r.Key);
                break;
            default:
                return;
        }

        Objects.Add(Key(r));
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();
}
