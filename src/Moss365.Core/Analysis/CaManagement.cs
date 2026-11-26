using Moss365.Core.Localization;
using System.Net;
using System.Text.Json;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Core.Analysis;

/// <summary>What the safety check found: errors stop the change, warnings are in the plan.</summary>
public sealed record CaCheckOutcome(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool Can => Errors.Count == 0;
}

/// <summary>
/// The safety checks before every change to Conditional Access. A policy that is on must exclude all emergency accounts
/// and must not lock out the administrator who makes the change. Enforced when planning and in the engine
/// (<see cref="Refusal"/>), so that undo or a scheduled plan cannot get around it either.
/// </summary>
public static class CaSafety
{
    /// <summary>The emergency accounts from the configuration, found in Entra (or null with the reason).</summary>
    public static (IReadOnlyList<Person> Accounts, string? Error) EmergencyAccounts(InventoryData d, OrganisationConfig c)
    {
        var upns = c.EmergencyAccounts.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (upns.Count == 0)
        {
            return (Array.Empty<Person>(), Texts.T["code.ca-management.no-emergency-accounts-are-marked"]);
        }

        var found = new List<Person>();
        foreach (var upn in upns)
        {
            var p = d.Persons.FirstOrDefault(x => string.Equals(x.Entra?.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                return (Array.Empty<Person>(), Texts.T.Format("code.ca-management.the-emergency-account-is-not", upn));
            }

            found.Add(p);
        }

        return (found, null);
    }

    /// <summary>Checks a policy as it will be after the change.</summary>
    public static CaCheckOutcome Check(CaPolicy newValue, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (newValue.Status == CaStatus.Off)
        {
            return new CaCheckOutcome(errors, warnings);
        }

        var ca = (d.ConditionalAccess ?? new CaData(Array.Empty<CaPolicy>(), Array.Empty<NamedLocation>(), Array.Empty<AuthenticationStrength>(),
            new Dictionary<string, string>(), null)) with { Policies = new[] { newValue } };
        var name = CaReader.Names(d);
        var list = newValue.Status == CaStatus.On ? errors : warnings;
        var behind = newValue.Status == CaStatus.On ? string.Empty : (" " + Texts.T["code.ca-management.turning-on-will-be-refused"]);

        // 1. Noodaccounts
        var (emergency, emergencyError) = EmergencyAccounts(d, c);
        if (emergencyError is not null)
        {
            list.Add(emergencyError + behind);
        }

        foreach (var p in emergency)
        {
            var g = CaUser.Of(p, d);
            var scope = g is null ? CaAnswer.Maybe(Texts.T["code.ca-management.no-entra-data"]) : CaReader.Scope(newValue, g, name, ca);
            if (scope.Applies != CaApplies.No)
            {
                list.Add(Texts.T.Format(scope.Applies == CaApplies.Yes ? "ca-policy.emergency-under" : "ca-policy.emergency-maybe", p.Name, scope.Reason) + behind);
            }
        }

        // 2. Do not lock yourself out
        var own = ownUpn is null ? null
            : d.Persons.FirstOrDefault(x => string.Equals(x.Entra?.UserPrincipalName, ownUpn, StringComparison.OrdinalIgnoreCase));
        var ownG = own is null ? null : CaUser.Of(own, d);
        if (ownG is null)
        {
            warnings.Add(Texts.T["code.ca-management.your-own-account-is-not"]);
        }
        else
        {
            foreach (var s in new[] { new CaScenario(CaApp.Office365, CaPlace.Belgium), new CaScenario(CaApp.AdminPortals, CaPlace.Belgium) })
            {
                var applies = CaReader.Applies(newValue, ownG, s, ca, name);
                if (applies.Applies == CaApplies.No)
                {
                    continue;
                }

                var location = s.App == CaApp.Office365 ? "Office 365" : Texts.T["code.ca-management.the-admin-portals"];
                if (newValue.Blocks)
                {
                    var text = Texts.T.Format(applies.Applies == CaApplies.Yes ? "ca-policy.blocks-you" : "ca-policy.blocks-you-maybe", own!.Name, location, applies.Reason);
                    (newValue.Status == CaStatus.On ? errors : warnings).Add(text);
                    break;
                }

                if (newValue.Checks.Count > 0 && newValue.Checks.All(x => x is "compliantDevice" or "domainJoinedDevice"))
                {
                    warnings.Add(Texts.T.Format("code.ca-management.this-policy-asks-a-managed", location));
                    break;
                }
            }
        }

        if (newValue.Status == CaStatus.On && newValue.Blocks)
        {
            warnings.Add(Texts.T["code.ca-management.this-policy-blocks-first-check"]);
        }

        return new CaCheckOutcome(errors.Distinct().ToList(), warnings.Distinct().ToList());
    }

    /// <summary>
    /// For the engine: why this change to Conditional Access is not allowed (or null). A policy that is on after the
    /// change must pass the check; if the policy is not loaded, the app refuses (it cannot check anything then).
    /// </summary>
    public static string? Refusal(Change w, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        if (w.ChangeAction is not (ChangeAction.GraphCreateCaPolicy or ChangeAction.GraphUpdateCaPolicy))
        {
            return null;
        }

        CaPolicy newValue;
        try
        {
            if (w.ChangeAction == ChangeAction.GraphCreateCaPolicy)
            {
                using var doc = JsonDocument.Parse(w.P("json"));
                newValue = CaJson.Read(doc.RootElement);
            }
            else
            {
                var current = d.ConditionalAccess?.Policies.FirstOrDefault(b => b.Id == w.Target);
                if (current is null)
                {
                    return Texts.T["code.ca-management.the-policy-is-not-in"];
                }

                newValue = CaJson.AfterPatch(current, w.P("json"));
            }
        }
        catch (JsonException)
        {
            return Texts.T["code.ca-management.the-change-is-not-valid"];
        }

        var outcome = Check(newValue, d, c, ownUpn);
        return outcome.Can ? null : string.Join(" ", outcome.Errors);
    }
}

/// <summary>A template for a new policy (the Microsoft baseline). A new policy always starts in report-only.</summary>
public sealed record CaTemplate(string Key, string Name, string Explanation, bool Blocks, bool CanPilot = true, bool AsksCountriesLocation = false);

/// <summary>Options for a template: a pilot group instead of everyone, the location with the allowed
/// countries.</summary>
public sealed record CaTemplateOptions(string? PilotGroupId = null, string? CountriesLocationId = null);

public static class CaTemplates
{
    /// <summary>Admin roles that cover the whole tenant (roleTemplateIds of Microsoft, the same for
    /// everyone).</summary>
    public static readonly IReadOnlyDictionary<string, string> CriticalRoles = new Dictionary<string, string>
    {
        [CaData.RoleGlobalAdmin] = "Global Administrator",
        ["e8611ab8-c189-46e8-94e1-60213ab1f814"] = "Privileged Role Administrator",
        ["194ae4cb-b126-40b2-bd5b-6091b380977d"] = "Security Administrator",
        ["29232cdf-9323-42fd-ade2-1d097af3e4de"] = "Exchange Administrator",
        ["f28a1f50-f6e7-4571-818b-6a12f2af6b6c"] = "SharePoint Administrator",
        ["fe930be7-5e62-47db-91af-98c3a49a38b1"] = "User Administrator",
        ["7be44c8a-adaf-4e2a-84d6-ab2649e08a13"] = "Privileged Authentication Administrator",
        ["b1be1c3e-b65d-4f19-8427-f6fa0d97feb9"] = "Conditional Access Administrator",
        ["9b895d92-2cd3-44c7-9d02-a6ac2d5ea5c3"] = "Application Administrator",
        ["158c047a-c907-4556-b7ef-446551a6b5f7"] = "Cloud Application Administrator",
        ["8ac3fc64-6eca-42ea-9e69-59f4c7b60eb2"] = "Hybrid Identity Administrator",
        ["c4e39bd9-1100-46d3-8c65-fb160da0071f"] = "Authentication Administrator",
        ["729827e3-9c14-49f7-bb1b-9608f156bbb8"] = "Helpdesk Administrator",
        ["b0f54661-2d74-4c50-afa3-1ec803f12efe"] = "Billing Administrator",
    };

