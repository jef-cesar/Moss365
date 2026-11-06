using Moss365.Core.Storage;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>A plan that undoes changes that were run, and what cannot be undone (with the reason).</summary>
public sealed record ReverseOutcome(IReadOnlyList<Change> Plan, IReadOnlyList<string> NotReversible);

/// <summary>
/// Turns succeeded entries from the change log into the reverse plan: add or remove a member again, give a permission
/// back, enable or disable an account again, back to the previous OU, and attributes back to their previous value (when
/// that is in the change log). The plan follows the reverse order, so that every step finds the same state as the
/// original. Not reversible: creating, deleting guests, passwords, sessions, licences and converting mailboxes.
/// </summary>
public static class Undo
{
    private static readonly Dictionary<ChangeAction, ChangeAction> Opposite = new()
    {
        [ChangeAction.AdAddGroupMember] = ChangeAction.AdRemoveGroupMember,
        [ChangeAction.AdRemoveGroupMember] = ChangeAction.AdAddGroupMember,
        [ChangeAction.GraphAddGroupMember] = ChangeAction.GraphRemoveGroupMember,
        [ChangeAction.GraphRemoveGroupMember] = ChangeAction.GraphAddGroupMember,
        [ChangeAction.ExchangeAddGroupMember] = ChangeAction.ExchangeRemoveGroupMember,
        [ChangeAction.ExchangeRemoveGroupMember] = ChangeAction.ExchangeAddGroupMember,
        [ChangeAction.GraphAddOwner] = ChangeAction.GraphRemoveOwner,
        [ChangeAction.GraphRemoveOwner] = ChangeAction.GraphAddOwner,
        [ChangeAction.ExchangeAddMailboxPermission] = ChangeAction.ExchangeRemoveMailboxPermission,
        [ChangeAction.ExchangeRemoveMailboxPermission] = ChangeAction.ExchangeAddMailboxPermission,
        [ChangeAction.AdDisableAccount] = ChangeAction.AdEnableAccount,
        [ChangeAction.AdEnableAccount] = ChangeAction.AdDisableAccount,
    };

    /// <summary>Attributes that can be restored with their previous value.</summary>
    private static readonly HashSet<ChangeAction> Attributes = new()
    {
        ChangeAction.AdSetAttributes, ChangeAction.GraphSetAttributes, ChangeAction.GraphSetGroupAttributes,
        ChangeAction.ExchangeUpdateGroup, ChangeAction.ExchangeUpdateMailbox, ChangeAction.ExchangeAutoReply, ChangeAction.GraphSetManager,
        ChangeAction.GraphUpdateCaPolicy, ChangeAction.GraphUpdateCaLocation,
    };

    /// <summary>Can this entry be undone? If not, why not.</summary>
    public static string? WhyNot(LogEntry r)
    {
        if (!r.Succeeded)
        {
            return "niet gelukt, dus niets terug te draaien";
        }

        if (WasAlreadySo(r))
        {
            return "er veranderde niets (" + r.Message.TrimEnd('.') + ")";
        }

        if (!LegacyNames.TryParseEnum<ChangeAction>(r.ChangeAction, out var action))
        {
            return "onbekende actie";
        }

        if (Opposite.ContainsKey(action) || action == ChangeAction.AdMove || action == ChangeAction.ExchangeGroupManager)
        {
            return null;
        }

        if (Attributes.Contains(action))
        {
            return r.Previous is { Count: > 0 } ? null : "de vorige waarden staan niet in het logboek (wijziging van voor versie 0.5)";
        }

        return action switch
        {
            ChangeAction.AdCreateUser or ChangeAction.ExchangeCreateSharedMailbox or ChangeAction.ExchangeCreateGroup or ChangeAction.GraphCreateCaLocation
                => "aangemaakte objecten verwijdert de app niet",
            ChangeAction.GraphCreateApplication or ChangeAction.GraphUpdateApplication or ChangeAction.GraphCreateServicePrincipal
                or ChangeAction.GraphUpdateServicePrincipal or ChangeAction.GraphGrantConsent or ChangeAction.GraphAssignAppRole
                => "de app-registratie wijzig je via Tenants > Inrichten of in Entra",
            ChangeAction.GraphCreateCaPolicy => "zet het beleid uit bij Conditional Access > Beheren (de app verwijdert geen beleiden)",
            ChangeAction.GraphInviteGuest => "verwijder de gast via Gastgebruikers",
            ChangeAction.GraphRemoveGuest => "terugzetten via Entra > Verwijderde gebruikers (30 dagen)",
            ChangeAction.AdSetPassword => "een vorig wachtwoord kan niet terug",
            ChangeAction.GraphRevokeSessions => "afgemelde sessies kunnen niet terug",
            ChangeAction.ExchangeConvertToSharedMailbox => "zet het postvak zelf terug om in Exchange (licentie nodig)",
            ChangeAction.GraphAssignLicence or ChangeAction.GraphRemoveLicences => "licenties wijzig je via de gebruiker of Entra",
            ChangeAction.AdUnlockAccount => "een account vergrendel je niet opnieuw",
            _ => "deze wijziging kan de app niet terugdraaien",
        };
    }

