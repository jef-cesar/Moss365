using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>The data of a user as it has to become; null = do not change.</summary>
public sealed class UserDesired
{
    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? Surname { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Office { get; set; }
    public string? City { get; set; }

    /// <summary>Only in AD.</summary>
    public string? Description { get; set; }

    /// <summary>Only in AD.</summary>
    public string? LogonScript { get; set; }

    public string? Phone { get; set; }
    public string? Mobile { get; set; }

    /// <summary>Key of the person who is the manager; "" = no manager; null = do not change.</summary>
    public string? Manager { get; set; }

    /// <summary>The current values (AD when the account comes from AD, otherwise Entra), as the start of the
    /// form.</summary>
    public static UserDesired Of(Person p, InventoryData? d = null)
    {
        var g = p.Ad is { } a
            ? new UserDesired { DisplayName = a.DisplayName, GivenName = a.GivenName, Surname = a.Surname, JobTitle = a.Title, Department = a.Department, Office = a.Office, City = a.City, Description = a.Description, LogonScript = a.ScriptPath, Phone = a.Phone, Mobile = a.Mobile }
            : new UserDesired { DisplayName = p.Entra?.DisplayName, GivenName = p.Entra?.GivenName, Surname = p.Entra?.Surname, JobTitle = p.Entra?.JobTitle, Department = p.Entra?.Department, Office = p.Entra?.OfficeLocation, City = p.Entra?.City, Phone = p.Entra?.Phone, Mobile = p.Entra?.Mobile };
        g.Manager = d is null ? null : AttributePlans.ManagerOf(d, p)?.Key ?? string.Empty;
        return g;
    }
}

/// <summary>
/// Changes attributes of a user along the right route: a synchronised account in AD (Entra follows after the
/// synchronisation), a cloud account via Graph. With the previous values, so it can be undone. Not the UPN and e-mail
/// address: they affect sign-in and mail, and come separately.
/// </summary>
public static class AttributePlans
{
    // Attribute in the form -> (LDAP name in AD, name in Graph or null when Entra does not know it, readable name)
    private static readonly (string Field, string Ad, string? Graph, string Name)[] Fields =
    {
        (nameof(UserDesired.DisplayName), "displayName", "displayName", "Weergavenaam"),
        (nameof(UserDesired.GivenName), "givenName", "givenName", "Voornaam"),
        (nameof(UserDesired.Surname), "sn", "surname", "Achternaam"),
        (nameof(UserDesired.JobTitle), "title", "jobTitle", "Functie"),
        (nameof(UserDesired.Department), "department", "department", "Dienst"),
        (nameof(UserDesired.Office), "physicalDeliveryOfficeName", "officeLocation", "Kantoor"),
        (nameof(UserDesired.City), "l", "city", "Stad"),
        (nameof(UserDesired.Description), "description", null, "Omschrijving"),
        (nameof(UserDesired.LogonScript), "scriptPath", null, "Logonscript"),
        (nameof(UserDesired.Phone), "telephoneNumber", "businessPhones", "Telefoon"),
        (nameof(UserDesired.Mobile), "mobile", "mobilePhone", "Gsm"),
    };

