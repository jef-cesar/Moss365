using Moss365.Core.Storage;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

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
        [ChangeAction.GoogleAddAlias] = ChangeAction.GoogleRemoveAlias,
        [ChangeAction.GoogleRemoveAlias] = ChangeAction.GoogleAddAlias,
        [ChangeAction.GoogleAddGroupMember] = ChangeAction.GoogleRemoveGroupMember,
        [ChangeAction.GoogleRemoveGroupMember] = ChangeAction.GoogleAddGroupMember,
        [ChangeAction.GoogleAssignLicence] = ChangeAction.GoogleRemoveLicence,
        [ChangeAction.GoogleRemoveLicence] = ChangeAction.GoogleAssignLicence,
        [ChangeAction.GmailAddDelegate] = ChangeAction.GmailRemoveDelegate,
        [ChangeAction.GmailRemoveDelegate] = ChangeAction.GmailAddDelegate,
    };

    /// <summary>Attributes that can be restored with their previous value.</summary>
    private static readonly HashSet<ChangeAction> Attributes = new()
    {
        ChangeAction.AdSetAttributes, ChangeAction.GraphSetAttributes, ChangeAction.GraphSetGroupAttributes,
        ChangeAction.ExchangeUpdateGroup, ChangeAction.ExchangeUpdateMailbox, ChangeAction.ExchangeAutoReply, ChangeAction.GraphSetManager,
        ChangeAction.GraphUpdateCaPolicy, ChangeAction.GraphUpdateCaLocation, ChangeAction.AdSetAccountFlag, ChangeAction.IntuneDeviceAction,
        ChangeAction.GraphSetDeviceEnabled, ChangeAction.IntuneSetComplianceSettings, ChangeAction.AdHardening,
        ChangeAction.GraphUpdatePolicy, ChangeAction.GraphRoleEligibility, ChangeAction.ExchangeOrganisationSetting, ChangeAction.ExchangeRoomPlace,
        ChangeAction.AdSetProxyAddresses, ChangeAction.ExchangeUpdateContact, ChangeAction.ExchangeRoomWorkingHours,
        ChangeAction.GoogleUpdateUser, ChangeAction.GoogleUpdateGroup, ChangeAction.GoogleUpdateGroupSettings, ChangeAction.GoogleUpdateResource,
        ChangeAction.GmailSetForwarding, ChangeAction.GmailSetVacation, ChangeAction.GmailSetSignature,
    };

    /// <summary>Can this entry be undone? If not, why not.</summary>
    public static string? WhyNot(LogEntry r)
    {
        if (!r.Succeeded)
        {
            return Texts.T["code.undo.did-not-succeed-so-nothing"];
        }

        if (WasAlreadySo(r))
        {
            return Texts.T.Format("undo.nothing-changed", r.Message.TrimEnd('.'));
        }

        if (!LegacyNames.TryParseEnum<ChangeAction>(r.ChangeAction, out var action))
        {
            return Texts.T["code.undo.unknown-action"];
        }

        if (Opposite.ContainsKey(action) || action == ChangeAction.AdMove || action == ChangeAction.ExchangeGroupManager)
        {
            return null;
        }

        if (action == ChangeAction.AdWriteLogonScript)
        {
            return r.Previous?.ContainsKey("inhoud") == true ? null : Texts.T["code.undo.the-app-does-not-delete"];
        }

        if (action == ChangeAction.AdHardening && r.Parameters.GetValueOrDefault("wat") == "recycleBin")
        {
            return Texts.T["advice.recyclebin.no-undo"];
        }

        if (action == ChangeAction.IntuneDeviceAction && r.Previous is not { Count: > 0 })
        {
            return Texts.T["device.undo.not"];
        }

        if (Attributes.Contains(action))
        {
            return r.Previous is { Count: > 0 } ? null : Texts.T["code.undo.the-previous-values-are-not"];
        }

        return action switch
        {
            ChangeAction.AdCreateUser or ChangeAction.GraphCreateUser or ChangeAction.ExchangeCreateSharedMailbox or ChangeAction.ExchangeCreateGroup or ChangeAction.GraphCreateCaLocation
                => Texts.T["code.undo.the-app-does-not-delete-2"],
            ChangeAction.GraphCreateApplication or ChangeAction.GraphUpdateApplication or ChangeAction.GraphCreateServicePrincipal
                or ChangeAction.GraphUpdateServicePrincipal or ChangeAction.GraphGrantConsent or ChangeAction.GraphAssignAppRole
                => Texts.T["code.undo.you-change-the-app-registration"],
            ChangeAction.GraphCreateCaPolicy => Texts.T["code.undo.turn-the-policy-off-in"],
            ChangeAction.GraphInviteGuest => Texts.T["code.undo.remove-the-guest-through-guest"],
            ChangeAction.GraphCreateTemporaryAccessPass => Texts.T["code.undo.a-temporary-access-pass-expires"],
            ChangeAction.GraphSendMail => Texts.T["code.undo.a-sent-e-mail-cannot"],
            ChangeAction.GraphRemoveGuest => Texts.T["code.undo.restore-through-entra-deleted-users"],
            ChangeAction.GraphDeleteDevice or ChangeAction.IntuneDeleteDevice => Texts.T["device.cleanup.no-undo"],
            ChangeAction.IntuneCreateCompliancePolicy => Texts.T["compliance.template.no-undo"],
            ChangeAction.AdSetPassword => Texts.T["code.undo.a-previous-password-cannot-come"],
            ChangeAction.GraphRevokeSessions => Texts.T["code.undo.revoked-sessions-cannot-come-back"],
            ChangeAction.ExchangeConvertToSharedMailbox => Texts.T["code.undo.convert-the-mailbox-back-yourself"],
            ChangeAction.GraphAssignLicence or ChangeAction.GraphRemoveLicences => Texts.T["code.undo.you-change-licences-through-the"],
            ChangeAction.AdUnlockAccount => Texts.T["code.undo.you-do-not-lock-an"],
            ChangeAction.GoogleCreateUser => Texts.T["code.undo.the-app-does-not-delete-2"],
            ChangeAction.GoogleSetPassword => Texts.T["code.undo.a-previous-password-cannot-come"],
            ChangeAction.GoogleSignOut or ChangeAction.GoogleRevokeTokens => Texts.T["code.undo.revoked-sessions-cannot-come-back"],
            ChangeAction.GmailRemoveFilter => r.Previous?.ContainsKey("json") == true ? null : Texts.T["code.undo.the-previous-values-are-not"],
            ChangeAction.GmailAddFilter => Texts.T["gmail.filter.undo"],
            _ => Texts.T["code.undo.the-app-cannot-undo-this"],
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
                    Texts.T.Format("code.undo.move-back-to", DnHelp.OuPath(previousOu)), DnHelp.OuPathOfObject(now), DnHelp.OuPath(previousOu));
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

                w = New(r, action, target, reversed, Texts.T.Format("undo.manager", string.Join(", ",
                    reversed.Where(p => p.Key is "Toevoegen" or "Verwijderen").Select(p => Texts.T.Format(p.Key == "Toevoegen" ? "undo.manager.add" : "undo.manager.remove", p.Value)))));
            }
            else if (action == ChangeAction.AdWriteLogonScript && r.Previous?.GetValueOrDefault("inhoud") is { } before)
            {
                w = New(r, action, target, new() { ["inhoud"] = before }, Texts.T.Format("code.undo.restore-logon-script-to-the", target),
                    previousValues: new(StringComparer.OrdinalIgnoreCase) { ["inhoud"] = r.Parameters.GetValueOrDefault("inhoud") ?? string.Empty });
            }
            else if (action == ChangeAction.GmailRemoveFilter && r.Previous?.GetValueOrDefault("json") is { } filter)
            {
                // A removed filter comes back as it was (Gmail gives it a new id)
                w = New(r, ChangeAction.GmailAddFilter, target, new() { ["json"] = filter }, Texts.T.Format("undo.reverse", r.Description));
            }
            else if (Attributes.Contains(action))
            {
                var previous = new Dictionary<string, string>(r.Previous!, StringComparer.OrdinalIgnoreCase);
                // Fixed parameters (e.g. Identity for Exchange) stay; the attributes get their previous value
                foreach (var (k, v) in previous)
                {
                    parameters[k] = v;
                }

                w = New(r, action, target, parameters, Texts.T.Format("undo.restore", r.Description),
                    string.Join(", ", previous.Keys.Select(k => $"{k}: {r.Parameters.GetValueOrDefault(k)}")),
                    string.Join(", ", previous.Select(p => $"{p.Key}: {(p.Value.Length == 0 ? Texts.T["undo.empty"] : p.Value)}")),
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
                        warning: Texts.T.Format("code.undo.is-a-privileged-group-only", g.Name), selected: false);
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
           || r.Message.Contains("al niet meer", StringComparison.OrdinalIgnoreCase)
           || AlreadyKeys.Any(k => Translator.Dictionaries.Values.Any(d => d.TryGetValue(k, out var t) && string.Equals(t, r.Message.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>The results that mean "it was already so", in every language (R15: the message is written in the
    /// language of whoever ran the plan).</summary>
    private static readonly string[] AlreadyKeys = { "result.already-member", "result.already-owner", "result.no-licences", "result.already-assigned" };

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
        var t when t.StartsWith("Graph", StringComparison.Ordinal) || t.StartsWith("Intune", StringComparison.Ordinal) => TargetSystem.Graph,
        var t when t.StartsWith("Google", StringComparison.Ordinal) || t.StartsWith("Gmail", StringComparison.Ordinal) => TargetSystem.Google,
        _ => TargetSystem.Exchange,
    };

    private static string Name(LogEntry r) => r.TargetName.Length > 0 ? r.TargetName : r.Target;

    private static string Description(ChangeAction a, LogEntry r)
    {
        var groupValue = r.Parameters.GetValueOrDefault("groep") ?? r.Parameters.GetValueOrDefault("groepNaam") ?? string.Empty;
        return a switch
        {
            ChangeAction.AdAddGroupMember or ChangeAction.GraphAddGroupMember or ChangeAction.ExchangeAddGroupMember => Texts.T.Format("code.undo.make-member-of-again", groupValue),
            ChangeAction.AdRemoveGroupMember or ChangeAction.GraphRemoveGroupMember or ChangeAction.ExchangeRemoveGroupMember => Texts.T.Format("code.undo.remove-from-again", groupValue),
            ChangeAction.GraphAddOwner => Texts.T.Format("undo.owner.add", r.Parameters.GetValueOrDefault("lid")),
            ChangeAction.GraphRemoveOwner => Texts.T.Format("undo.owner.remove", r.Parameters.GetValueOrDefault("lid")),
            ChangeAction.ExchangeAddMailboxPermission => Texts.T.Format("undo.permission.add", r.Parameters.GetValueOrDefault("Recht"), r.Parameters.GetValueOrDefault("Trustee")),
            ChangeAction.ExchangeRemoveMailboxPermission => Texts.T.Format("undo.permission.remove", r.Parameters.GetValueOrDefault("Recht"), r.Parameters.GetValueOrDefault("Trustee")),
            ChangeAction.AdEnableAccount => Texts.T["code.undo.enable-the-ad-account-again"],
            ChangeAction.AdDisableAccount => Texts.T["code.undo.disable-the-ad-account-again"],
            ChangeAction.GoogleAddGroupMember => Texts.T.Format("code.undo.make-member-of-again", groupValue),
            ChangeAction.GoogleRemoveGroupMember => Texts.T.Format("code.undo.remove-from-again", groupValue),
            _ => Texts.T.Format("undo.reverse", r.Description),
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
