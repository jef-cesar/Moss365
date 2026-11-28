using Moss365.Core.Localization;
using Moss365.Core.Inventory;
using static Moss365.Core.Changes.GroupPlans;

namespace Moss365.Core.Changes;

/// <summary>Desired settings of a mailbox; null = do not change. An empty forwarding address turns forwarding
/// off.</summary>
public sealed class MailboxDesired
{
    public string? DisplayName { get; set; }
    public bool? Hidden { get; set; }
    public string? ForwardingTo { get; set; }
    public bool? SaveForwardingAnd { get; set; }

    /// <summary>"Enabled" or "Disabled"; null = do not change.</summary>
    public string? AutoReply { get; set; }

    public string? AutoReplyInternal { get; set; }
    public string? AutoReplyExternal { get; set; }

    /// <summary>R21: keep a copy of what is sent as or on behalf of the mailbox in its Sent Items.</summary>
    public bool? SentItemsCopy { get; set; }

    /// <summary>R21: the whole list of who may send on behalf (addresses); null = do not change.</summary>
    public IReadOnlyList<string>? SendOnBehalf { get; set; }

    /// <summary>R21: the new primary address; the old one stays as an alias. Null = do not change.</summary>
    public string? PrimaryAddress { get; set; }
}

/// <summary>A new shared mailbox with its access group.</summary>
public sealed class NewMailboxRequest
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Part of the address before the @, e.g. "info" or "youth".</summary>
    public string LocalPart { get; set; } = string.Empty;

    public string Domain { get; set; } = string.Empty;
    public List<ObjectRef> Members { get; set; } = new();
}

