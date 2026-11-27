using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;
using Moss365.Core.Localization;

namespace Moss365.Connections.Graph;

/// <summary>Reads Entra ID via Microsoft Graph and runs Graph changes, with the sign-in of the administrator.</summary>
public sealed class GraphConnection : IGraphConnection, IExecutor
{
    /// <summary>The beta of Graph, for what Intune only has there (absolute URLs go through the same client).</summary>
    private const string BetaBase = "https://graph.microsoft.com/beta";

    private const string UserFields = "id,userPrincipalName,displayName,givenName,surname,mail,mailNickname,accountEnabled,userType,department,"
                                           + "jobTitle,officeLocation,companyName,city,usageLocation,createdDateTime,onPremisesSyncEnabled,"
                                           + "onPremisesSamAccountName,onPremisesDistinguishedName,assignedLicenses,businessPhones,mobilePhone,"
                                           + "employeeId,employeeType,streetAddress,postalCode,state,country";

    private const string GroupFields = "id,displayName,description,mail,mailEnabled,securityEnabled,groupTypes,membershipRule,"
                                       + "onPremisesSyncEnabled,onPremisesSamAccountName,assignedLicenses";

    private readonly GraphClient _client;

    /// <summary>Users this connection just created or invited (address or UPN to id): the next steps of the same
    /// plan find them at once, also before Entra can be searched for them (0.31).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string?> _signedInAs;
    private readonly Func<IReadOnlyList<string>?> _appRoles;
    private readonly Func<CancellationToken, Task>? Signin_;
    private readonly Func<CancellationToken, Task<bool>>? _silent;

    public GraphConnection(GraphClient client, Func<string?>? signedInAs = null, Func<IReadOnlyList<string>?>? appRoles = null,
        Func<CancellationToken, Task>? signIn = null, Func<CancellationToken, Task<bool>>? silent = null)
    {
        _client = client;
        _signedInAs = signedInAs ?? (() => null);
        _appRoles = appRoles ?? (() => null);
        Signin_ = signIn;
        _silent = silent;
    }

    public Task<bool> SilentSignedInAsync(CancellationToken cancel) => _silent?.Invoke(cancel) ?? Task.FromResult(false);

    public IReadOnlyList<string>? AppRoles => _appRoles();

    public Task SignInAsync(CancellationToken cancel) => Signin_?.Invoke(cancel) ?? Task.CompletedTask;

    public TargetSystem TargetSystem => TargetSystem.Graph;

    public string? SignedInAs => _signedInAs();

    // ------------------------------------------------------------------ reading

    public Task<GraphData> ReadAsync(IProgress<string>? progress, CancellationToken cancel) => ReadAsync(null, progress, cancel);

    // Attributes that /groups/delta returns (not assignedLicenses: that stays from the previous read)
    private const string DeltaFields = "displayName,description,mail,mailEnabled,securityEnabled,groupTypes,membershipRule,"
                                       + "onPremisesSyncEnabled,onPremisesSamAccountName,members";

    /// <summary>
    /// Users and licences always in full (the last sign-in changes without delta). The groups with their members (the
    /// most work: one request per group) via /groups/delta when there is a previous read; otherwise everything.
    /// </summary>
    public async Task<GraphData> ReadAsync(GraphBase? baseline, IProgress<string>? progress, CancellationToken cancel)
    {
        var (users, licences) = await ReadUsersAndLicencesAsync(progress, cancel);

        if (baseline is not null)
        {
            try
            {
                progress?.Report(Texts.T["code.graph-connection.entra-only-the-changed-groups"]);
                var (items, link) = await ReadGroupDeltaAsync(baseline.DeltaLink, cancel);
                var (groups, member) = GraphDelta.Apply(baseline, items);
                progress?.Report(Texts.T.Format("code.graph-connection.entra-users-read-group-changes", users.Count, items.Count, groups.Count));
                return new GraphData(users, groups, member, licences, link, Quick: true);
            }
            catch (GraphError ex)
            {
                // E.g. 410 Gone (the starting point is too old): then read everything
                progress?.Report(Texts.T.Format("code.graph-connection.entra-changes-not-available-reading", ex.Message));
            }
        }

        // First ask for a starting point for the next time (without data), then read everything
        string? deltaLink = null;
        try
        {
            var start = await _client.GetAsync($"/groups/delta?$select={DeltaFields}&$deltatoken=latest", cancel);
            deltaLink = start.TryGetProperty("@odata.deltaLink", out var dl) ? dl.GetString() : null;
        }
        catch (GraphError)
        {
            // No starting point: everything again the next time
        }

        var (allGroups, membership) = await ReadGroupsFullAsync(progress, cancel);
        progress?.Report(Texts.T.Format("code.graph-connection.entra-users-and-groups-read", users.Count, allGroups.Count));
        return new GraphData(users, allGroups, membership, licences, deltaLink);
    }

    /// <summary>All pages of a delta round, up to the new deltaLink.</summary>
    private async Task<(List<GroupDeltaItem> Items, string? DeltaLink)> ReadGroupDeltaAsync(string deltaLink, CancellationToken cancel)
    {
        var items = new List<GroupDeltaItem>();
        string? next = deltaLink;
        while (true)
        {
            var currentPage = await _client.GetAsync(next, cancel);
            if (currentPage.TryGetProperty("value", out var values))
            {
                items.AddRange(values.EnumerateArray().Select(e => ToDeltaItem(e.Clone())));
            }

            if (currentPage.TryGetProperty("@odata.nextLink", out var nl) && nl.GetString() is { } n)
            {
                next = n;
                continue;
            }

            return (items, currentPage.TryGetProperty("@odata.deltaLink", out var dl) ? dl.GetString() : null);
        }
    }

    /// <summary>One group from /groups/delta: deleted, changed attributes and/or changed (user) members.</summary>
    internal static GroupDeltaItem ToDeltaItem(JsonElement g)
    {
        var id = S(g, "id");
        if (g.TryGetProperty("@removed", out _))
        {
            return new GroupDeltaItem(id, true, null, Array.Empty<string>(), Array.Empty<string>());
        }

        var added = new List<string>();
        var removed = new List<string>();
        if (g.TryGetProperty("members@delta", out var md) && md.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in md.EnumerateArray().Where(m => S(m, "@odata.type") is "#microsoft.graph.user" or ""))
            {
                (m.TryGetProperty("@removed", out _) ? removed : added).Add(S(m, "id"));
            }
        }

