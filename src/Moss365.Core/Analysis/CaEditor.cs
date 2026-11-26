using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

public enum CaUsersMode
{
    All,
    None,
    Selected,
}

public enum CaAppsMode
{
    All,
    Office365,
    AdminPortals,
    Selected,
    UserAction,

    /// <summary>A shape the editor does not know (authentication context, app filter): left as it is.</summary>
    Other,
}

/// <summary>
/// A Conditional Access policy as the editor shows it (0.30): every condition, the grant and the session. Lists are
/// empty when a condition is not set. Built from a policy with <see cref="Of"/>; the JSON for Graph comes from
/// <see cref="CaEditor"/>, starting from the policy as read, so that fields the editor does not know stay as they are.
/// </summary>
public sealed class CaEdit
{
    public string Name { get; set; } = string.Empty;
    public CaStatus Status { get; set; } = CaStatus.Report;

    public CaUsersMode UsersMode { get; set; } = CaUsersMode.All;
    public List<string> UsersIn { get; set; } = new();
    public List<string> GroupsIn { get; set; } = new();
    public List<string> RolesIn { get; set; } = new();
    public bool GuestsIn { get; set; }
    public List<string> UsersOff { get; set; } = new();
    public List<string> GroupsOff { get; set; } = new();
    public List<string> RolesOff { get; set; } = new();
    public bool GuestsOff { get; set; }

    public CaAppsMode AppsMode { get; set; } = CaAppsMode.All;
    public List<string> AppsIn { get; set; } = new();
    public List<string> AppsOff { get; set; } = new();
    public string UserAction { get; set; } = CaEditor.RegisterSecurityInfo;

    /// <summary>Empty: all client apps.</summary>
    public List<string> ClientApps { get; set; } = new();

    /// <summary>Both empty: platforms not set. PlatformsIn empty with exclusions: all platforms except those.</summary>
    public List<string> PlatformsIn { get; set; } = new();
    public List<string> PlatformsOff { get; set; } = new();

    /// <summary>Both empty: locations not set. "All", "AllTrusted" or ids of named locations.</summary>
    public List<string> LocationsIn { get; set; } = new();
    public List<string> LocationsOff { get; set; } = new();

    public List<string> SignInRisk { get; set; } = new();
    public List<string> UserRisk { get; set; } = new();

    /// <summary>deviceCodeFlow, authenticationTransfer.</summary>
    public List<string> Transfer { get; set; } = new();

    /// <summary>"include" or "exclude"; with an empty rule there is no device filter.</summary>
    public string DeviceFilterMode { get; set; } = "exclude";
    public string DeviceFilterRule { get; set; } = string.Empty;

    public bool Block { get; set; }

    /// <summary>Built-in controls except block: mfa, compliantDevice, domainJoinedDevice, approvedApplication,
    /// compliantApplication, passwordChange.</summary>
    public List<string> Checks { get; set; } = new();
    public string Operator { get; set; } = "OR";
    public string? StrengthId { get; set; }

    /// <summary>null (not set), "hours", "days" or "everyTime".</summary>
    public string? FrequencyType { get; set; }
    public int FrequencyValue { get; set; } = 8;

    /// <summary>null (not set), "always" or "never".</summary>
    public string? Browser { get; set; }

    private static readonly string[] Special = { "All", "None", "GuestsOrExternalUsers" };