    public static bool CanReverse(LogEntry r) => WhyNot(r) is null;

    /// <param name="rules">The chosen entries; they are undone from new to old.</param>
    /// <param name="data">The current data: to find a moved AD account again and to recognise privileged
    /// groups.</param>
    public static ReverseOutcome Plan(IEnumerable<LogEntry> rules, InventoryData? data = null)
    {
        var plan = new List<Change>();
        var notValue = new List<string>();

        // The DN of AD accounts can change because of a move (also by a step in this plan); follow it here
        var currentDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Dn(string dn)
        {
            if (currentDn.TryGetValue(dn, out var now))
            {
                return now;
            }

            now = SearchCurrentDn(dn, data);
            currentDn[dn] = now;
            return now;
        }

        foreach (var r in rules.OrderByDescending(r => r.Time))
        {
            if (WhyNot(r) is { } reason)
            {
                notValue.Add($"{r.Description} ({Name(r)}): {reason}.");
                continue;
            }

            var action = LegacyNames.ParseEnum<ChangeAction>(r.ChangeAction);
            var system = SystemOf(action);
            var parameters = new Dictionary<string, string>(r.Parameters, StringComparer.OrdinalIgnoreCase);
            var target = r.Target;
            if (system == TargetSystem.ActiveDirectory && target.Contains('='))
            {
                target = Dn(r.Target);
            }

            Change w;
            if (action == ChangeAction.AdMove)
            {
                // Back to the OU from before; the account is now in the target OU of then
                var previousOu = DnHelp.Parent(r.Target);
                var rdn = r.Target[..(r.Target.Length - previousOu.Length)].TrimEnd(',');
                var now = Dn($"{rdn},{r.Parameters.GetValueOrDefault("doelOu")}");
                w = New(r, ChangeAction.AdMove, now, new() { ["doelOu"] = previousOu },
                    $"Terug naar {DnHelp.OuPath(previousOu)} verplaatsen", DnHelp.OuPathOfObject(now), DnHelp.OuPath(previousOu));
                currentDn[r.Target] = r.Target;
            }
            else if (action == ChangeAction.ExchangeGroupManager)
            {
                var reversed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (parameters.TryGetValue("Toevoegen", out var toValue) && toValue.Length > 0)
                {
                    reversed["Verwijderen"] = toValue;
                }

                if (parameters.TryGetValue("Verwijderen", out var route) && route.Length > 0)
                {
                    reversed["Toevoegen"] = route;
                }

                foreach (var (k, v) in parameters.Where(p => p.Key is not ("Toevoegen" or "Verwijderen")))
                {
                    reversed[k] = v;
                }

                w = New(r, action, target, reversed, "Beheerder terugzetten: " + string.Join(", ",
                    reversed.Where(p => p.Key is "Toevoegen" or "Verwijderen").Select(p => $"{p.Key.ToLowerInvariant()} {p.Value}")));
            }
            else if (Attributes.Contains(action))
            {
                var previous = new Dictionary<string, string>(r.Previous!, StringComparer.OrdinalIgnoreCase);
                // Fixed parameters (e.g. Identity for Exchange) stay; the attributes get their previous value
                foreach (var (k, v) in previous)
                {
                    parameters[k] = v;
                }

                w = New(r, action, target, parameters, "Terugzetten: " + r.Description,
                    string.Join(", ", previous.Keys.Select(k => $"{k}: {r.Parameters.GetValueOrDefault(k)}")),
                    string.Join(", ", previous.Select(p => $"{p.Key}: {(p.Value.Length == 0 ? "(leeg)" : p.Value)}")),
                    previousValues: previous.Keys.ToDictionary(k => k, k => r.Parameters.GetValueOrDefault(k) ?? string.Empty, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                var opposite = Opposite[action];
                w = New(r, opposite, target, parameters, Description(opposite, r));
                if (opposite == ChangeAction.AdAddGroupMember && AdGroup(parameters.GetValueOrDefault("groepDn") ?? string.Empty, data) is { } g
                    && MirrorUser.IsPrivileged(g))
                {
                    w = New(r, opposite, target, parameters, Description(opposite, r),
                        warning: $"{g.Name} is een bevoorrechte groep: zet ze enkel terug als dat echt nodig is.", selected: false);
                }
            }

            plan.Add(w);
        }

        return new ReverseOutcome(plan, notValue);
    }

    /// <summary>Was the change a confirmation of what was already there ("Was al lid.")? Then there is nothing to
    /// undo.</summary>
    private static bool WasAlreadySo(LogEntry r)
        => r.Message.StartsWith("Was al", StringComparison.OrdinalIgnoreCase)
           || r.Message.StartsWith("Had geen", StringComparison.OrdinalIgnoreCase)
           || r.Message.Contains("al niet meer", StringComparison.OrdinalIgnoreCase);

    /// <summary>The AD group with this DN from the data, or (not loaded) a group with only a name and DN.</summary>
    private static AdGroup? AdGroup(string dn, InventoryData? data)
    {
        if (dn.Length == 0)
        {
            return null;
        }

        return data is not null && data.AdGroupsByDn.TryGetValue(dn, out var g)
            ? g
            : new AdGroup { Name = DnHelp.Name(dn), SamAccountName = DnHelp.Name(dn), DistinguishedName = dn, OuPath = DnHelp.OuPathOfObject(dn) };
    }

    private static string SearchCurrentDn(string dn, InventoryData? data)
    {
        if (data is null || data.AdUsers.Any(a => string.Equals(a.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase)))
        {
            return dn;
        }

        // Moved? Then the same name (CN) in another OU; only when that is unambiguous
        var name = DnHelp.Name(dn);
        var candidates = data.AdUsers.Where(a => string.Equals(DnHelp.Name(a.DistinguishedName), name, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0].DistinguishedName : dn;
    }

    private static TargetSystem SystemOf(ChangeAction a) => a.ToString() switch
    {
        var t when t.StartsWith("Ad", StringComparison.Ordinal) => TargetSystem.ActiveDirectory,
        var t when t.StartsWith("Graph", StringComparison.Ordinal) => TargetSystem.Graph,
        _ => TargetSystem.Exchange,
    };

    private static string Name(LogEntry r) => r.TargetName.Length > 0 ? r.TargetName : r.Target;

    private static string Description(ChangeAction a, LogEntry r)
    {
        var groupValue = r.Parameters.GetValueOrDefault("groep") ?? r.Parameters.GetValueOrDefault("groepNaam") ?? string.Empty;
        return a switch
        {
            ChangeAction.AdAddGroupMember or ChangeAction.GraphAddGroupMember or ChangeAction.ExchangeAddGroupMember => $"Terug lid maken van {groupValue}",
            ChangeAction.AdRemoveGroupMember or ChangeAction.GraphRemoveGroupMember or ChangeAction.ExchangeRemoveGroupMember => $"Terug uit {groupValue} halen",
            ChangeAction.GraphAddOwner => $"Terug eigenaar maken: {r.Parameters.GetValueOrDefault("lid")}",
            ChangeAction.GraphRemoveOwner => $"Eigenaar terug weghalen: {r.Parameters.GetValueOrDefault("lid")}",
            ChangeAction.ExchangeAddMailboxPermission => $"{r.Parameters.GetValueOrDefault("Recht")} teruggeven aan {r.Parameters.GetValueOrDefault("Trustee")}",
            ChangeAction.ExchangeRemoveMailboxPermission => $"{r.Parameters.GetValueOrDefault("Recht")} terug intrekken van {r.Parameters.GetValueOrDefault("Trustee")}",
            ChangeAction.AdEnableAccount => "AD-account terug inschakelen",
            ChangeAction.AdDisableAccount => "AD-account terug uitschakelen",
            _ => "Terugdraaien: " + r.Description,
        };
    }

    private static Change New(LogEntry r, ChangeAction action, string target, Dictionary<string, string> parameters, string description,
        string? current = null, string? newValue = null, string? warning = null, bool selected = true, Dictionary<string, string>? previousValues = null) => new()
    {
        TargetSystem = SystemOf(action),
        ChangeAction = action,
        Target = target,
        TargetName = r.TargetName,
        Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
        Description = description,
        CurrentValue = current,
        NewValue = newValue,
        Warning = warning,
        Selected = selected,
        Previous = previousValues,
    };
}