    public static IReadOnlyList<CaTemplate> All => new[]
    {
        new CaTemplate("mfa-iedereen", Texts.T["ca-template.mfa-iedereen"], Texts.T["ca-template.mfa-iedereen.help"], false),
        new CaTemplate("verouderd-blokkeren", Texts.T["ca-template.verouderd-blokkeren"], Texts.T["ca-template.verouderd-blokkeren.help"], true),
        new CaTemplate("beheerders-phishingbestendig", Texts.T["ca-template.beheerders-phishingbestendig"], Texts.T["ca-template.beheerders-phishingbestendig.help"], false, CanPilot: false),
        new CaTemplate("gasten-mfa", Texts.T["ca-template.gasten-mfa"], Texts.T["ca-template.gasten-mfa.help"], false, CanPilot: false),
        new CaTemplate("toestelcode-blokkeren", Texts.T["ca-template.toestelcode-blokkeren"], Texts.T["ca-template.toestelcode-blokkeren.help"], true),
        new CaTemplate("registreren-vanop-kantoor", Texts.T["ca-template.registreren-vanop-kantoor"], Texts.T["ca-template.registreren-vanop-kantoor.help"], true),
        new CaTemplate("landen-blokkeren", Texts.T["ca-template.landen-blokkeren"], Texts.T["ca-template.landen-blokkeren.help"], true, AsksCountriesLocation: true),
        new CaTemplate("beheerportalen-beheerd-toestel", Texts.T["ca-template.beheerportalen-beheerd-toestel"], Texts.T["ca-template.beheerportalen-beheerd-toestel.help"], false, CanPilot: false),
    };

