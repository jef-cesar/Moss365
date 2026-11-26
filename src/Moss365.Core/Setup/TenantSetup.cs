using Moss365.Core.Localization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Permissions;

namespace Moss365.Core.Setup;

/// <summary>What the app registration "Moss365" must look like in every tenant (what Register-App.ps1 does, R5).</summary>
public static class AppManifest
{
    public const string Name = "Moss365";
    public const string RedirectUri = "http://localhost";
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";
    public const string ExchangeAppId = "00000002-0000-0ff1-ce00-000000000000";

    /// <summary>
    /// The public client of Microsoft Graph PowerShell (Connect-MgGraph, decision U6): the wizard signs in with it,
    /// because the app registration of Moss365 does not exist yet.
    /// </summary>
    public const string GraphPowerShellClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public const string Notes = "Management app Moss365 (github.com/jef-cesar/Moss365). Delegated permissions; assigned administrators only.";

    /// <summary>The delegated Microsoft Graph permissions of the app (the same list as Register-App.ps1).</summary>
    public static IReadOnlyList<string> GraphScopes { get; } = new[]
    {
        "User.ReadWrite.All", "Group.ReadWrite.All", "GroupMember.ReadWrite.All", "Directory.Read.All", "AuditLog.Read.All",
        "Policy.Read.All", "Calendars.Read.Shared", "OnPremDirectorySynchronization.Read.All",
        "Policy.ReadWrite.ConditionalAccess", "Application.Read.All",
        "UserAuthenticationMethod.ReadWrite.All", "BitLockerKey.Read.All", "DeviceLocalCredential.Read.All", "Device.Read.All", "Mail.Send",
        "SecurityEvents.Read.All", "RoleManagement.Read.Directory", "IdentityRiskyUser.Read.All",
        "DeviceManagementManagedDevices.Read.All", "Reports.Read.All", "SharePointTenantSettings.Read.All",
        "ServiceHealth.Read.All", "ServiceMessage.Read.All",
    };

    /// <summary>Exchange Online: so that Exchange uses the same sign-in as Graph.</summary>
    public static IReadOnlyList<string> ExchangeScopes { get; } = new[] { "Exchange.Manage" };

    /// <summary>What the wizard itself needs (once, with an Application or Global Administrator).</summary>
    public static IReadOnlyList<string> WizardScopes { get; } = new[]
    {
        "Application.ReadWrite.All", "DelegatedPermissionGrant.ReadWrite.All", "AppRoleAssignment.ReadWrite.All",
        "User.Read.All", "Group.ReadWrite.All", "Directory.Read.All", "Policy.Read.All",
    };

    /// <summary>The explanation of a profile, as description of its app role and group.</summary>
    public static string Explanation(Role role) => role switch
    {
        Role.Reader => "Search and view, statistics; changes nothing.",
        Role.Helpdesk => "Passwords, unlocking, members of normal groups, inviting guests, exporting.",
        Role.Administrator => "All changes, also privileged accounts and groups; scripts.",
        Role.Auditor => "View, security, sign-ins and the full change log; changes nothing.",
        _ => "The settings of Moss365 (organisation, choice lists).",
    };

    /// <summary>The profiles the administrator who sets up the tenant gets.</summary>
    public static IReadOnlyList<Role> FirstAdministratorRoles { get; } = new[] { Role.Administrator, Role.AppAdmin };
}

/// <summary>Finding the tenant ID from a domain (Microsoft's OpenID metadata, no sign-in needed).</summary>
public static class TenantLookup
{
    public static bool IsTenantId(string? value) => Guid.TryParse(value?.Trim(), out _);

    /// <summary>A domain or tenant ID from what was typed (an e-mail address, a URL), or null when it cannot be
    /// one.</summary>
    public static string? Clean(string? input)
    {
        var s = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (IsTenantId(s))
        {
            return Guid.Parse(s).ToString();
        }

        if (s.Contains("://", StringComparison.Ordinal))
        {
            s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        }

        s = s.Split('/')[0];
        if (s.Contains('@'))
        {
            s = s[(s.LastIndexOf('@') + 1)..];
        }

        s = s.Trim('.');
        return s.Length is > 2 and < 254 && s.Contains('.') && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') ? s : null;
    }

