using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>Which group you create; that also decides along which route.</summary>
public enum NewGroupKind
{
    /// <summary>AD security group in the groups OU (synchronised to Entra).</summary>
    AdSecurity,

    /// <summary>Security group in the cloud only (Graph).</summary>
    CloudSecurity,

    /// <summary>Microsoft 365 group: shared mailbox, calendar, Teams possible (Graph).</summary>
    Microsoft365,

    /// <summary>Mail-enabled security group (Exchange), e.g. for access to a shared mailbox.</summary>
    MailSecurity,

    /// <summary>Distributielijst (Exchange).</summary>
    Distribution,
}

public sealed class NewGroupRequest
{
    public NewGroupKind Kind { get; set; } = NewGroupKind.AdSecurity;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>E-mail address (only Microsoft 365 group, mail-enabled security group and distribution list).</summary>
    public string Mail { get; set; } = string.Empty;

    /// <summary>AD: Global (default) or Universal.</summary>
    public string Scope { get; set; } = "Global";

    /// <summary>Key of the user who becomes the manager (AD, Exchange) or owner (cloud); empty = nobody.</summary>
    public string Administrator { get; set; } = string.Empty;

    /// <summary>Keys of the first members.</summary>
    public List<string> Members { get; set; } = new();
}

/// <summary>
/// Creates a new group (0.8, #13), in one plan: create, then the manager or owner and the first members (those steps
/// depend on the creation). Checks: the name and the address do not exist yet, no privileged name, no name of a dynamic
/// group, and a warning when the name does not follow the naming convention.
/// </summary>
public static class NewGroupPlans
{
    // AD groupType: beveiligingsgroep (0x80000000) + Global (2) of Universal (8)
    public const int GlobalSecurity = unchecked((int)0x80000002);
    public const int UniversalSecurity = unchecked((int)0x80000008);

    /// <summary>The prefix the naming convention suggests for this kind.</summary>
    public static string Prefix(NewGroupKind kind) => kind switch
    {
        NewGroupKind.AdSecurity or NewGroupKind.CloudSecurity => "gg_",
        NewGroupKind.Microsoft365 => "WKG_",
        NewGroupKind.MailSecurity => "SG-",
        _ => string.Empty,
    };

    public static bool WithMail(NewGroupKind kind) => kind is NewGroupKind.Microsoft365 or NewGroupKind.MailSecurity or NewGroupKind.Distribution;

    public static PlanOutcome Plan(InventoryData data, OrganisationConfig config, NewGroupRequest a)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var name = a.Name.Trim();
        var mail = a.Mail.Trim().ToLowerInvariant();

        if (name.Length == 0)
        {
            return PlanOutcome.Error("Geef de groep een naam.");
        }

        if (name.IndexOfAny(new[] { '"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@' }) >= 0)
        {
            errors.Add("De naam bevat een teken dat niet mag in een groepsnaam (bv. / \\ : ; , @).");
        }

