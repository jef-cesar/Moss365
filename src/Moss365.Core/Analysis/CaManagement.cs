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
            return (Array.Empty<Person>(), "Er zijn geen noodaccounts aangeduid (Organisatie > Noodaccounts). Zonder noodaccounts wijzigt de app geen beleid dat aan staat.");
        }

        var found = new List<Person>();
        foreach (var upn in upns)
        {
            var p = d.Persons.FirstOrDefault(x => string.Equals(x.Entra?.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                return (Array.Empty<Person>(), $"Het noodaccount {upn} staat niet in de geladen gegevens van Entra. Laad Entra live of kijk de naam na bij Organisatie > Noodaccounts.");
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
        var behind = newValue.Status == CaStatus.On ? string.Empty : " Aanzetten zal geweigerd worden tot dat in orde is.";

        // 1. Noodaccounts
        var (emergency, emergencyError) = EmergencyAccounts(d, c);
        if (emergencyError is not null)
        {
            list.Add(emergencyError + behind);
        }

        foreach (var p in emergency)
        {
            var g = CaUser.Of(p, d);
            var scope = g is null ? CaAnswer.Maybe("geen Entra-gegevens") : CaReader.Scope(newValue, g, name, ca);
            if (scope.Applies != CaApplies.No)
            {
                list.Add($"Het noodaccount {p.Name} valt {(scope.Applies == CaApplies.Yes ? "onder" : "misschien onder")} dit beleid ({scope.Reason}). Sluit het uit.{behind}");
            }
        }

        // 2. Do not lock yourself out
        var own = ownUpn is null ? null
            : d.Persons.FirstOrDefault(x => string.Equals(x.Entra?.UserPrincipalName, ownUpn, StringComparison.OrdinalIgnoreCase));
        var ownG = own is null ? null : CaUser.Of(own, d);
        if (ownG is null)
        {
            warnings.Add("Je eigen account staat niet in de geladen gegevens van Entra: de app kan niet nakijken of je jezelf buitensluit.");
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

                var location = s.App == CaApp.Office365 ? "Office 365" : "de beheerportalen";
                if (newValue.Blocks)
                {
                    var text = $"Dit beleid blokkeert {(applies.Applies == CaApplies.Yes ? "" : "misschien ")}jouw eigen account ({own!.Name}) voor {location} vanuit België ({applies.Reason}).";
                    (newValue.Status == CaStatus.On ? errors : warnings).Add(text);
                    break;
                }

                if (newValue.Checks.Count > 0 && newValue.Checks.All(x => x is "compliantDevice" or "domainJoinedDevice"))
                {
                    warnings.Add($"Dit beleid vraagt voor jouw account een beheerd toestel ({location}): zonder zo'n toestel geraak je er niet meer in.");
                    break;
                }
            }
        }

        if (newValue.Status == CaStatus.On && newValue.Blocks)
        {
            warnings.Add("Dit beleid blokkeert: kijk eerst het effect na in alleen rapport (Conditional Access > Beleiden > Effect laden).");
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
                    return "het beleid staat niet in de geladen gegevens; laad Conditional Access live, zodat de app de noodaccounts kan nakijken";
                }

                newValue = CaJson.AfterPatch(current, w.P("json"));
            }
        }
        catch (JsonException)
        {
            return "de wijziging is geen geldige JSON";
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

    public static readonly IReadOnlyList<CaTemplate> All = new[]
    {
        new CaTemplate("mfa-iedereen", "MFA voor alle gebruikers", "Iedereen moet MFA doen bij elke app. De basis.", false),
        new CaTemplate("verouderd-blokkeren", "Verouderde aanmelding blokkeren", "Blokkeert oude programma's zonder MFA (IMAP, POP, SMTP-aanmelding, ActiveSync). Kijk eerst na wie die nog gebruikt (scanners, koppelingen).", true),
        new CaTemplate("beheerders-phishingbestendig", "Phishingbestendige MFA voor beheerrollen", "Wie een beheerrol heeft, meldt zich aan met Authenticator zonder wachtwoord, een FIDO2-sleutel, passkey of Windows Hello.", false, CanPilot: false),
        new CaTemplate("gasten-mfa", "MFA voor gasten", "Gastgebruikers moeten MFA doen.", false, CanPilot: false),
        new CaTemplate("toestelcode-blokkeren", "Aanmelden met een toestelcode blokkeren", "Blokkeert de device code flow, een veelgebruikte phishingtruc. Vergaderzaaltoestellen gebruiken die soms.", true),
        new CaTemplate("registreren-vanop-kantoor", "MFA registreren enkel vanop kantoor", "Beveiligingsgegevens (MFA) registreren kan enkel vanaf een vertrouwde locatie. Daarbuiten: met een Temporary Access Pass.", true),
        new CaTemplate("landen-blokkeren", "Toegang blokkeren buiten de toegelaten landen", "Blokkeert aanmelden vanuit landen die niet in de gekozen benoemde locatie staan.", true, AsksCountriesLocation: true),
        new CaTemplate("beheerportalen-beheerd-toestel", "Beheerd toestel voor de beheerportalen", "Wie een beheerrol heeft, opent de beheerportalen enkel vanop een beheerd (Intune) of hybride toestel.", false, CanPilot: false),
    };

    /// <summary>A new policy from a template, always in report-only, with the emergency accounts excluded.</summary>
    public static PlanOutcome Plan(string key, CaTemplateOptions options, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        var template = All.FirstOrDefault(s => s.Key == key);
        if (template is null)
        {
            return PlanOutcome.Error("Onbekend sjabloon.");
        }

        if (d.ConditionalAccess is not { Message: null } ca)
        {
            return PlanOutcome.Error("Conditional Access is niet geladen. Laad eerst live (Verbindingen).");
        }

        if (ca.Policies.Any(b => string.Equals(b.Name, template.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return PlanOutcome.Error($"Er bestaat al een beleid \"{template.Name}\".");
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
                    return PlanOutcome.Error("Er is nog geen vertrouwde benoemde locatie (bv. de IP-adressen van de gebouwen). Maak die eerst bij Beheren > Benoemde locaties.");
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
                    return PlanOutcome.Error("Kies de benoemde locatie met de toegelaten landen (een locatie met landen, geen IP-adressen).");
                }

                if (!location.ContainsBelgium)
                {
                    return PlanOutcome.Error($"De locatie \"{location.Name}\" bevat België niet: dan blokkeert dit beleid iedereen.");
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
            Description = $"Nieuw Conditional Access-beleid \"{policy.Name}\" in alleen rapport" + (pilot is null ? string.Empty : " (enkel de pilotgroep)"),
            CurrentValue = "(bestaat niet)",
            NewValue = $"alleen rapport; {emergency.Count} noodaccount(s) uitgesloten",
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
            return PlanOutcome.Error($"Het beleid staat al op {CaReader.StatusName(newValue).ToLowerInvariant()}.");
        }

        var patch = CaJson.StatusPatch(newValue);
        return WithCheck(b, patch, d, c, ownUpn, warning => Modify(b, patch,
            $"\"{b.Name}\" op {CaReader.StatusName(newValue).ToLowerInvariant()} zetten", CaReader.StatusName(b.Status), CaReader.StatusName(newValue), warning));
    }

    /// <summary>Exclude someone from a policy (or remove the exclusion).</summary>
    public static PlanOutcome Exclusion(CaPolicy b, Person p, bool add, InventoryData? d = null, OrganisationConfig? c = null, string? ownUpn = null)
    {
        if (p.Entra is not { } e)
        {
            return PlanOutcome.Error($"{p.Name} heeft geen Entra-account; Conditional Access geldt enkel in Entra.");
        }

        var state = b.UsersOff.Contains(e.Id, StringComparer.OrdinalIgnoreCase);
        if (state == add)
        {
            return PlanOutcome.Error(add ? $"{p.Name} is al uitgezonderd van \"{b.Name}\"." : $"{p.Name} staat al niet meer uitgezonderd van \"{b.Name}\".");
        }

        var list = add
            ? b.UsersOff.Append(e.Id).ToList()
            : b.UsersOff.Where(x => !string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        var patch = CaJson.ExclusionsPatch(b, list);
        Change Make(string? warning) => Modify(b, patch,
            add ? $"{p.Name} uitzonderen van \"{b.Name}\"" : $"Uitzondering van {p.Name} op \"{b.Name}\" weghalen",
            $"{b.UsersOff.Count} rechtstreeks uitgezonderd", $"{list.Count} rechtstreeks uitgezonderd", warning);

        // Removing can bring an emergency account under the policy again: check when the data is there
        return d is not null && c is not null ? WithCheck(b, patch, d, c, ownUpn, Make) : PlanOutcome.Of(new[] { Make(null) });
    }

    /// <summary>Restore a policy as it was in a backup (or create it again if it is gone in the meantime).</summary>
    public static PlanOutcome Restore(CaPolicy fromBackup, CaPolicy? current, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        if (fromBackup.Json.Length == 0)
        {
            return PlanOutcome.Error("De back-up bevat de volledige gegevens van dit beleid niet.");
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
                    Description = $"Beleid \"{fromBackup.Name}\" opnieuw maken uit de back-up (het bestaat niet meer)",
                    CurrentValue = "(bestaat niet)",
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
                Description = $"Beleid \"{current.Name}\" terugzetten zoals in de back-up",
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
            errors.Add("Geef de locatie een naam.");
        }

        if (list.Count == 0)
        {
            errors.Add(isIp ? "Geef minstens één IP-bereik (bv. 203.0.113.10/32)." : "Geef minstens één land (bv. BE).");
        }

        errors.AddRange(isIp
            ? list.Where(w => !IsCidr(w)).Select(w => $"\"{w}\" is geen IP-bereik in CIDR-notatie (bv. 203.0.113.0/24).")
            : list.Where(w => w.Length != 2 || !w.All(char.IsAsciiLetter)).Select(w => $"\"{w}\" is geen landcode van twee letters (bv. BE, NL)."));
        if (existing is not null && existing.IsIp != isIp)
        {
            errors.Add("Een locatie met IP-adressen kan geen locatie met landen worden (of omgekeerd).");
        }

        if (existing is null && d.ConditionalAccess?.Locations.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) == true)
        {
            errors.Add($"Er bestaat al een locatie \"{name}\".");
        }

        if (errors.Count > 0)
        {
            return new PlanOutcome(Array.Empty<Change>(), errors);
        }

        var json = CaJson.LocationJson(name, isIp, list, trusted, existing?.UnknownCountries ?? false);
        var used = existing is null ? new List<CaPolicy>()
            : d.ConditionalAccess?.Policies.Where(b => b.LocationsIn.Concat(b.LocationsOff).Contains(existing.Id, StringComparer.OrdinalIgnoreCase)).ToList() ?? new();
        var warning = used.Count == 0 ? null
            : $"Deze locatie staat in {used.Count} beleid(en): {string.Join(", ", used.Select(b => b.Name))}. De wijziging geldt meteen voor die beleiden.";
        var text = string.Join(", ", list);
        var w = existing is null
            ? new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphCreateCaLocation,
                Target = "nieuw",
                TargetName = name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json },
                Description = $"Nieuwe benoemde locatie \"{name}\"" + (isIp && trusted ? " (vertrouwd)" : string.Empty),
                CurrentValue = "(bestaat niet)",
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
                Description = $"Benoemde locatie \"{existing.Name}\" wijzigen",
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
                details.Add("andere instellingen (sessie, toestelfilter, ...)");
            }

            if (details.Count > 0)
            {
                output.Add(new CaDifference(n.Id, n.Name, "gewijzigd", details));
            }
        }

        output.AddRange(old.Where(o => newValue.All(n => n.Id != o.Id)).Select(o => new CaDifference(o.Id, o.Name, "verdwenen", new[] { "bestaat niet meer" })));
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
                     ("opgenomen gebruikers", o.UsersIn, n.UsersIn, name),
                     ("uitgezonderde gebruikers", o.UsersOff, n.UsersOff, name),
                     ("opgenomen groepen", o.GroupsIn, n.GroupsIn, name),
                     ("uitgezonderde groepen", o.GroupsOff, n.GroupsOff, name),
                     ("opgenomen rollen", o.RolesIn, n.RolesIn, Role),
                     ("uitgezonderde rollen", o.RolesOff, n.RolesOff, Role),
                     ("apps", o.AppsIn, n.AppsIn, CaReader.AppName),
                     ("uitgezonderde apps", o.AppsOff, n.AppsOff, CaReader.AppName),
                     ("programma's", o.ClientApps, n.ClientApps, CaReader.ClientName),
                     ("locaties", o.LocationsIn, n.LocationsIn, Location),
                     ("uitgezonderde locaties", o.LocationsOff, n.LocationsOff, Location),
                     ("vereisten", o.Checks, n.Checks, CaReader.CheckName),
                 })
        {
            var added = b.Except(a, StringComparer.OrdinalIgnoreCase).Select(names).ToList();
            var removed = a.Except(b, StringComparer.OrdinalIgnoreCase).Select(names).ToList();
            if (added.Count + removed.Count > 0)
            {
                yield return $"{title}: " + string.Join("; ", added.Select(x => "+ " + x).Concat(removed.Select(x => "− " + x)));
            }
        }

        if (o.StrengthId != n.StrengthId)
        {
            yield return $"authenticatiesterkte: {o.StrengthName ?? o.StrengthId ?? "(geen)"} → {n.StrengthName ?? n.StrengthId ?? "(geen)"}";
        }
    }
}
