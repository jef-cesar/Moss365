using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Setup;

namespace Moss365.Connections.Graph;

/// <summary>
/// The wizard (R5): signs in with the public client of Microsoft Graph PowerShell (decision U6), because the app
/// registration of Moss365 may not exist yet. Reads what Moss365 needs and runs the plan of the wizard; every step
/// looks up the app and groups by name, so that running it again duplicates nothing.
/// </summary>
public sealed class TenantSetupConnection : ITenantSetupConnection
{
    private readonly GraphClient _client;
    private readonly MsalTokenSource _token;

    public TenantSetupConnection(HttpClient http, string tenantId, SignInStatus? status)
    {
        _token = new MsalTokenSource(tenantId, AppManifest.GraphPowerShellClientId, null, status, AppManifest.WizardScopes);
        _client = new GraphClient(http, _token.TokenAsync);
        TenantId = tenantId;
    }

    public string TenantId { get; }

    public string? SignedInAs => _token.SignedInAs;

    public TargetSystem TargetSystem => TargetSystem.Graph;

    public async Task<TenantSetupReading> ReadAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report("Tenant lezen...");
        var organisation = (await _client.GetAllAsync("/organization?$select=id,displayName,verifiedDomains,onPremisesSyncEnabled,onPremisesLastSyncDateTime", cancel)).FirstOrDefault();
        var me = await _client.GetAsync("/me?$select=id,displayName,userPrincipalName", cancel);

        progress?.Report("Microsoft Graph en Exchange Online...");
        var graphSp = await ServicePrincipalAsync(AppManifest.GraphAppId, cancel);
        var exchangeSp = await ServicePrincipalAsync(AppManifest.ExchangeAppId, cancel);

        progress?.Report($"App-registratie {AppManifest.Name}...");
        var app = (await _client.GetAllAsync($"/applications?$filter={Filter("displayName", AppManifest.Name)}&$select=id,appId,displayName,signInAudience,isFallbackPublicClient,publicClient,requiredResourceAccess,appRoles", cancel))
            .Select(ToApplication).FirstOrDefault();
        var appSp = app is null ? null : await ServicePrincipalAsync(app.AppId, cancel);
        var grants = new List<GrantInfo>();
        var assignments = new List<AssignmentInfo>();
        if (appSp is not null)
        {
            grants.AddRange((await _client.GetAllAsync($"/oauth2PermissionGrants?$filter={Filter("clientId", appSp.Id)}", cancel)).Select(ToGrant));
            assignments.AddRange((await _client.GetAllAsync($"/servicePrincipals/{appSp.Id}/appRoleAssignedTo?$select=principalId,appRoleId", cancel))
                .Select(a => new AssignmentInfo(S(a, "principalId"), S(a, "appRoleId"))));
        }

        progress?.Report("Groepen van de profielen...");
        var groups = (await _client.GetAllAsync($"/groups?$filter={Uri.EscapeDataString("startswith(displayName,'GG-Moss365-')")}&$select=id,displayName", cancel))
            .GroupBy(g => S(g, "displayName"), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => S(g.First(), "id"), StringComparer.OrdinalIgnoreCase);

