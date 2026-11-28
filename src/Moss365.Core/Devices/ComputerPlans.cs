using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Devices;

/// <summary>The details of a computer in AD as they have to become (R21); null = do not change.</summary>
public sealed record ComputerDesired(string? Description = null, string? Location = null, string? ManagedBy = null, string? Ou = null);

/// <summary>
/// Editing a computer in AD (R21): the description, the location and who manages it in one change with the previous
/// values (can be undone), and a move to another OU as the last step (after that the DN changes).
/// </summary>
public static class ComputerPlans
{
    public static PlanOutcome Edit(InventoryData data, OrganisationConfig config, AdComputer computer, ComputerDesired desired)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = new List<string>();
        var becomes = new List<string>();
        var errors = new List<string>();
        void Field(string attribute, string label, string current, string? wanted, string? shownNow = null, string? shownNew = null)
        {
            if (wanted is null || string.Equals(current, wanted.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            if (wanted.Trim().Length > 1024)
            {
                errors.Add(Texts.T.Format("attribute.invalid", label));
                return;
            }

            parameters[attribute] = wanted.Trim();
            previous[attribute] = current;
            var before = shownNow ?? current;
            var after = shownNew ?? wanted.Trim();
            now.Add(label + ": " + (before.Length > 0 ? before : Texts.T["undo.empty"]));
            becomes.Add(label + ": " + (after.Length > 0 ? after : Texts.T["undo.empty"]));
        }

        Field("description", Texts.T["computer.description"], computer.Description, desired.Description);
        Field("location", Texts.T["computer.location"], computer.Location, desired.Location);
        if (desired.ManagedBy is { } managedBy)
        {
            Field("managedBy", Texts.T["computer.managed-by"], computer.ManagedBy, managedBy, NameOf(data, computer.ManagedBy), NameOf(data, managedBy));
        }

        var plan = new List<Change>();
        if (parameters.Count > 0)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdSetAttributes,
                Target = computer.DistinguishedName,
                TargetName = computer.Name,
                Parameters = parameters,
                Previous = previous,
                Description = Texts.T.Format("computer.plan.details", parameters.Count),
                CurrentValue = string.Join("; ", now),
                NewValue = string.Join("; ", becomes),
            });
        }

        if (desired.Ou is { Length: > 0 } ou && !string.Equals(ou.Trim('/'), computer.OuPath, StringComparison.OrdinalIgnoreCase))
        {
            // The domain of the computer itself (its DC= parts), else of the data or the organisation
            var domain = string.Join(',', computer.DistinguishedName.Split(',').Where(p => p.TrimStart().StartsWith("DC=", StringComparison.OrdinalIgnoreCase)).Select(p => p.Trim()));
            domain = domain.Length > 0 ? domain : data.DomainDn.Length > 0 ? data.DomainDn : config.DomainDn;
            if (string.IsNullOrEmpty(domain))
            {
                errors.Add(Texts.T["computer.no-domain"]);
            }
            else
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdMove,
                    Target = computer.DistinguishedName,
                    TargetName = computer.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["doelOu"] = config.OuDn(ou.Trim('/'), domain) },
                    Description = Texts.T.Format("code.plan-builder.move-to", ou.Trim('/')),
                    CurrentValue = computer.OuPath,
                    NewValue = ou.Trim('/'),
                });
            }
        }

        if (plan.Count == 0 && errors.Count == 0)
        {
            errors.Add(Texts.T["plan.nothing-changed"]);
        }

        return new PlanOutcome(plan, errors);
    }

    /// <summary>The OUs where computers are now: the choices for a move.</summary>
    public static IReadOnlyList<string> Ous(IEnumerable<AdComputer> computers)
        => computers.Select(c => c.OuPath).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(o => o, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The name of whoever a DN points to (a person or a group), or the DN.</summary>
    public static string NameOf(InventoryData data, string dn)
        => dn.Length == 0 ? string.Empty
            : data.Persons.FirstOrDefault(p => string.Equals(p.Ad?.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase))?.Name
              ?? (data.AdGroupsByDn.TryGetValue(dn, out var g) ? g.Name : DnHelp.Name(dn));
}
