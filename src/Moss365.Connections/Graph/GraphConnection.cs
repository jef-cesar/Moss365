using System.Globalization;
using System.Net;
using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Connections.Graph;

/// <summary>Reads Entra ID via Microsoft Graph and runs Graph changes, with the sign-in of the administrator.</summary>
public sealed class GraphConnection : IGraphConnection, IExecutor
{
    private const string UserFields = "id,userPrincipalName,displayName,givenName,surname,mail,mailNickname,accountEnabled,userType,department,"
                                           + "jobTitle,officeLocation,companyName,city,usageLocation,createdDateTime,onPremisesSyncEnabled,"
                                           + "onPremisesSamAccountName,onPremisesDistinguishedName,assignedLicenses,businessPhones,mobilePhone";

    private const string GroupFields = "id,displayName,description,mail,mailEnabled,securityEnabled,groupTypes,membershipRule,"
                                       + "onPremisesSyncEnabled,onPremisesSamAccountName,assignedLicenses";

    private readonly GraphClient _client;
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
                progress?.Report("Entra: enkel de gewijzigde groepen...");
                var (items, link) = await ReadGroupDeltaAsync(baseline.DeltaLink, cancel);
                var (groups, member) = GraphDelta.Apply(baseline, items);
                progress?.Report($"Entra: {users.Count} gebruikers gelezen, {items.Count} groepswijzigingen toegepast ({groups.Count} groepen).");
                return new GraphData(users, groups, member, licences, link, Quick: true);
            }
            catch (GraphError ex)
            {
                // E.g. 410 Gone (the starting point is too old): then read everything
                progress?.Report($"Entra: wijzigingen niet beschikbaar ({ex.Message}); alle groepen lezen.");
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
        progress?.Report($"Entra: {users.Count} gebruikers en {allGroups.Count} groepen gelezen.");
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
        progress?.Report("Entra: licenties...");
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
            progress?.Report("Entra: laatste aanmelding niet beschikbaar, verder zonder.");
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
            progress?.Report("Entra: managers niet beschikbaar, verder zonder.");
        }

        return (users, licences);
    }

    private async Task<(List<CloudGroup> Groups, Dictionary<string, HashSet<string>> Membership)> ReadGroupsFullAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report("Entra: groepen...");
        var rawGroups = await _client.GetAllAsync($"/groups?$select={GroupFields}&$top=999", cancel);

        var membership = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var counter = 0;
        using var brake = new SemaphoreSlim(4);
        var tasks = rawGroups.Select(async g =>
        {
            var id = S(g, "id");
            await brake.WaitAsync(cancel);
            try
            {
                var members = await _client.GetAllAsync($"/groups/{id}/members/microsoft.graph.user?$select=id&$top=999", cancel);
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

    // ------------------------------------------------------------------ security

    public async Task<SecurityData?> ReadSecurityAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report("Entra: MFA-registraties...");
        IReadOnlyList<MfaRegistration> mfa = Array.Empty<MfaRegistration>();
        string? mfaMessage = null;
        try
        {
            var raw = await _client.GetAllAsync("/reports/authenticationMethods/userRegistrationDetails?$top=999", cancel);
            mfa = raw.Select(ToMfa).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            mfaMessage = "Niet leesbaar: je account heeft de rol Reports Reader, Security Reader of Global Reader nodig, en de tenant Entra ID P1 (" + ex.Message + ")";
        }

        progress?.Report("Entra: beheerrollen...");
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
            rolesMessage = "Niet leesbaar: " + ex.Message;
        }

        progress?.Report($"Entra: {mfa.Count} MFA-registraties en {roles.Count} rollidmaatschappen gelezen.");
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
        progress?.Report("Entra: Conditional Access-beleiden...");
        List<CaPolicy> policies;
        try
        {
            policies = (await _client.GetAllAsync("/identity/conditionalAccess/policies", cancel)).Select(ToCaPolicy).ToList();
        }
        catch (GraphError ex) when (ex.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return new CaData(Array.Empty<CaPolicy>(), Array.Empty<NamedLocation>(), Array.Empty<AuthenticationStrength>(),
                new Dictionary<string, string>(), null,
                "Niet leesbaar: de app-registratie heeft het recht Policy.Read.All nodig (Register-App.ps1 opnieuw uitvoeren) en je account "
                + "de rol Security Reader, Global Reader of Conditional Access Administrator (" + ex.Message + ")");
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

        progress?.Report($"Entra: {policies.Count} Conditional Access-beleiden en {locations.Count} benoemde locaties gelezen.");
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
            S(a, "ipAddress"), location, code, code == 0 ? string.Empty : reason, S(a, "clientAppUsed"), ca);
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
            return ChangeResult.Error(w, $"{member} bestaat niet in Entra.");
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
                return ChangeResult.Ok(w, "Was al eigenaar.");
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
        var content = System.Text.Json.Nodes.JsonNode.Parse(w.P("json")) ?? throw new InvalidOperationException("Geen JSON in de wijziging.");
        switch (w.ChangeAction)
        {
            case ChangeAction.GraphCreateCaPolicy:
            {
                var newValue = await _client.PostWithResponseAsync("/identity/conditionalAccess/policies", content, cancel);
                return ChangeResult.Ok(w, $"Gemaakt; id {S(newValue, "id")}");
            }

            case ChangeAction.GraphUpdateCaPolicy:
                await _client.PatchAsync($"/identity/conditionalAccess/policies/{Uri.EscapeDataString(w.Target)}", content, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GraphCreateCaLocation:
            {
                var newValue = await _client.PostWithResponseAsync("/identity/conditionalAccess/namedLocations", content, cancel);
                return ChangeResult.Ok(w, $"Gemaakt; id {S(newValue, "id")}");
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
        return ChangeResult.Ok(w, $"Aangemaakt; id {S(groupValue, "id")}");
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
                var invitation = await _client.PostWithResponseAsync("/invitations", new Dictionary<string, object>
                {
                    ["invitedUserEmailAddress"] = w.Target,
                    ["invitedUserDisplayName"] = w.P("naam"),
                    ["inviteRedirectUrl"] = "https://myapps.microsoft.com",
                    ["sendInvitationMessage"] = string.Equals(w.P("bericht"), "True", StringComparison.OrdinalIgnoreCase),
                }, cancel);
                var id = invitation.TryGetProperty("invitedUser", out var iu) ? S(iu, "id") : string.Empty;
                return ChangeResult.Ok(w, $"Uitgenodigd; id {id}");
            }

            if (w.ChangeAction == ChangeAction.GraphCreateGroup)
            {
                return await CreateGroupAsync(w, cancel);
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
                    return ChangeResult.Error(w, "Dit is geen gastaccount; de app verwijdert enkel gasten.");
                }

                await _client.DeleteAsync($"/users/{S(guest, "id")}", cancel);
                return ChangeResult.Ok(w, "Verwijderd; 30 dagen terug te zetten via Entra > Verwijderde gebruikers.");
            }

            var userId = w.Target.Contains('@') ? await FindUserIdAsync(w.Target, cancel) : w.Target;
            if (userId is null)
            {
                return ChangeResult.Error(w, $"{w.Target} bestaat (nog) niet in Entra.");
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
                        return ChangeResult.Ok(w, "Was al lid.");
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
                            return ChangeResult.Ok(w, "Had al geen manager.");
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
                            return ChangeResult.Error(w, $"Licentie {w.P("sku")} bestaat niet in deze tenant.");
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
                    if (ids.Length == 0)
                    {
                        return ChangeResult.Ok(w, "Had geen licenties.");
                    }

                    await _client.PostAsync($"/users/{userId}/assignLicense",
                        new { addLicenses = Array.Empty<object>(), removeLicenses = ids }, cancel);
                    return ChangeResult.Ok(w, $"{ids.Length} licentie(s) verwijderd.");
                }

                case ChangeAction.GraphRevokeSessions:
                    await _client.PostAsync($"/users/{userId}/revokeSignInSessions", null, cancel);
                    return ChangeResult.Ok(w);

                default:
                    return ChangeResult.Error(w, $"Actie {w.ChangeAction} hoort niet bij Graph.");
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