    public static CaEdit Of(CaPolicy b)
    {
        JsonObject? json = b.Json.Length > 0 ? JsonNode.Parse(b.Json) as JsonObject : null;
        var e = new CaEdit
        {
            Name = b.Name,
            Status = b.Status,
            UsersMode = b.UsersIn.Contains("All") ? CaUsersMode.All
                : b.UsersIn.Contains("None") && b.GroupsIn.Count == 0 && b.RolesIn.Count == 0 && !b.GuestsIn ? CaUsersMode.None
                : CaUsersMode.Selected,
            UsersIn = b.UsersIn.Where(u => !Special.Contains(u)).ToList(),
            GroupsIn = b.GroupsIn.ToList(),
            RolesIn = b.RolesIn.ToList(),
            GuestsIn = b.GuestsIn,
            UsersOff = b.UsersOff.Where(u => !Special.Contains(u)).ToList(),
            GroupsOff = b.GroupsOff.ToList(),
            RolesOff = b.RolesOff.ToList(),
            GuestsOff = b.GuestsOff,
            AppsOff = b.AppsOff.ToList(),
            ClientApps = b.ClientApps.Where(c => c != "all").ToList(),
            PlatformsIn = b.PlatformsIn.Where(p => p != "all").ToList(),
            PlatformsOff = b.PlatformsOff.ToList(),
            LocationsIn = b.LocationsIn.ToList(),
            LocationsOff = b.LocationsOff.ToList(),
            SignInRisk = b.SignInRisk.ToList(),
            UserRisk = b.UsersRisk.ToList(),
            Transfer = b.Transfer.ToList(),
            Block = b.Blocks,
            Checks = b.Checks.Where(c => c != "block").ToList(),
            Operator = b.Operator,
            StrengthId = b.StrengthId,
        };

        // "All platforms" with nothing excluded stays a set condition
        if (b.PlatformsIn.Contains("all") && e.PlatformsOff.Count == 0)
        {
            e.PlatformsIn = new() { "all" };
        }

        var apps = json?["conditions"]?["applications"] as JsonObject;
        var hasContext = apps?["includeAuthenticationContextClassReferences"] is JsonArray { Count: > 0 } || apps?["applicationFilter"] is JsonObject;
        if (hasContext)
        {
            e.AppsMode = CaAppsMode.Other;
        }
        else if (b.UserActions.Count > 0)
        {
            e.AppsMode = CaAppsMode.UserAction;
            e.UserAction = b.UserActions[0];
        }
        else if (b.AppsIn.Count == 1 && b.AppsIn[0] == "All")
        {
            e.AppsMode = CaAppsMode.All;
        }
        else if (b.AppsIn.Count == 1 && b.AppsIn[0] == "Office365")
        {
            e.AppsMode = CaAppsMode.Office365;
        }
        else if (b.AppsIn.Count == 1 && b.AppsIn[0] == "MicrosoftAdminPortals")
        {
            e.AppsMode = CaAppsMode.AdminPortals;
        }
        else
        {
            e.AppsMode = CaAppsMode.Selected;
            e.AppsIn = b.AppsIn.Where(a => a != "None").ToList();
        }

        if (json?["conditions"]?["devices"]?["deviceFilter"] is JsonObject filter)
        {
            e.DeviceFilterMode = filter["mode"]?.GetValue<string>() ?? "exclude";
            e.DeviceFilterRule = filter["rule"]?.GetValue<string>() ?? string.Empty;
        }

        if (json?["sessionControls"]?["signInFrequency"] is JsonObject f && f["isEnabled"]?.GetValue<bool>() == true)
        {
            if (f["frequencyInterval"]?.GetValue<string>() == "everyTime")
            {
                e.FrequencyType = "everyTime";
            }
            else
            {
                e.FrequencyType = f["type"]?.GetValue<string>() == "days" ? "days" : "hours";
                e.FrequencyValue = f["value"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 8;
            }
        }

        if (json?["sessionControls"]?["persistentBrowser"] is JsonObject pb && pb["isEnabled"]?.GetValue<bool>() == true)
        {
            e.Browser = pb["mode"]?.GetValue<string>() == "never" ? "never" : "always";
        }

        return e;
    }
}

/// <summary>
/// The Conditional Access editor (0.30): the JSON of an edited policy, the partial change for Graph (only the parts
/// that changed, with the old values for undo), the checks before planning, and the impact on who falls under the
/// policy. Every plan goes through <see cref="CaSafety"/>; a new policy never starts switched on, and a policy is never
/// deleted.
/// </summary>
public static class CaEditor
{
    public const string RegisterSecurityInfo = "urn:user:registersecurityinfo";
    public const string RegisterDevice = "urn:user:registerdevice";

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public static readonly IReadOnlyList<string> ClientAppChoices = new[] { "browser", "mobileAppsAndDesktopClients", "exchangeActiveSync", "other" };
    public static readonly IReadOnlyList<string> PlatformChoices = new[] { "android", "iOS", "windows", "macOS", "linux", "windowsPhone" };
    public static readonly IReadOnlyList<string> RiskChoices = new[] { "low", "medium", "high", "none" };
    public static readonly IReadOnlyList<string> CheckChoices = new[] { "mfa", "compliantDevice", "domainJoinedDevice", "approvedApplication", "compliantApplication", "passwordChange" };
    public static readonly IReadOnlyList<string> TransferChoices = new[] { "deviceCodeFlow", "authenticationTransfer" };