        return ToReading(organisation, me, TenantId, graphSp, exchangeSp, app, appSp, grants, assignments, groups);
    }

    internal static TenantSetupReading ToReading(JsonElement organisation, JsonElement me, string tenantId, ServicePrincipalInfo? graphSp, ServicePrincipalInfo? exchangeSp,
        ApplicationInfo? app, ServicePrincipalInfo? appSp, IReadOnlyList<GrantInfo> grants, IReadOnlyList<AssignmentInfo> assignments, IReadOnlyDictionary<string, string> groups)
    {
        var domains = organisation.ValueKind == JsonValueKind.Object && organisation.TryGetProperty("verifiedDomains", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(d => S(d, "name")).Where(n => n.Length > 0).ToList()
            : new List<string>();
        bool? sync = organisation.ValueKind == JsonValueKind.Object && organisation.TryGetProperty("onPremisesSyncEnabled", out var se)
            ? se.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False or JsonValueKind.Null => false, _ => null }
            : null;
        DateTimeOffset? last = organisation.ValueKind == JsonValueKind.Object
                               && DateTimeOffset.TryParse(S(organisation, "onPremisesLastSyncDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var l) ? l : null;
        var id = organisation.ValueKind == JsonValueKind.Object && S(organisation, "id").Length > 0 ? S(organisation, "id") : tenantId;
        return new TenantSetupReading(id, organisation.ValueKind == JsonValueKind.Object ? S(organisation, "displayName") : string.Empty, domains, sync, last,
            new DirectoryPrincipal(S(me, "id"), S(me, "displayName"), S(me, "userPrincipalName")),
            graphSp, exchangeSp, app, appSp, grants, assignments, groups);
    }

    private async Task<ServicePrincipalInfo?> ServicePrincipalAsync(string appId, CancellationToken cancel)
        => (await _client.GetAllAsync($"/servicePrincipals?$filter={Filter("appId", appId)}&$select=id,appId,displayName,appRoleAssignmentRequired,oauth2PermissionScopes", cancel))
            .Select(ToServicePrincipal).FirstOrDefault();

    internal static ServicePrincipalInfo ToServicePrincipal(JsonElement e)
    {
        var scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.TryGetProperty("oauth2PermissionScopes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                scopes[S(s, "value")] = S(s, "id");
            }
        }

        return new ServicePrincipalInfo(S(e, "id"), S(e, "appId"), S(e, "displayName"), B(e, "appRoleAssignmentRequired"), scopes);
    }

    internal static ApplicationInfo ToApplication(JsonElement e)
    {
        var redirects = e.TryGetProperty("publicClient", out var pc) && pc.ValueKind == JsonValueKind.Object && pc.TryGetProperty("redirectUris", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : new List<string>();
        var access = new List<ResourceAccess>();
        if (e.TryGetProperty("requiredResourceAccess", out var rra) && rra.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in rra.EnumerateArray())
            {
                if (resource.TryGetProperty("resourceAccess", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    access.AddRange(items.EnumerateArray().Select(a => new ResourceAccess(S(resource, "resourceAppId"), S(a, "id"), S(a, "type"))));
                }
            }
        }

        var roles = e.TryGetProperty("appRoles", out var ar) && ar.ValueKind == JsonValueKind.Array
            ? ar.EnumerateArray().Select(a => new AppRoleInfo(S(a, "id"), S(a, "value"), S(a, "displayName"), S(a, "description"), B(a, "isEnabled"),
                a.TryGetProperty("allowedMemberTypes", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : new List<string>())).ToList()
            : new List<AppRoleInfo>();
        return new ApplicationInfo(S(e, "id"), S(e, "appId"), S(e, "displayName"), S(e, "signInAudience"), B(e, "isFallbackPublicClient"), redirects, access, roles);
    }

    internal static GrantInfo ToGrant(JsonElement e) => new(S(e, "id"), S(e, "resourceId"), S(e, "consentType"), S(e, "scope"));

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
    {
        var output = new List<ChangeResult>();
        foreach (var w in changes)
        {
            try
            {
                output.Add(await RunAsync(w, cancel));
            }
            catch (GraphError ex)
            {
                output.Add(ChangeResult.Error(w, ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                    ? "Geen rechten: dit vraagt de rol Application Administrator of Global Administrator. " + ex.Message
                    : ex.Message));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                output.Add(ChangeResult.Error(w, ex.Message));
            }
        }

        return output;
    }

    private async Task<ChangeResult> RunAsync(Change w, CancellationToken cancel)
    {
        switch (w.ChangeAction)
        {
            case ChangeAction.GraphCreateApplication:
            {
                if (await FindApplicationAsync(w.P("appName"), cancel) is { } existing)
                {
                    return ChangeResult.Ok(w, $"Bestond al; client ID {existing.AppId}");
                }

                var app = await _client.PostWithResponseAsync("/applications", JsonNode.Parse(w.P("json"))!, cancel);
                return ChangeResult.Ok(w, $"Gemaakt; client ID {S(app, "appId")}");
            }

            case ChangeAction.GraphUpdateApplication:
                await _client.PatchAsync($"/applications/{Uri.EscapeDataString(w.Target)}", JsonNode.Parse(w.P("json"))!, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GraphCreateServicePrincipal:
            {
                var app = await FindApplicationAsync(w.P("appName"), cancel) ?? throw new InvalidOperationException($"De app-registratie {w.P("appName")} bestaat niet.");
                if (await ServicePrincipalAsync(app.AppId, cancel) is { } existing)
                {
                    if (!existing.AssignmentRequired)
                    {
                        await _client.PatchAsync($"/servicePrincipals/{existing.Id}", new Dictionary<string, object> { ["appRoleAssignmentRequired"] = true }, cancel);
                    }

                    return ChangeResult.Ok(w, "Bestond al");
                }

                // A new app is not visible everywhere at once: try again a few times
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await _client.PostAsync("/servicePrincipals", new Dictionary<string, object> { ["appId"] = app.AppId, ["appRoleAssignmentRequired"] = true }, cancel);
                        return ChangeResult.Ok(w);
                    }
                    catch (GraphError ex) when (attempt < 5 && ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancel);
                    }
                }
            }

            case ChangeAction.GraphUpdateServicePrincipal:
                await _client.PatchAsync($"/servicePrincipals/{Uri.EscapeDataString(w.Target)}", JsonNode.Parse(w.P("json"))!, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GraphGrantConsent:
            {
                var sp = await AppServicePrincipalAsync(w.P("appName"), cancel);
                if (w.P("grantId").Length > 0)
                {
                    await _client.PatchAsync($"/oauth2PermissionGrants/{Uri.EscapeDataString(w.P("grantId"))}", new Dictionary<string, object> { ["scope"] = w.P("scope") }, cancel);
                    return ChangeResult.Ok(w);
                }

                var existing = (await _client.GetAllAsync($"/oauth2PermissionGrants?$filter={Filter("clientId", sp.Id)}", cancel)).Select(ToGrant)
                    .FirstOrDefault(g => g.ResourceId == w.P("resourceId") && g.ConsentType == "AllPrincipals");
                if (existing is not null)
                {
                    await _client.PatchAsync($"/oauth2PermissionGrants/{existing.Id}", new Dictionary<string, object> { ["scope"] = w.P("scope") }, cancel);
                }
                else
                {
                    await _client.PostAsync("/oauth2PermissionGrants", new Dictionary<string, object>
                    {
                        ["clientId"] = sp.Id, ["consentType"] = "AllPrincipals", ["resourceId"] = w.P("resourceId"), ["scope"] = w.P("scope"),
                    }, cancel);
                }

                return ChangeResult.Ok(w);
            }

            case ChangeAction.GraphAssignAppRole:
            {
                var sp = await AppServicePrincipalAsync(w.P("appName"), cancel);
                var principal = Guid.TryParse(w.Target, out _) ? w.Target : await GroupIdAsync(w.Target, cancel);
                try
                {
                    await _client.PostAsync($"/servicePrincipals/{sp.Id}/appRoleAssignedTo", new Dictionary<string, object>
                    {
                        ["principalId"] = principal, ["resourceId"] = sp.Id, ["appRoleId"] = w.P("appRoleId"),
                    }, cancel);
                }
                catch (GraphError ex) when (ex.Status == HttpStatusCode.BadRequest && ex.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase))
                {
                    return ChangeResult.Ok(w, "Was al toegewezen.");
                }

                return ChangeResult.Ok(w);
            }

            case ChangeAction.GraphCreateGroup:
            {
                var name = w.P("displayName");
                if ((await _client.GetAllAsync($"/groups?$filter={Filter("displayName", name)}&$select=id", cancel)).FirstOrDefault() is { ValueKind: JsonValueKind.Object } found)
                {
                    return ChangeResult.Ok(w, $"Bestond al; id {S(found, "id")}");
                }

                var group = await _client.PostWithResponseAsync("/groups", new Dictionary<string, object>
                {
                    ["displayName"] = name, ["mailNickname"] = w.P("mailNickname"), ["mailEnabled"] = false, ["securityEnabled"] = true,
                    ["description"] = w.P("description"),
                }, cancel);
                return ChangeResult.Ok(w, $"Aangemaakt; id {S(group, "id")}");
            }

            default:
                return ChangeResult.Error(w, "Deze wijziging hoort niet bij het inrichten van een tenant.");
        }
    }

    private async Task<ApplicationInfo?> FindApplicationAsync(string name, CancellationToken cancel)
        => (await _client.GetAllAsync($"/applications?$filter={Filter("displayName", name)}&$select=id,appId,displayName", cancel)).Select(ToApplication).FirstOrDefault();

    private async Task<ServicePrincipalInfo> AppServicePrincipalAsync(string name, CancellationToken cancel)
    {
        var app = await FindApplicationAsync(name, cancel) ?? throw new InvalidOperationException($"De app-registratie {name} bestaat niet.");
        return await ServicePrincipalAsync(app.AppId, cancel) ?? throw new InvalidOperationException($"De bedrijfstoepassing van {name} bestaat niet.");
    }

    private async Task<string> GroupIdAsync(string name, CancellationToken cancel)
    {
        // A group made in the same plan can take a moment to be found
        for (var attempt = 1; ; attempt++)
        {
            var found = (await _client.GetAllAsync($"/groups?$filter={Filter("displayName", name)}&$select=id", cancel)).Select(g => S(g, "id")).FirstOrDefault();
            if (found is { Length: > 0 })
            {
                return found;
            }

            if (attempt >= 5)
            {
                throw new InvalidOperationException($"De groep {name} bestaat niet.");
            }

            await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancel);
        }
    }

    /// <summary>An OData filter "field eq 'value'", escaped for the URL.</summary>
    internal static string Filter(string field, string value) => Uri.EscapeDataString($"{field} eq '{value.Replace("'", "''", StringComparison.Ordinal)}'");

    private static string S(JsonElement e, string name)
        => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;

    private static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.True;
}
