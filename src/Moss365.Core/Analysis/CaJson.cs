using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

/// <summary>
/// Conditional Access in the shape of Graph (JSON): reading to <see cref="CaPolicy"/>, and creating the JSON the app
/// sends for a new policy or a change. A change is always a partial JSON (only what changes), with the old values of
/// the same fields next to it, so that undo can go back the same way.
/// </summary>
public static class CaJson
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private static string S(JsonElement e, string name)
        => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;

    private static bool B(JsonElement e, string name)
        => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.True;

    public static CaPolicy Read(JsonElement p)
    {
        static JsonElement? O(JsonElement e, string name) => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object ? w : null;

        static IReadOnlyList<string> L(JsonElement? e, string name)
            => e is { } x && x.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : Array.Empty<string>();

        static DateTimeOffset? D(JsonElement e, string name)
            => DateTimeOffset.TryParse(S(e, name), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        var conditions = O(p, "conditions");
        var who = conditions is { } v1 ? O(v1, "users") : null;
        var apps = conditions is { } v2 ? O(v2, "applications") : null;
        var platforms = conditions is { } v3 ? O(v3, "platforms") : null;
        var locations = conditions is { } v4 ? O(v4, "locations") : null;
        var devices = conditions is { } v5 ? O(v5, "devices") : null;
        var streams = conditions is { } v6 ? O(v6, "authenticationFlows") : null;
        var assign = O(p, "grantControls");
        var session = O(p, "sessionControls");
        var strength = assign is { } t1 ? O(t1, "authenticationStrength") : null;

        string? frequency = null;
        if (session is { } s1 && O(s1, "signInFrequency") is { } f && f.TryGetProperty("isEnabled", out var fa) && fa.ValueKind == JsonValueKind.True)
        {
            frequency = S(f, "frequencyInterval") == "everyTime"
                ? "elke keer"
                : f.TryGetProperty("value", out var fv) && fv.ValueKind == JsonValueKind.Number
                    ? $"{fv.GetInt32()} {(S(f, "type") == "days" ? "dagen" : "uur")}"
                    : null;
        }

        string? browser = null;
        if (session is { } s2 && O(s2, "persistentBrowser") is { } pb && pb.TryGetProperty("isEnabled", out var pa) && pa.ValueKind == JsonValueKind.True)
        {
            browser = S(pb, "mode") == "never" ? "nooit aangemeld blijven" : "altijd aangemeld blijven";
        }

        var usersIn = L(who, "includeUsers");
        var usersOff = L(who, "excludeUsers");
        return new CaPolicy
        {
            Id = S(p, "id"),
            Name = S(p, "displayName"),
            Status = S(p, "state") switch
            {
                "enabled" => CaStatus.On,
                "enabledForReportingButNotEnforced" => CaStatus.Report,
                _ => CaStatus.Off,
            },
            Created = D(p, "createdDateTime"),
            Changed = D(p, "modifiedDateTime"),
            UsersIn = usersIn,
            UsersOff = usersOff,
            GroupsIn = L(who, "includeGroups"),
            GroupsOff = L(who, "excludeGroups"),
            RolesIn = L(who, "includeRoles"),
            RolesOff = L(who, "excludeRoles"),
            GuestsIn = usersIn.Contains("GuestsOrExternalUsers") || (who is { } w1 && O(w1, "includeGuestsOrExternalUsers") is not null),
            GuestsOff = usersOff.Contains("GuestsOrExternalUsers") || (who is { } w2 && O(w2, "excludeGuestsOrExternalUsers") is not null),
            AppsIn = L(apps, "includeApplications"),
            AppsOff = L(apps, "excludeApplications"),
            UserActions = L(apps, "includeUserActions"),
            ClientApps = L(conditions, "clientAppTypes"),
            PlatformsIn = L(platforms, "includePlatforms"),
            PlatformsOff = L(platforms, "excludePlatforms"),
            LocationsIn = L(locations, "includeLocations"),
            LocationsOff = L(locations, "excludeLocations"),
            SignInRisk = L(conditions, "signInRiskLevels"),
            UsersRisk = L(conditions, "userRiskLevels"),
            DeviceFilter = devices is { } d1 && O(d1, "deviceFilter") is not null,
            Transfer = streams is { } st ? S(st, "transferMethods").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>(),
            Operator = assign is { } t2 && S(t2, "operator") == "AND" ? "AND" : "OR",
            Checks = L(assign, "builtInControls"),
            StrengthId = strength is { } a1 && S(a1, "id").Length > 0 ? S(a1, "id") : null,
            StrengthName = strength is { } a2 && S(a2, "displayName").Length > 0 ? S(a2, "displayName") : null,
            TermsOfUse = L(assign, "termsOfUse").Count > 0,
            SignInFrequency = frequency,
            PersistentBrowser = browser,
            Json = p.GetRawText(),
        };
    }

    public static NamedLocation ReadLocation(JsonElement l)
    {
        var isIp = S(l, "@odata.type").EndsWith("ipNamedLocation", StringComparison.OrdinalIgnoreCase);
        var values = new List<string>();
        if (isIp && l.TryGetProperty("ipRanges", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            values.AddRange(r.EnumerateArray().Select(x => S(x, "cidrAddress")).Where(x => x.Length > 0));
        }
        else if (l.TryGetProperty("countriesAndRegions", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            values.AddRange(c.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0));
        }

        return new NamedLocation(S(l, "id"), S(l, "displayName"), isIp, B(l, "isTrusted"), values, B(l, "includeUnknownCountriesAndRegions")) { Json = l.GetRawText() };
    }

    /// <summary>The Graph value of a status.</summary>
    public static string StatusValue(CaStatus s) => s switch
    {
        CaStatus.On => "enabled",
        CaStatus.Report => "enabledForReportingButNotEnforced",
        _ => "disabled",
    };

    /// <summary>Fields that Graph keeps itself and that you must not send along.</summary>
    private static readonly string[] ReadOnlyMode = { "@odata.context", "@odata.etag", "id", "createdDateTime", "modifiedDateTime", "templateId", "deletedDateTime" };

    /// <summary>
    /// A policy from Graph (or a backup) as JSON you can send back: without the fields Graph keeps itself, and the
    /// authentication strength only with its id.
    /// </summary>
    public static string Writable(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var field in ReadOnlyMode)
        {
            node.Remove(field);
        }

        if (node["grantControls"] is JsonObject assign && assign["authenticationStrength"] is JsonObject strength)
        {
            assign["authenticationStrength"] = strength["id"] is { } id ? new JsonObject { ["id"] = id.GetValue<string>() } : null;
        }

        return node.ToJsonString(Compact);
    }

    /// <summary>A named location as JSON you can send back (the type stays: Graph asks for it).</summary>
    public static string LocationWritable(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var field in ReadOnlyMode)
        {
            node.Remove(field);
        }

        return node.ToJsonString(Compact);
    }

    private static JsonArray Items(IEnumerable<string> values) => new(values.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());

    /// <summary>A new policy (from a template) as Graph JSON.</summary>
    public static string ToGraph(CaPolicy b)
    {
        var users = new JsonObject
        {
            ["includeUsers"] = Items(b.UsersIn.Count == 0 && b.GroupsIn.Count == 0 && b.RolesIn.Count == 0 ? new[] { "None" } : b.UsersIn),
            ["excludeUsers"] = Items(b.UsersOff),
            ["includeGroups"] = Items(b.GroupsIn),
            ["excludeGroups"] = Items(b.GroupsOff),
            ["includeRoles"] = Items(b.RolesIn),
            ["excludeRoles"] = Items(b.RolesOff),
        };
        var applications = new JsonObject
        {
            ["includeApplications"] = Items(b.AppsIn),
            ["excludeApplications"] = Items(b.AppsOff),
            ["includeUserActions"] = Items(b.UserActions),
        };
        var conditions = new JsonObject
        {
            ["users"] = users,
            ["applications"] = applications,
            ["clientAppTypes"] = Items(b.ClientApps.Count == 0 ? new[] { "all" } : b.ClientApps),
        };
        if (b.LocationsIn.Count > 0 || b.LocationsOff.Count > 0)
        {
            conditions["locations"] = new JsonObject { ["includeLocations"] = Items(b.LocationsIn), ["excludeLocations"] = Items(b.LocationsOff) };
        }

        if (b.PlatformsIn.Count > 0 || b.PlatformsOff.Count > 0)
        {
            conditions["platforms"] = new JsonObject { ["includePlatforms"] = Items(b.PlatformsIn), ["excludePlatforms"] = Items(b.PlatformsOff) };
        }

        if (b.Transfer.Count > 0)
        {
            conditions["authenticationFlows"] = new JsonObject { ["transferMethods"] = string.Join(",", b.Transfer) };
        }

        var grant = new JsonObject
        {
            ["operator"] = b.Operator,
            ["builtInControls"] = Items(b.Checks),
        };
        if (b.StrengthId is not null)
        {
            grant["authenticationStrength"] = new JsonObject { ["id"] = b.StrengthId };
        }

        var policy = new JsonObject
        {
            ["displayName"] = b.Name,
            ["state"] = StatusValue(b.Status),
            ["conditions"] = conditions,
            ["grantControls"] = grant,
        };
        return policy.ToJsonString(Compact);
    }

    /// <summary>The status as partial JSON.</summary>
    public static string StatusPatch(CaStatus s) => new JsonObject { ["state"] = StatusValue(s) }.ToJsonString(Compact);

    /// <summary>
    /// The users of a policy with other excluded users, as partial JSON. The whole "users" block goes along (from the
    /// read JSON if present), so that Graph loses no inclusion or other exclusion.
    /// </summary>
    public static string ExclusionsPatch(CaPolicy b, IEnumerable<string> excluded)
    {
        JsonObject users;
        if (b.Json.Length > 0 && JsonNode.Parse(b.Json)?["conditions"]?["users"] is JsonObject read)
        {
            users = (JsonObject)read.DeepClone();
        }
        else
        {
            users = new JsonObject
            {
                ["includeUsers"] = Items(b.UsersIn),
                ["includeGroups"] = Items(b.GroupsIn),
                ["excludeGroups"] = Items(b.GroupsOff),
                ["includeRoles"] = Items(b.RolesIn),
                ["excludeRoles"] = Items(b.RolesOff),
            };
        }

        users["excludeUsers"] = Items(excluded);
        return new JsonObject { ["conditions"] = new JsonObject { ["users"] = users } }.ToJsonString(Compact);
    }

    /// <summary>A named location with IP ranges (CIDR) or countries, as JSON for Graph.</summary>
    public static string LocationJson(string name, bool isIp, IEnumerable<string> values, bool trusted, bool unknownCountries = false)
    {
        var node = new JsonObject { ["displayName"] = name };
        if (isIp)
        {
            node["@odata.type"] = "#microsoft.graph.ipNamedLocation";
            node["isTrusted"] = trusted;
            node["ipRanges"] = new JsonArray(values.Select(w => (JsonNode?)new JsonObject
            {
                ["@odata.type"] = w.Contains(':', StringComparison.Ordinal) ? "#microsoft.graph.iPv6CidrRange" : "#microsoft.graph.iPv4CidrRange",
                ["cidrAddress"] = w,
            }).ToArray());
        }
        else
        {
            node["@odata.type"] = "#microsoft.graph.countryNamedLocation";
            node["countriesAndRegions"] = Items(values.Select(w => w.ToUpperInvariant()));
            node["includeUnknownCountriesAndRegions"] = unknownCountries;
        }

        return node.ToJsonString(Compact);
    }

    /// <summary>A policy as it becomes after a partial JSON (to check beforehand).</summary>
    public static CaPolicy AfterPatch(CaPolicy b, string patch)
    {
        var baseline = b.Json.Length > 0 ? JsonNode.Parse(b.Json)!.AsObject() : JsonNode.Parse(ToGraph(b))!.AsObject();
        if (b.Json.Length == 0)
        {
            baseline["id"] = b.Id;
        }

        Merge(baseline, JsonNode.Parse(patch)!.AsObject());
        using var doc = JsonDocument.Parse(baseline.ToJsonString());
        return Read(doc.RootElement);
    }

    /// <summary>As Graph applies a partial change: objects are merged, lists and values replaced.</summary>
    private static void Merge(JsonObject target, JsonObject change)
    {
        foreach (var (name, value) in change.ToList())
        {
            if (value is JsonObject sub && target[name] is JsonObject existing)
            {
                Merge(existing, sub);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }

    /// <summary>The same fields as in the change, with the current values (for undo).</summary>
    public static string Previous(string currentJson, string patch)
    {
        var current = JsonNode.Parse(currentJson)!.AsObject();
        return Choose(current, JsonNode.Parse(patch)!.AsObject()).ToJsonString(Compact);
    }

    private static JsonObject Choose(JsonObject source, JsonObject shape)
    {
        var output = new JsonObject();
        foreach (var (name, value) in shape)
        {
            if (value is JsonObject sub && source[name] is JsonObject b)
            {
                output[name] = Choose(b, sub);
            }
            else
            {
                output[name] = source[name]?.DeepClone();
            }
        }

        return output;
    }
}