        if (data.Index.Groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(g.Ad?.SamAccountName, name, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Er bestaat al een groep {name}.");
        }

        if (MirrorUser.IsPrivileged(new AdGroup { Name = name }))
        {
            errors.Add($"{name} is een bevoorrechte naam; zo'n groep maak je bewust buiten deze app.");
        }

        if (config.IsDynamicGroup(name))
        {
            errors.Add($"{name} is een dynamische groep in de organisatie: die volgt uit regels en maak je niet hier.");
        }

        var prefixes = config.GroupPrefixes.Count > 0 ? config.GroupPrefixes : new List<string> { "gg_", "AFD_", "DNST_", "SG-", "WKG_" };
        if (!prefixes.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add($"De naam volgt de naamgeving niet (begint niet met {string.Join(", ", prefixes)}).");
        }

        if (WithMail(a.Kind))
        {
            if (!mail.Contains('@') || mail.StartsWith('@') || mail.EndsWith('@'))
            {
                errors.Add("Geef een geldig e-mailadres.");
            }
            else if (data.Index.Groups.Any(g => string.Equals(g.Mail, mail, StringComparison.OrdinalIgnoreCase))
                     || data.Mailboxes.Any(p => string.Equals(p.Smtp, mail, StringComparison.OrdinalIgnoreCase))
                     || data.Index.AccountOf(mail) is not null)
            {
                errors.Add($"Het adres {mail} is al in gebruik.");
            }
        }

        var administrator = a.Administrator.Length == 0 ? null : data.SearchPerson(a.Administrator);
        var members = a.Members.Select(data.SearchPerson).Where(p => p is not null).Cast<Person>().ToList();

        string? ouDn = null;
        if (a.Kind == NewGroupKind.AdSecurity)
        {
            if (string.IsNullOrWhiteSpace(config.GroupsOu))
            {
                errors.Add("Stel eerst bij Organisatie de OU voor nieuwe groepen in.");
            }
            else
            {
                ouDn = config.OuDn(config.GroupsOu, data.DomainDn);
            }
        }

        if (errors.Count > 0)
        {
            return new PlanOutcome(Array.Empty<Change>(), errors);
        }

        var plan = new List<Change>();
        var warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        switch (a.Kind)
        {
            case NewGroupKind.AdSecurity:
            {
                var dn = $"CN={PlanBuilder.EscapeCn(name)},{ouDn}";
                var p = GroupPlans.P(("ou", ouDn!), ("cn", name), ("sAMAccountName", name),
                    ("groupType", (a.Scope == "Universal" ? UniversalSecurity : GlobalSecurity).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (a.Description.Trim().Length > 0)
                {
                    p["description"] = a.Description.Trim();
                }

                if (administrator?.Ad is { } b)
                {
                    p["managedBy"] = b.DistinguishedName;
                }
                else if (administrator is not null)
                {
                    errors.Add($"{administrator.Name} heeft geen AD-account en kan geen beheerder van een AD-groep zijn.");
                }

                var create = new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdCreateGroup,
                    Target = dn,
                    TargetName = name,
                    Parameters = p,
                    Description = $"AD-beveiligingsgroep {name} aanmaken ({a.Scope}) in {config.GroupsOu}",
                    NewValue = dn,
                    Warning = warning,
                };
                plan.Add(create);
                foreach (var member in members)
                {
                    if (member.Ad is null)
                    {
                        errors.Add($"{member.Name} heeft geen AD-account en kan geen lid worden van een AD-groep.");
                        continue;
                    }

                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = ChangeAction.AdAddGroupMember,
                        Target = member.Ad.DistinguishedName,
                        TargetName = member.Name,
                        Parameters = GroupPlans.P(("groepDn", dn), ("groep", name)),
                        Description = $"Lid maken van de nieuwe AD-groep {name}",
                        DependentOf = create.Id,
                    });
                }

                break;
            }

            case NewGroupKind.CloudSecurity:
            case NewGroupKind.Microsoft365:
            {
                var m365 = a.Kind == NewGroupKind.Microsoft365;
                var alias = m365 ? mail.Split('@')[0] : new string(name.Where(char.IsLetterOrDigit).ToArray());
                var withoutEntra = members.Where(l => l.Entra is null).ToList();
                errors.AddRange(withoutEntra.Select(l => $"{l.Name} bestaat niet in Entra en kan geen lid worden van een cloudgroep."));
                if (administrator is { Entra: null })
                {
                    errors.Add($"{administrator.Name} bestaat niet in Entra en kan geen eigenaar zijn.");
                }

                var p = GroupPlans.P(("displayName", name), ("mailNickname", alias), ("soort", m365 ? "M365" : "Beveiliging"));
                if (a.Description.Trim().Length > 0)
                {
                    p["description"] = a.Description.Trim();
                }

                if (administrator?.Entra is { } e)
                {
                    p["eigenaar"] = e.Id;
                }

                // Members and owner go along in the same request: the group does not exist yet, so there is no id yet
                var membersIds = members.Where(l => l.Entra is not null).Select(l => l.Entra!.Id).ToList();
                if (membersIds.Count > 0)
                {
                    p["leden"] = string.Join(',', membersIds);
                }

                if (membersIds.Count + (p.ContainsKey("eigenaar") ? 1 : 0) > 20)
                {
                    errors.Add("Een nieuwe cloudgroep kan bij het aanmaken maximaal 20 leden en eigenaars krijgen; voeg de rest daarna toe.");
                    break;
                }

                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphCreateGroup,
                    Target = m365 ? mail : alias,
                    TargetName = name,
                    Parameters = p,
                    Description = (m365 ? $"Microsoft 365-groep {name} aanmaken ({mail})" : $"Cloudbeveiligingsgroep {name} aanmaken")
                                   + (membersIds.Count > 0 ? $" met {membersIds.Count} {(membersIds.Count == 1 ? "lid" : "leden")}" : string.Empty)
                                   + (administrator?.Entra is not null ? $", eigenaar {administrator.Name}" : string.Empty),
                    NewValue = name,
                    Warning = warning,
                });
                break;
            }

            default:
            {
                var create = new Change
                {
                    TargetSystem = TargetSystem.Exchange,
                    ChangeAction = ChangeAction.ExchangeCreateGroup,
                    Target = mail,
                    TargetName = name,
                    Parameters = GroupPlans.P(("Name", name), ("DisplayName", name), ("Alias", mail.Split('@')[0]),
                        ("Type", a.Kind == NewGroupKind.Distribution ? "Distribution" : "Security")),
                    Description = a.Kind == NewGroupKind.Distribution
                        ? $"Distributielijst {name} aanmaken ({mail})"
                        : $"Mail-beveiligingsgroep {name} aanmaken ({mail}, lidmaatschap gesloten)",
                    NewValue = mail,
                    Warning = warning,
                };
                plan.Add(create);
                if (administrator is not null)
                {
                    if (GroupPlans.Address(data, data.Index.Ref(administrator)) is { } address)
                    {
                        plan.Add(new Change
                        {
                            TargetSystem = TargetSystem.Exchange,
                            ChangeAction = ChangeAction.ExchangeGroupManager,
                            Target = mail,
                            TargetName = name,
                            Parameters = GroupPlans.P(("Toevoegen", address)),
                            Description = $"{administrator.Name} beheerder maken",
                            NewValue = administrator.Name,
                            DependentOf = create.Id,
                        });
                    }
                    else
                    {
                        errors.Add($"{administrator.Name} heeft geen e-mailadres en kan geen beheerder zijn.");
                    }
                }

                foreach (var member in members)
                {
                    if (GroupPlans.Address(data, data.Index.Ref(member)) is not { } address)
                    {
                        errors.Add($"{member.Name} heeft geen e-mailadres en kan geen lid worden van een mailgroep.");
                        continue;
                    }

                    plan.Add(new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeAddGroupMember,
                        Target = address,
                        TargetName = member.Name,
                        Parameters = GroupPlans.P(("groep", mail), ("groepNaam", name)),
                        Description = $"Lid maken van de nieuwe groep {name} (Exchange)",
                        DependentOf = create.Id,
                    });
                }

                break;
            }
        }

        return new PlanOutcome(plan, errors);
    }
}