    /// <summary>A new policy from a template, always in report-only, with the emergency accounts excluded.</summary>
    public static PlanOutcome Plan(string key, CaTemplateOptions options, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        var template = All.FirstOrDefault(s => s.Key == key);
        if (template is null)
        {
            return PlanOutcome.Error(Texts.T["code.ca-management.unknown-template"]);
        }

        if (d.ConditionalAccess is not { Message: null } ca)
        {
            return PlanOutcome.Error(Texts.T["code.ca-management.conditional-access-is-not-loaded"]);
        }

        if (ca.Policies.Any(b => string.Equals(b.Name, template.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return PlanOutcome.Error(Texts.T.Format("code.ca-management.there-is-already-a-policy", template.Name));
        }

        var (emergency, emergencyError) = CaSafety.EmergencyAccounts(d, c);
        if (emergencyError is not null)
        {
            return PlanOutcome.Error(emergencyError);
        }

        var pilot = template.CanPilot && options.PilotGroupId is { Length: > 0 } g ? g : null;
        string[] everyone = pilot is null ? new[] { "All" } : Array.Empty<string>();
        string[] pilotGroup = pilot is null ? Array.Empty<string>() : new[] { pilot };
        var roles = CriticalRoles.Keys.ToList();
        var baseline = new CaPolicy
        {
            Name = template.Name,
            Status = CaStatus.Report,
            UsersIn = everyone,
            GroupsIn = pilotGroup,
            UsersOff = emergency.Select(p => p.Entra!.Id).ToList(),
            AppsIn = new[] { "All" },
        };

        CaPolicy policy;
        switch (template.Key)
        {
            case "mfa-iedereen":
                policy = baseline with { Checks = new[] { "mfa" } };
                break;
            case "verouderd-blokkeren":
                policy = baseline with { ClientApps = new[] { "exchangeActiveSync", "other" }, Checks = new[] { "block" } };
                break;
            case "beheerders-phishingbestendig":
                policy = baseline with { UsersIn = Array.Empty<string>(), GroupsIn = Array.Empty<string>(), RolesIn = roles, StrengthId = CaData.StrengthPhishingResistant };
                break;
            case "gasten-mfa":
                policy = baseline with { UsersIn = new[] { "GuestsOrExternalUsers" }, GroupsIn = Array.Empty<string>(), GuestsIn = true, Checks = new[] { "mfa" } };
                break;
            case "toestelcode-blokkeren":
                policy = baseline with { Transfer = new[] { "deviceCodeFlow" }, Checks = new[] { "block" } };
                break;
            case "registreren-vanop-kantoor":
                if (!ca.Locations.Any(l => l.Trusted))
                {
                    return PlanOutcome.Error(Texts.T["code.ca-management.there-is-no-trusted-named"]);
                }

                policy = baseline with
                {
                    AppsIn = Array.Empty<string>(),
                    UserActions = new[] { "urn:user:registersecurityinfo" },
                    LocationsIn = new[] { "All" },
                    LocationsOff = new[] { "AllTrusted" },
                    Checks = new[] { "block" },
                };
                break;
            case "landen-blokkeren":
                if (options.CountriesLocationId is not { Length: > 0 } countries || ca.Location(countries) is not { IsIp: false } location)
                {
                    return PlanOutcome.Error(Texts.T["code.ca-management.choose-the-named-location-with"]);
                }

                if (!location.ContainsBelgium)
                {
                    return PlanOutcome.Error(Texts.T.Format("code.ca-management.the-location-does-not-contain", location.Name));
                }

                policy = baseline with { LocationsIn = new[] { "All" }, LocationsOff = new[] { countries }, Checks = new[] { "block" } };
                break;
            default:
                policy = baseline with
                {
                    UsersIn = Array.Empty<string>(),
                    GroupsIn = Array.Empty<string>(),
                    RolesIn = roles,
                    AppsIn = new[] { "MicrosoftAdminPortals" },
                    Checks = new[] { "compliantDevice", "domainJoinedDevice" },
                };
                break;
        }

        var check = CaSafety.Check(policy, d, c, ownUpn);
        if (!check.Can)
        {
            return new PlanOutcome(Array.Empty<Change>(), check.Errors);
        }

        var json = CaJson.ToGraph(policy);
        var w = new Change
        {
            TargetSystem = TargetSystem.Graph,
            ChangeAction = ChangeAction.GraphCreateCaPolicy,
            Target = "nieuw",
            TargetName = policy.Name,
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
            Description = Texts.T.Format("ca-policy.new", policy.Name) + (pilot is null ? string.Empty : (" " + Texts.T["code.ca-management.only-the-pilot-group"])),
            CurrentValue = Texts.T["code.ca-management.does-not-exist"],
            NewValue = Texts.T.Format("ca-policy.new.value", emergency.Count),
            Warning = check.Warnings.Count == 0 ? null : string.Join(" ", check.Warnings),
        };
        return PlanOutcome.Of(new[] { w });
    }
}

/// <summary>Plans that change an existing policy or a named location; always with the old values for undo.</summary>
public static class CaPlans
{
    private static string CurrentJson(CaPolicy b) => b.Json.Length > 0 ? b.Json : CaJson.ToGraph(b);

    private static Change Modify(CaPolicy b, string patch, string description, string current, string newValue, string? warning) => new()
    {
        TargetSystem = TargetSystem.Graph,
        ChangeAction = ChangeAction.GraphUpdateCaPolicy,
        Target = b.Id,
        TargetName = b.Name,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = patch },
        Previous = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.Previous(CurrentJson(b), patch) },
        Description = description,
        CurrentValue = current,
        NewValue = newValue,
        Warning = warning,
    };

