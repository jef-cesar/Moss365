using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Connections.Google;

/// <summary>An error of a Google API: the status and Google's message (never the request or the token).</summary>
public sealed class GoogleError(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Reading Google Workspace through the Admin SDK Directory API (G1): users (projection full), organisational units and
/// groups, with paging (nextPageToken) and one retry on 429/503.
/// </summary>
public sealed class GoogleConnection : IGoogleConnection
{
    internal const string Directory = "https://admin.googleapis.com/admin/directory/v1";

    internal const string Reports = "https://admin.googleapis.com/admin/reports/v1";
    internal const string Licensing = "https://licensing.googleapis.com/apps/licensing/v1";
    internal const string GroupsSettings = "https://www.googleapis.com/groups/v1/groups";
    internal const string AlertCenter = "https://alertcenter.googleapis.com/v1beta1";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly Func<string?> _signedInAs;
    private readonly IReadOnlyList<string> _parts;
    private readonly string _domain;

    public GoogleConnection(HttpClient http, Func<CancellationToken, Task<string>> token, Func<string?>? signedInAs = null,
        IReadOnlyList<string>? parts = null, string domain = "")
    {
        _http = http;
        _token = token;
        _signedInAs = signedInAs ?? (() => null);
        _parts = parts ?? Array.Empty<string>();
        _domain = domain.Trim();
    }

    private bool PartOn(string part) => Core.Setup.GoogleParts.IsOn(_parts, part);

    /// <summary>The check of the own project (G1): the sign-in, then one small question per API Moss365 uses (a part only
    /// when it is on), each with what to do when it does not work.</summary>
    public async Task<IReadOnlyList<GoogleCheck>> CheckAsync(CancellationToken cancel)
    {
        var checks = new List<GoogleCheck>();
        try
        {
            await _token(cancel);
            checks.Add(new GoogleCheck(Texts.T["google.check.sign-in"], true, SignedInAs));
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            checks.Add(new GoogleCheck(Texts.T["google.check.sign-in"], false, ex.Message));
            return checks;
        }

        var probes = new List<(string Name, string Url, string Api)>
        {
            (Texts.T["google.read.users"], $"{Directory}/users?customer=my_customer&maxResults=1", "admin.googleapis.com"),
            (Texts.T["google.read.org-units"], $"{Directory}/customer/my_customer/orgunits?type=children", "admin.googleapis.com"),
            (Texts.T["google.read.groups"], $"{Directory}/groups?customer=my_customer&maxResults=1", "admin.googleapis.com"),
            (Texts.T["google.read.resources"], $"{Directory}/customer/my_customer/resources/calendars?maxResults=1", "admin.googleapis.com"),
            (Texts.T["google.read.roles"], $"{Directory}/customer/my_customer/roles?maxResults=1", "admin.googleapis.com"),
            (Texts.T["google.read.logs"], $"{Reports}/activity/users/all/applications/login?maxResults=1", "admin.googleapis.com"),
        };
        if (PartOn(Core.Setup.GoogleParts.Licences))
        {
            probes.Add((Texts.T["google.read.licences"], $"{Licensing}/product/Google-Apps/users?customerId={Uri.EscapeDataString(_domain)}&maxResults=1", "licensing.googleapis.com"));
        }

        if (PartOn(Core.Setup.GoogleParts.Alerts))
        {
            probes.Add((Texts.T["google.read.alerts"], $"{AlertCenter}/alerts?pageSize=1", "alertcenter.googleapis.com"));
        }

        foreach (var (name, url, api) in probes)
        {
            try
            {
                await GetAsync(url, cancel);
                checks.Add(new GoogleCheck(name, true));
            }
            catch (GoogleError ex)
            {
                checks.Add(new GoogleCheck(name, false, Advice(ex, api)));
            }
        }

        return checks;
    }

    /// <summary>What to do about an error of the check: the API off in the project, a scope not granted, or no
    /// administrator.</summary>
    internal static string Advice(GoogleError ex, string api)
    {
        var m = ex.Message;
        if (m.Contains("has not been used in project", StringComparison.OrdinalIgnoreCase) || m.Contains("is disabled", StringComparison.OrdinalIgnoreCase)
            || m.Contains("SERVICE_DISABLED", StringComparison.Ordinal) || m.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase))
        {
            return Texts.T.Format("google.check.api-off", api);
        }

        if (m.Contains("insufficient", StringComparison.OrdinalIgnoreCase) && m.Contains("scope", StringComparison.OrdinalIgnoreCase))
        {
            return Texts.T["google.check.scope"];
        }

        return ex.Status == HttpStatusCode.Forbidden ? Texts.T["google.check.not-admin"] : m;
    }

    public string? SignedInAs => _signedInAs();

    public async Task<GoogleData> ReadAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report(Texts.T["google.read.users"]);
        var users = (await AllAsync($"{Directory}/users?customer=my_customer&projection=full&maxResults=500&orderBy=email", "users", cancel)).Select(ToUser).ToList();
        progress?.Report(Texts.T["google.read.org-units"]);
        var root = await GetAsync($"{Directory}/customer/my_customer/orgunits?type=all", cancel);
        var unitItems = Items(root, "organizationUnits").ToList();
        var units = unitItems.Select(ToOrgUnit).ToList();
        progress?.Report(Texts.T["google.read.groups"]);
        var groups = (await AllAsync($"{Directory}/groups?customer=my_customer&maxResults=200", "groups", cancel)).Select(ToGroup).ToList();

        // Resources and admin roles: each on its own, so that a missing right leaves the rest (null = not read)
        IReadOnlyList<GoogleResource>? resources = null;
        IReadOnlyList<GoogleRoleAssignment>? roles = null;
        try
        {
            progress?.Report(Texts.T["google.read.resources"]);
            resources = (await AllAsync($"{Directory}/customer/my_customer/resources/calendars?maxResults=500", "items", cancel)).Select(ToResource).ToList();
        }
        catch (GoogleError ex)
        {
            progress?.Report(Texts.T.Format("google.read.part-failed", Texts.T["google.read.resources"], ex.Message));
        }

        try
        {
            progress?.Report(Texts.T["google.read.roles"]);
            var definitions = (await AllAsync($"{Directory}/customer/my_customer/roles?maxResults=100", "items", cancel))
                .ToDictionary(r => S(r, "roleId"), r => (Name: S(r, "roleName"), Super: B(r, "isSuperAdminRole")));
            var unitPaths = unitItems.Where(u => S(u, "orgUnitId").Length > 0).ToDictionary(u => S(u, "orgUnitId"), u => S(u, "orgUnitPath"));
            roles = (await AllAsync($"{Directory}/customer/my_customer/roleassignments?maxResults=200", "items", cancel))
                .Select(a => ToRoleAssignment(a, definitions, unitPaths)).Where(a => a is not null).Select(a => a!).ToList();
        }
        catch (GoogleError ex)
        {
            progress?.Report(Texts.T.Format("google.read.part-failed", Texts.T["google.read.roles"], ex.Message));
        }

        progress?.Report(Texts.T.Format("google.read.done", users.Count, groups.Count, units.Count));
        return new GoogleData(users, groups, units, resources, roles);
    }

    /// <summary>All items of a list across its pages.</summary>
    internal async Task<List<JsonElement>> AllAsync(string url, string property, CancellationToken cancel)
    {
        var output = new List<JsonElement>();
        string? page = null;
        do
        {
            var answer = await GetAsync(page is null ? url : $"{url}&pageToken={Uri.EscapeDataString(page)}", cancel);
            output.AddRange(Items(answer, property));
            page = answer.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (!string.IsNullOrEmpty(page));

        return output;
    }

    private async Task<JsonElement> GetAsync(string url, CancellationToken cancel)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(cancel));
            using var answer = await _http.SendAsync(request, cancel);
            var text = await answer.Content.ReadAsStringAsync(cancel);
            if (answer.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(text);
                return json.RootElement.Clone();
            }

            if (attempt == 0 && answer.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancel);
                continue;
            }

            throw new GoogleError(answer.StatusCode, Message(answer.StatusCode, text));
        }
    }

    /// <summary>Google's message, with a hint for the usual cause (403: not an administrator, or the API is off).</summary>
    internal static string Message(HttpStatusCode status, string text)
    {
        string? message = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m))
            {
                message = m.GetString();
            }
        }
        catch (JsonException)
        {
        }

        var hint = status == HttpStatusCode.Forbidden ? " " + Texts.T["google.forbidden"] : string.Empty;
        return $"{(int)status} {message ?? status.ToString()}{hint}";
    }

    internal static GoogleUser ToUser(JsonElement u)
    {
        var name = u.TryGetProperty("name", out var n) ? n : default;
        var organisations = Items(u, "organizations").ToList();
        var organisation = organisations.FirstOrDefault(o => B(o, "primary"));
        if (organisation.ValueKind == JsonValueKind.Undefined)
        {
            organisation = organisations.FirstOrDefault();
        }

        var lastLogin = S(u, "lastLoginTime");
        return new GoogleUser
        {
            Id = S(u, "id"),
            PrimaryEmail = S(u, "primaryEmail"),
            Aliases = Strings(u, "aliases"),
            Name = S(name, "fullName"),
            GivenName = S(name, "givenName"),
            FamilyName = S(name, "familyName"),
            Department = S(organisation, "department"),
            OrgUnitPath = S(u, "orgUnitPath") is { Length: > 0 } ou ? ou : "/",
            Suspended = B(u, "suspended"),
            Archived = B(u, "archived"),
            EmployeeId = Items(u, "externalIds").Where(x => S(x, "type") == "organization").Select(x => S(x, "value")).FirstOrDefault() ?? string.Empty,
            // Google gives 1970 for "never signed in"
            LastLogin = lastLogin.StartsWith("1970-", StringComparison.Ordinal) ? string.Empty : lastLogin,
            CreationTime = S(u, "creationTime"),
            IsAdmin = B(u, "isAdmin"),
            TwoStepEnrolled = B(u, "isEnrolledIn2Sv"),
            TwoStepEnforced = B(u, "isEnforcedIn2Sv"),
        };
    }

    internal static GoogleGroup ToGroup(JsonElement g) => new()
    {
        Id = S(g, "id"),
        Email = S(g, "email"),
        Name = S(g, "name"),
        Description = S(g, "description"),
        // A number as text in the Directory API
        CountMembers = int.TryParse(S(g, "directMembersCount"), out var n) ? n : 0,
        Aliases = Strings(g, "aliases"),
        AdminCreated = B(g, "adminCreated"),
    };

    internal static GoogleResource ToResource(JsonElement r) => new()
    {
        Id = S(r, "resourceId"),
        Email = S(r, "resourceEmail"),
        Name = S(r, "resourceName"),
        Type = S(r, "resourceType"),
        Category = S(r, "resourceCategory"),
        Capacity = r.TryGetProperty("capacity", out var c) && c.TryGetInt32(out var n) ? n : null,
        Building = S(r, "buildingId"),
        Floor = S(r, "floorName"),
        Description = S(r, "resourceDescription"),
    };

    /// <summary>An assignment to a user (a role given to a group is not one person: left out here); the scope as the
    /// path of the organisational unit.</summary>
    internal static GoogleRoleAssignment? ToRoleAssignment(JsonElement a, IReadOnlyDictionary<string, (string Name, bool Super)> roles, IReadOnlyDictionary<string, string> unitPaths)
    {
        if (S(a, "assigneeType") is { Length: > 0 } type && !string.Equals(type, "user", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var role = roles.TryGetValue(S(a, "roleId"), out var r) ? r : (Name: S(a, "roleId"), Super: false);
        var unit = S(a, "orgUnitId");
        return new GoogleRoleAssignment
        {
            UserId = S(a, "assignedTo"),
            Role = role.Name,
            SuperAdmin = role.Super,
            Scope = S(a, "scopeType") == "ORG_UNIT" ? (unitPaths.TryGetValue(unit, out var path) ? path : unit) : "CUSTOMER",
        };
    }

    internal static GoogleOrgUnit ToOrgUnit(JsonElement o) => new()
    {
        Path = S(o, "orgUnitPath"),
        Name = S(o, "name"),
        Description = S(o, "description"),
        ParentPath = S(o, "parentOrgUnitPath"),
    };

    private static IEnumerable<JsonElement> Items(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(x => x.Clone()) : Enumerable.Empty<JsonElement>();

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
        => Items(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList();

    private static string S(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? string.Empty,
            JsonValueKind.Number => v.GetRawText(),
            _ => string.Empty,
        } : string.Empty;

    private static bool B(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