    /// <summary>The manager of a person (AD: manager DN; cloud: manager id), or null.</summary>
    public static Person? ManagerOf(InventoryData d, Person p)
        => ViaAd(p)
            ? p.Ad!.Manager is { Length: > 0 } dn ? d.Persons.FirstOrDefault(x => string.Equals(x.Ad?.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase)) : null
            : p.Entra?.ManagerId is { Length: > 0 } id ? d.Persons.FirstOrDefault(x => string.Equals(x.Entra?.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>Is this account changed in AD (synchronised or AD only), or via Graph (cloud)?</summary>
    public static bool ViaAd(Person p) => p.Ad is not null && (p.Entra is null || p.Entra.OnPremisesSyncEnabled);

    public static PlanOutcome User(InventoryData data, OrganisationConfig config, Person p, UserDesired desired)
    {
        if (p.Ad is null && p.Entra is null)
        {
            return PlanOutcome.Error("Dit account staat niet in de geladen gegevens.");
        }

        if (p.IsGuest)
        {
            return PlanOutcome.Error("Gastgebruikers beheer je in Entra zelf; hun gegevens komen uit hun eigen organisatie.");
        }

        var current = UserDesired.Of(p, data);
        var viaAd = ViaAd(p);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = new List<string>();
        var becomes = new List<string>();
        var warnings = new List<string>();

        // The department as spelled in the organisation (spaces and case)
        if (desired.Department is { } d && config.SearchDepartment(d) is { } department)
        {
            desired.Department = department.Department;
        }
        else if (!string.IsNullOrWhiteSpace(desired.Department) && config.Departments.Count > 0)
        {
            warnings.Add($"De dienst \"{desired.Department.Trim()}\" staat niet in de lijst bij Organisatie.");
        }

        foreach (var (field, adName, graphName, name) in Fields)
        {
            var old = Value(current, field) ?? string.Empty;
            var newValue = Value(desired, field);
            if (newValue is null || string.Equals(old, newValue.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            var key = viaAd ? adName : graphName;
            if (key is null)
            {
                warnings.Add($"{name} bestaat enkel in AD; dit account komt uit de cloud.");
                continue;
            }

            parameters[key] = newValue.Trim();
            previous[key] = old;
            now.Add($"{name}: {(old.Length == 0 ? "(leeg)" : old)}");
            becomes.Add($"{name}: {(newValue.Trim().Length == 0 ? "(leeg)" : newValue.Trim())}");
        }

        // Manager: in AD an attribute (DN), in the cloud a separate command
        Change? setManager = null;
        if (desired.Manager is { } newKey && !string.Equals(newKey, current.Manager ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            var oldManager = ManagerOf(data, p);
            var newManager = newKey.Length == 0 ? null : data.SearchPerson(newKey);
            if (newKey.Length > 0 && newManager is null)
            {
                warnings.Add("De gekozen manager staat niet in de geladen gegevens.");
            }
            else if (viaAd)
            {
                if (newManager is not null && newManager.Ad is null)
                {
                    warnings.Add($"{newManager.Name} heeft geen AD-account en kan in AD geen manager zijn.");
                }
                else
                {
                    parameters["manager"] = newManager?.Ad!.DistinguishedName ?? string.Empty;
                    previous["manager"] = p.Ad!.Manager;
                    now.Add($"Manager: {oldManager?.Name ?? "(geen)"}");
                    becomes.Add($"Manager: {newManager?.Name ?? "(geen)"}");
                }
            }
            else if (newManager is not null && newManager.Entra is null)
            {
                warnings.Add($"{newManager.Name} bestaat niet in Entra en kan geen manager zijn.");
            }
            else
            {
                setManager = new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetManager,
                    Target = p.Entra!.Id,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = newManager?.Entra!.Id ?? string.Empty },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = p.Entra.ManagerId },
                    Description = "Manager zetten in Entra",
                    CurrentValue = oldManager?.Name ?? "(geen)",
                    NewValue = newManager?.Name ?? "(geen)",
                };
            }
        }

        if (parameters.Count == 0 && setManager is null)
        {
            return new PlanOutcome(Array.Empty<Change>(), new[] { warnings.Count > 0 ? string.Join(" ", warnings) : "Er is niets gewijzigd." });
        }

        var plan = new List<Change>();
        if (setManager is not null)
        {
            plan.Add(setManager);
        }

        if (parameters.Count > 0)
        {
            plan.Insert(0, new()
            {
                TargetSystem = viaAd ? TargetSystem.ActiveDirectory : TargetSystem.Graph,
                ChangeAction = viaAd ? ChangeAction.AdSetAttributes : ChangeAction.GraphSetAttributes,
                Target = viaAd ? p.Ad!.DistinguishedName : p.Entra!.Id,
                TargetName = p.Name,
                Parameters = parameters,
                Previous = previous,
                Description = viaAd
                    ? $"Gegevens wijzigen in AD ({parameters.Count})" + (p.Entra is not null ? "; Entra volgt na de synchronisatie (± 30 min)" : string.Empty)
                    : $"Gegevens wijzigen in Entra ({parameters.Count})",
                CurrentValue = string.Join("; ", now),
                NewValue = string.Join("; ", becomes),
                Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            });
        }

        // Another department can be another division: switch the division group too (dynamic groups follow by
        // themselves)
        var errors = new List<string>();
        if (parameters.ContainsKey("department"))
        {
            var oldDivision = config.DivisionFor(current.Department);
            var newDivision = config.DivisionFor(desired.Department);
            if (!string.Equals(oldDivision?.Group, newDivision?.Group, StringComparison.OrdinalIgnoreCase))
            {
                var r = data.Index.Ref(p);
                foreach (var (division, added) in new[] { (oldDivision, false), (newDivision, true) })
                {
                    if (division?.Group is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    if (config.IsDynamicGroup(name) || data.Index.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } groupValue)
                    {
                        errors.Add($"{name} volgt vanzelf uit de dienst (dynamisch) of staat niet in de geladen groepen; niets te doen.");
                        continue;
                    }

                    if (groupValue.Dynamic)
                    {
                        errors.Add($"{name} is dynamisch en volgt vanzelf uit de dienst.");
                        continue;
                    }

                    var u = added ? GroupPlans.AddMember(data, groupValue, r) : GroupPlans.RemoveMember(data, groupValue, r);
                    plan.AddRange(u.Plan);
                    errors.AddRange(u.Errors);
                }
            }
        }

        return new PlanOutcome(plan, errors);
    }

    /// <summary>
    /// Fixes a difference between AD and Entra in AD (for a synchronised account Entra then follows by itself): the
    /// value without superfluous spaces, and a department as it is spelled in the organisation.
    /// </summary>
    public static PlanOutcome Fix(InventoryData data, OrganisationConfig config, Difference v)
    {
        if (v.Kind != DifferenceKind.AttributeDiffers || v.Person.Ad is null)
        {
            return PlanOutcome.Error("Enkel een kenmerk van een account in AD kan rechtgezet worden.");
        }

        // "Dienst (Department)" -> "Dienst"
        var shortValue = v.Attribute.Split(" (")[0].Trim();
        var field = Fields.FirstOrDefault(x => string.Equals(x.Name, shortValue, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Ad, v.Attribute, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Graph, v.Attribute, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Field, v.Attribute, StringComparison.OrdinalIgnoreCase));
        if (field.Field is null)
        {
            return PlanOutcome.Error($"{v.Attribute} kan de app niet rechtzetten.");
        }

        var value = string.Join(' ', v.AdValue.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (field.Field == nameof(UserDesired.Department) && config.SearchDepartment(value) is { } department)
        {
            value = department.Department;
        }

        var desired = new UserDesired();
        typeof(UserDesired).GetProperty(field.Field)!.SetValue(desired, value);
        var output = User(data, config, v.Person, desired);
        return output.Plan.Count == 0
            ? PlanOutcome.Error($"In AD staat al \"{value}\"; het verschil verdwijnt na de volgende synchronisatie.")
            : output;
    }

    private static string? Value(UserDesired g, string field) => (string?)typeof(UserDesired).GetProperty(field)!.GetValue(g);
}