    private static PlanOutcome WithCheck(CaPolicy b, string patch, InventoryData d, OrganisationConfig c, string? ownUpn,
        Func<string?, Change> make)
    {
        var check = CaSafety.Check(CaJson.AfterPatch(b, patch), d, c, ownUpn);
        return check.Can
            ? PlanOutcome.Of(new[] { make(check.Warnings.Count == 0 ? null : string.Join(" ", check.Warnings)) })
            : new PlanOutcome(Array.Empty<Change>(), check.Errors);
    }

    /// <summary>Turn a policy on, set it to report-only or turn it off.</summary>
    public static PlanOutcome Status(CaPolicy b, CaStatus newValue, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        if (b.Status == newValue)
        {
            return PlanOutcome.Error(Texts.T.Format("code.ca-management.the-policy-is-already", CaReader.StatusName(newValue).ToLowerInvariant()));
        }

        var patch = CaJson.StatusPatch(newValue);
        return WithCheck(b, patch, d, c, ownUpn, warning => Modify(b, patch,
            Texts.T.Format("code.ca-management.set-to", b.Name, CaReader.StatusName(newValue).ToLowerInvariant()), CaReader.StatusName(b.Status), CaReader.StatusName(newValue), warning));
    }

    /// <summary>Exclude someone from a policy (or remove the exclusion).</summary>
    public static PlanOutcome Exclusion(CaPolicy b, Person p, bool add, InventoryData? d = null, OrganisationConfig? c = null, string? ownUpn = null)
    {
        if (p.Entra is not { } e)
        {
            return PlanOutcome.Error(Texts.T.Format("ca-management.noentra", p.Name));
        }

        var state = b.UsersOff.Contains(e.Id, StringComparer.OrdinalIgnoreCase);
        if (state == add)
        {
            return PlanOutcome.Error(add ? Texts.T.Format("code.ca-management.is-already-excluded-from", p.Name, b.Name) : Texts.T.Format("code.ca-management.is-already-no-longer-excluded", p.Name, b.Name));
        }

        var list = add
            ? b.UsersOff.Append(e.Id).ToList()
            : b.UsersOff.Where(x => !string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var patch = CaJson.ExclusionsPatch(b, list);
        Change Make(string? warning) => Modify(b, patch,
            add ? Texts.T.Format("code.ca-management.exclude-from", p.Name, b.Name) : Texts.T.Format("code.ca-management.remove-the-exception-of-on", p.Name, b.Name),
            Texts.T.Format("ca-policy.direct-exclusions", b.UsersOff.Count), Texts.T.Format("ca-policy.direct-exclusions", list.Count), warning);

        // Removing can bring an emergency account under the policy again: check when the data is there
        return d is not null && c is not null ? WithCheck(b, patch, d, c, ownUpn, Make) : PlanOutcome.Of(new[] { Make(null) });
    }

    /// <summary>Restore a policy as it was in a backup (or create it again if it is gone in the meantime).</summary>
    public static PlanOutcome Restore(CaPolicy fromBackup, CaPolicy? current, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        if (fromBackup.Json.Length == 0)
        {
            return PlanOutcome.Error(Texts.T["code.ca-management.the-backup-does-not-contain"]);
        }

        var check = CaSafety.Check(fromBackup, d, c, ownUpn);
        if (!check.Can)
        {
            return new PlanOutcome(Array.Empty<Change>(), check.Errors);
        }

        var warning = check.Warnings.Count == 0 ? null : string.Join(" ", check.Warnings);
        var json = CaJson.Writable(fromBackup.Json);
        if (current is null)
        {
            return PlanOutcome.Of(new[]
            {
                new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphCreateCaPolicy,
                    Target = "nieuw",
                    TargetName = fromBackup.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                    Description = Texts.T.Format("code.ca-management.make-policy-again-from-the", fromBackup.Name),
                    CurrentValue = Texts.T["code.ca-management.does-not-exist"],
                    NewValue = CaReader.StatusName(fromBackup.Status),
                    Warning = warning,
                },
            });
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphUpdateCaPolicy,
                Target = current.Id,
                TargetName = current.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.Writable(CurrentJson(current)) },
                Description = Texts.T.Format("code.ca-management.put-policy-back-as-in", current.Name),
                CurrentValue = CaReader.StatusName(current.Status),
                NewValue = CaReader.StatusName(fromBackup.Status),
                Warning = warning,
            },
        });
    }

    /// <summary>Is this a valid IP range in CIDR notation (e.g. 203.0.113.0/24)?</summary>
    public static bool IsCidr(string value)
    {
        var share = value.Trim().Split('/');
        if (share.Length != 2 || !IPAddress.TryParse(share[0], out var ip) || !int.TryParse(share[1], out var length))
        {
            return false;
        }

        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return length >= 1 && length <= max;
    }

    /// <summary>Create or change a named location: IP ranges (CIDR) or countries (ISO code, e.g. BE).</summary>
    public static PlanOutcome Location(NamedLocation? existing, string name, bool isIp, IEnumerable<string> values, bool trusted, InventoryData d)
    {
        var list = values.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        name = name.Trim();
        var errors = new List<string>();
        if (name.Length == 0)
        {
            errors.Add(Texts.T["code.ca-management.give-the-location-a-name"]);
        }

        if (list.Count == 0)
        {
            errors.Add(isIp ? Texts.T["code.ca-management.enter-at-least-one-ip"] : Texts.T["code.ca-management.enter-at-least-one-country"]);
        }

        errors.AddRange(isIp
            ? list.Where(w => !IsCidr(w)).Select(w => Texts.T.Format("code.ca-management.is-not-an-ip-range", w))
            : list.Where(w => w.Length != 2 || !w.All(char.IsAsciiLetter)).Select(w => Texts.T.Format("code.ca-management.is-not-a-two-letter", w)));
        if (existing is not null && existing.IsIp != isIp)
        {
            errors.Add(Texts.T["code.ca-management.a-location-with-ip-addresses"]);
        }

        if (existing is null && d.ConditionalAccess?.Locations.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            errors.Add(Texts.T.Format("code.ca-management.there-is-already-a-location", name));
        }

        if (errors.Count > 0)
        {
            return new PlanOutcome(Array.Empty<Change>(), errors);
        }

        var json = CaJson.LocationJson(name, isIp, list, trusted, existing?.UnknownCountries ?? false);
        var used = existing is null ? new List<CaPolicy>()
            : d.ConditionalAccess?.Policies.Where(b => b.LocationsIn.Concat(b.LocationsOff).Contains(existing.Id, StringComparer.OrdinalIgnoreCase)).ToList() ?? new();
        var warning = used.Count == 0 ? null
            : Texts.T.Format("ca-management.locationused", used.Count, string.Join(", ", used.Select(b => b.Name)));
        var text = string.Join(", ", list);
        var w = existing is null
            ? new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphCreateCaLocation,
                Target = "nieuw",
                TargetName = name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Description = Texts.T.Format(isIp && trusted ? "ca-location.new.trusted" : "ca-location.new", name),
                CurrentValue = Texts.T["code.ca-management.does-not-exist"],
                NewValue = text,
            }
            : new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphUpdateCaLocation,
                Target = existing.Id,
                TargetName = existing.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Previous = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["json"] = existing.Json.Length > 0
                        ? CaJson.LocationWritable(existing.Json)
                        : CaJson.LocationJson(existing.Name, existing.IsIp, existing.Values, existing.Trusted, existing.UnknownCountries),
                },
                Description = Texts.T.Format("ca-location.change", existing.Name),
                CurrentValue = string.Join(", ", existing.Values),
                NewValue = text,
                Warning = warning,
            };
        return PlanOutcome.Of(new[] { w });
    }
}