    /// <summary>A new policy: everyone except the emergency accounts, all apps, MFA, in report-only.</summary>
    public static CaEdit New(InventoryData d, OrganisationConfig c)
        => new()
        {
            Name = string.Empty,
            Status = CaStatus.Report,
            UsersMode = CaUsersMode.All,
            UsersOff = CaSafety.EmergencyAccounts(d, c).Accounts.Select(p => p.Entra!.Id).ToList(),
            AppsMode = CaAppsMode.All,
            Checks = new() { "mfa" },
        };

    /// <summary>A copy of a policy, to start a new one from: in report-only, with another name.</summary>
    public static CaEdit Duplicate(CaPolicy b)
    {
        var e = CaEdit.Of(b);
        e.Name = "Kopie van " + b.Name;
        e.Status = CaStatus.Report;
        return e;
    }

    /// <summary>Adds the emergency accounts that are not excluded yet; returns how many.</summary>
    public static int ExcludeEmergencyAccounts(CaEdit e, InventoryData d, OrganisationConfig c)
    {
        var added = 0;
        foreach (var id in CaSafety.EmergencyAccounts(d, c).Accounts.Select(p => p.Entra!.Id))
        {
            if (!e.UsersOff.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                e.UsersOff.Add(id);
                added++;
            }
        }

        return added;
    }

