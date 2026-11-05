using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

/// <summary>
/// Conditional Access in de vorm van Graph (JSON): lezen naar <see cref="CaBeleid"/>, en de JSON maken die de app
/// stuurt bij een nieuw beleid of een wijziging. Een wijziging is altijd een gedeeltelijke JSON (enkel wat verandert),
/// met de oude waarden van dezelfde velden ernaast, zodat ongedaan maken dezelfde weg terug kan.
/// </summary>
public static class CaJson
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    private static string S(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;

    private static bool B(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var w) && w.ValueKind == JsonValueKind.True;

    public static CaBeleid Lees(JsonElement p)
    {
        static JsonElement? O(JsonElement e, string naam) => e.TryGetProperty(naam, out var w) && w.ValueKind == JsonValueKind.Object ? w : null;

        static IReadOnlyList<string> L(JsonElement? e, string naam)
            => e is { } x && x.TryGetProperty(naam, out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : Array.Empty<string>();

        static DateTimeOffset? D(JsonElement e, string naam)
            => DateTimeOffset.TryParse(S(e, naam), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        var voorwaarden = O(p, "conditions");
        var wie = voorwaarden is { } v1 ? O(v1, "users") : null;
        var apps = voorwaarden is { } v2 ? O(v2, "applications") : null;
        var platforms = voorwaarden is { } v3 ? O(v3, "platforms") : null;
        var locaties = voorwaarden is { } v4 ? O(v4, "locations") : null;
        var toestellen = voorwaarden is { } v5 ? O(v5, "devices") : null;
        var stromen = voorwaarden is { } v6 ? O(v6, "authenticationFlows") : null;
        var toekennen = O(p, "grantControls");
        var sessie = O(p, "sessionControls");
        var sterkte = toekennen is { } t1 ? O(t1, "authenticationStrength") : null;

        string? frequentie = null;
        if (sessie is { } s1 && O(s1, "signInFrequency") is { } f && f.TryGetProperty("isEnabled", out var fa) && fa.ValueKind == JsonValueKind.True)
        {
            frequentie = S(f, "frequencyInterval") == "everyTime"
                ? "elke keer"
                : f.TryGetProperty("value", out var fv) && fv.ValueKind == JsonValueKind.Number
                    ? $"{fv.GetInt32()} {(S(f, "type") == "days" ? "dagen" : "uur")}"
                    : null;
        }

        string? browser = null;
        if (sessie is { } s2 && O(s2, "persistentBrowser") is { } pb && pb.TryGetProperty("isEnabled", out var pa) && pa.ValueKind == JsonValueKind.True)
        {
            browser = S(pb, "mode") == "never" ? "nooit aangemeld blijven" : "altijd aangemeld blijven";
        }

        var gebruikersIn = L(wie, "includeUsers");
        var gebruikersUit = L(wie, "excludeUsers");
        return new CaBeleid
        {
            Id = S(p, "id"),
            Naam = S(p, "displayName"),
            Status = S(p, "state") switch
            {
                "enabled" => CaStatus.Aan,
                "enabledForReportingButNotEnforced" => CaStatus.Rapport,
                _ => CaStatus.Uit,
            },
            Aangemaakt = D(p, "createdDateTime"),
            Gewijzigd = D(p, "modifiedDateTime"),
            GebruikersIn = gebruikersIn,
            GebruikersUit = gebruikersUit,
            GroepenIn = L(wie, "includeGroups"),
            GroepenUit = L(wie, "excludeGroups"),
            RollenIn = L(wie, "includeRoles"),
            RollenUit = L(wie, "excludeRoles"),
            GastenIn = gebruikersIn.Contains("GuestsOrExternalUsers") || (wie is { } w1 && O(w1, "includeGuestsOrExternalUsers") is not null),
            GastenUit = gebruikersUit.Contains("GuestsOrExternalUsers") || (wie is { } w2 && O(w2, "excludeGuestsOrExternalUsers") is not null),
            AppsIn = L(apps, "includeApplications"),
            AppsUit = L(apps, "excludeApplications"),
            Gebruikersacties = L(apps, "includeUserActions"),
            ClientApps = L(voorwaarden, "clientAppTypes"),
            PlatformsIn = L(platforms, "includePlatforms"),
            PlatformsUit = L(platforms, "excludePlatforms"),
            LocatiesIn = L(locaties, "includeLocations"),
            LocatiesUit = L(locaties, "excludeLocations"),
            AanmeldRisico = L(voorwaarden, "signInRiskLevels"),
            GebruikersRisico = L(voorwaarden, "userRiskLevels"),
            Toestelfilter = toestellen is { } d1 && O(d1, "deviceFilter") is not null,
            Overdracht = stromen is { } st ? S(st, "transferMethods").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>(),
            Operator = toekennen is { } t2 && S(t2, "operator") == "AND" ? "AND" : "OR",
            Controles = L(toekennen, "builtInControls"),
            SterkteId = sterkte is { } a1 && S(a1, "id").Length > 0 ? S(a1, "id") : null,
            SterkteNaam = sterkte is { } a2 && S(a2, "displayName").Length > 0 ? S(a2, "displayName") : null,
            Gebruiksvoorwaarden = L(toekennen, "termsOfUse").Count > 0,
            AanmeldFrequentie = frequentie,
            BlijvendeBrowser = browser,
            Json = p.GetRawText(),
        };
    }

    public static BenoemdeLocatie LeesLocatie(JsonElement l)
    {
        var isIp = S(l, "@odata.type").EndsWith("ipNamedLocation", StringComparison.OrdinalIgnoreCase);
        var waarden = new List<string>();
        if (isIp && l.TryGetProperty("ipRanges", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            waarden.AddRange(r.EnumerateArray().Select(x => S(x, "cidrAddress")).Where(x => x.Length > 0));
        }
        else if (l.TryGetProperty("countriesAndRegions", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            waarden.AddRange(c.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0));
        }

        return new BenoemdeLocatie(S(l, "id"), S(l, "displayName"), isIp, B(l, "isTrusted"), waarden, B(l, "includeUnknownCountriesAndRegions")) { Json = l.GetRawText() };
    }

    /// <summary>De Graph-waarde van een status.</summary>
    public static string StatusWaarde(CaStatus s) => s switch
    {
        CaStatus.Aan => "enabled",
        CaStatus.Rapport => "enabledForReportingButNotEnforced",
        _ => "disabled",
    };

    /// <summary>Velden die Graph zelf bijhoudt en die je niet mag meesturen.</summary>
    private static readonly string[] Alleenlezen = { "@odata.context", "@odata.etag", "id", "createdDateTime", "modifiedDateTime", "templateId", "deletedDateTime" };

    /// <summary>
    /// Een beleid uit Graph (of een back-up) als JSON die je kan terugsturen: zonder de velden die Graph zelf bijhoudt, en de
    /// authenticatiesterkte enkel met haar id.
    /// </summary>
    public static string Schrijfbaar(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var veld in Alleenlezen)
        {
            node.Remove(veld);
        }

        if (node["grantControls"] is JsonObject toekennen && toekennen["authenticationStrength"] is JsonObject sterkte)
        {
            toekennen["authenticationStrength"] = sterkte["id"] is { } id ? new JsonObject { ["id"] = id.GetValue<string>() } : null;
        }

        return node.ToJsonString(Compact);
    }

    /// <summary>Een benoemde locatie als JSON die je kan terugsturen (het type blijft: Graph vraagt het).</summary>
    public static string LocatieSchrijfbaar(string json)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var veld in Alleenlezen)
        {
            node.Remove(veld);
        }

        return node.ToJsonString(Compact);
    }

    private static JsonArray Lijst(IEnumerable<string> waarden) => new(waarden.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());

    /// <summary>Een nieuw beleid (uit een sjabloon) als Graph-JSON.</summary>
    public static string NaarGraph(CaBeleid b)
    {
        var users = new JsonObject
        {
            ["includeUsers"] = Lijst(b.GebruikersIn.Count == 0 && b.GroepenIn.Count == 0 && b.RollenIn.Count == 0 ? new[] { "None" } : b.GebruikersIn),
            ["excludeUsers"] = Lijst(b.GebruikersUit),
            ["includeGroups"] = Lijst(b.GroepenIn),
            ["excludeGroups"] = Lijst(b.GroepenUit),
            ["includeRoles"] = Lijst(b.RollenIn),
            ["excludeRoles"] = Lijst(b.RollenUit),
        };
        var applications = new JsonObject
        {
            ["includeApplications"] = Lijst(b.AppsIn),
            ["excludeApplications"] = Lijst(b.AppsUit),
            ["includeUserActions"] = Lijst(b.Gebruikersacties),
        };
        var conditions = new JsonObject
        {
            ["users"] = users,
            ["applications"] = applications,
            ["clientAppTypes"] = Lijst(b.ClientApps.Count == 0 ? new[] { "all" } : b.ClientApps),
        };
        if (b.LocatiesIn.Count > 0 || b.LocatiesUit.Count > 0)
        {
            conditions["locations"] = new JsonObject { ["includeLocations"] = Lijst(b.LocatiesIn), ["excludeLocations"] = Lijst(b.LocatiesUit) };
        }

        if (b.PlatformsIn.Count > 0 || b.PlatformsUit.Count > 0)
        {
            conditions["platforms"] = new JsonObject { ["includePlatforms"] = Lijst(b.PlatformsIn), ["excludePlatforms"] = Lijst(b.PlatformsUit) };
        }

        if (b.Overdracht.Count > 0)
        {
            conditions["authenticationFlows"] = new JsonObject { ["transferMethods"] = string.Join(",", b.Overdracht) };
        }

        var grant = new JsonObject
        {
            ["operator"] = b.Operator,
            ["builtInControls"] = Lijst(b.Controles),
        };
        if (b.SterkteId is not null)
        {
            grant["authenticationStrength"] = new JsonObject { ["id"] = b.SterkteId };
        }

        var beleid = new JsonObject
        {
            ["displayName"] = b.Naam,
            ["state"] = StatusWaarde(b.Status),
            ["conditions"] = conditions,
            ["grantControls"] = grant,
        };
        return beleid.ToJsonString(Compact);
    }

    /// <summary>De status als gedeeltelijke JSON.</summary>
    public static string StatusPatch(CaStatus s) => new JsonObject { ["state"] = StatusWaarde(s) }.ToJsonString(Compact);

    /// <summary>
    /// De gebruikers van een beleid met andere uitgezonderde gebruikers, als gedeeltelijke JSON. Het hele blok "users" gaat
    /// mee (uit de gelezen JSON als die er is), zodat Graph geen opname of andere uitzondering kwijtraakt.
    /// </summary>
    public static string UitzonderingenPatch(CaBeleid b, IEnumerable<string> uitgezonderd)
    {
        JsonObject users;
        if (b.Json.Length > 0 && JsonNode.Parse(b.Json)?["conditions"]?["users"] is JsonObject gelezen)
        {
            users = (JsonObject)gelezen.DeepClone();
        }
        else
        {
            users = new JsonObject
            {
                ["includeUsers"] = Lijst(b.GebruikersIn),
                ["includeGroups"] = Lijst(b.GroepenIn),
                ["excludeGroups"] = Lijst(b.GroepenUit),
                ["includeRoles"] = Lijst(b.RollenIn),
                ["excludeRoles"] = Lijst(b.RollenUit),
            };
        }

        users["excludeUsers"] = Lijst(uitgezonderd);
        return new JsonObject { ["conditions"] = new JsonObject { ["users"] = users } }.ToJsonString(Compact);
    }

    /// <summary>Een benoemde locatie met IP-bereiken (CIDR) of landen, als JSON voor Graph.</summary>
    public static string LocatieJson(string naam, bool isIp, IEnumerable<string> waarden, bool vertrouwd, bool onbekendeLanden = false)
    {
        var node = new JsonObject { ["displayName"] = naam };
        if (isIp)
        {
            node["@odata.type"] = "#microsoft.graph.ipNamedLocation";
            node["isTrusted"] = vertrouwd;
            node["ipRanges"] = new JsonArray(waarden.Select(w => (JsonNode?)new JsonObject
            {
                ["@odata.type"] = w.Contains(':', StringComparison.Ordinal) ? "#microsoft.graph.iPv6CidrRange" : "#microsoft.graph.iPv4CidrRange",
                ["cidrAddress"] = w,
            }).ToArray());
        }
        else
        {
            node["@odata.type"] = "#microsoft.graph.countryNamedLocation";
            node["countriesAndRegions"] = Lijst(waarden.Select(w => w.ToUpperInvariant()));
            node["includeUnknownCountriesAndRegions"] = onbekendeLanden;
        }

        return node.ToJsonString(Compact);
    }

    /// <summary>Een beleid zoals het wordt na een gedeeltelijke JSON (om vooraf te controleren).</summary>
    public static CaBeleid NaPatch(CaBeleid b, string patch)
    {
        var basis = b.Json.Length > 0 ? JsonNode.Parse(b.Json)!.AsObject() : JsonNode.Parse(NaarGraph(b))!.AsObject();
        if (b.Json.Length == 0)
        {
            basis["id"] = b.Id;
        }

        Samenvoegen(basis, JsonNode.Parse(patch)!.AsObject());
        using var doc = JsonDocument.Parse(basis.ToJsonString());
        return Lees(doc.RootElement);
    }

    /// <summary>Zoals Graph een gedeeltelijke wijziging toepast: objecten worden samengevoegd, lijsten en waarden vervangen.</summary>
    private static void Samenvoegen(JsonObject doel, JsonObject wijziging)
    {
        foreach (var (naam, waarde) in wijziging.ToList())
        {
            if (waarde is JsonObject sub && doel[naam] is JsonObject bestaand)
            {
                Samenvoegen(bestaand, sub);
            }
            else
            {
                doel[naam] = waarde?.DeepClone();
            }
        }
    }

    /// <summary>Dezelfde velden als in de wijziging, met de waarden van nu (voor ongedaan maken).</summary>
    public static string Vorige(string huidigeJson, string patch)
    {
        var huidig = JsonNode.Parse(huidigeJson)!.AsObject();
        return Kies(huidig, JsonNode.Parse(patch)!.AsObject()).ToJsonString(Compact);
    }

    private static JsonObject Kies(JsonObject bron, JsonObject vorm)
    {
        var uit = new JsonObject();
        foreach (var (naam, waarde) in vorm)
        {
            if (waarde is JsonObject sub && bron[naam] is JsonObject b)
            {
                uit[naam] = Kies(b, sub);
            }
            else
            {
                uit[naam] = bron[naam]?.DeepClone();
            }
        }

        return uit;
    }
}
