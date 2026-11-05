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
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { $"Het account van {mailbox.DisplayName} staat niet in de geladen gegevens (AD of Entra). Laad live." });
        }

        var warning = allow
            ? "Microsoft raadt aan om aanmelden met een gedeeld postvak te blokkeren: niemand hoort zich ermee aan te melden. Gebruik de toegangsgroep (SG-SM-...-Full)."
            : Analysis.Statistic.LastSignIn(account) is { } last && (DateTime.Now - last).TotalDays <= 90
                ? $"Met dit account werd aangemeld op {last:dd/MM/yyyy}: gebruikt iemand het nog (bv. een scanner of toestel)?"
                : null;
        var change = account.Ad is { } ad
            ? new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = allow ? ChangeAction.AdEnableAccount : ChangeAction.AdDisableAccount,
                Target = ad.DistinguishedName,
                TargetName = mailbox.DisplayName,
                Description = allow ? "Aanmelden toestaan: AD-account van het postvak inschakelen" : "Aanmelden blokkeren: AD-account van het postvak uitschakelen (het postvak blijft werken)",
                CurrentValue = ad.Enabled ? "kan aanmelden" : "aanmelden geblokkeerd",
                NewValue = allow ? "kan aanmelden" : "aanmelden geblokkeerd",
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
                    Description = allow ? "Aanmelden toestaan (Entra)" : "Aanmelden blokkeren in Entra (het postvak blijft werken)",
                    CurrentValue = entra.AccountEnabled ? "kan aanmelden" : "aanmelden geblokkeerd",
                    NewValue = allow ? "kan aanmelden" : "aanmelden geblokkeerd",
                    Warning = warning,
                }
                : null;
        if (change is null)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), new[] { "Geen account gevonden." });
        }

        return PlanOutcome.Of(new[] { change }, Array.Empty<string>());
    }

    public static readonly string[] Permissions = { "FullAccess", "SendAs" };

    /// <summary>Settings of the mailbox: only what differs from the known value.</summary>
    public static PlanOutcome Settings(Mailbox p, MailboxDesired g)
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
                errors.Add($"'{to}' is geen e-mailadres.");
            }
            else
            {
                Set("ForwardingSmtpAddress", p.ForwardingTo, to);
            }
        }

        Set("DeliverToMailboxAndForward", p.SaveForwardingAnd is null ? string.Empty : p.SaveForwardingAnd.Value ? "True" : "False",
            g.SaveForwardingAnd is null ? null : g.SaveForwardingAnd.Value ? "True" : "False");

        if (configure.Count > 0)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeUpdateMailbox,
                Target = p.Smtp,
                TargetName = p.DisplayName,
                Parameters = configure,
                Description = "Instellingen van het postvak aanpassen",
                Previous = previous,
                CurrentValue = string.Join(", ", old),
                NewValue = string.Join(", ", newValue),
                Warning = configure.TryGetValue("ForwardingSmtpAddress", out var f) && f.Length > 0 && !IsOwnDomain(f, p.Smtp)
                    ? "Doorsturen naar een ander domein: controleer of dat mag." : null,
            });
        }

        if (g.AutoReply is { } snapshot)
        {
            if (snapshot is not ("Enabled" or "Disabled"))
            {
                errors.Add("Automatisch antwoord kan enkel aan of uit.");
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
                        errors.Add("Vul een tekst in voor het automatisch antwoord.");
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
                        Description = snapshot == "Enabled" ? "Automatisch antwoord aanzetten" : "Automatisch antwoord uitzetten",
                        Previous = p.AutoReply.Length == 0 ? null : new(StringComparer.OrdinalIgnoreCase)
                        {
                            ["AutoReplyState"] = p.AutoReply, ["InternalMessage"] = p.AutoReplyInternal, ["ExternalMessage"] = p.AutoReplyExternal,
                        },
                        CurrentValue = p.AutoReply.Length == 0 ? "(onbekend, niet live geladen)" : p.AutoReply,
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
            return PlanOutcome.Error($"Er is nog geen toegangsgroep ({ObjectIndex.ExpectedAccessGroup(p)}). Maak die eerst aan.");
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
                plan.Add(PermissionRoute(p, r, null, $"Onbekende rechthebbende {r.Trustee} (verwijderd account?)", selected: false));
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

            plan.Add(PermissionRoute(p, r, after, $"Rechtstreeks {r.Permission} van {r.Who.Name} weghalen (loopt voortaan via {groupValue.Name})", selected: true));
        }

        if (plan.Count == 0 && errors.Count == 0)
        {
            errors.Add("Er zijn geen rechtstreekse rechten; alle toegang loopt al via de groep.");
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>Creates the access group SG-SM-&lt;name&gt;-Full and gives it FullAccess and SendAs.</summary>
    public static PlanOutcome CreateAccessGroup(InventoryData data, Mailbox p)
    {
        if (data.Index.AccessGroupOf(p) is { } existing)
        {
            return PlanOutcome.Error($"{existing.Name} bestaat al en geeft toegang tot dit postvak.");
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
        if (a.DisplayName.Trim().Length == 0) errors.Add("Vul een weergavenaam in.");
        if (local.Length == 0 || local.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) errors.Add("Het adres mag enkel letters, cijfers, punt, streepje of underscore bevatten.");
        if (domain.Length == 0) errors.Add("Kies een domein.");
        var smtp = $"{local}@{domain}";
        if (errors.Count == 0 && data.Index.Find(smtp) is { } exists)
        {
            errors.Add($"{smtp} is al in gebruik ({exists.KindText} {exists.Name}).");
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
            Description = "Gedeeld postvak aanmaken (zonder licentie, aanmelden geblokkeerd)",
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
                errors.Add($"{member.Name} heeft geen e-mailadres en kan geen lid worden.");
                continue;
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeAddGroupMember,
                Target = address,
                TargetName = member.Name,
                Parameters = P(("groep", groupValue.Target), ("groepNaam", groupName)),
                Description = $"Lid maken van {groupName}: toegang tot {a.DisplayName.Trim()}",
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
        Description = $"Mail-beveiligingsgroep {name} aanmaken (lidmaatschap gesloten)",
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
                Description = $"{name} krijgt {permission} op het postvak",
                NewValue = $"{permission} voor {name}",
                DependentOf = createGroup?.Id,
            };
        }
    }

    private static Change PermissionRoute(Mailbox p, PermissionRule r, Guid? after, string description, bool selected) => new()
    {
        TargetSystem = TargetSystem.Exchange,
        ChangeAction = ChangeAction.ExchangeRemoveMailboxPermission,
        Target = p.Smtp,
        TargetName = p.DisplayName,
        Parameters = P(("Recht", r.Permission), ("Trustee", r.Trustee)),
        Description = description,
        CurrentValue = $"{r.Permission} voor {r.Who.Name}",
        NewValue = "(weg)",
        DependentOf = after,
        Selected = selected,
    };

    private static string Domain(string smtp) => smtp.Contains('@') ? smtp[(smtp.IndexOf('@') + 1)..] : string.Empty;

    private static bool IsOwnDomain(string address, string mailboxSmtp)
        => string.Equals(Domain(address), Domain(mailboxSmtp), StringComparison.OrdinalIgnoreCase);
}