    /// <summary>What is wrong with the edited policy before the safety check (empty when nothing).</summary>
    public static IReadOnlyList<string> Validate(CaEdit e, CaData? ca = null, string? ownId = null)
    {
        var errors = new List<string>();
        if (e.Name.Trim().Length == 0)
        {
            errors.Add("Geef het beleid een naam.");
        }
        else if (ca is not null && ca.Policies.Any(b => b.Id != ownId && string.Equals(b.Name.Trim(), e.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"Er bestaat al een beleid \"{e.Name.Trim()}\".");
        }

        if (e.UsersMode == CaUsersMode.Selected && e.UsersIn.Count + e.GroupsIn.Count + e.RolesIn.Count == 0 && !e.GuestsIn)
        {
            errors.Add("Kies wie onder het beleid valt: gebruikers, groepen, rollen of gasten.");
        }

        if (e.AppsMode == CaAppsMode.Selected && e.AppsIn.Count == 0)
        {
            errors.Add("Kies minstens één app, of kies alle apps.");
        }

        if (e.AppsMode == CaAppsMode.UserAction && e.UserAction is not (RegisterSecurityInfo or RegisterDevice))
        {
            errors.Add("Kies een gebruikersactie.");
        }

        var hasSession = e.FrequencyType is not null || e.Browser is not null;
        if (!e.Block && e.Checks.Count == 0 && e.StrengthId is null && !hasSession)
        {
            errors.Add("Kies wat er gebeurt: blokkeren, een vereiste (bv. MFA) of een sessie-instelling.");
        }

        if (!e.Block && e.Checks.Contains("mfa") && e.StrengthId is not null)
        {
            errors.Add("MFA en een authenticatiesterkte samen kan niet: de sterkte vraagt al MFA. Kies er één.");
        }

        if (e.FrequencyType == "hours" && e.FrequencyValue is < 1 or > 23)
        {
            errors.Add("De aanmeldfrequentie in uren ligt tussen 1 en 23.");
        }

        if (e.FrequencyType == "days" && e.FrequencyValue is < 1 or > 365)
        {
            errors.Add("De aanmeldfrequentie in dagen ligt tussen 1 en 365.");
        }

        if (e.DeviceFilterRule.Trim().Length > 0 && e.DeviceFilterMode is not ("include" or "exclude"))
        {
            errors.Add("Kies of het toestelfilter toestellen opneemt of uitsluit.");
        }

        if (e.LocationsIn.Count == 0 && e.LocationsOff.Count > 0 && e.LocationsOff.Contains("All"))
        {
            errors.Add("Alle locaties uitsluiten laat niets over.");
        }

        return errors;
    }

    /// <summary>The whole policy as Graph JSON (writable), starting from the policy as read when there is one.</summary>
    public static string ToJson(CaEdit e, CaPolicy? current = null)
    {
        var node = current is { Json.Length: > 0 }
            ? JsonNode.Parse(CaJson.Writable(current.Json))!.AsObject()
            : current is not null ? JsonNode.Parse(CaJson.ToGraph(current))!.AsObject() : new JsonObject();

        node["displayName"] = e.Name.Trim();
        node["state"] = CaJson.StatusValue(e.Status);

        var conditions = node["conditions"] as JsonObject ?? new JsonObject();
        node["conditions"] = conditions;
        conditions["users"] = Users(e, conditions["users"] as JsonObject);
        if (e.AppsMode != CaAppsMode.Other || conditions["applications"] is null)
        {
            conditions["applications"] = Applications(e, conditions["applications"] as JsonObject);
        }

        conditions["clientAppTypes"] = Items(e.ClientApps.Count == 0 ? new[] { "all" } : e.ClientApps);
        conditions["platforms"] = e.PlatformsIn.Count == 0 && e.PlatformsOff.Count == 0
            ? null
            : new JsonObject { ["includePlatforms"] = Items(e.PlatformsIn.Count == 0 ? new[] { "all" } : e.PlatformsIn), ["excludePlatforms"] = Items(e.PlatformsOff) };
        conditions["locations"] = e.LocationsIn.Count == 0 && e.LocationsOff.Count == 0
            ? null
            : new JsonObject { ["includeLocations"] = Items(e.LocationsIn.Count == 0 ? new[] { "All" } : e.LocationsIn), ["excludeLocations"] = Items(e.LocationsOff) };
        conditions["signInRiskLevels"] = Items(e.SignInRisk);
        conditions["userRiskLevels"] = Items(e.UserRisk);
        conditions["authenticationFlows"] = e.Transfer.Count == 0 ? null : new JsonObject { ["transferMethods"] = string.Join(",", e.Transfer) };
        conditions["devices"] = e.DeviceFilterRule.Trim().Length == 0
            ? null
            : new JsonObject { ["deviceFilter"] = new JsonObject { ["mode"] = e.DeviceFilterMode, ["rule"] = e.DeviceFilterRule.Trim() } };

        node["grantControls"] = Grant(e, node["grantControls"] as JsonObject);
        node["sessionControls"] = Session(e, node["sessionControls"] as JsonObject);
        return node.ToJsonString(Compact);
    }

    /// <summary>
    /// The change for Graph: only the parts that differ from the policy as read (a whole block per part, null for a
    /// part that goes away); empty when nothing changes.
    /// </summary>
    public static string Patch(CaPolicy current, CaEdit e)
    {
        var before = JsonNode.Parse(current.Json.Length > 0 ? CaJson.Writable(current.Json) : CaJson.ToGraph(current))!.AsObject();
        var after = JsonNode.Parse(ToJson(e, current))!.AsObject();
        var patch = new JsonObject();
        foreach (var key in new[] { "displayName", "state", "grantControls", "sessionControls" })
        {
            if (!Same(before[key], after[key]))
            {
                patch[key] = after[key]?.DeepClone();
            }
        }

        var oldConditions = before["conditions"] as JsonObject ?? new JsonObject();
        var newConditions = after["conditions"] as JsonObject ?? new JsonObject();
        var conditions = new JsonObject();
        foreach (var key in oldConditions.Select(p => p.Key).Union(newConditions.Select(p => p.Key)).ToList())
        {
            if (!Same(oldConditions[key], newConditions[key]))
            {
                conditions[key] = newConditions[key]?.DeepClone();
            }
        }

        if (conditions.Count > 0)
        {
            patch["conditions"] = conditions;
        }

        return patch.Count == 0 ? string.Empty : patch.ToJsonString(Compact);
    }

    /// <summary>The policy as it would be after the edit (for the checks, the explanation and the impact).</summary>
    public static CaPolicy Preview(CaEdit e, CaPolicy? current)
    {
        var json = ToJson(e, current);
        using var doc = JsonDocument.Parse(json);
        return CaJson.Read(doc.RootElement) with { Id = current?.Id ?? string.Empty };
    }

    /// <summary>Who comes under the policy and who is no longer under it (certain or maybe) after the edit.</summary>
    public static (IReadOnlyList<Person> Added, IReadOnlyList<Person> Removed, int After) Impact(CaPolicy? current, CaPolicy after, InventoryData d)
    {
        var then = current is null ? new List<Person>() : CaReader.WhoFallsUnder(current, d).Select(x => x.Person).ToList();
        var now = CaReader.WhoFallsUnder(after, d).Select(x => x.Person).ToList();
        var thenKeys = then.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nowKeys = now.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (now.Where(p => !thenKeys.Contains(p.Key)).ToList(), then.Where(p => !nowKeys.Contains(p.Key)).ToList(), now.Count);
    }

    /// <summary>The plan that changes an existing policy, after the checks; with the differences in plain language.</summary>
    public static PlanOutcome Update(CaPolicy current, CaEdit e, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        var errors = Validate(e, d.ConditionalAccess, current.Id);
        if (errors.Count > 0)
        {
            return new PlanOutcome(Array.Empty<Change>(), errors);
        }

        var patch = Patch(current, e);
        if (patch.Length == 0)
        {
            return PlanOutcome.Error(Localization.Texts.T["plan.nothing-changed"]);
        }

        var after = CaJson.AfterPatch(current, patch);
        var check = CaSafety.Check(after, d, c, ownUpn);
        if (!check.Can)
        {
            return new PlanOutcome(Array.Empty<Change>(), check.Errors);
        }

        var details = Differences(current, after, d);
        var currentJson = current.Json.Length > 0 ? current.Json : CaJson.ToGraph(current);
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphUpdateCaPolicy,
                Target = current.Id,
                TargetName = current.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = patch },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["json"] = CaJson.Previous(currentJson, patch) },
                Description = $"Beleid \"{current.Name}\" wijzigen",
                CurrentValue = CaReader.StatusName(current.Status),
                NewValue = details.Count == 0 ? "andere instellingen" : string.Join("; ", details),
                Warning = check.Warnings.Count == 0 ? null : string.Join(" ", check.Warnings),
            },
        });
    }

    /// <summary>The plan for a new policy (free, or a copy); never switched on at once.</summary>
    public static PlanOutcome Create(CaEdit e, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        if (d.ConditionalAccess is not { Message: null } ca)
        {
            return PlanOutcome.Error("Conditional Access is niet geladen. Laad eerst live (Verbindingen).");
        }

        var errors = Validate(e, ca).ToList();
        if (e.Status == CaStatus.On)
        {
            errors.Add("Een nieuw beleid begint in alleen rapport (of uit). Kijk eerst het effect na en zet het daarna aan.");
        }

        if (errors.Count > 0)
        {
            return new PlanOutcome(Array.Empty<Change>(), errors);
        }

        var policy = Preview(e, null);
        var check = CaSafety.Check(policy, d, c, ownUpn);
        if (!check.Can)
        {
            return new PlanOutcome(Array.Empty<Change>(), check.Errors);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphCreateCaPolicy,
                Target = "nieuw",
                TargetName = policy.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = ToJson(e) },
                Description = $"Nieuw Conditional Access-beleid \"{policy.Name}\" ({CaReader.StatusName(policy.Status).ToLowerInvariant()})",
                CurrentValue = "(bestaat niet)",
                NewValue = string.Join("; ", CaReader.Explanation(policy, ca, CaReader.Names(d)).Who.Take(2)),
                Warning = check.Warnings.Count == 0 ? null : string.Join(" ", check.Warnings),
            },
        });
    }

    /// <summary>The differences between the policy now and after the edit, in plain language.</summary>
    public static IReadOnlyList<string> Differences(CaPolicy current, CaPolicy after, InventoryData d)
        => CaComparison.Compare(new[] { Plain(current) }, new[] { Plain(after) with { Id = current.Id } }, CaReader.Names(d), d.ConditionalAccess)
            .SelectMany(v => v.Details).ToList();

    /// <summary>"All client apps" written out or left empty is the same; the JSON is not compared here.</summary>
    private static CaPolicy Plain(CaPolicy b) => b with { ClientApps = b.ClientApps.Where(c => c != "all").ToList(), Json = string.Empty };

    // ------------------------------------------------------------------ the blocks

    private static JsonObject Users(CaEdit e, JsonObject? read)
    {
        var users = read is null ? new JsonObject() : (JsonObject)read.DeepClone();
        var include = e.UsersMode switch
        {
            CaUsersMode.All => new List<string> { "All" },
            CaUsersMode.None => new List<string> { "None" },
            _ => e.UsersIn.ToList(),
        };

        var selected = e.UsersMode == CaUsersMode.Selected;
        var guestsInObject = users["includeGuestsOrExternalUsers"] is JsonObject;
        if (!(selected && e.GuestsIn))
        {
            users.Remove("includeGuestsOrExternalUsers");
        }
        else if (!guestsInObject)
        {
            include.Add("GuestsOrExternalUsers");
        }

        if (include.Count == 0 && !(selected && e.GuestsIn && guestsInObject) && (!selected || e.GroupsIn.Count + e.RolesIn.Count == 0))
        {
            include.Add("None");
        }

        var exclude = e.UsersOff.ToList();
        if (!e.GuestsOff)
        {
            users.Remove("excludeGuestsOrExternalUsers");
        }
        else if (users["excludeGuestsOrExternalUsers"] is not JsonObject)
        {
            exclude.Add("GuestsOrExternalUsers");
        }

        users["includeUsers"] = Items(include);
        users["excludeUsers"] = Items(exclude);
        users["includeGroups"] = Items(selected ? e.GroupsIn : new List<string>());
        users["excludeGroups"] = Items(e.GroupsOff);
        users["includeRoles"] = Items(selected ? e.RolesIn : new List<string>());
        users["excludeRoles"] = Items(e.RolesOff);
        return users;
    }

    private static JsonObject Applications(CaEdit e, JsonObject? read)
    {
        var apps = read is null ? new JsonObject() : (JsonObject)read.DeepClone();
        var include = e.AppsMode switch
        {
            CaAppsMode.All => new List<string> { "All" },
            CaAppsMode.Office365 => new List<string> { "Office365" },
            CaAppsMode.AdminPortals => new List<string> { "MicrosoftAdminPortals" },
            CaAppsMode.UserAction => new List<string>(),
            _ => e.AppsIn.ToList(),
        };

        apps["includeApplications"] = Items(include);
        apps["excludeApplications"] = Items(e.AppsMode == CaAppsMode.UserAction ? new List<string>() : e.AppsOff);
        apps["includeUserActions"] = Items(e.AppsMode == CaAppsMode.UserAction ? new[] { e.UserAction } : Array.Empty<string>());
        return apps;
    }

    private static JsonObject? Grant(CaEdit e, JsonObject? read)
    {
        if (e.Block)
        {
            return new JsonObject { ["operator"] = "OR", ["builtInControls"] = Items(new[] { "block" }) };
        }

        var termsOfUse = read?["termsOfUse"] is JsonArray { Count: > 0 } terms ? terms.DeepClone() : null;
        var custom = read?["customAuthenticationFactors"] is JsonArray { Count: > 0 } factors ? factors.DeepClone() : null;
        if (e.Checks.Count == 0 && e.StrengthId is null && termsOfUse is null && custom is null)
        {
            return null;
        }

        var grant = new JsonObject
        {
            ["operator"] = e.Operator == "AND" ? "AND" : "OR",
            ["builtInControls"] = Items(e.Checks),
        };
        if (e.StrengthId is not null)
        {
            grant["authenticationStrength"] = new JsonObject { ["id"] = e.StrengthId };
        }

        if (termsOfUse is not null)
        {
            grant["termsOfUse"] = termsOfUse;
        }

        if (custom is not null)
        {
            grant["customAuthenticationFactors"] = custom;
        }

        return grant;
    }

    private static JsonObject? Session(CaEdit e, JsonObject? read)
    {
        var session = read is null ? new JsonObject() : (JsonObject)read.DeepClone();
        session["signInFrequency"] = e.FrequencyType switch
        {
            "everyTime" => new JsonObject { ["isEnabled"] = true, ["frequencyInterval"] = "everyTime", ["authenticationType"] = "primaryAndSecondaryAuthentication" },
            "hours" or "days" => new JsonObject
            {
                ["isEnabled"] = true,
                ["value"] = e.FrequencyValue,
                ["type"] = e.FrequencyType,
                ["frequencyInterval"] = "timeBased",
                ["authenticationType"] = "primaryAndSecondaryAuthentication",
            },
            _ => null,
        };
        session["persistentBrowser"] = e.Browser is null ? null : new JsonObject { ["isEnabled"] = true, ["mode"] = e.Browser };

        // Nothing left: no session controls at all
        return session.Any(p => p.Value is not null && !(p.Value is JsonValue v && v.TryGetValue<bool>(out var flag) && !flag)) ? session : null;
    }

    private static JsonArray Items(IEnumerable<string> values) => new(values.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());

    /// <summary>Equal apart from what Graph adds or leaves out by itself: null values, empty lists and OData
    /// annotations.</summary>
    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Canon(a), Canon(b));

    private static JsonNode? Canon(JsonNode? n)
    {
        switch (n)
        {
            case null:
                return null;
            case JsonArray { Count: 0 }:
                return null;
            case JsonArray array:
                return new JsonArray(array.Select(Canon).ToArray());
            case JsonObject o:
            {
                var output = new JsonObject();
                foreach (var (key, value) in o)
                {
                    if (key.Contains("@odata", StringComparison.Ordinal) || Canon(value) is not { } v)
                    {
                        continue;
                    }

                    output[key] = v;
                }

                return output.Count == 0 ? null : output;
            }

            default:
                return n.DeepClone();
        }
    }
}