    public static string MetadataUrl(string domainOrId) => $"https://login.microsoftonline.com/{Uri.EscapeDataString(domainOrId)}/v2.0/.well-known/openid-configuration";

    /// <summary>The tenant ID in the OpenID metadata (issuer https://login.microsoftonline.com/{id}/v2.0).</summary>
    public static string? TenantIdFrom(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var field in new[] { "issuer", "token_endpoint" })
            {
                if (doc.RootElement.TryGetProperty(field, out var v) && v.GetString() is { } url)
                {
                    var id = url.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(IsTenantId);
                    if (id is not null)
                    {
                        return Guid.Parse(id).ToString();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}

/// <summary>Found or not: the tenant ID, or why not.</summary>
public sealed record TenantLookupResult(string? TenantId, string? Error);

/// <summary>The domain of this PC and its domain controller (logon server), when it is joined to a domain.</summary>
public sealed record LocalDomain(string Domain, string? Controller);

public sealed record ServicePrincipalInfo(string Id, string AppId, string DisplayName, bool AssignmentRequired, IReadOnlyDictionary<string, string> Scopes);

/// <summary>One entry of requiredResourceAccess (type Scope = delegated, Role = application).</summary>
public sealed record ResourceAccess(string ResourceAppId, string Id, string Type);

public sealed record AppRoleInfo(string Id, string Value, string DisplayName, string Description, bool Enabled, IReadOnlyList<string> AllowedMemberTypes);

public sealed record ApplicationInfo(
    string ObjectId, string AppId, string DisplayName, string SignInAudience, bool FallbackPublicClient,
    IReadOnlyList<string> RedirectUris, IReadOnlyList<ResourceAccess> RequiredAccess, IReadOnlyList<AppRoleInfo> AppRoles);

public sealed record GrantInfo(string Id, string ResourceId, string ConsentType, string Scope);

public sealed record AssignmentInfo(string PrincipalId, string AppRoleId);

public sealed record DirectoryPrincipal(string Id, string Name, string Upn = "");

/// <summary>What the wizard read from the tenant, with the sign-in of the wizard.</summary>
public sealed record TenantSetupReading(
    string TenantId,
    string OrganisationName,
    IReadOnlyList<string> VerifiedDomains,
    bool? SyncEnabled,
    DateTimeOffset? LastSync,
    DirectoryPrincipal SignedIn,
    ServicePrincipalInfo? GraphSp,
    ServicePrincipalInfo? ExchangeSp,
    ApplicationInfo? App,
    ServicePrincipalInfo? AppSp,
    IReadOnlyList<GrantInfo> Grants,
    IReadOnlyList<AssignmentInfo> Assignments,
    IReadOnlyDictionary<string, string> Groups);

/// <summary>Reads a tenant for the wizard and runs its plan, with the sign-in of Microsoft Graph PowerShell.</summary>
public interface ITenantSetupConnection : IExecutor
{
    string? SignedInAs { get; }

    Task<TenantSetupReading> ReadAsync(IProgress<string>? progress, CancellationToken cancel);
}

public enum SetupStatus
{
    InOrder,
    Missing,
    Attention,
    Unknown,
}

/// <summary>One line of the overview: what the wizard checked and what it found.</summary>
public sealed record SetupItem(string Step, string Name, SetupStatus Status, string Explanation);

/// <summary>What is in order, what is missing, and the plan that fixes what is missing.</summary>
public sealed record SetupAssessment(IReadOnlyList<SetupItem> Items, IReadOnlyList<Change> Plan, string? ClientId)
{
    public bool Ready => Plan.Count == 0 && ClientId is not null && Items.All(i => i.Status != SetupStatus.Missing);
}

/// <summary>Choices in the wizard.</summary>
public sealed record SetupChoices(bool CreateGroups = true, bool AssignSignedIn = true);

/// <summary>
/// Compares a tenant with what Moss365 needs and makes the plan for the difference: the app registration, the
/// enterprise app with "assignment required", admin consent, the profiles as app roles, a group per profile and the
/// profiles of the administrator who sets it up. Nothing is changed here: the plan goes through the preview.
/// </summary>
public static class TenantSetup
{
    public static string AppStep => Localization.Texts.T["setup.step.app"];
    public static string ProfilesStep => Localization.Texts.T["setup.step.profiles"];
    public static string TenantStep => Localization.Texts.T["setup.step.tenant"];

    public static SetupAssessment Assess(TenantSetupReading r, SetupChoices? choices = null)
    {
        choices ??= new SetupChoices();
        var items = new List<SetupItem>();
        var plan = new List<Change>();

        items.Add(new(TenantStep, r.OrganisationName.Length > 0 ? r.OrganisationName : r.TenantId, SetupStatus.InOrder,
            r.VerifiedDomains.Count > 0 ? Localization.Texts.T.Format("setup.tenant.domains", r.TenantId, string.Join(", ", r.VerifiedDomains)) : Localization.Texts.T.Format("setup.tenant", r.TenantId)));
        items.Add(r.SyncEnabled switch
        {
            true => new(TenantStep, Texts.T["code.tenant-setup.hybrid"], SetupStatus.InOrder,
                r.LastSync is { } l ? Localization.Texts.T.Format("setup.hybrid.synced", l.LocalDateTime) : Localization.Texts.T["setup.hybrid"]),
            false => new(TenantStep, Texts.T["code.tenant-setup.cloud-only"], SetupStatus.InOrder, Texts.T["code.tenant-setup.no-synchronisation-with-ad-the"]),
            _ => new(TenantStep, Texts.T["code.tenant-setup.hybrid"], SetupStatus.Unknown, Texts.T["code.tenant-setup.cannot-read-whether-ad-is"]),
        });

        if (r.GraphSp is null)
        {
            items.Add(new(AppStep, "Microsoft Graph", SetupStatus.Missing, Texts.T["code.tenant-setup.microsoft-graph-is-not-in"]));
            return new SetupAssessment(items, plan, r.App?.AppId);
        }

        var wanted = new List<ResourceAccess>();
        foreach (var scope in AppManifest.GraphScopes)
        {
            if (r.GraphSp.Scopes.TryGetValue(scope, out var id))
            {
                wanted.Add(new ResourceAccess(AppManifest.GraphAppId, id, "Scope"));
            }
            else
            {
                items.Add(new(AppStep, scope, SetupStatus.Attention, Texts.T["code.tenant-setup.this-permission-does-not-exist"]));
            }
        }

        if (r.ExchangeSp is null)
        {
            items.Add(new(AppStep, "Exchange Online", SetupStatus.Attention, Texts.T["code.tenant-setup.exchange-online-is-not-in"]));
        }
        else
        {
            foreach (var scope in AppManifest.ExchangeScopes)
            {
                if (r.ExchangeSp.Scopes.TryGetValue(scope, out var id))
                {
                    wanted.Add(new ResourceAccess(AppManifest.ExchangeAppId, id, "Scope"));
                }
            }
        }

        // 1. The app registration
        Change? createApp = null;
        if (r.App is null)
        {
            createApp = Graph(ChangeAction.GraphCreateApplication, AppManifest.Name, Texts.T.Format("code.tenant-setup.create-app-registration-public-client", AppManifest.Name, wanted.Count, PermissionTable.AppRoles.Count),
                parameters: ("json", NewApplication(wanted).ToJsonString()));
            plan.Add(createApp);
            items.Add(new(AppStep, AppManifest.Name, SetupStatus.Missing, Texts.T["code.tenant-setup.does-not-exist-yet-the"]));
        }
        else
        {
            items.Add(new(AppStep, AppManifest.Name, SetupStatus.InOrder, Texts.T.Format("code.tenant-setup.found-client-id", r.App.AppId)));
            var update = UpdateApplication(r.App, wanted, items);
            if (update is not null)
            {
                plan.Add(update);
            }
        }

        // 2. The enterprise app: only assigned people may sign in
        Change? createSp = null;
        if (r.AppSp is null)
        {
            createSp = Graph(ChangeAction.GraphCreateServicePrincipal, AppManifest.Name, Texts.T["code.tenant-setup.create-the-enterprise-application-with"],
                dependentOf: createApp?.Id);
            plan.Add(createSp);
            items.Add(new(AppStep, Texts.T["setup.enterprise-app"], SetupStatus.Missing, Texts.T["code.tenant-setup.does-not-exist-yet"]));
        }
        else if (!r.AppSp.AssignmentRequired)
        {
            plan.Add(Graph(ChangeAction.GraphUpdateServicePrincipal, r.AppSp.Id, Texts.T["code.tenant-setup.turn-on-assignment-required-only"],
                current: Texts.T["code.tenant-setup.everyone-in-the-tenant-can"], newValue: Texts.T["code.tenant-setup.assigned-only"], previous: ("json", "{\"appRoleAssignmentRequired\":false}"),
                parameters: ("json", "{\"appRoleAssignmentRequired\":true}")));
            items.Add(new(AppStep, Texts.T["code.tenant-setup.assignment-required"], SetupStatus.Missing, Texts.T["code.tenant-setup.off-everyone-in-the-tenant"]));
        }
        else
        {
            items.Add(new(AppStep, Texts.T["setup.enterprise-app"], SetupStatus.InOrder, Texts.T["code.tenant-setup.found-assignment-required"]));
        }

        // 3. Admin consent, per API
        foreach (var (sp, scopes) in new[] { (r.GraphSp, AppManifest.GraphScopes), (r.ExchangeSp, AppManifest.ExchangeScopes) })
        {
            if (sp is null)
            {
                continue;
            }

            var grant = r.AppSp is null ? null : r.Grants.FirstOrDefault(g => g.ResourceId == sp.Id && g.ConsentType == "AllPrincipals");
            var has = (grant?.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var missing = scopes.Where(s => sp.Scopes.ContainsKey(s) && !has.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count == 0)
            {
                items.Add(new(AppStep, Localization.Texts.T.Format("setup.consent", sp.DisplayName), SetupStatus.InOrder, Texts.T["code.tenant-setup.admin-consent-for-all-permissions"]));
                continue;
            }

            var scope = string.Join(' ', has.Concat(missing));
            plan.Add(Graph(ChangeAction.GraphGrantConsent, AppManifest.Name, Texts.T.Format("setup.consent.plan", sp.DisplayName, string.Join(", ", missing)),
                dependentOf: createSp?.Id, current: grant?.Scope ?? string.Empty, newValue: scope, previous: grant is null ? null : ("scope", grant.Scope),
                parameters: new[] { ("resourceId", sp.Id), ("scope", scope), ("grantId", grant?.Id ?? string.Empty) }));
            items.Add(new(AppStep, Localization.Texts.T.Format("setup.consent", sp.DisplayName), SetupStatus.Missing, Texts.T.Format("code.tenant-setup.missing-for-permission-s", missing.Count)));
        }

        // 4. A group per profile, assigned to its app role
        var assigned = r.AppSp is null ? new HashSet<(string, string)>() : r.Assignments.Select(a => (a.PrincipalId.ToLowerInvariant(), a.AppRoleId.ToLowerInvariant())).ToHashSet();
        foreach (var (value, role, groupName, roleId) in PermissionTable.AppRoles)
        {
            Change? createGroup = null;
            if (!r.Groups.TryGetValue(groupName, out var groupId))
            {
                if (!choices.CreateGroups)
                {
                    items.Add(new(ProfilesStep, groupName, SetupStatus.Attention, Texts.T["code.tenant-setup.does-not-exist-without-a"]));
                    continue;
                }

                createGroup = Graph(ChangeAction.GraphCreateGroup, groupName, Texts.T.Format("code.tenant-setup.create-group-security-cloud", groupName), targetName: groupName,
                    parameters: new[] { ("displayName", groupName), ("mailNickname", groupName), ("soort", "Beveiliging"), ("description", $"Profile {role} in Moss365. {AppManifest.Explanation(role)}") });
                plan.Add(createGroup);
            }

            if (groupId is not null && assigned.Contains((groupId.ToLowerInvariant(), roleId)))
            {
                items.Add(new(ProfilesStep, groupName, SetupStatus.InOrder, Texts.T.Format("code.tenant-setup.linked-to-the-profile-who", role)));
                continue;
            }

            plan.Add(Graph(ChangeAction.GraphAssignAppRole, groupId ?? groupName, Texts.T.Format("code.tenant-setup.link-group-to-the-profile", groupName, role, value),
                targetName: groupName, dependentOf: createGroup?.Id ?? createSp?.Id, warning: Texts.T["code.tenant-setup.linking-a-group-to-an"],
                parameters: new[] { ("appRoleId", roleId), ("kind", "group") }));
            items.Add(new(ProfilesStep, groupName, SetupStatus.Missing, groupId is null ? Texts.T["code.tenant-setup.does-not-exist-yet-and"] : Texts.T["code.tenant-setup.exists-but-is-not-linked"]));
        }

        // 5. The administrator who sets it up: Administrator and AppAdmin
        if (choices.AssignSignedIn && r.SignedIn.Id.Length > 0)
        {
            foreach (var role in AppManifest.FirstAdministratorRoles)
            {
                var (value, _, _, roleId) = PermissionTable.AppRoles.First(a => a.Role == role);
                if (assigned.Contains((r.SignedIn.Id.ToLowerInvariant(), roleId)))
                {
                    continue;
                }

                plan.Add(Graph(ChangeAction.GraphAssignAppRole, r.SignedIn.Id, Texts.T.Format("code.tenant-setup.give-the-profile", r.SignedIn.Upn, role, value),
                    targetName: r.SignedIn.Name, dependentOf: createSp?.Id, parameters: new[] { ("appRoleId", roleId), ("kind", "user") }));
            }
        }

        return new SetupAssessment(items, plan, r.App?.AppId);
    }

    private static Change Graph(ChangeAction action, string target, string description, Guid? dependentOf = null, string? targetName = null,
        string? current = null, string? newValue = null, (string Key, string Value)? previous = null, string? warning = null,
        params (string Key, string Value)[] parameters)
    {
        var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["appName"] = AppManifest.Name };
        foreach (var (k, v) in parameters)
        {
            p[k] = v;
        }

        return new Change
        {
            TargetSystem = TargetSystem.Graph,
            ChangeAction = action,
            Target = target,
            TargetName = targetName ?? (target == AppManifest.Name ? AppManifest.Name : string.Empty),
            Description = description,
            Parameters = p,
            DependentOf = dependentOf,
            CurrentValue = current,
            NewValue = newValue,
            Warning = warning,
            Previous = previous is { } x ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [x.Key] = x.Value } : null,
        };
    }

    internal static JsonObject NewApplication(IReadOnlyList<ResourceAccess> wanted) => new()
    {
        ["displayName"] = AppManifest.Name,
        ["signInAudience"] = "AzureADMyOrg",
        ["isFallbackPublicClient"] = true,
        ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(AppManifest.RedirectUri) },
        ["requiredResourceAccess"] = RequiredAccess(wanted),
        ["appRoles"] = new JsonArray(ProfileRoles().Select(Json).ToArray<JsonNode?>()),
        ["notes"] = AppManifest.Notes,
    };

    /// <summary>The profiles as app roles (fixed ids, the same as Register-App.ps1).</summary>
    public static IReadOnlyList<AppRoleInfo> ProfileRoles() => PermissionTable.AppRoles
        .Select(a => new AppRoleInfo(a.Id, a.Value, $"Moss365 {a.Role}", AppManifest.Explanation(a.Role), true, new[] { "User" }))
        .ToList();

    private static JsonObject Json(AppRoleInfo a) => new()
    {
        ["id"] = a.Id,
        ["value"] = a.Value,
        ["displayName"] = a.DisplayName,
        ["description"] = a.Description,
        ["isEnabled"] = a.Enabled,
        ["allowedMemberTypes"] = new JsonArray(a.AllowedMemberTypes.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
    };

    private static JsonArray RequiredAccess(IEnumerable<ResourceAccess> access) => new(access
        .GroupBy(a => a.ResourceAppId, StringComparer.OrdinalIgnoreCase)
        .Select(g => (JsonNode?)new JsonObject
        {
            ["resourceAppId"] = g.Key,
            ["resourceAccess"] = new JsonArray(g.DistinctBy(a => (a.Id.ToLowerInvariant(), a.Type)).Select(a => (JsonNode?)new JsonObject { ["id"] = a.Id, ["type"] = a.Type }).ToArray()),
        }).ToArray());

    /// <summary>The fields of an existing app registration that differ, as one change (null when all is in order).</summary>
    private static Change? UpdateApplication(ApplicationInfo app, IReadOnlyList<ResourceAccess> wanted, List<SetupItem> items)
    {
        var patch = new JsonObject();
        var previous = new JsonObject();
        var what = new List<string>();

        if (!app.RedirectUris.Contains(AppManifest.RedirectUri, StringComparer.OrdinalIgnoreCase))
        {
            patch["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(app.RedirectUris.Append(AppManifest.RedirectUri).Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()) };
            previous["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray(app.RedirectUris.Select(u => (JsonNode?)JsonValue.Create(u)).ToArray()) };
            what.Add(Localization.Texts.T.Format("setup.app.address", AppManifest.RedirectUri));
        }

        if (!app.FallbackPublicClient)
        {
            patch["isFallbackPublicClient"] = true;
            previous["isFallbackPublicClient"] = false;
            what.Add(Texts.T["code.tenant-setup.public-client"]);
        }

        var has = app.RequiredAccess.Select(a => (a.ResourceAppId.ToLowerInvariant(), a.Id.ToLowerInvariant())).ToHashSet();
        var missing = wanted.Where(w => !has.Contains((w.ResourceAppId.ToLowerInvariant(), w.Id.ToLowerInvariant()))).ToList();
        if (missing.Count > 0)
        {
            patch["requiredResourceAccess"] = RequiredAccess(app.RequiredAccess.Concat(missing));
            previous["requiredResourceAccess"] = RequiredAccess(app.RequiredAccess);
            what.Add(Texts.T.Format("code.tenant-setup.permission-s", missing.Count));
            items.Add(new(AppStep, Texts.T["code.tenant-setup.permissions"], SetupStatus.Missing, Texts.T.Format("code.tenant-setup.permission-s-missing-in-the", missing.Count)));
        }
        else
        {
            items.Add(new(AppStep, Texts.T["code.tenant-setup.permissions"], SetupStatus.InOrder, Texts.T["code.tenant-setup.all-permissions-are-in-the"]));
        }

        var roles = ProfileRoles();
        var missingRoles = roles.Where(p => !app.AppRoles.Any(a => string.Equals(a.Id, p.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        var off = app.AppRoles.Where(a => roles.Any(p => string.Equals(a.Id, p.Id, StringComparison.OrdinalIgnoreCase)) && !a.Enabled).ToList();
        if (missingRoles.Count > 0)
        {
            patch["appRoles"] = new JsonArray(app.AppRoles.Concat(missingRoles).Select(Json).ToArray<JsonNode?>());
            previous["appRoles"] = new JsonArray(app.AppRoles.Select(Json).ToArray<JsonNode?>());
            what.Add(Texts.T.Format("code.tenant-setup.profile-s", missingRoles.Count));
            items.Add(new(ProfilesStep, Texts.T["code.tenant-setup.app-roles"], SetupStatus.Missing, Localization.Texts.T.Format("setup.roles.missing", string.Join(", ", missingRoles.Select(m => m.Value)))));
        }
        else
        {
            items.Add(new(ProfilesStep, Texts.T["code.tenant-setup.app-roles"], off.Count > 0 ? SetupStatus.Attention : SetupStatus.InOrder,
                off.Count > 0 ? Localization.Texts.T.Format("setup.roles.off", string.Join(", ", off.Select(o => o.Value))) : Texts.T["code.tenant-setup.all-profiles-are-app-roles"]));
        }

        if (patch.Count == 0)
        {
            return null;
        }

        return Graph(ChangeAction.GraphUpdateApplication, app.ObjectId, Localization.Texts.T.Format("setup.app.update", AppManifest.Name, string.Join(", ", what)),
            targetName: app.DisplayName, previous: ("json", previous.ToJsonString()), parameters: ("json", patch.ToJsonString()));
    }
}