        var attributes = new[] { "displayName", "description", "mail", "mailEnabled", "securityEnabled", "groupTypes", "membershipRule", "onPremisesSyncEnabled", "onPremisesSamAccountName" };
        var changed = attributes.Any(k => g.TryGetProperty(k, out _));
        return new GroupDeltaItem(id, false, changed ? existing => Merge(existing, g) : null, added, removed);
    }

    /// <summary>The attributes in the delta response, on top of the previous version of the group.</summary>
    internal static CloudGroup Merge(CloudGroup? existing, JsonElement g)
    {
        var b = existing ?? new CloudGroup();
        bool Has(string k) => g.TryGetProperty(k, out _);
        var newValue = ToGroup(g, b.CountMembers ?? 0);
        return b with
        {
            DisplayName = Has("displayName") ? newValue.DisplayName : b.DisplayName,
            Description = Has("description") ? newValue.Description : b.Description,
            Mail = Has("mail") ? newValue.Mail : b.Mail,
            OnPremisesSamAccountName = Has("onPremisesSamAccountName") ? newValue.OnPremisesSamAccountName : b.OnPremisesSamAccountName,
            FromAd = Has("onPremisesSyncEnabled") ? newValue.FromAd : b.FromAd,
            MembershipRule = Has("membershipRule") ? newValue.MembershipRule : b.MembershipRule,
            Dynamic = Has("groupTypes") ? newValue.Dynamic : b.Dynamic,

            // The kind follows from three attributes together; only recompute when all three are present
            Type = Has("groupTypes") && Has("mailEnabled") && Has("securityEnabled") ? newValue.Type : existing?.Type ?? newValue.Type,
        };
    }

    private async Task<(List<EntraUser> Users, List<Licence> Licences)> ReadUsersAndLicencesAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report(Texts.T["code.graph-connection.entra-licences"]);
        var skus = await _client.GetAllAsync("/subscribedSkus?$select=skuId,skuPartNumber,consumedUnits,prepaidUnits", cancel);
        var licences = skus.Select(s => new Licence(
            S(s, "skuId"), S(s, "skuPartNumber"), s.TryGetProperty("consumedUnits", out var c) ? c.GetInt32() : 0,
            s.TryGetProperty("prepaidUnits", out var p) && p.TryGetProperty("enabled", out var e) ? e.GetInt32() : 0)).ToList();
        var skuName = licences.ToDictionary(l => l.SkuId, l => l.SkuPartNumber, StringComparer.OrdinalIgnoreCase);

        progress?.Report("Entra: gebruikers...");
        List<JsonElement> rawUsers;
        try
        {
            rawUsers = await _client.GetAllAsync($"/users?$select={UserFields},signInActivity&$top=999", cancel);
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // The last sign-in needs AuditLog.Read.All and Entra ID P1; continue without that data
            progress?.Report(Texts.T["code.graph-connection.entra-last-sign-in-not"]);
            rawUsers = await _client.GetAllAsync($"/users?$select={UserFields}&$top=999", cancel);
        }

        var users = rawUsers.Select(u => ToUser(u, skuName)).ToList();

        // Managers separately (an $expand in the large read would, on a refusal, also cost the last sign-in)
        try
        {
            var managers = await _client.GetAllAsync("/users?$select=id&$expand=manager($select=id)&$top=999", cancel);
            var onId = managers.Where(m => m.TryGetProperty("manager", out var mg) && mg.ValueKind == JsonValueKind.Object)
                .ToDictionary(m => S(m, "id"), m => S(m.GetProperty("manager"), "id"), StringComparer.OrdinalIgnoreCase);
            users = users.Select(g => onId.TryGetValue(g.Id, out var mid) ? g with { ManagerId = mid } : g).ToList();
        }
        catch (GraphError)
        {
            progress?.Report(Texts.T["code.graph-connection.entra-managers-not-available-continuing"]);
        }

        return (users, licences);
    }

    private async Task<(List<CloudGroup> Groups, Dictionary<string, HashSet<string>> Membership)> ReadGroupsFullAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report(Texts.T["code.graph-connection.entra-groups"]);
        var rawGroups = await _client.GetAllAsync($"/groups?$select={GroupFields}&$top=999", cancel);

        var membership = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var counter = 0;

        // The members of 20 groups per $batch call instead of a call per group (issue 40); a group whose answer is not
        // there (throttled, an error) is read on its own, as before, and further pages are followed
        static string MembersPath(string id) => $"/groups/{id}/members/microsoft.graph.user?$select=id&$top=999";
        using var brake = new SemaphoreSlim(4);
        var tasks = rawGroups.Select(g => S(g, "id")).Chunk(GraphClient.BatchSize).Select(async ids =>
        {
            await brake.WaitAsync(cancel);
            try
            {
                var firstPages = await _client.BatchGetAsync(ids.Select(MembersPath).ToList(), cancel);
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = ids[i];
                    var members = firstPages[i] is { } first ? await _client.AllPagesAsync(first, cancel) : await _client.GetAllAsync(MembersPath(id), cancel);
                    lock (membership)
                    {
                        counts[id] = members.Count;
                        foreach (var member in members)
                        {
                            var memberId = S(member, "id");
                            if (!membership.TryGetValue(memberId, out var set))
                            {
                                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                membership[memberId] = set;
                            }

                            set.Add(id);
                        }

                        counter++;
                        if (counter % 50 == 0)
                        {
                            progress?.Report($"Entra: groepsleden {counter}/{rawGroups.Count}...");
                        }
                    }
                }
            }
            finally
            {
                brake.Release();
            }
        });
        await Task.WhenAll(tasks);

        return (rawGroups.Select(g => ToGroup(g, counts.GetValueOrDefault(S(g, "id")))).ToList(), membership);
    }

    public async Task<GraphUserReading?> ReadUserAsync(string idOrUpn, CancellationToken cancel)
    {
        var skus = await _client.GetAllAsync("/subscribedSkus?$select=skuId,skuPartNumber", cancel);
        var skuName = skus.ToDictionary(s => S(s, "skuId"), s => S(s, "skuPartNumber"), StringComparer.OrdinalIgnoreCase);
        var path = $"/users/{Uri.EscapeDataString(idOrUpn)}";
        JsonElement u;
        try
        {
            try
            {
                u = await _client.GetAsync($"{path}?$select={UserFields},signInActivity&$expand=manager($select=id)", cancel);
            }
            catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
            {
                u = await _client.GetAsync($"{path}?$select={UserFields}", cancel);
            }
        }
        catch (GraphError ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        var user = ToUser(u, skuName);
        var groups = await _client.GetAllAsync($"/users/{user.Id}/memberOf/microsoft.graph.group?$select=id&$top=999", cancel);
        return new GraphUserReading(user, groups.Select(g => S(g, "id")).ToList());
    }

    public async Task<GraphGroupReading?> ReadGroupAsync(string id, CancellationToken cancel)
    {
        JsonElement g;
        try
        {
            g = await _client.GetAsync($"/groups/{Uri.EscapeDataString(id)}?$select={GroupFields}", cancel);
        }
        catch (GraphError ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }

        var members = await _client.GetAllAsync($"/groups/{Uri.EscapeDataString(id)}/members/microsoft.graph.user?$select=id&$top=999", cancel);
        return new GraphGroupReading(ToGroup(g, members.Count), members.Select(l => S(l, "id")).ToList());
    }

    public async Task<string?> FindUserIdAsync(string upn, CancellationToken cancel)
    {
        if (_created.TryGetValue(upn, out var created))
        {
            return created;
        }

        try
        {
            var u = await _client.GetAsync($"/users/{Uri.EscapeDataString(upn)}?$select=id", cancel);
            return S(u, "id");
        }
        catch (GraphError ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ devices, BitLocker and LAPS (0.33)

    public async Task<IReadOnlyList<Core.Helpdesk.DeviceInfo>> FindDevicesAsync(string name, CancellationToken cancel)
    {
        var value = name.Trim().Replace("'", "''", StringComparison.Ordinal);
        if (value.Length == 0)
        {
            return Array.Empty<Core.Helpdesk.DeviceInfo>();
        }

        const string select = "$select=id,deviceId,displayName,operatingSystem,trustType,approximateLastSignInDateTime&$top=25";
        var found = await _client.GetAllAsync($"/devices?$filter=startswith(displayName,'{Uri.EscapeDataString(value)}')&{select}", cancel);
        return found.Select(d => new Core.Helpdesk.DeviceInfo(S(d, "id"), S(d, "deviceId"), S(d, "displayName"), S(d, "operatingSystem"), S(d, "trustType"),
                DateTimeOffset.TryParse(S(d, "approximateLastSignInDateTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Who sent mail with SMTP AUTH in the last 30 days (R16), from the usage report of the mail apps. When the report
    /// hides the names (a setting in the Microsoft 365 admin center), the users are hashes: said as such.
    /// </summary>
    public async Task<Core.Security.SmtpAuthUsage?> ReadSmtpAuthUsageAsync(CancellationToken cancel)
    {
        static bool Used(JsonElement e) => e.TryGetProperty("smtpApp", out var v) && v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Array => v.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 }),
            JsonValueKind.String => v.GetString() is { Length: > 0 } t && !t.Equals("No", StringComparison.OrdinalIgnoreCase) && !t.Equals("False", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        try
        {
            var users = (await _client.GetAllAsync("https://graph.microsoft.com/beta/reports/getEmailAppUsageUserDetail(period='D30')?$format=application/json", cancel))
                .Where(Used).Select(e => S(e, "userPrincipalName")).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
            return new Core.Security.SmtpAuthUsage(users, users.Any(u => !u.Contains('@', StringComparison.Ordinal)));
        }
        catch (GraphError)
        {
            return null;
        }
    }

    /// <summary>
    /// A permanent admin role made eligible through PIM (R16), or back (undo): first the new schedule, then the old one
    /// removed, so that the role is never gone in between.
    /// </summary>
    private async Task<ChangeResult> RoleEligibilityAsync(Change w, CancellationToken cancel)
    {
        var why = Texts.T.Format("advice.roles.justification", w.P("rol"));
        object Request(string action, bool withSchedule) => withSchedule
            ? new { action, justification = why, roleDefinitionId = w.P("rolId"), directoryScopeId = "/", principalId = w.Target,
                    scheduleInfo = new { startDateTime = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), expiration = new { type = "noExpiration" } } }
            : new { action, justification = why, roleDefinitionId = w.P("rolId"), directoryScopeId = "/", principalId = w.Target };
        const string eligibility = "/roleManagement/directory/roleEligibilityScheduleRequests";
        const string assignment = "/roleManagement/directory/roleAssignmentScheduleRequests";
        if (w.P("naar") == "eligible")
        {
            await _client.PostAsync(eligibility, Request("adminAssign", true), cancel);
            await _client.PostAsync(assignment, Request("adminRemove", false), cancel);
        }
        else
        {
            await _client.PostAsync(assignment, Request("adminAssign", true), cancel);
            await _client.PostAsync(eligibility, Request("adminRemove", false), cancel);
        }

        return ChangeResult.Ok(w);
    }

    /// <summary>
    /// The settings of the tenant for the advice of R16: Security Defaults, the authorization policy (apps, tenants,
    /// consent, guests), the admin consent workflow, the authentication methods, sharing in SharePoint and the licences
    /// (Entra ID P1 and P2). What cannot be read is named in the notes and stays null.
    /// </summary>
    public async Task<Core.Security.TenantSettingsReading?> ReadTenantSettingsAsync(CancellationToken cancel)
    {
        var notes = new List<string>();
        async Task<JsonElement?> Get(string path, string what)
        {
            try
            {
                return await _client.GetAsync(path, cancel);
            }
            catch (GraphError ex)
            {
                notes.Add($"{what}: {ex.Message}");
                return null;
            }
        }

        var defaults = await Get("/policies/identitySecurityDefaultsEnforcementPolicy", "Security Defaults");
        var authorization = await Get("/policies/authorizationPolicy", "authorizationPolicy");
        var workflow = await Get("/policies/adminConsentRequestPolicy", "adminConsentRequestPolicy");
        var methods = await Get("/policies/authenticationMethodsPolicy", "authenticationMethodsPolicy");
        var sharepoint = await Get("/admin/sharepoint/settings", "SharePoint");
        if (defaults is null && authorization is null && methods is null)
        {
            return null;
        }

        static bool? Bool(JsonElement? e, string name) => e is { } v && v.TryGetProperty(name, out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False ? b.GetBoolean() : null;
        JsonElement? user = authorization is { } a && a.TryGetProperty("defaultUserRolePermissions", out var u) && u.ValueKind == JsonValueKind.Object ? u : null;
        var consent = user is { } du && du.TryGetProperty("permissionGrantPoliciesAssigned", out var pg) && pg.ValueKind == JsonValueKind.Array
            ? pg.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : new List<string>();
        var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (methods is { } m && m.TryGetProperty("authenticationMethodConfigurations", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in list.EnumerateArray())
            {
                states[S(c, "id").ToLowerInvariant()] = S(c, "state");
            }
        }

        var (p1, p2) = (false, false);
        try
        {
            foreach (var sku in await _client.GetAllAsync("/subscribedSkus", cancel))
            {
                if (sku.TryGetProperty("servicePlans", out var plans) && plans.ValueKind == JsonValueKind.Array)
                {
                    foreach (var plan in plans.EnumerateArray().Where(x => S(x, "provisioningStatus") is not ("Disabled" or "PendingActivation")))
                    {
                        p1 |= S(plan, "servicePlanName") is "AAD_PREMIUM" or "AAD_PREMIUM_P2";
                        p2 |= S(plan, "servicePlanName") == "AAD_PREMIUM_P2";
                    }
                }
            }
        }
        catch (GraphError ex)
        {
            notes.Add($"subscribedSkus: {ex.Message}");
        }

        return new Core.Security.TenantSettingsReading
        {
            SecurityDefaults = Bool(defaults, "isEnabled"),
            UsersCreateApps = Bool(user, "allowedToCreateApps"),
            UsersCreateTenants = Bool(user, "allowedToCreateTenants"),
            ConsentPolicies = consent,
            GuestRoleId = authorization is { } g ? S(g, "guestUserRoleId") : null,
            AllowInvitesFrom = authorization is { } i ? S(i, "allowInvitesFrom") : null,
            ConsentWorkflow = Bool(workflow, "isEnabled"),
            ConsentWorkflowJson = workflow?.GetRawText(),
            Methods = states,
            Sharing = sharepoint is { } sp ? S(sp, "sharingCapability") : null,
            HasP1 = p1,
            HasP2 = p2,
            Notes = notes,
        };
    }

    /// <summary>Why a device in Entra may not be disabled or deleted, asked again just before (R17, U18): hybrid joined
    /// (cleaned up in AD), Autopilot (can no longer be provisioned), or still enabled (delete only after disabling).</summary>
    private async Task<string?> DeviceCleanupRefusalAsync(string objectId, bool deleting, CancellationToken cancel)
    {
        var device = await _client.GetAsync($"/devices/{Uri.EscapeDataString(objectId)}?$select=accountEnabled,trustType,physicalIds", cancel);
        if (string.Equals(S(device, "trustType"), "ServerAd", StringComparison.OrdinalIgnoreCase))
        {
            return Texts.T["device.cleanup.hybrid"];
        }

        if (IsAutopilot(device))
        {
            return Texts.T["device.cleanup.autopilot"];
        }

        return deleting && device.TryGetProperty("accountEnabled", out var on) && on.ValueKind == JsonValueKind.True ? Texts.T["device.cleanup.disable-first"] : null;
    }

    private static bool IsAutopilot(JsonElement device) => device.TryGetProperty("physicalIds", out var ids) && ids.ValueKind == JsonValueKind.Array
        && ids.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.String && i.GetString()!.StartsWith("[ZTDID]", StringComparison.OrdinalIgnoreCase));

    /// <summary>How many BitLocker recovery keys a device has (only the list: no key is read).</summary>
    public async Task<int?> CountBitLockerKeysAsync(string deviceId, CancellationToken cancel)
    {
        try
        {
            return (await _client.GetAllAsync($"/informationProtection/bitlocker/recoveryKeys?$filter=deviceId eq '{Uri.EscapeDataString(deviceId)}'", cancel)).Count;
        }
        catch (GraphError)
        {
            return null;
        }
    }

    /// <summary>Compliance per policy (R17, U26): the policies with the status of their devices, the settings that fail,
    /// and whether a device without a policy counts as not compliant.</summary>
    public async Task<Core.Devices.ComplianceReading?> ReadComplianceAsync(CancellationToken cancel)
    {
        List<JsonElement> policies;
        try
        {
            policies = await _client.GetAllAsync("/deviceManagement/deviceCompliancePolicies", cancel);
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
        {
            return new Core.Devices.ComplianceReading(Array.Empty<Core.Devices.CompliancePolicy>(), Array.Empty<Core.Devices.ComplianceSetting>(), null,
                Texts.T.Format("compliance.not-read", ex.Message));
        }

        // The status of the devices per policy, in $batch calls of 20 (a request of its own for what a batch did not answer)
        var output = new List<Core.Devices.CompliancePolicy>();
        foreach (var chunk in policies.Chunk(GraphClient.BatchSize))
        {
            var paths = chunk.Select(p => $"/deviceManagement/deviceCompliancePolicies/{Uri.EscapeDataString(S(p, "id"))}/deviceStatuses").ToList();
            var answers = await _client.BatchGetAsync(paths, cancel);
            for (var i = 0; i < chunk.Length; i++)
            {
                var p = chunk[i];
                var statuses = answers[i] is { } first ? await _client.AllPagesAsync(first, cancel) : await _client.GetAllAsync(paths[i], cancel);
                output.Add(new Core.Devices.CompliancePolicy(S(p, "id"), S(p, "displayName"), Core.Devices.ComplianceAnalysis.PlatformOf(S(p, "@odata.type")),
                    statuses.Select(d => new Core.Devices.ComplianceDeviceStatus(S(d, "deviceDisplayName"), S(d, "userPrincipalName"), S(d, "status"), Date(d, "lastReportedDateTime"))).ToList()));
            }
        }

        var settings = new List<Core.Devices.ComplianceSetting>();
        try
        {
            settings.AddRange((await _client.GetAllAsync("/deviceManagement/deviceCompliancePolicySettingStateSummaries", cancel))
                .Select(x => new Core.Devices.ComplianceSetting(S(x, "settingName"), S(x, "platformType"), I(x, "compliantDeviceCount"), I(x, "nonCompliantDeviceCount"), I(x, "errorDeviceCount"))));
        }
        catch (GraphError)
        {
            // The settings are an extra; the policies are what matters
        }

        bool? secure = null;
        string? settingsJson = null;
        try
        {
            var management = await _client.GetAsync("/deviceManagement?$select=settings", cancel);
            if (management.TryGetProperty("settings", out var st) && st.ValueKind == JsonValueKind.Object)
            {
                settingsJson = st.GetRawText();
                if (st.TryGetProperty("secureByDefault", out var sb) && sb.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    secure = sb.GetBoolean();
                }
            }
        }
        catch (GraphError)
        {
            // Unknown: said as such on the screen
        }

        return new Core.Devices.ComplianceReading(output, settings.OrderByDescending(x => x.NonCompliant + x.Error).ToList(), secure, null) { SettingsJson = settingsJson };
    }

    private static int I(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    /// <summary>The compliance policies of one device with the state of each setting (the beta of Intune: v1.0 leaves the
    /// settings out).</summary>
    public async Task<IReadOnlyList<Core.Devices.DeviceComplianceState>?> ReadDeviceComplianceAsync(string managedDeviceId, CancellationToken cancel)
    {
        try
        {
            return (await _client.GetAllAsync($"{BetaBase}/deviceManagement/managedDevices/{Uri.EscapeDataString(managedDeviceId)}/deviceCompliancePolicyStates", cancel))
                .Select(p => new Core.Devices.DeviceComplianceState(S(p, "displayName"), S(p, "state"),
                    p.TryGetProperty("settingStates", out var st) && st.ValueKind == JsonValueKind.Array
                        ? st.EnumerateArray().Select(x => new Core.Devices.ComplianceSettingState(S(x, "settingName").Length > 0 ? S(x, "settingName") : S(x, "setting"), S(x, "state"))).ToList()
                        : new List<Core.Devices.ComplianceSettingState>()))
                .ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Core.Helpdesk.BitLockerKey>> BitLockerKeysAsync(string? deviceId, string? keyId, CancellationToken cancel)
    {
        List<JsonElement> keys;
        if (deviceId is { Length: > 0 })
        {
            keys = await _client.GetAllAsync($"/informationProtection/bitlocker/recoveryKeys?$filter=deviceId eq '{Uri.EscapeDataString(deviceId)}'", cancel);
        }
        else if (keyId is { Length: > 0 } k)
        {
            var prefix = k.Trim().Trim('{', '}');
            keys = (await _client.GetAllAsync("/informationProtection/bitlocker/recoveryKeys", cancel))
                .Where(e => S(e, "id").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        else
        {
            return Array.Empty<Core.Helpdesk.BitLockerKey>();
        }

        var output = new List<Core.Helpdesk.BitLockerKey>();
        foreach (var e in keys.Take(20))
        {
            var withKey = await _client.GetAsync($"/informationProtection/bitlocker/recoveryKeys/{Uri.EscapeDataString(S(e, "id"))}?$select=key,deviceId,createdDateTime,volumeType", cancel);
            output.Add(new Core.Helpdesk.BitLockerKey(S(e, "id"), S(withKey, "deviceId"), S(withKey, "key"),
                DateTimeOffset.TryParse(S(withKey, "createdDateTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var c) ? c : null,
                S(withKey, "volumeType")));
        }

        return output;
    }

    public async Task<Core.Helpdesk.LapsPassword?> LapsAsync(string deviceId, CancellationToken cancel)
    {
        try
        {
            var c = await _client.GetAsync($"/directory/deviceLocalCredentials/{Uri.EscapeDataString(deviceId)}?$select=credentials", cancel);
            if (!c.TryGetProperty("credentials", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var newest = list.EnumerateArray().OrderByDescending(x => S(x, "backupDateTime"), StringComparer.Ordinal).FirstOrDefault();
            if (newest.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var password = S(newest, "passwordBase64") is { Length: > 0 } b ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b)) : null;
            return new Core.Helpdesk.LapsPassword(S(newest, "accountName"), password,
                DateTimeOffset.TryParse(S(newest, "backupDateTime"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null,
                "Windows LAPS in Entra");
        }
        catch (GraphError ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ service health (0.37)

    public async Task<Core.Reporting.ServiceHealthReading?> ReadServiceHealthAsync(CancellationToken cancel)
    {
        List<Core.Reporting.ServiceStatus> services;
        List<Core.Reporting.ServiceIssue> issues;
        try
        {
            services = (await _client.GetAllAsync("/admin/serviceAnnouncement/healthOverviews?$select=service,status", cancel))
                .Select(s => new Core.Reporting.ServiceStatus(S(s, "service"), S(s, "status"))).OrderBy(s => s.Service, StringComparer.OrdinalIgnoreCase).ToList();
            issues = (await _client.GetAllAsync("/admin/serviceAnnouncement/issues?$filter=isResolved eq false", cancel))
                .Select(i => new Core.Reporting.ServiceIssue(S(i, "id"), S(i, "title"), S(i, "service"), S(i, "status"), S(i, "classification"), Date(i, "startDateTime"), Date(i, "lastModifiedDateTime")))
                .OrderByDescending(i => i.LastUpdate).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        var messages = new List<Core.Reporting.ServiceMessage>();
        string? note = null;
        try
        {
            var page = await _client.GetAsync("/admin/serviceAnnouncement/messages?$top=30&$orderby=lastModifiedDateTime desc", cancel);
            if (page.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in v.EnumerateArray())
                {
                    var services2 = m.TryGetProperty("services", out var s2) && s2.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", s2.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : string.Empty;
                    messages.Add(new Core.Reporting.ServiceMessage(S(m, "id"), S(m, "title"), S(m, "category"), services2, Date(m, "startDateTime"), Date(m, "actionRequiredByDateTime")));
                }
            }
        }
        catch (GraphError)
        {
            note = Texts.T["code.graph-connection.the-message-center-cannot-be"];
        }

        return new Core.Reporting.ServiceHealthReading(services, issues, messages, note);
    }

    // ------------------------------------------------------------------ devices, Teams and SharePoint (0.36)

    public async Task<IReadOnlyList<Core.Devices.EntraDevice>?> ReadEntraDevicesAsync(CancellationToken cancel)
    {
        try
        {
            const string select = "id,deviceId,displayName,operatingSystem,operatingSystemVersion,accountEnabled,approximateLastSignInDateTime,trustType,physicalIds";
            return (await _client.GetAllAsync($"/devices?$select={select}&$top=999", cancel))
                .Select(d => new Core.Devices.EntraDevice(S(d, "id"), S(d, "deviceId"), S(d, "displayName"), S(d, "operatingSystem"), S(d, "operatingSystemVersion"),
                    !d.TryGetProperty("accountEnabled", out var on) || on.ValueKind != JsonValueKind.False, Date(d, "approximateLastSignInDateTime"), S(d, "trustType"))
                    { Autopilot = IsAutopilot(d) })
                .ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Core.Devices.IntuneDevice>?> ReadIntuneDevicesAsync(CancellationToken cancel)
    {
        try
        {
            const string select = "id,deviceName,userPrincipalName,operatingSystem,osVersion,complianceState,lastSyncDateTime,managedDeviceOwnerType,"
                + "serialNumber,model,manufacturer,isEncrypted,enrolledDateTime,azureADDeviceId";
            return (await _client.GetAllAsync($"/deviceManagement/managedDevices?$select={select}&$top=999", cancel))
                .Select(d => new Core.Devices.IntuneDevice(S(d, "id"), S(d, "deviceName"), S(d, "userPrincipalName"), S(d, "operatingSystem"), S(d, "osVersion"),
                    S(d, "complianceState"), Date(d, "lastSyncDateTime"), S(d, "managedDeviceOwnerType"))
                {
                    Serial = S(d, "serialNumber"),
                    Model = S(d, "model"),
                    Manufacturer = S(d, "manufacturer"),
                    Encrypted = d.TryGetProperty("isEncrypted", out var enc) && enc.ValueKind is JsonValueKind.True or JsonValueKind.False ? enc.GetBoolean() : null,
                    Enrolled = Date(d, "enrolledDateTime"),
                    EntraDeviceId = S(d, "azureADDeviceId"),
                })
                .ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    public async Task<Core.Devices.CollaborationReading?> ReadCollaborationAsync(CancellationToken cancel)
    {
        var notes = new List<string>();
        var groups = new List<Core.Devices.GroupActivity>();
        var activity = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in await _client.GetAllAsync("https://graph.microsoft.com/beta/reports/getOffice365GroupsActivityDetail(period='D180')?$format=application/json", cancel))
            {
                activity[S(r, "groupId")] = Date(r, "lastActivityDate");
            }
        }
        catch (GraphError)
        {
            notes.Add(Texts.T["code.graph-connection.the-usage-report-of-the"]);
        }

        foreach (var g in await _client.GetAllAsync("/groups?$filter=groupTypes/any(c:c eq 'Unified')&$select=id,displayName,resourceProvisioningOptions&$expand=owners($select=id)&$top=100", cancel))
        {
            var isTeam = g.TryGetProperty("resourceProvisioningOptions", out var o) && o.ValueKind == JsonValueKind.Array
                         && o.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "Team");
            var owners = g.TryGetProperty("owners", out var w) && w.ValueKind == JsonValueKind.Array ? w.GetArrayLength() : 0;
            groups.Add(new Core.Devices.GroupActivity(S(g, "id"), S(g, "displayName"), isTeam, owners, activity.GetValueOrDefault(S(g, "id"))));
        }

        var sites = new List<Core.Devices.SiteUsage>();
        try
        {
            foreach (var r in await _client.GetAllAsync("https://graph.microsoft.com/beta/reports/getSharePointSiteUsageDetail(period='D180')?$format=application/json", cancel))
            {
                if (B(r, "isDeleted"))
                {
                    continue;
                }

                long L(string name) => r.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
                sites.Add(new Core.Devices.SiteUsage(S(r, "siteUrl"), S(r, "ownerPrincipalName"), L("storageUsedInBytes"), L("fileCount"), Date(r, "lastActivityDate")));
            }
        }
        catch (GraphError)
        {
            notes.Add(Texts.T["code.graph-connection.the-usage-report-of-sharepoint"]);
        }

        string? sharing = null;
        try
        {
            sharing = S(await _client.GetAsync("/admin/sharepoint/settings?$select=sharingCapability", cancel), "sharingCapability");
        }
        catch (GraphError)
        {
            notes.Add(Texts.T["code.graph-connection.the-sharing-setting-of-sharepoint"]);
        }

        if (sites.Count > 0 && sites.All(s => s.Url.Length == 0))
        {
            notes.Add(Texts.T["code.graph-connection.the-reports-show-no-names"]);
        }

        return new Core.Devices.CollaborationReading(groups, sites, string.IsNullOrEmpty(sharing) ? null : sharing, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    // ------------------------------------------------------------------ security checks (0.34)

    private static DateTimeOffset? Date(JsonElement e, string name)
        => DateTimeOffset.TryParse(S(e, name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null;

    public async Task<Core.Security.AppsReading?> ReadAppsAsync(CancellationToken cancel)
    {
        var credentials = new List<Core.Security.AppCredential>();
        foreach (var app in await _client.GetAllAsync("/applications?$select=id,appId,displayName,passwordCredentials,keyCredentials&$top=999", cancel))
        {
            foreach (var (list, kind) in new[] { ("passwordCredentials", "geheim"), ("keyCredentials", "certificaat") })
            {
                if (app.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    credentials.AddRange(items.EnumerateArray().Select(c => Date(c, "endDateTime")).OfType<DateTimeOffset>()
                        .Select(end => new Core.Security.AppCredential(S(app, "displayName"), S(app, "appId"), kind, end)));
                }
            }
        }

        var names = (await _client.GetAllAsync("/servicePrincipals?$select=id,appId,displayName&$top=999", cancel))
            .ToDictionary(sp => S(sp, "id"), sp => (Name: S(sp, "displayName"), AppId: S(sp, "appId")), StringComparer.OrdinalIgnoreCase);
        var users = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var permissions = new List<Core.Security.AppPermission>();
        foreach (var g in await _client.GetAllAsync("/oauth2PermissionGrants", cancel))
        {
            var client = names.GetValueOrDefault(S(g, "clientId"));
            foreach (var scope in S(g, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                permissions.Add(new Core.Security.AppPermission(client.Name ?? S(g, "clientId"), client.AppId ?? string.Empty, scope, "Delegated",
                    S(g, "consentType"), S(g, "principalId") is { Length: > 0 } pid ? pid : null));
            }
        }

        // Application permissions on Microsoft Graph and Exchange Online (what an app can do without a user)
        foreach (var resource in new[] { "00000003-0000-0000-c000-000000000000", "00000002-0000-0ff1-ce00-000000000000" })
        {
            try
            {
                var sp = await _client.GetAsync($"/servicePrincipals(appId='{resource}')?$select=id,appRoles", cancel);
                var roles = sp.TryGetProperty("appRoles", out var r) && r.ValueKind == JsonValueKind.Array
                    ? r.EnumerateArray().ToDictionary(x => S(x, "id"), x => S(x, "value"), StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>();
                foreach (var a in await _client.GetAllAsync($"/servicePrincipals/{S(sp, "id")}/appRoleAssignedTo?$top=999", cancel))
                {
                    if (S(a, "principalType") == "ServicePrincipal" && roles.TryGetValue(S(a, "appRoleId"), out var value))
                    {
                        var who = names.GetValueOrDefault(S(a, "principalId"));
                        permissions.Add(new Core.Security.AppPermission(who.Name ?? S(a, "principalDisplayName"), who.AppId ?? string.Empty, value, "Application", "AllPrincipals", null));
                    }
                }
            }
            catch (GraphError ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                // Exchange Online not in this tenant
            }
        }

        return new Core.Security.AppsReading(credentials, permissions);
    }

    public async Task<Core.Security.SecureScoreReading?> ReadSecureScoreAsync(CancellationToken cancel)
    {
        try
        {
            var scores = (await _client.GetAsync("/security/secureScores?$top=90", cancel)).TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.Clone()).ToList() : new List<JsonElement>();
            var history = scores.Select(x => (Date(x, "createdDateTime") ?? DateTimeOffset.MinValue,
                    x.TryGetProperty("currentScore", out var c) && c.TryGetDouble(out var cd) ? cd : 0,
                    x.TryGetProperty("maxScore", out var m) && m.TryGetDouble(out var md) ? md : 0))
                .Where(h => h.Item1 > DateTimeOffset.MinValue).ToList();
            var achieved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (scores.OrderByDescending(x => S(x, "createdDateTime"), StringComparer.Ordinal).FirstOrDefault() is { ValueKind: JsonValueKind.Object } latest
                && latest.TryGetProperty("controlScores", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cs.EnumerateArray())
                {
                    achieved[S(c, "controlName")] = c.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var d) ? d : 0;
                }
            }

            var actions = new List<(string, double, string)>();
            foreach (var p in await _client.GetAllAsync("/security/secureScoreControlProfiles?$top=200", cancel))
            {
                var max = p.TryGetProperty("maxScore", out var mx) && mx.TryGetDouble(out var mxd) ? mxd : 0;
                var left = max - achieved.GetValueOrDefault(S(p, "id"));
                var deprecated = p.TryGetProperty("deprecated", out var dep) && dep.ValueKind == JsonValueKind.True;
                if (left > 0 && !deprecated)
                {
                    actions.Add((S(p, "title"), left, S(p, "actionUrl")));
                }
            }

            return new Core.Security.SecureScoreReading(history, actions);
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Core.Security.RoleAssignmentInfo>?> ReadRoleAssignmentsAsync(CancellationToken cancel)
    {
        var output = new List<Core.Security.RoleAssignmentInfo>();
        static string Principal(JsonElement e) => e.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.Object
            ? (S(p, "userPrincipalName") is { Length: > 0 } u ? u : S(p, "displayName")) : S(e, "principalId");
        static string RoleName(JsonElement e) => e.TryGetProperty("roleDefinition", out var r) && r.ValueKind == JsonValueKind.Object ? S(r, "displayName") : S(e, "roleDefinitionId");
        static string Kind(JsonElement e) => e.TryGetProperty("principal", out var p) && p.ValueKind == JsonValueKind.Object
            ? S(p, "@odata.type").Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal) : string.Empty;
        static Core.Security.RoleAssignmentInfo Info(JsonElement a, string kind, DateTimeOffset? end, bool pim)
            => new(RoleName(a), Principal(a), kind, end) { RoleId = S(a, "roleDefinitionId"), PrincipalId = S(a, "principalId"), PrincipalType = Kind(a), ViaPim = pim };
        try
        {
            // With PIM (P2): active assignments with their end, and the eligible ones
            foreach (var a in await _client.GetAllAsync("/roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal,roleDefinition", cancel))
            {
                var end = Date(a, "endDateTime");
                output.Add(Info(a, S(a, "assignmentType") == "Activated" ? "actief (PIM)" : end is null ? "permanent" : "tijdelijk", end, true));
            }

            foreach (var a in await _client.GetAllAsync("/roleManagement/directory/roleEligibilityScheduleInstances?$expand=principal,roleDefinition", cancel))
            {
                output.Add(Info(a, "in aanmerking", Date(a, "endDateTime"), true));
            }
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // Without P2: the assignments themselves (all permanent)
            output.Clear();
            foreach (var a in await _client.GetAllAsync("/roleManagement/directory/roleAssignments?$expand=principal,roleDefinition", cancel))
            {
                output.Add(Info(a, "permanent", null, false));
            }
        }

        return output;
    }

    public async Task<IReadOnlyList<Core.Security.RiskyUser>?> ReadRiskyUsersAsync(CancellationToken cancel)
    {
        try
        {
            return (await _client.GetAllAsync("/identityProtection/riskyUsers?$top=500", cancel))
                .Select(u => new Core.Security.RiskyUser(S(u, "userPrincipalName"), S(u, "riskLevel"), S(u, "riskState"), Date(u, "riskLastUpdatedDateTime")))
                .ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ security

    public async Task<SecurityData?> ReadSecurityAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report(Texts.T["code.graph-connection.entra-mfa-registrations"]);
        IReadOnlyList<MfaRegistration> mfa = Array.Empty<MfaRegistration>();
        string? mfaMessage = null;
        try
        {
            var raw = await _client.GetAllAsync("/reports/authenticationMethods/userRegistrationDetails?$top=999", cancel);
            mfa = raw.Select(ToMfa).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            mfaMessage = Texts.T.Format("code.graph-connection.cannot-be-read-your-account", ex.Message);
        }

        progress?.Report(Texts.T["code.graph-connection.entra-admin-roles"]);
        var roles = new List<RoleMember>();
        string? rolesMessage = null;
        try
        {
            foreach (var role in await _client.GetAllAsync("/directoryRoles?$select=id,displayName,roleTemplateId", cancel))
            {
                var members = await _client.GetAllAsync($"/directoryRoles/{S(role, "id")}/members?$select=id,displayName,userPrincipalName", cancel);
                roles.AddRange(members.Select(l => new RoleMember(S(role, "displayName"), S(l, "id"), S(l, "displayName"), S(l, "userPrincipalName"),
                    S(l, "@odata.type").Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal), S(role, "roleTemplateId"))));
            }
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            rolesMessage = Texts.T.Format("code.graph-connection.cannot-be-read", ex.Message);
        }

        progress?.Report(Texts.T.Format("code.graph-connection.entra-mfa-registrations-and-role", mfa.Count, roles.Count));
        return new SecurityData(mfa, roles, mfaMessage, rolesMessage);
    }

    public async Task<IReadOnlyList<SignInEvent>> ReadSignInsAsync(string? userId, int days, bool onlyFailed, CancellationToken cancel)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var filter = $"createdDateTime ge {since}";
        if (userId is not null)
        {
            filter += $" and userId eq '{userId.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        if (onlyFailed)
        {
            filter += " and status/errorCode ne 0";
        }

        // One page is enough for the screen (newest first); sign-in logs can be very large
        var currentPage = await _client.GetAsync($"/auditLogs/signIns?$filter={Uri.EscapeDataString(filter)}&$top={(userId is null ? 500 : 100)}", cancel);
        return currentPage.TryGetProperty("value", out var values) ? values.EnumerateArray().Select(ToSignIn).ToList() : new List<SignInEvent>();
    }

    public async Task<IReadOnlyList<SignInEvent>> ReadSignInsAsync(int days, int max, CancellationToken cancel)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var output = new List<SignInEvent>();
        string? next = $"/auditLogs/signIns?$filter={Uri.EscapeDataString($"createdDateTime ge {since}")}&$top=999";
        while (next is not null && output.Count < max)
        {
            var currentPage = await _client.GetAsync(next, cancel);
            if (currentPage.TryGetProperty("value", out var values))
            {
                output.AddRange(values.EnumerateArray().Select(ToSignIn));
            }

            next = currentPage.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }

        return output.Count > max ? output.GetRange(0, max) : output;
    }

    // ------------------------------------------------------------------ calendar of a room

    public async Task<IReadOnlyList<Appointment>> ReadCalendarAsync(string address, DateTime start, DateTime until, CancellationToken cancel)
    {
        static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var path = $"/users/{Uri.EscapeDataString(address)}/calendarView?startDateTime={Utc(start)}&endDateTime={Utc(until)}"
            + "&$select=subject,organizer,start,end,isAllDay,sensitivity,showAs,isCancelled&$orderby=start/dateTime&$top=250";
        try
        {
            return (await _client.GetAllAsync(path, cancel)).Select(ToAppointment).OrderBy(a => a.Begin).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
        {
            throw new NoPermissionError(ex.Message);
        }
    }

    /// <summary>Graph returns the times in UTC (without a Prefer header); an all-day appointment stays on its
    /// date.</summary>
    internal static Appointment ToAppointment(JsonElement e)
    {
        var wholeDay = B(e, "isAllDay");
        DateTime Time(string name)
        {
            if (!e.TryGetProperty(name, out var t) || !DateTime.TryParse(S(t, "dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                return default;
            }

            return wholeDay ? value.Date : DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
        }

        var organiser = e.TryGetProperty("organizer", out var o) && o.TryGetProperty("emailAddress", out var m) ? m : default;
        return new Appointment(
            Time("start"),
            Time("end"),
            S(e, "subject"),
            organiser.ValueKind == JsonValueKind.Object ? S(organiser, "name") : string.Empty,
            organiser.ValueKind == JsonValueKind.Object ? S(organiser, "address") : string.Empty,
            wholeDay,
            S(e, "sensitivity") is "private" or "confidential",
            S(e, "showAs"),
            B(e, "isCancelled"));
    }

    // ------------------------------------------------------------------ conditional access

    public async Task<CaData?> ReadConditionalAccessAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report(Texts.T["code.graph-connection.entra-conditional-access-policies"]);
        List<CaPolicy> policies;
        try
        {
            policies = (await _client.GetAllAsync("/identity/conditionalAccess/policies", cancel)).Select(ToCaPolicy).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return new CaData(Array.Empty<CaPolicy>(), Array.Empty<NamedLocation>(), Array.Empty<AuthenticationStrength>(),
                new Dictionary<string, string>(), null,Texts.T.Format("code.graph-connection.cannot-be-read-the-app", ex.Message));
        }

        var locations = new List<NamedLocation>();
        var strengths = new List<AuthenticationStrength>();
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool? defaults = null;
        try
        {
            locations = (await _client.GetAllAsync("/identity/conditionalAccess/namedLocations", cancel)).Select(ToLocation).ToList();
        }
        catch (GraphError)
        {
            // Without locations the screens show the ids
        }

        try
        {
            strengths = (await _client.GetAllAsync("/policies/authenticationStrengthPolicies", cancel)).Select(ToStrength).ToList();
        }
        catch (GraphError)
        {
        }

        try
        {
            foreach (var r in await _client.GetAllAsync("/directoryRoleTemplates?$select=id,displayName", cancel))
            {
                roles[S(r, "id")] = S(r, "displayName");
            }
        }
        catch (GraphError)
        {
        }

        try
        {
            var sd = await _client.GetAsync("/policies/identitySecurityDefaultsEnforcementPolicy", cancel);
            defaults = sd.TryGetProperty("isEnabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
        }
        catch (GraphError)
        {
        }

        // Groups in the policies: all members, also via nested groups (that is how Conditional Access itself works)
        Dictionary<string, IReadOnlyList<string>>? groupMembers = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var id in policies.SelectMany(b => b.GroupsIn.Concat(b.GroupsOff)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var members = await _client.GetAllAsync($"/groups/{Uri.EscapeDataString(id)}/transitiveMembers/microsoft.graph.user?$select=id&$top=999", cancel);
                groupMembers[id] = members.Select(l => S(l, "id")).Where(x => x.Length > 0).ToList();
            }
        }
        catch (GraphError)
        {
            groupMembers = null;
        }

        progress?.Report(Texts.T.Format("code.graph-connection.entra-conditional-access-policies-and", policies.Count, locations.Count));
        return new CaData(policies.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), locations, strengths, roles, defaults,
            null, groupMembers);
    }

    internal static CaPolicy ToCaPolicy(JsonElement p) => CaJson.Read(p);

    internal static NamedLocation ToLocation(JsonElement l) => CaJson.ReadLocation(l);

    internal static AuthenticationStrength ToStrength(JsonElement a)
    {
        var combinations = a.TryGetProperty("allowedCombinations", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new AuthenticationStrength(S(a, "id"), S(a, "displayName"), S(a, "policyType") == "builtIn", combinations);
    }

    internal static MfaRegistration ToMfa(JsonElement m)
    {
        var methods = m.TryGetProperty("methodsRegistered", out var mr) && mr.ValueKind == JsonValueKind.Array
            ? mr.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : new List<string>();
        var defaultValue = S(m, "defaultMfaMethod");
        if (defaultValue.Length == 0 || defaultValue == "none")
        {
            defaultValue = S(m, "userPreferredMethodForSecondaryAuthentication");
        }

        return new MfaRegistration(S(m, "id"), S(m, "userPrincipalName"), B(m, "isMfaRegistered"), B(m, "isMfaCapable"), methods,
            defaultValue == "none" ? string.Empty : defaultValue, B(m, "isAdmin"));
    }

    internal static SignInEvent ToSignIn(JsonElement a)
    {
        var time = DateTimeOffset.TryParse(S(a, "createdDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTimeOffset.MinValue;
        var location = a.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object
            ? string.Join(", ", new[] { S(l, "city"), S(l, "countryOrRegion") }.Where(x => x.Length > 0))
            : string.Empty;
        var code = 0;
        var reason = string.Empty;
        if (a.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            code = st.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            reason = S(st, "failureReason");
        }

        List<CaApplication>? ca = null;
        if (a.TryGetProperty("appliedConditionalAccessPolicies", out var cap) && cap.ValueKind == JsonValueKind.Array)
        {
            ca = cap.EnumerateArray().Select(x => new CaApplication(S(x, "id"), S(x, "displayName"), S(x, "result"))).Where(x => x.PolicyId.Length > 0).ToList();
        }

        return new SignInEvent(time, S(a, "userId"), S(a, "userPrincipalName"), S(a, "userDisplayName"), S(a, "appDisplayName"),
            S(a, "ipAddress"), location, code, code == 0 ? string.Empty : reason, S(a, "clientAppUsed"), ca, S(a, "id"), S(a, "riskLevelDuringSignIn"));
    }

    internal static EntraUser ToUser(JsonElement u, IReadOnlyDictionary<string, string> skuName)
    {
        var licences = u.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.EnumerateArray().Select(l => S(l, "skuId")).Select(id => skuName.GetValueOrDefault(id, id)).ToList()
            : new List<string>();
        string last = string.Empty;
        if (u.TryGetProperty("signInActivity", out var sa) && sa.ValueKind == JsonValueKind.Object)
        {
            var dates = new[] { S(sa, "lastSignInDateTime"), S(sa, "lastNonInteractiveSignInDateTime") }
                .Select(d => DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var x) ? x : (DateTimeOffset?)null)
                .Where(d => d is not null)
                .ToList();
            last = dates.Count == 0 ? string.Empty : dates.Max()!.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return new EntraUser
        {
            Id = S(u, "id"),
            UserPrincipalName = S(u, "userPrincipalName"),
            DisplayName = S(u, "displayName"),
            GivenName = S(u, "givenName"),
            Surname = S(u, "surname"),
            Mail = S(u, "mail"),
            MailNickname = S(u, "mailNickname"),
            AccountEnabled = B(u, "accountEnabled"),
            UserType = S(u, "userType"),
            Department = S(u, "department"),
            JobTitle = S(u, "jobTitle"),
            OfficeLocation = S(u, "officeLocation"),
            Phone = u.TryGetProperty("businessPhones", out var bp) && bp.ValueKind == JsonValueKind.Array && bp.GetArrayLength() > 0 ? bp[0].GetString() ?? string.Empty : string.Empty,
            Mobile = S(u, "mobilePhone"),
            ManagerId = u.TryGetProperty("manager", out var mgr) && mgr.ValueKind == JsonValueKind.Object ? S(mgr, "id") : string.Empty,
            CompanyName = S(u, "companyName"),
            City = S(u, "city"),
            UsageLocation = S(u, "usageLocation"),
            EmployeeId = S(u, "employeeId"),
            EmployeeType = S(u, "employeeType"),
            StreetAddress = S(u, "streetAddress"),
            PostalCode = S(u, "postalCode"),
            State = S(u, "state"),
            Country = S(u, "country"),
            CreatedDateTime = S(u, "createdDateTime"),
            OnPremisesSyncEnabled = B(u, "onPremisesSyncEnabled"),
            OnPremisesSamAccountName = S(u, "onPremisesSamAccountName"),
            OnPremisesDistinguishedName = S(u, "onPremisesDistinguishedName"),
            Licences = licences,
            LastSignIn = last,
        };
    }

    internal static CloudGroup ToGroup(JsonElement g, int countMembers)
    {
        var types = g.TryGetProperty("groupTypes", out var gt) && gt.ValueKind == JsonValueKind.Array
            ? gt.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : new List<string>();
        var mailEnabled = B(g, "mailEnabled");
        var securityEnabled = B(g, "securityEnabled");
        var type = types.Contains("Unified") ? CloudGroupType.Microsoft365
            : mailEnabled && securityEnabled ? CloudGroupType.MailSecurity
            : mailEnabled ? CloudGroupType.Distribution
            : CloudGroupType.Security;
        return new CloudGroup
        {
            Id = S(g, "id"),
            DisplayName = S(g, "displayName"),
            Description = S(g, "description"),
            Mail = S(g, "mail"),
            OnPremisesSamAccountName = S(g, "onPremisesSamAccountName"),
            Type = type,
            FromAd = B(g, "onPremisesSyncEnabled"),
            Dynamic = types.Contains("DynamicMembership"),
            MembershipRule = S(g, "membershipRule"),
            GroupLicence = g.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array && al.GetArrayLength() > 0,
            CountMembers = countMembers,
        };
    }

    // ------------------------------------------------------------------ running

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
    {
        var output = new List<ChangeResult>();
        foreach (var w in changes)
        {
            output.Add(await RunAsync(w, cancel));
        }

        return output;
    }

    public async Task<IReadOnlyList<GraphOwner>> ReadOwnersAsync(string groupId, CancellationToken cancel)
    {
        var list = await _client.GetAllAsync($"/groups/{Uri.EscapeDataString(groupId)}/owners?$select=id,displayName,userPrincipalName", cancel);
        return list.Select(e => new GraphOwner(S(e, "id"), S(e, "displayName"), S(e, "userPrincipalName"))).ToList();
    }

    /// <summary>Changes to a group itself: Target is the group id, a user (if any) is in the parameter "lid".</summary>
    private async Task<ChangeResult> RunGroupAsync(Change w, CancellationToken cancel)
    {
        var groupValue = Uri.EscapeDataString(w.Target);
        if (w.ChangeAction == ChangeAction.GraphSetGroupAttributes)
        {
            await _client.PatchAsync($"/groups/{groupValue}", AttributesContent(w.Parameters), cancel);
            return ChangeResult.Ok(w);
        }

        var member = w.P("lid");
        var memberId = member.Contains('@') ? await FindUserIdAsync(member, cancel) : member;
        if (string.IsNullOrEmpty(memberId))
        {
            return ChangeResult.Error(w, Texts.T.Format("code.group-plans.does-not-exist-in-entra", member));
        }

        if (w.ChangeAction == ChangeAction.GraphAddOwner)
        {
            try
            {
                await _client.PostAsync($"/groups/{groupValue}/owners/$ref",
                    new Dictionary<string, string> { ["@odata.id"] = $"{GraphClient.Base}/directoryObjects/{memberId}" }, cancel);
            }
            catch (GraphError ex) when (ex.Status == HttpStatusCode.BadRequest && ex.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase))
            {
                return ChangeResult.Ok(w, Texts.T["result.already-owner"]);
            }

            return ChangeResult.Ok(w);
        }

        await _client.DeleteAsync($"/groups/{groupValue}/owners/{memberId}/$ref", cancel);
        return ChangeResult.Ok(w);
    }

    /// <summary>Conditional Access: the JSON goes as it is in the plan (the plan is checked and in the change
    /// log).</summary>
    private async Task<ChangeResult> RunCaAsync(Change w, CancellationToken cancel)
    {
        var content = System.Text.Json.Nodes.JsonNode.Parse(w.P("json")) ?? throw new InvalidOperationException(Texts.T["code.graph-connection.no-json-in-the-change"]);
        switch (w.ChangeAction)
        {
            case ChangeAction.GraphCreateCaPolicy:
            {
                var newValue = await _client.PostWithResponseAsync("/identity/conditionalAccess/policies", content, cancel);
                return ChangeResult.Ok(w, Texts.T.Format("result.created-id", S(newValue, "id")));
            }

            case ChangeAction.GraphUpdateCaPolicy:
                await _client.PatchAsync($"/identity/conditionalAccess/policies/{Uri.EscapeDataString(w.Target)}", content, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GraphCreateCaLocation:
            {
                var newValue = await _client.PostWithResponseAsync("/identity/conditionalAccess/namedLocations", content, cancel);
                return ChangeResult.Ok(w, Texts.T.Format("result.created-id", S(newValue, "id")));
            }

            default:
                await _client.PatchAsync($"/identity/conditionalAccess/namedLocations/{Uri.EscapeDataString(w.Target)}", content, cancel);
                return ChangeResult.Ok(w);
        }
    }

    /// <summary>Who changed Conditional Access (Entra audit log, the last days).</summary>
    public async Task<IReadOnlyList<CaAuditEntry>> ReadCaAuditAsync(int days, CancellationToken cancel)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var filter = $"activityDateTime ge {since} and loggedByService eq 'Conditional Access'";
        try
        {
            var list = await _client.GetAllAsync($"/auditLogs/directoryAudits?$filter={Uri.EscapeDataString(filter)}&$top=200", cancel);
            return list.Select(ToCaAudit).OrderByDescending(a => a.Time).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new NoPermissionError(ex.Message);
        }
    }

    // ------------------------------------------------------------------ logs kept locally (R18)

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<SignInEvent>> ReadSignInsToKeepAsync(DateTimeOffset since, IReadOnlyCollection<string> userIds, int max, CancellationToken cancel)
    {
        var after = $"createdDateTime gt {Iso(since)}";
        var output = new List<SignInEvent>();
        try
        {
            output.AddRange(await SignInPagesAsync($"{after} and status/errorCode ne 0", max, cancel));

            // Administrators and emergency accounts: 15 per request, so that the filter stays short
            foreach (var chunk in userIds.Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(15))
            {
                var users = string.Join(" or ", chunk.Select(i => $"userId eq '{i.Replace("'", "''", StringComparison.Ordinal)}'"));
                output.AddRange(await SignInPagesAsync($"{after} and ({users})", max, cancel));
            }
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new NoPermissionError(ex.Message);
        }

        try
        {
            // Risky sign-ins need Entra ID P2: without it Graph answers with an error, which only skips this part
            output.AddRange(await SignInPagesAsync($"{after} and riskState eq 'atRisk'", max, cancel));
        }
        catch (GraphError)
        {
        }

        return output.Where(e => e.Id.Length > 0).DistinctBy(e => e.Id, StringComparer.OrdinalIgnoreCase).OrderByDescending(e => e.Time).ToList();
    }

    private async Task<List<SignInEvent>> SignInPagesAsync(string filter, int max, CancellationToken cancel)
    {
        var output = new List<SignInEvent>();
        string? next = $"/auditLogs/signIns?$filter={Uri.EscapeDataString(filter)}&$top=999";
        while (next is not null && output.Count < max)
        {
            var currentPage = await _client.GetAsync(next, cancel);
            if (currentPage.TryGetProperty("value", out var values))
            {
                output.AddRange(values.EnumerateArray().Select(ToSignIn));
            }

            next = currentPage.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
        }

        return output.Count > max ? output.GetRange(0, max) : output;
    }

    public async Task<IReadOnlyList<Core.Security.AuditEvent>> ReadDirectoryAuditsAsync(DateTimeOffset since, int max, CancellationToken cancel)
    {
        var output = new List<Core.Security.AuditEvent>();
        string? next = $"/auditLogs/directoryAudits?$filter={Uri.EscapeDataString($"activityDateTime gt {Iso(since)}")}&$top=500";
        try
        {
            while (next is not null && output.Count < max)
            {
                var currentPage = await _client.GetAsync(next, cancel);
                if (currentPage.TryGetProperty("value", out var values))
                {
                    output.AddRange(values.EnumerateArray().Select(ToAudit));
                }

                next = currentPage.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
            }
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new NoPermissionError(ex.Message);
        }

        return output.Count > max ? output.GetRange(0, max) : output;
    }

    internal static Core.Security.AuditEvent ToAudit(JsonElement a)
    {
        var ca = ToCaAudit(a);
        return new Core.Security.AuditEvent(S(a, "id"), ca.Time, ca.Activity, S(a, "category"), S(a, "loggedByService"), ca.Who, ca.TargetId, ca.TargetName, ca.Result);
    }

    internal static CaAuditEntry ToCaAudit(JsonElement a)
    {
        var who = a.TryGetProperty("initiatedBy", out var i)
            ? (i.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? S(u, "userPrincipalName")
                : i.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object ? S(app, "displayName") : string.Empty)
            : string.Empty;
        var target = a.TryGetProperty("targetResources", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().FirstOrDefault() : default;
        DateTimeOffset.TryParse(S(a, "activityDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time);
        return new CaAuditEntry(time, who, S(a, "activityDisplayName"),
            target.ValueKind == JsonValueKind.Object ? S(target, "id") : string.Empty,
            target.ValueKind == JsonValueKind.Object ? S(target, "displayName") : string.Empty,
            S(a, "result"));
    }

    /// <summary>A cloud security group or Microsoft 365 group, with owner and members in the same request (the group
    /// has no id yet).</summary>
    private async Task<ChangeResult> CreateGroupAsync(Change w, CancellationToken cancel)
    {
        var m365 = w.P("soort") == "M365";
        var content = new Dictionary<string, object>
        {
            ["displayName"] = w.P("displayName"),
            ["mailNickname"] = w.P("mailNickname"),
            ["mailEnabled"] = m365,
            ["securityEnabled"] = !m365,
            ["groupTypes"] = m365 ? new[] { "Unified" } : Array.Empty<string>(),
        };
        if (m365)
        {
            content["visibility"] = "Private";
        }

        if (w.P("description").Length > 0)
        {
            content["description"] = w.P("description");
        }

        string Ref(string id) => $"{GraphClient.Base}/directoryObjects/{id}";
        if (w.P("eigenaar").Length > 0)
        {
            content["owners@odata.bind"] = new[] { Ref(w.P("eigenaar")) };
        }

        var members = w.P("leden").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (members.Length > 0)
        {
            content["members@odata.bind"] = members.Select(Ref).ToArray();
        }

        var groupValue = await _client.PostWithResponseAsync("/groups", content, cancel);
        return ChangeResult.Ok(w, Texts.T.Format("result.created-id", S(groupValue, "id")));
    }

    /// <summary>A new cloud user (0.31): enabled, with the first password from the plan (never logged), to change at
    /// the first sign-in.</summary>
    private async Task<ChangeResult> CreateUserAsync(Change w, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(w.Secret))
        {
            return ChangeResult.Error(w, Texts.T["code.graph-connection.no-initial-password-in-the"]);
        }

        var content = new Dictionary<string, object>
        {
            ["accountEnabled"] = true,
            ["userPrincipalName"] = w.Target,
            ["displayName"] = w.P("displayName"),
            ["mailNickname"] = w.P("mailNickname"),
            ["passwordProfile"] = new Dictionary<string, object> { ["forceChangePasswordNextSignIn"] = true, ["password"] = w.Secret },
        };
        foreach (var field in new[] { "givenName", "surname", "jobTitle", "department", "officeLocation", "companyName", "city", "usageLocation" })
        {
            if (w.P(field) is { Length: > 0 } value)
            {
                content[field] = value;
            }
        }

        var user = await _client.PostWithResponseAsync("/users", content, cancel);
        var id = S(user, "id");
        if (id.Length > 0)
        {
            _created[w.Target] = id;
        }

        return ChangeResult.Ok(w, Texts.T.Format("code.graph-connection.created-id", id));
    }

    /// <summary>
    /// An action on an Intune device (R17). Wipe and fresh start ask Intune again whether the device is personal and
    /// refuse it then, also when the plan said otherwise; some actions only exist in the beta of Graph.
    /// </summary>
    private async Task<ChangeResult> RunDeviceAsync(Change w, CancellationToken cancel)
    {
        const string beta = "https://graph.microsoft.com/beta";
        var path = $"/deviceManagement/managedDevices/{Uri.EscapeDataString(w.Target)}";
        var action = w.P("actie");
        if (action is "wipe" or "freshStart")
        {
            var device = await _client.GetAsync($"{path}?$select=managedDeviceOwnerType", cancel);
            if (!string.Equals(S(device, "managedDeviceOwnerType"), "company", StringComparison.OrdinalIgnoreCase))
            {
                return ChangeResult.Error(w, Texts.T["device.safety.personal"]);
            }
        }

        switch (action)
        {
            case "sync": await _client.PostAsync($"{path}/syncDevice", null, cancel); break;
            case "reboot": await _client.PostAsync($"{path}/rebootNow", null, cancel); break;
            case "rotateLaps": await _client.PostAsync($"{beta}{path}/rotateLocalAdminPassword", null, cancel); break;
            case "rotateBitLocker": await _client.PostAsync($"{beta}{path}/rotateBitLockerKeys", null, cancel); break;
            case "defenderScan": await _client.PostAsync($"{path}/windowsDefenderScan", new Dictionary<string, object> { ["quickScan"] = true }, cancel); break;
            case "rename": await _client.PostAsync($"{beta}{path}/setDeviceName", new Dictionary<string, object> { ["deviceName"] = w.P("naam") }, cancel); break;
            case "retire": await _client.PostAsync($"{path}/retire", null, cancel); break;
            case "primaryUser":
                await _client.PostAsync($"{beta}{path}/users/$ref", new Dictionary<string, object> { ["@odata.id"] = $"{beta}/users/{Uri.EscapeDataString(w.P("gebruiker"))}" }, cancel);
                break;
            case "wipe": await _client.PostAsync($"{path}/wipe", new Dictionary<string, object> { ["keepEnrollmentData"] = false, ["keepUserData"] = false }, cancel); break;
            case "freshStart": await _client.PostAsync($"{path}/cleanWindowsDevice", new Dictionary<string, object> { ["keepUserData"] = false }, cancel); break;
            default: return ChangeResult.Error(w, Texts.T["device.action.unknown"]);
        }

        return ChangeResult.Ok(w, Texts.T["device.action.sent"]);
    }

    private async Task<ChangeResult> RunAsync(Change w, CancellationToken cancel)
    {
        try
        {
            if (w.ChangeAction is ChangeAction.GraphSetGroupAttributes or ChangeAction.GraphAddOwner or ChangeAction.GraphRemoveOwner)
            {
                return await RunGroupAsync(w, cancel);
            }

            if (w.ChangeAction == ChangeAction.GraphInviteGuest)
            {
                var body = new Dictionary<string, object>
                {
                    ["invitedUserEmailAddress"] = w.Target,
                    ["invitedUserDisplayName"] = w.P("naam"),
                    ["inviteRedirectUrl"] = "https://myapps.microsoft.com",
                    ["sendInvitationMessage"] = string.Equals(w.P("bericht"), "True", StringComparison.OrdinalIgnoreCase),
                };
                // R21: invite an existing guest again (its redemption reset; the account and its groups stay)
                if (string.Equals(w.P("opnieuw"), "True", StringComparison.OrdinalIgnoreCase) && w.P("id").Length > 0)
                {
                    body["resetRedemption"] = true;
                    body["invitedUser"] = new Dictionary<string, string> { ["id"] = w.P("id") };
                }

                var invitation = await _client.PostWithResponseAsync("/invitations", body, cancel);
                var id = invitation.TryGetProperty("invitedUser", out var iu) ? S(iu, "id") : string.Empty;
                if (id.Length > 0)
                {
                    _created[w.Target] = id;
                }

                return ChangeResult.Ok(w, Texts.T.Format("code.graph-connection.invited-id", id));
            }

            if (w.ChangeAction == ChangeAction.GraphCreateUser)
            {
                return await CreateUserAsync(w, cancel);
            }

            if (w.ChangeAction == ChangeAction.GraphCreateTemporaryAccessPass)
            {
                // The pass goes into Secret (shown once by the app), never into the result or the change log
                var tap = await _client.PostWithResponseAsync($"/users/{Uri.EscapeDataString(w.Target)}/authentication/temporaryAccessPassMethods",
                    new Dictionary<string, object>
                    {
                        ["lifetimeInMinutes"] = int.Parse(w.P("minuten"), System.Globalization.CultureInfo.InvariantCulture),
                        ["isUsableOnce"] = string.Equals(w.P("eenmalig"), "True", StringComparison.OrdinalIgnoreCase),
                    }, cancel);
                w.Secret = S(tap, "temporaryAccessPass");
                return ChangeResult.Ok(w, Texts.T.Format("result.tap-created", S(tap, "startDateTime")));
            }

            if (w.ChangeAction == ChangeAction.GraphSendMail)
            {
                var to = w.Target.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => new Dictionary<string, object> { ["emailAddress"] = new Dictionary<string, string> { ["address"] = a } }).ToArray();
                await _client.PostAsync("/me/sendMail", new Dictionary<string, object>
                {
                    ["message"] = new Dictionary<string, object>
                    {
                        ["subject"] = w.P("onderwerp"),
                        ["body"] = new Dictionary<string, string> { ["contentType"] = "Text", ["content"] = w.P("tekst") },
                        ["toRecipients"] = to,
                    },
                    ["saveToSentItems"] = true,
                }, cancel);
                return ChangeResult.Ok(w, "Verstuurd");
            }

            if (w.ChangeAction == ChangeAction.GraphCreateGroup)
            {
                return await CreateGroupAsync(w, cancel);
            }

            if (w.ChangeAction == ChangeAction.IntuneDeviceAction)
            {
                return await RunDeviceAsync(w, cancel);
            }

            // Compliance and the clean-up of devices (R17, U18, U25, U26)
            switch (w.ChangeAction)
            {
                case ChangeAction.IntuneCreateCompliancePolicy:
                {
                    var made = await _client.PostWithResponseAsync("/deviceManagement/deviceCompliancePolicies", JsonNode.Parse(w.P("json"))!, cancel);
                    return ChangeResult.Ok(w, Texts.T.Format("result.created-id", S(made, "id")));
                }

                case ChangeAction.GraphSetDeviceEnabled:
                {
                    // Asks Entra again: a hybrid joined device is managed in AD, an Autopilot device is never touched
                    var on = string.Equals(w.P("aan"), "True", StringComparison.OrdinalIgnoreCase);
                    if (!on && await DeviceCleanupRefusalAsync(w.Target, false, cancel) is { } why)
                    {
                        return ChangeResult.Error(w, why);
                    }

                    await _client.PatchAsync($"/devices/{Uri.EscapeDataString(w.Target)}", new Dictionary<string, object> { ["accountEnabled"] = on }, cancel);
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.GraphDeleteDevice:
                {
                    if (await DeviceCleanupRefusalAsync(w.Target, true, cancel) is { } why)
                    {
                        return ChangeResult.Error(w, why);
                    }

                    await _client.DeleteAsync($"/devices/{Uri.EscapeDataString(w.Target)}", cancel);
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.IntuneDeleteDevice:
                    await _client.DeleteAsync($"/deviceManagement/managedDevices/{Uri.EscapeDataString(w.Target)}", cancel);
                    return ChangeResult.Ok(w);
                case ChangeAction.GraphUpdatePolicy:
                {
                    // Only the policies of the advice (R16); the admin consent workflow is replaced whole (PUT)
                    if (!Core.Security.TenantAdvice.PolicyTargets.ContainsKey(w.Target))
                    {
                        return ChangeResult.Error(w, Texts.T["advice.policy.not-allowed"]);
                    }

                    var body = JsonNode.Parse(w.P("json"))!;
                    if (string.Equals(w.Target, "policies/adminConsentRequestPolicy", StringComparison.OrdinalIgnoreCase))
                    {
                        await _client.PutAsync("/" + w.Target, body, cancel);
                    }
                    else
                    {
                        await _client.PatchAsync("/" + w.Target, body, cancel);
                    }

                    return ChangeResult.Ok(w);
                }

                case ChangeAction.GraphRoleEligibility:
                    return await RoleEligibilityAsync(w, cancel);

                case ChangeAction.IntuneSetComplianceSettings:
                    // All the settings as read, with one changed: a complex property is sent whole
                    await _client.PatchAsync("/deviceManagement", new JsonObject { ["settings"] = JsonNode.Parse(w.P("json")) }, cancel);
                    return ChangeResult.Ok(w);
            }

            if (w.ChangeAction is ChangeAction.GraphCreateCaPolicy or ChangeAction.GraphUpdateCaPolicy or ChangeAction.GraphCreateCaLocation or ChangeAction.GraphUpdateCaLocation)
            {
                return await RunCaAsync(w, cancel);
            }

            if (w.ChangeAction == ChangeAction.GraphRemoveGuest)
            {
                // Safety: guest accounts only; a normal user goes through offboarding and is never deleted
                var guest = await _client.GetAsync($"/users/{Uri.EscapeDataString(w.Target)}?$select=id,userType,userPrincipalName", cancel);
                if (!string.Equals(S(guest, "userType"), "Guest", StringComparison.OrdinalIgnoreCase))
                {
                    return ChangeResult.Error(w, Texts.T["code.graph-connection.this-is-not-a-guest"]);
                }

                await _client.DeleteAsync($"/users/{S(guest, "id")}", cancel);
                return ChangeResult.Ok(w, Texts.T["code.graph-connection.removed-can-be-restored-for"]);
            }

            var userId = w.Target.Contains('@') ? await FindUserIdAsync(w.Target, cancel) : w.Target;
            if (userId is null)
            {
                return ChangeResult.Error(w, Texts.T.Format("code.graph-connection.does-not-exist-in-entra", w.Target));
            }

            switch (w.ChangeAction)
            {
                case ChangeAction.GraphAddGroupMember:
                    try
                    {
                        await _client.PostAsync($"/groups/{w.P("groepId")}/members/$ref",
                            new Dictionary<string, string> { ["@odata.id"] = $"{GraphClient.Base}/directoryObjects/{userId}" }, cancel);
                    }
                    catch (GraphError ex) when (ex.Status == HttpStatusCode.BadRequest && ex.Message.Contains("already exist", StringComparison.OrdinalIgnoreCase))
                    {
                        return ChangeResult.Ok(w, Texts.T["result.already-member"]);
                    }

                    return ChangeResult.Ok(w);

                case ChangeAction.GraphRemoveGroupMember:
                    await _client.DeleteAsync($"/groups/{w.P("groepId")}/members/{userId}/$ref", cancel);
                    return ChangeResult.Ok(w);

                case ChangeAction.GraphSetAttributes:
                    await _client.PatchAsync($"/users/{userId}", AttributesContent(w.Parameters), cancel);
                    return ChangeResult.Ok(w);

                case ChangeAction.GraphSetManager:
                    if (w.P("managerId").Length == 0)
                    {
                        try
                        {
                            await _client.DeleteAsync($"/users/{userId}/manager/$ref", cancel);
                        }
                        catch (GraphError ex) when (ex.Status == HttpStatusCode.NotFound)
                        {
                            return ChangeResult.Ok(w, Texts.T["code.graph-connection.already-had-no-manager"]);
                        }
                    }
                    else
                    {
                        await _client.PutAsync($"/users/{userId}/manager/$ref",
                            new Dictionary<string, string> { ["@odata.id"] = $"{GraphClient.Base}/users/{w.P("managerId")}" }, cancel);
                    }

                    return ChangeResult.Ok(w);

                case ChangeAction.GraphAssignLicence:
                {
                    var skuId = w.P("skuId");
                    if (skuId.Length == 0)
                    {
                        var skus = await _client.GetAllAsync("/subscribedSkus?$select=skuId,skuPartNumber", cancel);
                        skuId = skus.Where(s => string.Equals(S(s, "skuPartNumber"), w.P("sku"), StringComparison.OrdinalIgnoreCase))
                            .Select(s => S(s, "skuId")).FirstOrDefault() ?? string.Empty;
                        if (skuId.Length == 0)
                        {
                            return ChangeResult.Error(w, Texts.T.Format("result.licence-unknown", w.P("sku")));
                        }
                    }

                    await _client.PostAsync($"/users/{userId}/assignLicense", new
                    {
                        addLicenses = new[] { new { skuId, disabledPlans = Array.Empty<string>() } },
                        removeLicenses = Array.Empty<string>(),
                    }, cancel);
                    return ChangeResult.Ok(w);
                }

                case ChangeAction.GraphRemoveLicences:
                {
                    var u = await _client.GetAsync($"/users/{userId}?$select=assignedLicenses", cancel);
                    var ids = u.GetProperty("assignedLicenses").EnumerateArray().Select(l => S(l, "skuId")).ToArray();
                    // Only some licences (0.35, a duplicate plan); otherwise all
                    var only = w.P("skuIds").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (only.Length > 0)
                    {
                        ids = ids.Where(i => only.Contains(i, StringComparer.OrdinalIgnoreCase)).ToArray();
                    }

                    if (ids.Length == 0)
                    {
                        return ChangeResult.Ok(w, Texts.T["result.no-licences"]);
                    }

                    await _client.PostAsync($"/users/{userId}/assignLicense",
                        new { addLicenses = Array.Empty<object>(), removeLicenses = ids }, cancel);
                    return ChangeResult.Ok(w, Texts.T.Format("code.graph-connection.licence-s-removed", ids.Length));
                }

                case ChangeAction.GraphRevokeSessions:
                    await _client.PostAsync($"/users/{userId}/revokeSignInSessions", null, cancel);
                    return ChangeResult.Ok(w);

                default:
                    return ChangeResult.Error(w, Texts.T.Format("code.graph-connection.action-does-not-belong-to", w.ChangeAction));
            }
        }
        catch (Exception ex)
        {
            return ChangeResult.Error(w, ex.Message);
        }
    }

    /// <summary>Text values to JSON: "true"/"false" become booleans (e.g. accountEnabled), the rest stays
    /// text.</summary>
    internal static Dictionary<string, object?> AttributesContent(IReadOnlyDictionary<string, string> parameters)
        => parameters.ToDictionary(
            p => p.Key,
            p => p.Key == "businessPhones"
                ? (p.Value.Length == 0 ? Array.Empty<string>() : new[] { p.Value })
                : p.Value switch
            {
                "true" or "True" => true,
                "false" or "False" => (object?)false,
                "" => null,
                _ => p.Value,
            });

    private static string S(JsonElement e, string name)
        => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;

    private static bool B(JsonElement e, string name)
        => e.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.True;
}