/// <summary>A difference between two states of a policy.</summary>
public sealed record CaDifference(string PolicyId, string Name, string Kind, IReadOnlyList<string> Details);

/// <summary>Compares two states of the policies (a backup with now, or two backups).</summary>
public static class CaComparison
{
    public static IReadOnlyList<CaDifference> Compare(IReadOnlyList<CaPolicy> old, IReadOnlyList<CaPolicy> newValue, Func<string, string> name, CaData? ca = null)
    {
        var output = new List<CaDifference>();
        foreach (var n in newValue)
        {
            var o = old.FirstOrDefault(x => x.Id == n.Id);
            if (o is null)
            {
                output.Add(new CaDifference(n.Id, n.Name, "nieuw", new[] { "status: " + CaReader.StatusName(n.Status) }));
                continue;
            }

            var details = Details(o, n, name, ca).ToList();
            if (details.Count == 0 && Plain(o.Json) != Plain(n.Json))
            {
                details.Add(Texts.T["code.ca-management.other-settings-session-device-filter"]);
            }

            if (details.Count > 0)
            {
                output.Add(new CaDifference(n.Id, n.Name, "gewijzigd", details));
            }
        }

        output.AddRange(old.Where(o => newValue.All(n => n.Id != o.Id)).Select(o => new CaDifference(o.Id, o.Name, "verdwenen", new[] { Texts.T["code.manage-ca.no-longer-exists"] })));
        return output.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The JSON without what Graph keeps itself (modified date), to see real differences.</summary>
    private static string Plain(string json) => json.Length == 0 ? string.Empty : CaJson.Writable(json);

    private static IEnumerable<string> Details(CaPolicy o, CaPolicy n, Func<string, string> name, CaData? ca)
    {
        if (o.Name != n.Name)
        {
            yield return $"naam: {o.Name} → {n.Name}";
        }

        if (o.Status != n.Status)
        {
            yield return $"status: {CaReader.StatusName(o.Status)} → {CaReader.StatusName(n.Status)}";
        }

        string Role(string id) => ca?.RoleName(id) ?? (CaTemplates.CriticalRoles.TryGetValue(id, out var r) ? r : id);
        string Location(string id) => ca is null ? id : CaReader.LocationName(id, ca);
        foreach (var (title, a, b, names) in new (string, IReadOnlyList<string>, IReadOnlyList<string>, Func<string, string>)[]
                 {
                     (Texts.T["ca-diff.users-in"], o.UsersIn, n.UsersIn, name),
                     (Texts.T["ca-diff.users-off"], o.UsersOff, n.UsersOff, name),
                     (Texts.T["ca-diff.groups-in"], o.GroupsIn, n.GroupsIn, name),
                     (Texts.T["ca-diff.groups-off"], o.GroupsOff, n.GroupsOff, name),
                     (Texts.T["ca-diff.roles-in"], o.RolesIn, n.RolesIn, Role),
                     (Texts.T["ca-diff.roles-off"], o.RolesOff, n.RolesOff, Role),
                     (Texts.T["ca-diff.apps-in"], o.AppsIn, n.AppsIn, CaReader.AppName),
                     (Texts.T["ca-diff.apps-off"], o.AppsOff, n.AppsOff, CaReader.AppName),
                     (Texts.T["ca-diff.client-apps"], o.ClientApps, n.ClientApps, CaReader.ClientName),
                     (Texts.T["ca-diff.locations-in"], o.LocationsIn, n.LocationsIn, Location),
                     (Texts.T["ca-diff.locations-off"], o.LocationsOff, n.LocationsOff, Location),
                     (Texts.T["ca-diff.checks"], o.Checks, n.Checks, CaReader.CheckName),
                     (Texts.T["ca-diff.user-actions"], o.UserActions, n.UserActions, CaReader.ActionName),
                     (Texts.T["ca-diff.platforms-in"], o.PlatformsIn, n.PlatformsIn, CaReader.PlatformName),
                     (Texts.T["ca-diff.platforms-off"], o.PlatformsOff, n.PlatformsOff, CaReader.PlatformName),
                     (Texts.T["ca-diff.sign-in-risk"], o.SignInRisk, n.SignInRisk, CaReader.RiskName),
                     (Texts.T["ca-diff.user-risk"], o.UsersRisk, n.UsersRisk, CaReader.RiskName),
                     (Texts.T["ca-diff.transfer"], o.Transfer, n.Transfer, t => t == "deviceCodeFlow" ? Texts.T["ca-diff.device-code"] : t == "authenticationTransfer" ? Texts.T["ca-diff.authentication-transfer"] : t),
                 })
        {
            var added = b.Except(a, StringComparer.OrdinalIgnoreCase).Select(names).ToList();
            var removed = a.Except(b, StringComparer.OrdinalIgnoreCase).Select(names).ToList();
            if (added.Count + removed.Count > 0)
            {
                yield return $"{title}: " + string.Join("; ", added.Select(x => "+ " + x).Concat(removed.Select(x => "− " + x)));
            }
        }

        foreach (var (title, a, b) in new (string, string?, string?)[]
                 {
                     (Texts.T["ca-diff.guestsin"], YesNo(o.GuestsIn), YesNo(n.GuestsIn)),
                     (Texts.T["ca-diff.guestsoff"], YesNo(o.GuestsOff), YesNo(n.GuestsOff)),
                     (Texts.T["ca-diff.operator"], o.Operator == "AND" ? Texts.T["ca-diff.alltogether"] : Texts.T["ca-diff.oneofthem"], n.Operator == "AND" ? Texts.T["ca-diff.alltogether"] : Texts.T["ca-diff.oneofthem"]),
                     (Texts.T["ca-diff.frequency"], o.SignInFrequency ?? Texts.T["ca-policy-editor.not-set"], n.SignInFrequency ?? Texts.T["ca-policy-editor.not-set"]),
                     (Texts.T["ca-diff.browser"], o.PersistentBrowser ?? Texts.T["ca-policy-editor.not-set"], n.PersistentBrowser ?? Texts.T["ca-policy-editor.not-set"]),
                     (Texts.T["ca-diff.devicefilter"], YesNo(o.DeviceFilter), YesNo(n.DeviceFilter)),
                 })
        {
            if (a != b && !(title == Texts.T["ca-diff.operator"] && Math.Max(o.Checks.Count, n.Checks.Count) <= 1))
            {
                yield return $"{title}: {a} → {b}";
            }
        }

        if (o.StrengthId != n.StrengthId)
        {
            yield return Texts.T.Format("ca-diff.strength", o.StrengthName ?? o.StrengthId ?? Texts.T["plan.none"], n.StrengthName ?? n.StrengthId ?? Texts.T["plan.none"]);
        }
    }

    private static string YesNo(bool value) => value ? Texts.T["yesno.yes"] : Texts.T["yesno.no"];
}