/// <summary>
/// Plans for shared mailboxes. Access should go through the group SG-SM-&lt;name&gt;-Full: that group gets FullAccess
/// and SendAs, the people become members of the group (see docs/conventies.md).
/// </summary>
public static class MailboxPlans
{
    /// <summary>
    /// Blocks or allows signing in with the account of a shared mailbox. The mailbox itself keeps working; only signing
    /// in with the account (password) is no longer possible. A synchronised account is disabled in AD (Entra follows
    /// after the synchronisation), a cloud account in Entra (accountEnabled). Undo is possible via the change log.
    /// </summary>
    public static PlanOutcome SignIn(InventoryData d, Mailbox mailbox, bool allow)
    {
        if (d.Index.AccountOf(mailbox.Smtp, mailbox.Alias) is not { } account)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T.Format("code.mailbox-plans.the-account-of-is-not", mailbox.DisplayName) });
        }

        var warning = allow
            ? Texts.T["code.mailbox-plans.microsoft-recommends-blocking-sign-in"]
            : Analysis.Statistic.LastSignIn(account) is { } last && (DateTime.Now - last).TotalDays <= 90
                ? Texts.T.Format("code.mailbox-plans.this-account-signed-in-on", last)
                : null;
        var change = account.Ad is { } ad
            ? new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = allow ? ChangeAction.AdEnableAccount : ChangeAction.AdDisableAccount,
                Target = ad.DistinguishedName,
                TargetName = mailbox.DisplayName,
                Description = allow ? Texts.T["code.mailbox-plans.allow-sign-in-enable-the"] : Texts.T["code.mailbox-plans.block-sign-in-disable-the"],
                CurrentValue = ad.Enabled ? Texts.T["code.mailbox-plans.can-sign-in"] : Texts.T["code.mailbox-plans.sign-in-blocked"],
                NewValue = allow ? Texts.T["code.mailbox-plans.can-sign-in"] : Texts.T["code.mailbox-plans.sign-in-blocked"],
                Warning = warning,
            }
            : account.Entra is { } entra
                ? new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetAttributes,
                    Target = entra.Id,
                    TargetName = mailbox.DisplayName,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = allow ? "true" : "false" },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = entra.AccountEnabled ? "true" : "false" },
                    Description = allow ? Texts.T["code.mailbox-plans.allow-sign-in-entra"] : Texts.T["code.mailbox-plans.block-sign-in-in-entra"],
                    CurrentValue = entra.AccountEnabled ? Texts.T["code.mailbox-plans.can-sign-in"] : Texts.T["code.mailbox-plans.sign-in-blocked"],
                    NewValue = allow ? Texts.T["code.mailbox-plans.can-sign-in"] : Texts.T["code.mailbox-plans.sign-in-blocked"],
                    Warning = warning,
                }
                : null;
        if (change is null)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { Texts.T["code.mailbox-plans.no-account-found"] });
        }

        return PlanOutcome.Of(new[] { change }, Array.Empty<string>());
    }

    public static readonly string[] Permissions = { "FullAccess", "SendAs" };

    /// <summary>Settings of the mailbox: only what differs from the known value.</summary>
    public static PlanOutcome Settings(Mailbox p, MailboxDesired g, InventoryData? data = null)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        var configure = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var old = new List<string>();
        var newValue = new List<string>();
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Set(string name, string current, string? value)
        {
            if (value is not null && !string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
            {
                configure[name] = value;

                // An unknown yes/no value cannot be undone (empty is not a valid value there)
                if (current.Length > 0 || name is "DisplayName" or "ForwardingSmtpAddress")
                {
                    previous[name] = current;
                }

                old.Add($"{name}={(current.Length == 0 ? "(leeg)" : current)}");
                newValue.Add($"{name}={(value.Length == 0 ? "(leeg)" : value)}");
            }
        }

        Set("DisplayName", p.DisplayName, New(p.DisplayName, g.DisplayName));
        Set("HiddenFromAddressListsEnabled", p.Hidden ? "True" : "False", g.Hidden is null ? null : g.Hidden.Value ? "True" : "False");
        if (g.ForwardingTo is not null)
        {
            var to = g.ForwardingTo.Trim();
            if (to.Length > 0 && !to.Contains('@'))
            {
                errors.Add(Texts.T.Format("code.mailbox-plans.is-not-an-e-mail", to));
            }
            else
            {
                Set("ForwardingSmtpAddress", p.ForwardingTo, to);
            }
        }

        Set("DeliverToMailboxAndForward", p.SaveForwardingAnd is null ? string.Empty : p.SaveForwardingAnd.Value ? "True" : "False",
            g.SaveForwardingAnd is null ? null : g.SaveForwardingAnd.Value ? "True" : "False");

        // R21: the copy of sent items (both ways of sending at once) and who sends on behalf
        if (g.SentItemsCopy is { } copy)
        {
            var current = p.SentItemsCopy is null ? string.Empty : p.SentItemsCopy.Value ? "True" : "False";
            Set("MessageCopyForSentAsEnabled", current, copy ? "True" : "False");
            Set("MessageCopyForSendOnBehalfEnabled", current, copy ? "True" : "False");
        }

        if (g.SendOnBehalf is { } onBehalf)
        {
            var list = onBehalf.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.FirstOrDefault(a => !a.Contains('@')) is { } bad)
            {
                errors.Add(Texts.T.Format("code.mailbox-plans.is-not-an-e-mail", bad));
            }
            else
            {
                var before = string.Join('|', p.SendOnBehalf);
                var after = string.Join('|', list);
                if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                {
                    configure["GrantSendOnBehalfTo"] = after;
                    // The names Exchange gave can be names, not addresses: undone only when they are all addresses
                    if (p.SendOnBehalf.All(a => a.Contains('@')))
                    {
                        previous["GrantSendOnBehalfTo"] = before;
                    }

                    old.Add($"GrantSendOnBehalfTo={(before.Length == 0 ? "(leeg)" : before.Replace("|", ", "))}");
                    newValue.Add($"GrantSendOnBehalfTo={(after.Length == 0 ? "(leeg)" : after.Replace("|", ", "))}");
                }
            }
        }

        // R21: the primary address (WindowsEmailAddress: in Exchange Online the old primary address stays as an alias,
        // so mail to it keeps arriving and undo finds the mailbox by it)
        string? addressWarning = null;
        if (g.PrimaryAddress is { } wanted && wanted.Trim() is { Length: > 0 } address && !string.Equals(address, p.Smtp, StringComparison.OrdinalIgnoreCase))
        {
            if (PrimaryAddressProblem(p, address, data) is { } problem)
            {
                errors.Add(problem);
            }
            else
            {
                Set("WindowsEmailAddress", p.Smtp, address);
                addressWarning = Texts.T.Format("mailbox.primary.alias", p.Smtp);
            }
        }

        if (configure.Count > 0)
        {
            var forwarding = configure.TryGetValue("ForwardingSmtpAddress", out var f) && f.Length > 0 && !IsOwnDomain(f, p.Smtp)
                ? Texts.T["code.mailbox-plans.forwarding-to-another-domain-check"] : null;
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeUpdateMailbox,
                Target = p.Smtp,
                TargetName = p.DisplayName,
                Parameters = configure,
                Description = Texts.T["code.mailbox-plans.change-the-mailbox-settings"],
                Previous = previous,
                CurrentValue = string.Join(", ", old),
                NewValue = string.Join(", ", newValue),
                Warning = forwarding is null && addressWarning is null ? null : string.Join(" ", new[] { forwarding, addressWarning }.Where(w => w is not null)),
            });
        }

        if (g.AutoReply is { } snapshot)
        {
            if (snapshot is not ("Enabled" or "Disabled"))
            {
                errors.Add(Texts.T["code.mailbox-plans.the-automatic-reply-can-only"]);
            }
            else
            {
                var ap = P(("AutoReplyState", snapshot));
                if (snapshot == "Enabled")
                {
                    ap["InternalMessage"] = g.AutoReplyInternal ?? p.AutoReplyInternal;
                    ap["ExternalMessage"] = g.AutoReplyExternal ?? p.AutoReplyExternal;
                    if (ap["InternalMessage"].Trim().Length == 0)
                    {
                        errors.Add(Texts.T["code.mailbox-plans.enter-a-text-for-the"]);
                    }
                }

                var changed = !string.Equals(p.AutoReply, snapshot, StringComparison.OrdinalIgnoreCase)
                                || (snapshot == "Enabled" && (ap["InternalMessage"] != p.AutoReplyInternal || ap["ExternalMessage"] != p.AutoReplyExternal));
                if (changed && errors.Count == 0)
                {
                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeAutoReply,
                        Target = p.Smtp,
                        TargetName = p.DisplayName,
                        Parameters = ap,
                        Description = snapshot == "Enabled" ? Texts.T["code.mailbox-plans.turn-on-the-automatic-reply"] : Texts.T["absence-card.turn-off-automatic-reply"],
                        Previous = p.AutoReply.Length == 0 ? null : new(StringComparer.OrdinalIgnoreCase)
                        {
                            ["AutoReplyState"] = p.AutoReply, ["InternalMessage"] = p.AutoReplyInternal, ["ExternalMessage"] = p.AutoReplyExternal,
                        },
                        CurrentValue = p.AutoReply.Length == 0 ? Texts.T["code.mailbox-plans.unknown-not-loaded-live"] : p.AutoReply,
                        NewValue = snapshot,
                    });
                }
            }
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>
    /// Moves direct permissions to the access group: whoever has FullAccess or SendAs directly becomes a member of the
    /// group and loses the direct permission. The group first gets both permissions itself when they are missing.
    /// </summary>
    public static PlanOutcome CleanupDirectPermissions(InventoryData data, Mailbox p)
    {
        var index = data.Index;
        var groupValue = index.AccessGroupOf(p);
        if (groupValue is null)
        {
            return PlanOutcome.Error(Texts.T.Format("code.mailbox-plans.there-is-no-access-group", ObjectIndex.ExpectedAccessGroup(p)));
        }

        var permissions = index.PermissionsOn(p);
        var plan = new List<Change>();
        plan.AddRange(CompleteGroupPermissions(p, groupValue, permissions, null));

        var members = index.MembersOf(groupValue).Select(r => r.Object).ToList();
        var added = new Dictionary<string, Change>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var r in permissions.Where(r => r.Direct))
        {
            Guid? after = null;
            if (r.Who.Kind == ObjectKind.Unknown)
            {
                // E.g. a SID of a deleted account: only clean up, and not ticked by default
                plan.Add(PermissionRoute(p, r, null, Texts.T.Format("code.mailbox-plans.unknown-trustee-deleted-account", r.Trustee), selected: false));
                continue;
            }

            var alreadyMember = members.Any(l => l.IsSame(r.Who));
            if (!alreadyMember)
            {
                if (!added.TryGetValue(r.Who.Key, out var memberBecomes))
                {
                    var outcome = GroupPlans.AddMember(data, groupValue, r.Who);
                    if (outcome.Plan.Count == 0)
                    {
                        errors.AddRange(outcome.Errors);
                        continue;
                    }

                    memberBecomes = outcome.Plan[0];
                    added[r.Who.Key] = memberBecomes;
                    plan.Add(memberBecomes);
                }

                after = memberBecomes.Id;
            }

            plan.Add(PermissionRoute(p, r, after, Texts.T.Format("code.mailbox-plans.remove-direct-of-from-now", r.Permission, r.Who.Name, groupValue.Name), selected: true));
        }

        if (plan.Count == 0 && errors.Count == 0)
        {
            errors.Add(Texts.T["code.mailbox-plans.there-are-no-direct-permissions"]);
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>Creates the access group SG-SM-&lt;name&gt;-Full and gives it FullAccess and SendAs.</summary>
    public static PlanOutcome CreateAccessGroup(InventoryData data, Mailbox p)
    {
        if (data.Index.AccessGroupOf(p) is { } existing)
        {
            return PlanOutcome.Error(Texts.T.Format("code.mailbox-plans.already-exists-and-gives-access", existing.Name));
        }

        var name = ObjectIndex.ExpectedAccessGroup(p);
        var create = CreateGroup(name, Domain(p.Smtp));
        return PlanOutcome.Of(new[] { create }.Concat(CompleteGroupPermissions(p, null, Array.Empty<PermissionRule>(), create, name)));
    }

    /// <summary>New shared mailbox, the access group, the permissions of the group and the first members.</summary>
    public static PlanOutcome NewMailbox(InventoryData data, NewMailboxRequest a)
    {
        var errors = new List<string>();
        var local = a.LocalPart.Trim().ToLowerInvariant();
        var domain = a.Domain.Trim().TrimStart('@').ToLowerInvariant();
        if (a.DisplayName.Trim().Length == 0) errors.Add(Texts.T["code.mailbox-plans.enter-a-display-name"]);
        if (local.Length == 0 || local.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) errors.Add(Texts.T["code.mailbox-plans.the-address-may-only-contain"]);
        if (domain.Length == 0) errors.Add(Texts.T["code.mailbox-plans.choose-a-domain"]);
        var smtp = $"{local}@{domain}";
        if (errors.Count == 0 && data.Index.Find(smtp) is { } exists)
        {
            errors.Add(Texts.T.Format("code.mailbox-plans.is-already-in-use", smtp, exists.KindText, exists.Name));
        }

        if (errors.Count > 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        var mailbox = new Change
        {
            TargetSystem = TargetSystem.Exchange,
            ChangeAction = ChangeAction.ExchangeCreateSharedMailbox,
            Target = smtp,
            TargetName = a.DisplayName.Trim(),
            Parameters = P(("Name", a.DisplayName.Trim()), ("DisplayName", a.DisplayName.Trim()), ("Alias", local)),
            Description = Texts.T["code.mailbox-plans.create-a-shared-mailbox-without"],
            NewValue = smtp,
        };
        var groupName = $"SG-SM-{local}-Full";
        // Chain: mailbox, then the group, then the permissions and members. If a step fails, the following ones are
        // skipped.
        var groupValue = CreateGroup(groupName, domain, mailbox.Id);
        var pv = new Mailbox { Smtp = smtp, DisplayName = a.DisplayName.Trim(), Type = "SharedMailbox" };
        var plan = new List<Change> { mailbox, groupValue };
        plan.AddRange(CompleteGroupPermissions(pv, null, Array.Empty<PermissionRule>(), groupValue, groupName));
        foreach (var member in a.Members.DistinctBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
        {
            var address = Address(data, member);
            if (address is null)
            {
                errors.Add(Texts.T.Format("code.mailbox-plans.has-no-e-mail-address", member.Name));
                continue;
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeAddGroupMember,
                Target = address,
                TargetName = member.Name,
                Parameters = P(("groep", groupValue.Target), ("groepNaam", groupName)),
                Description = Texts.T.Format("code.mailbox-plans.make-member-of-access-to", groupName, a.DisplayName.Trim()),
                DependentOf = groupValue.Id,
            });
        }

        return PlanOutcome.Of(plan, errors);
    }

    // ---------------------------------------------------------------- hulpfuncties

    private static Change CreateGroup(string name, string domain, Guid? dependentOf = null) => new()
    {
        DependentOf = dependentOf,
        TargetSystem = TargetSystem.Exchange,
        ChangeAction = ChangeAction.ExchangeCreateGroup,
        Target = $"{name.ToLowerInvariant()}@{domain}",
        TargetName = name,
        Parameters = P(("Name", name), ("DisplayName", name), ("Alias", name)),
        Description = Texts.T.Format("code.mailbox-plans.create-mail-enabled-security-group", name),
        NewValue = $"{name.ToLowerInvariant()}@{domain}",
    };

    /// <summary>FullAccess and SendAs for the group, as far as they are not there yet.</summary>
    private static IEnumerable<Change> CompleteGroupPermissions(Mailbox p, Group? groupValue, IReadOnlyList<PermissionRule> permissions, Change? createGroup, string? groupName = null)
    {
        var name = groupValue?.Name ?? groupName ?? string.Empty;
        var identity = groupValue is not null ? ExoIdentity(groupValue) : createGroup!.Target;
        foreach (var permission in Permissions)
        {
            if (groupValue is not null && permissions.Any(r => r.Permission.Equals(permission, StringComparison.OrdinalIgnoreCase) && r.Who.Key.Equals(groupValue.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeAddMailboxPermission,
                Target = p.Smtp,
                TargetName = p.DisplayName,
                Parameters = P(("Recht", permission), ("Trustee", identity)),
                Description = Texts.T.Format("code.mailbox-plans.gets-on-the-mailbox", name, permission),
                NewValue = Texts.T.Format("helpdesk.0-for-1", permission, name),
                DependentOf = createGroup?.Id,
            };
        }
    }

    /// <summary>
    /// Gives one person access to a shared mailbox (from the page of the user): via its access group, as the mailbox
    /// model wants it. Without an access group: an error (make one on the page of the mailbox first).
    /// </summary>
    public static PlanOutcome GiveAccess(InventoryData data, Mailbox p, Person person)
    {
        var who = data.Index.Ref(person);
        if (data.Index.MailboxPermissionsOf(person).Any(r => r.Mailbox == p))
        {
            return PlanOutcome.Error(Texts.T.Format("code.mailbox-plans.already-has-access-to", person.Name, p.DisplayName));
        }

        return data.Index.AccessGroupOf(p) is { } g
            ? GroupPlans.AddMember(data, g, who)
            : PlanOutcome.Error(Texts.T.Format("code.mailbox-plans.has-no-access-group-yet", p.DisplayName, ObjectIndex.ExpectedAccessGroup(p)));
    }

    /// <summary>
    /// Takes the access of one person to a shared mailbox away: out of the access group, and direct permissions
    /// removed. Access through another group is named, not changed (that group may give more).
    /// </summary>
    public static PlanOutcome RemoveAccess(InventoryData data, Mailbox p, Person person)
    {
        var index = data.Index;
        var who = index.Ref(person);
        var rules = index.MailboxPermissionsOf(person).Where(r => r.Mailbox == p).ToList();
        if (rules.Count == 0)
        {
            return PlanOutcome.Error(Texts.T.Format("code.mailbox-plans.has-no-access-to", person.Name, p.DisplayName));
        }

        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var r in rules.Where(r => r.ViaGroup is null))
        {
            plan.Add(PermissionRoute(p, r, null, Texts.T.Format("code.mailbox-plans.remove-direct-of-on", r.Permission, person.Name, p.DisplayName), selected: true));
        }

        foreach (var via in rules.Where(r => r.ViaGroup is not null).Select(r => r.ViaGroup!).DistinctBy(v => v.Key))
        {
            if (index.Group(via.Key) is { } g && index.MembersOf(g).Any(m => m.Object.IsSame(who)))
            {
                var outcome = GroupPlans.RemoveMember(data, g, who);
                plan.AddRange(outcome.Plan);
                errors.AddRange(outcome.Errors);
            }
            else
            {
                errors.Add(Texts.T.Format("code.mailbox-plans.the-access-goes-through-which", via.Name, person.Name));
            }
        }

        return PlanOutcome.Of(plan, errors);
    }

    private static Change PermissionRoute(Mailbox p, PermissionRule r, Guid? after, string description, bool selected) => new()
    {
        TargetSystem = TargetSystem.Exchange,
        ChangeAction = ChangeAction.ExchangeRemoveMailboxPermission,
        Target = p.Smtp,
        TargetName = p.DisplayName,
        Parameters = P(("Recht", r.Permission), ("Trustee", r.Trustee)),
        Description = description,
        CurrentValue = Texts.T.Format("helpdesk.0-for-1", r.Permission, r.Who.Name),
        NewValue = "(weg)",
        DependentOf = after,
        Selected = selected,
    };

    private static string Domain(string smtp) => smtp.Contains('@') ? smtp[(smtp.IndexOf('@') + 1)..] : string.Empty;

    /// <summary>Why a mailbox cannot get this primary address (R21), or null: not an address, a mailbox from AD (its
    /// addresses are proxyAddresses there), or an address another recipient has.</summary>
    public static string? PrimaryAddressProblem(Mailbox p, string address, InventoryData? data)
    {
        if (!AttributePlans.ValidValue(nameof(UserDesired.Upn), address))
        {
            return Texts.T.Format("code.mailbox-plans.is-not-an-e-mail", address);
        }

        if (p.IsDirSynced)
        {
            return Texts.T["mailbox.primary.synced"];
        }

        return data is not null && AddressHolder(data, address, p.Smtp) is { } holder ? Texts.T.Format("mailbox.primary.taken", address, holder) : null;
    }

    /// <summary>Who has this address already (a person, mailbox, room or group), apart from the object itself (its own
    /// address, or the person with key <paramref name="self"/>).</summary>
    public static string? AddressHolder(InventoryData data, string address, string own, string? self = null)
    {
        bool Is(string? a) => !string.IsNullOrEmpty(a) && string.Equals(a, address, StringComparison.OrdinalIgnoreCase) && !string.Equals(a, own, StringComparison.OrdinalIgnoreCase);
        return data.Persons.FirstOrDefault(x => x.Key != self && (Is(x.Ad?.EmailAddress) || Is(x.Entra?.Mail) || Is(x.Entra?.UserPrincipalName) || Is(x.Ad?.UserPrincipalName)
                                                                   || x.Ad?.ProxyAddresses.Any(a => a.Length > 5 && a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase) && Is(a[5..])) == true))?.Name
               ?? data.Mailboxes.FirstOrDefault(m => Is(m.Smtp))?.DisplayName
               ?? data.Rooms.FirstOrDefault(r => Is(r.Smtp))?.DisplayName
               ?? data.ExoGroups.FirstOrDefault(x => Is(x.Smtp))?.DisplayName
               ?? data.CloudGroups.Values.FirstOrDefault(x => Is(x.Mail))?.DisplayName
               ?? data.AdGroupsByDn.Values.FirstOrDefault(x => Is(x.Mail))?.Name;
    }

    private static bool IsOwnDomain(string address, string mailboxSmtp)
        => string.Equals(Domain(address), Domain(mailboxSmtp), StringComparison.OrdinalIgnoreCase);
}
