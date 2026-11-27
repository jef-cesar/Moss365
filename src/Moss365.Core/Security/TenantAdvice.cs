using System.Text.Json.Nodes;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Setup;

namespace Moss365.Core.Security;

/// <summary>The settings of the tenant the advice for Microsoft 365 reads (R16); null where that could not be read.</summary>
public sealed record TenantSettingsReading
{
    public bool? SecurityDefaults { get; init; }

    public bool? UsersCreateApps { get; init; }

    public bool? UsersCreateTenants { get; init; }

    /// <summary>permissionGrantPoliciesAssigned of the default user role (the consent users may give themselves).</summary>
    public IReadOnlyList<string> ConsentPolicies { get; init; } = Array.Empty<string>();

    public string? GuestRoleId { get; init; }

    public string? AllowInvitesFrom { get; init; }

    /// <summary>The admin consent workflow as read (JSON), to put it back on undo.</summary>
    public string? ConsentWorkflowJson { get; init; }

    public bool? ConsentWorkflow { get; init; }

    /// <summary>The state per authentication method (id in lower case: sms, voice, fido2, microsoftauthenticator, ...).</summary>
    public IReadOnlyDictionary<string, string> Methods { get; init; } = new Dictionary<string, string>();

    /// <summary>sharingCapability of SharePoint and OneDrive.</summary>
    public string? Sharing { get; init; }

    public bool HasP1 { get; init; }

    public bool HasP2 { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>What the plans of the advice need beyond the data: the organisation and who is signed in.</summary>
public sealed record AdviceContext(OrganisationConfig Config, string? OwnUpn);

/// <summary>
/// The advice for Microsoft 365 that is carried out as a plan (R16, chapter 10): the settings of the tenant (MFA without
/// Security Defaults or Conditional Access, apps and consent, guests, authentication methods, sharing in SharePoint),
/// admin roles made eligible (PIM), shared mailboxes where signing in is allowed and the sign-ins of the emergency
/// accounts. Every write permission belongs to a part (U25): the plan and the engine refuse it when the part is off.
/// </summary>
public static class TenantAdvice
{
    public const string RestrictedGuests = "2af84b1e-32c8-42b7-82bc-daa82404023b";
    public const string LimitedGuests = "10dae51f-b6af-4016-8d66-8c2a99b929b3";
    public const string MemberGuests = "a0b1b346-4d3e-4e8b-98f8-753987be4970";

    // The kinds of fix on a line (CheckRow.Fix)
    public const string SecurityDefaultsOn = "security-defaults";
    public const string CaBaseline = "ca-baseline";
    public const string Apps = "apps";
    public const string Consent = "consent";
    public const string Guests = "guests";
    public const string StrongMethods = "strong-methods";
    public const string WeakMethods = "weak-methods";
    public const string Sharing = "sharing";
    public const string Pim = "pim";
    public const string SharedSignIn = "shared-signin";

    public static readonly IReadOnlyList<string> Fixes = new[] { SecurityDefaultsOn, CaBaseline, Apps, Consent, Guests, StrongMethods, WeakMethods, Sharing, Pim, SharedSignIn };

    /// <summary>The policies <see cref="ChangeAction.GraphUpdatePolicy"/> may change, with the part each needs (empty: none);
    /// the engine refuses any other.</summary>
    public static IReadOnlyDictionary<string, string> PolicyTargets { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["policies/identitySecurityDefaultsEnforcementPolicy"] = string.Empty,
        ["policies/authorizationPolicy"] = PermissionParts.TenantPolicies,
        ["policies/adminConsentRequestPolicy"] = PermissionParts.TenantPolicies,
        ["policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Sms"] = PermissionParts.AuthenticationMethods,
        ["policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Voice"] = PermissionParts.AuthenticationMethods,
        ["policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Fido2"] = PermissionParts.AuthenticationMethods,
        ["policies/authenticationMethodsPolicy/authenticationMethodConfigurations/MicrosoftAuthenticator"] = PermissionParts.AuthenticationMethods,
        ["admin/sharepoint/settings"] = PermissionParts.SharePointSettings,
    };

    /// <summary>The CA templates of the baseline (report-only first, as every template).</summary>
    public static readonly IReadOnlyList<string> BaselineTemplates = new[] { "mfa-iedereen", "verouderd-blokkeren", "beheerders-phishingbestendig", "gasten-mfa" };

    private static readonly string[] WeakMethodIds = { "sms", "voice" };

    private static Dictionary<string, string> Data(params (string Key, string Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The check of the settings of the tenant.</summary>
    public static SecurityCheck Check(TenantSettingsReading? r, InventoryData d)
    {
        var title = Texts.T["security.check.tenant-settings"];
        if (r is null)
        {
            return SecurityCheck.Unknown("m365-instellingen", title, Texts.T["code.security-checks.not-checked-yet-microsoft-graph"]);
        }

        var rows = new List<CheckRow>();

        // MFA for everyone: Security Defaults, or Conditional Access (needs Entra ID P1)
        var policies = d.ConditionalAccess?.Policies ?? Array.Empty<CaPolicy>();
        var on = policies.Count(p => p.Status == CaStatus.On);
        var report = policies.Count(p => p.Status == CaStatus.Report);
        if (r.SecurityDefaults == true)
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.mfa"], Texts.T["advice.tenant.defaults-on"], CheckStatus.Ok));
        }
        else if (on > 0)
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.mfa"], Texts.T.Format("advice.tenant.ca-on", on), CheckStatus.Ok));
        }
        else if (report > 0)
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.mfa"], Texts.T.Format("advice.tenant.ca-report", report), CheckStatus.Attention, "conditional-access"));
        }
        else if (r.SecurityDefaults == false)
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.mfa"], r.HasP1 ? Texts.T["advice.tenant.none-p1"] : Texts.T["advice.tenant.none"], CheckStatus.Problem)
            {
                Fix = r.HasP1 ? CaBaseline : SecurityDefaultsOn,
            });
        }

        // Apps and tenants made by users
        if (r.UsersCreateApps == true || r.UsersCreateTenants == true)
        {
            var what = new[] { r.UsersCreateApps == true ? Texts.T["advice.tenant.what-apps"] : null, r.UsersCreateTenants == true ? Texts.T["advice.tenant.what-tenants"] : null }
                .OfType<string>();
            rows.Add(new CheckRow(Texts.T["advice.tenant.apps"], Texts.T.Format("advice.tenant.users-create", string.Join(", ", what)), CheckStatus.Attention)
            {
                Fix = Apps,
                FixData = Data(("apps", r.UsersCreateApps == true ? "True" : "False"), ("tenants", r.UsersCreateTenants == true ? "True" : "False")),
            });
        }

        // Consent of users to apps, and the admin consent workflow
        var self = r.ConsentPolicies.Where(p => p.StartsWith("ManagePermissionGrantsForSelf.", StringComparison.OrdinalIgnoreCase)).ToList();
        if (self.Count > 0 || r.ConsentWorkflow == false)
        {
            var any = self.Any(p => p.EndsWith("-legacy", StringComparison.OrdinalIgnoreCase));
            rows.Add(new CheckRow(Texts.T["advice.tenant.consent"],
                (self.Count == 0 ? string.Empty : (any ? Texts.T["advice.tenant.consent-any"] : Texts.T["advice.tenant.consent-low"]) + " ")
                + (r.ConsentWorkflow == true ? string.Empty : Texts.T["advice.tenant.no-workflow"]),
                any ? CheckStatus.Problem : CheckStatus.Attention)
            {
                Fix = Consent,
                FixData = Data(("beleid", string.Join(";", r.ConsentPolicies)), ("workflow", r.ConsentWorkflowJson ?? string.Empty), ("workflowAan", r.ConsentWorkflow == true ? "True" : "False")),
            });
        }

        // Guests
        var guestsOpen = r.GuestRoleId is { } g && !string.Equals(g, RestrictedGuests, StringComparison.OrdinalIgnoreCase);
        var invitesOpen = r.AllowInvitesFrom is "everyone" or "adminsGuestInvitersAndAllMembers";
        if (guestsOpen || invitesOpen)
        {
            var parts = new List<string>();
            if (guestsOpen)
            {
                parts.Add(string.Equals(r.GuestRoleId, MemberGuests, StringComparison.OrdinalIgnoreCase) ? Texts.T["advice.tenant.guests-member"] : Texts.T["advice.tenant.guests-limited"]);
            }

            if (invitesOpen)
            {
                parts.Add(r.AllowInvitesFrom == "everyone" ? Texts.T["advice.tenant.invites-everyone"] : Texts.T["advice.tenant.invites-members"]);
            }

            rows.Add(new CheckRow(Texts.T["advice.tenant.guests"], string.Join("; ", parts),
                string.Equals(r.GuestRoleId, MemberGuests, StringComparison.OrdinalIgnoreCase) ? CheckStatus.Problem : CheckStatus.Attention)
            {
                Fix = Guests,
                FixData = Data(("rol", r.GuestRoleId ?? string.Empty), ("uitnodigen", r.AllowInvitesFrom ?? string.Empty)),
            });
        }

        // Authentication methods: SMS and voice still allowed (guided), passkeys and Authenticator not on
        var weakOn = WeakMethodIds.Where(m => r.Methods.TryGetValue(m, out var s) && s == "enabled").ToList();
        if (weakOn.Count > 0)
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.weak"], Texts.T.Format("advice.tenant.weak-on", string.Join(", ", weakOn.Select(MethodName))), CheckStatus.Attention)
            {
                Fix = WeakMethods,
                FixData = Data(("aan", string.Join(";", weakOn))),
            });
        }

        var strongOff = new[] { "fido2", "microsoftauthenticator" }.Where(m => r.Methods.TryGetValue(m, out var s) && s != "enabled").ToList();
        if (strongOff.Count == 2)
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.strong"], Texts.T["advice.tenant.strong-off"], CheckStatus.Attention) { Fix = StrongMethods });
        }

        // Links for everyone, without signing in
        if (r.Sharing == "externalUserAndGuestSharing")
        {
            rows.Add(new CheckRow(Texts.T["advice.tenant.sharing"], Texts.T["advice.tenant.sharing-anyone"], CheckStatus.Attention) { Fix = Sharing, FixData = Data(("was", r.Sharing)) });
        }

        var shown = rows.Count(x => x.Status != CheckStatus.Ok);
        var summary = shown == 0 ? Texts.T["advice.tenant.none-open"] : Texts.T.Format("code.security-checks.point-s-to-check", shown);
        if (r.Notes.Count > 0)
        {
            summary += " " + Texts.T.Format("advice.tenant.not-read", string.Join("; ", r.Notes));
        }

        return new SecurityCheck("m365-instellingen", title, rows.Select(x => x.Status).DefaultIfEmpty(CheckStatus.Ok).Max(), summary, rows);
    }

    public static string MethodName(string id) => id switch
    {
        "sms" => "SMS",
        "voice" => Texts.T["advice.method.voice"],
        "fido2" => Texts.T["advice.method.passkey"],
        "microsoftauthenticator" => "Microsoft Authenticator",
        _ => id,
    };

    /// <summary>Shared mailboxes whose account may still sign in (nobody should: the mailbox is used through permissions).</summary>
    public static SecurityCheck SharedMailboxes(InventoryData d)
    {
        var title = Texts.T["security.check.shared-sign-in"];
        if (d.Mailboxes.Count == 0)
        {
            return SecurityCheck.Unknown("gedeelde-postvakken", title, Texts.T["code.security-checks.not-checked-yet-exchange-online"]);
        }

        var users = d.EntraUsers.Where(u => u.Mail.Length > 0).GroupBy(u => u.Mail, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rows = new List<CheckRow>();
        foreach (var m in d.Mailboxes.Where(m => m.IsShared))
        {
            var u = users.GetValueOrDefault(m.Smtp) ?? d.EntraUsers.FirstOrDefault(e => string.Equals(e.UserPrincipalName, m.Smtp, StringComparison.OrdinalIgnoreCase));
            if (u is not { AccountEnabled: true })
            {
                continue;
            }

            var ad = u.OnPremisesSyncEnabled ? d.AdUsers.FirstOrDefault(a => string.Equals(a.DistinguishedName, u.OnPremisesDistinguishedName, StringComparison.OrdinalIgnoreCase)) : null;
            rows.Add(new CheckRow(m.Smtp, u.OnPremisesSyncEnabled ? Texts.T["advice.shared.sign-in-ad"] : Texts.T["advice.shared.sign-in"], CheckStatus.Attention)
            {
                Fix = SharedSignIn,
                FixData = Data(("id", u.Id), ("naam", m.DisplayName), ("adDn", ad?.DistinguishedName ?? string.Empty), ("gesynchroniseerd", u.OnPremisesSyncEnabled ? "True" : "False")),
            });
        }

        return new SecurityCheck("gedeelde-postvakken", title, rows.Count == 0 ? CheckStatus.Ok : CheckStatus.Attention,
            rows.Count == 0 ? Texts.T.Format("advice.shared.none", d.Mailboxes.Count(m => m.IsShared)) : Texts.T.Format("advice.shared.some", rows.Count), rows);
    }

    /// <summary>The sign-ins of the emergency accounts in the last 30 days, from the kept sign-ins (R18): each one should
    /// be explained.</summary>
    public static SecurityCheck Emergency(OrganisationConfig c, IReadOnlyList<KeptSignIn> kept, DateTimeOffset now)
    {
        var title = Texts.T["security.check.emergency"];
        var accounts = c.EmergencyAccounts.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (accounts.Count == 0)
        {
            return new SecurityCheck("noodaccounts", title, CheckStatus.Attention, Texts.T["advice.emergency.none-named"],
                new[] { new CheckRow(Texts.T["advice.emergency.accounts"], Texts.T["advice.emergency.none-named"], CheckStatus.Attention, "organisatie") });
        }

        if (!c.Retention.KeepLocalLogs)
        {
            return SecurityCheck.Unknown("noodaccounts", title, Texts.T["advice.emergency.logs-off"]);
        }

        var rows = kept.Where(k => k.Reasons.Contains("emergency") && k.SignIn.Time >= now.AddDays(-30))
            .OrderByDescending(k => k.SignIn.Time)
            .Select(k => new CheckRow(k.SignIn.Upn, Texts.T.Format("advice.emergency.sign-in", k.SignIn.Time.LocalDateTime.ToString("g", Texts.T.Culture), k.SignIn.App, k.SignIn.Ip,
                k.SignIn.Succeeded ? Texts.T["advice.emergency.succeeded"] : Texts.T["advice.emergency.failed"]), CheckStatus.Problem, "bewaarde-logboeken"))
            .ToList();
        return new SecurityCheck("noodaccounts", title, rows.Count == 0 ? CheckStatus.Ok : CheckStatus.Problem,
            rows.Count == 0 ? Texts.T.Format("advice.emergency.quiet", accounts.Count) : Texts.T.Format("advice.emergency.some", rows.Count), rows);
    }

    /// <summary>The lines of the check of admin roles about the accounts themselves: synchronised from AD, or the daily
    /// account with a mailbox; and a permanent critical role that PIM can make eligible.</summary>
    public static IReadOnlyList<CheckRow> RoleRows(IReadOnlyList<RoleAssignmentInfo> roles, InventoryData d, OrganisationConfig c, string? ownUpn)
    {
        var rows = new List<CheckRow>();
        var users = d.EntraUsers.Where(u => u.Id.Length > 0).ToDictionary(u => u.Id, StringComparer.OrdinalIgnoreCase);
        var emergency = c.EmergencyAccounts.Select(a => a.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var critical = CaTemplates.CriticalRoles.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var who in roles.Where(r => r.PrincipalId.Length > 0).GroupBy(r => r.PrincipalId, StringComparer.OrdinalIgnoreCase))
        {
            if (users.GetValueOrDefault(who.Key) is not { } u)
            {
                continue;
            }

            if (u.OnPremisesSyncEnabled)
            {
                rows.Add(new CheckRow(u.UserPrincipalName, Texts.T.Format("advice.roles.synced", string.Join(", ", who.Select(r => r.Role).Distinct())), CheckStatus.Attention));
            }
            else if (u.Mail.Length > 0 && u.Licences.Count > 0 && !emergency.Contains(u.UserPrincipalName))
            {
                rows.Add(new CheckRow(u.UserPrincipalName, Texts.T.Format("advice.roles.daily", string.Join(", ", who.Select(r => r.Role).Distinct())), CheckStatus.Attention));
            }
        }

        foreach (var r in roles.Where(r => r.Kind == "permanent" && critical.Contains(r.Role) && r.PrincipalId.Length > 0 && r.RoleId.Length > 0 && r.PrincipalType == "user"))
        {
            var upn = users.GetValueOrDefault(r.PrincipalId)?.UserPrincipalName ?? r.Principal;
            if (emergency.Contains(upn) || string.Equals(upn, ownUpn, StringComparison.OrdinalIgnoreCase))
            {
                continue; // an emergency account stays permanent; the own account is changed last, in Entra
            }

            rows.Add(r.ViaPim
                ? new CheckRow($"{upn} ({r.Role})", Texts.T["advice.roles.make-eligible"], CheckStatus.Attention)
                {
                    Fix = Pim,
                    FixData = Data(("rolId", r.RoleId), ("rol", r.Role), ("id", r.PrincipalId), ("upn", upn)),
                }
                : new CheckRow($"{upn} ({r.Role})", Texts.T["advice.roles.needs-p2"], CheckStatus.Attention));
        }

        return rows;
    }

    /// <summary>The plan for one line with a fix of this class (null when the fix is not one of them).</summary>
    public static PlanOutcome? Plan(CheckRow row, InventoryData d, AdviceContext? context)
    {
        if (row.Fix is not { } fix || !Fixes.Contains(fix))
        {
            return null;
        }

        var config = context?.Config ?? new OrganisationConfig();
        var parts = config.PermissionParts;
        var data = row.FixData ?? new Dictionary<string, string>();
        string P(string k) => data.TryGetValue(k, out var v) ? v : string.Empty;
        PlanOutcome Needs(string part) => PlanOutcome.Error(Texts.T.Format("advice.part-off", Texts.T[$"setup.part.{part}"]));

        switch (fix)
        {
            case SecurityDefaultsOn:
                return PlanOutcome.Of(new[]
                {
                    Policy("policies/identitySecurityDefaultsEnforcementPolicy", new JsonObject { ["isEnabled"] = true }, new JsonObject { ["isEnabled"] = false },
                        Texts.T["advice.tenant.plan-defaults"], Texts.T["advice.tenant.plan-defaults-warning"]),
                });

            case CaBaseline:
            {
                var plan = new List<Change>();
                var errors = new List<string>();
                foreach (var key in BaselineTemplates)
                {
                    var outcome = CaTemplates.Plan(key, new CaTemplateOptions(), d, config, context?.OwnUpn);
                    plan.AddRange(outcome.Plan);
                    errors.AddRange(outcome.Errors);
                }

                return PlanOutcome.Of(plan, errors);
            }

            case Apps:
                if (!PermissionParts.IsOn(parts, PermissionParts.TenantPolicies))
                {
                    return Needs(PermissionParts.TenantPolicies);
                }

                return PlanOutcome.Of(new[]
                {
                    Policy("policies/authorizationPolicy",
                        new JsonObject { ["defaultUserRolePermissions"] = new JsonObject { ["allowedToCreateApps"] = false, ["allowedToCreateTenants"] = false } },
                        new JsonObject { ["defaultUserRolePermissions"] = new JsonObject { ["allowedToCreateApps"] = P("apps") == "True", ["allowedToCreateTenants"] = P("tenants") == "True" } },
                        Texts.T["advice.tenant.plan-apps"], Texts.T["advice.tenant.plan-apps-warning"]),
                });

            case Consent:
            {
                if (!PermissionParts.IsOn(parts, PermissionParts.TenantPolicies))
                {
                    return Needs(PermissionParts.TenantPolicies);
                }

                var before = P("beleid").Split(';', StringSplitOptions.RemoveEmptyEntries);
                var after = before.Where(p => !p.StartsWith("ManagePermissionGrantsForSelf.", StringComparison.OrdinalIgnoreCase)).ToArray();
                var plan = new List<Change>();
                if (after.Length != before.Length)
                {
                    plan.Add(Policy("policies/authorizationPolicy",
                        new JsonObject { ["defaultUserRolePermissions"] = new JsonObject { ["permissionGrantPoliciesAssigned"] = Strings(after) } },
                        new JsonObject { ["defaultUserRolePermissions"] = new JsonObject { ["permissionGrantPoliciesAssigned"] = Strings(before) } },
                        Texts.T["advice.tenant.plan-consent"], Texts.T["advice.tenant.plan-consent-warning"]));
                }

                if (P("workflowAan") != "True")
                {
                    var own = d.EntraUsers.FirstOrDefault(u => string.Equals(u.UserPrincipalName, context?.OwnUpn, StringComparison.OrdinalIgnoreCase));
                    if (own is null)
                    {
                        return PlanOutcome.Of(plan, new[] { Texts.T["advice.tenant.no-reviewer"] });
                    }

                    var workflow = new JsonObject
                    {
                        ["isEnabled"] = true, ["notifyReviewers"] = true, ["remindersEnabled"] = true, ["requestDurationInDays"] = 30,
                        ["reviewers"] = new JsonArray { new JsonObject { ["query"] = $"/users/{own.Id}", ["queryType"] = "MicrosoftGraph" } },
                    };
                    var previous = P("workflow").Length > 0 && JsonNode.Parse(P("workflow")) is JsonObject read ? Without(read, "@odata.context", "id", "version") : new JsonObject { ["isEnabled"] = false };
                    plan.Add(Policy("policies/adminConsentRequestPolicy", workflow, previous,
                        Texts.T.Format("advice.tenant.plan-workflow", own.UserPrincipalName), Texts.T["advice.tenant.plan-workflow-warning"]));
                }

                return PlanOutcome.Of(plan);
            }

            case Guests:
                if (!PermissionParts.IsOn(parts, PermissionParts.TenantPolicies))
                {
                    return Needs(PermissionParts.TenantPolicies);
                }

                return PlanOutcome.Of(new[]
                {
                    Policy("policies/authorizationPolicy",
                        new JsonObject { ["guestUserRoleId"] = RestrictedGuests, ["allowInvitesFrom"] = "adminsAndGuestInviters" },
                        new JsonObject { ["guestUserRoleId"] = P("rol"), ["allowInvitesFrom"] = P("uitnodigen") },
                        Texts.T["advice.tenant.plan-guests"], Texts.T["advice.tenant.plan-guests-warning"]),
                });

            case StrongMethods:
                if (!PermissionParts.IsOn(parts, PermissionParts.AuthenticationMethods))
                {
                    return Needs(PermissionParts.AuthenticationMethods);
                }

                return PlanOutcome.Of(new[]
                {
                    Method("MicrosoftAuthenticator", "microsoftAuthenticatorAuthenticationMethodConfiguration", true),
                    Method("Fido2", "fido2AuthenticationMethodConfiguration", true),
                });

            case WeakMethods:
            {
                if (!PermissionParts.IsOn(parts, PermissionParts.AuthenticationMethods))
                {
                    return Needs(PermissionParts.AuthenticationMethods);
                }

                // Guided (U16): first who has no other method yet; they would lose their second factor
                var only = OnlyWeak(d);
                if (only.Count > 0)
                {
                    return PlanOutcome.Error(Texts.T.Format("advice.tenant.weak-first", only.Count, string.Join(", ", only.Take(10)) + (only.Count > 10 ? ", ..." : string.Empty)));
                }

                return PlanOutcome.Of(P("aan").Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(m => m == "sms" ? Method("Sms", "smsAuthenticationMethodConfiguration", false) : Method("Voice", "voiceAuthenticationMethodConfiguration", false)).ToList());
            }

            case Sharing:
                if (!PermissionParts.IsOn(parts, PermissionParts.SharePointSettings))
                {
                    return Needs(PermissionParts.SharePointSettings);
                }

                return PlanOutcome.Of(new[]
                {
                    Policy("admin/sharepoint/settings", new JsonObject { ["sharingCapability"] = "externalUserSharingOnly" }, new JsonObject { ["sharingCapability"] = P("was") },
                        Texts.T["advice.tenant.plan-sharing"], Texts.T["advice.tenant.plan-sharing-warning"]),
                });

            case Pim:
                if (!PermissionParts.IsOn(parts, PermissionParts.Pim))
                {
                    return Needs(PermissionParts.Pim);
                }

                return PlanOutcome.Of(new[]
                {
                    new Change
                    {
                        TargetSystem = TargetSystem.Graph,
                        ChangeAction = ChangeAction.GraphRoleEligibility,
                        Target = P("id"),
                        TargetName = P("upn"),
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["rolId"] = P("rolId"), ["rol"] = P("rol"), ["naar"] = "eligible" },
                        Previous = new(StringComparer.OrdinalIgnoreCase) { ["naar"] = "active" },
                        Description = Texts.T.Format("advice.roles.plan", P("rol"), P("upn")),
                        CurrentValue = Texts.T["advice.roles.active"],
                        NewValue = Texts.T["advice.roles.eligible"],
                        Warning = Texts.T["advice.roles.plan-warning"],
                    },
                });

            case SharedSignIn:
                if (P("gesynchroniseerd") == "True")
                {
                    if (P("adDn").Length == 0)
                    {
                        return PlanOutcome.Error(Texts.T.Format("advice.shared.ad-not-read", row.Object));
                    }

                    return PlanOutcome.Of(new[]
                    {
                        new Change
                        {
                            TargetSystem = TargetSystem.ActiveDirectory,
                            ChangeAction = ChangeAction.AdDisableAccount,
                            Target = P("adDn"),
                            TargetName = row.Object,
                            Description = Texts.T.Format("advice.shared.plan", row.Object),
                            Warning = Texts.T["advice.shared.plan-warning"],
                        },
                    });
                }

                return PlanOutcome.Of(new[]
                {
                    new Change
                    {
                        TargetSystem = TargetSystem.Graph,
                        ChangeAction = ChangeAction.GraphSetAttributes,
                        Target = P("id"),
                        TargetName = row.Object,
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
                        Previous = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "true" },
                        Description = Texts.T.Format("advice.shared.plan", row.Object),
                        CurrentValue = Texts.T["device.enabled"],
                        NewValue = Texts.T["device.disabled"],
                        Warning = Texts.T["advice.shared.plan-warning"],
                    },
                });

            default:
                return null;
        }
    }

    /// <summary>The accounts whose only second factor is SMS or a phone call (they lose it when those are turned off).</summary>
    public static IReadOnlyList<string> OnlyWeak(InventoryData d)
    {
        var weak = new[] { "mobilePhone", "alternateMobilePhone", "officePhone", "sms", "voice" };
        return (d.Security?.Mfa ?? Array.Empty<MfaRegistration>())
            .Where(m =>
            {
                var methods = m.Methods.Where(x => !x.Equals("password", StringComparison.OrdinalIgnoreCase) && !x.Equals("email", StringComparison.OrdinalIgnoreCase)).ToList();
                return methods.Count > 0 && methods.All(x => weak.Any(w => x.Contains(w, StringComparison.OrdinalIgnoreCase)));
            })
            .Select(m => m.Upn).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The engine refuses a policy that is not in <see cref="PolicyTargets"/>.</summary>
    public static string? Refusal(Change w)
        => w.ChangeAction == ChangeAction.GraphUpdatePolicy && !PolicyTargets.ContainsKey(w.Target) ? Texts.T["advice.policy.not-allowed"] : null;

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject Without(JsonObject o, params string[] names)
    {
        var copy = (JsonObject)o.DeepClone();
        foreach (var n in names)
        {
            copy.Remove(n);
        }

        return copy;
    }

    private static Change Policy(string target, JsonObject json, JsonObject previous, string description, string? warning) => new()
    {
        TargetSystem = TargetSystem.Graph,
        ChangeAction = ChangeAction.GraphUpdatePolicy,
        Target = target,
        TargetName = description,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["json"] = json.ToJsonString() },
        Previous = new(StringComparer.OrdinalIgnoreCase) { ["json"] = previous.ToJsonString() },
        Description = description,
        Warning = warning,
    };

    private static Change Method(string id, string type, bool on)
    {
        JsonObject State(bool value) => new() { ["@odata.type"] = "#microsoft.graph." + type, ["state"] = value ? "enabled" : "disabled" };
        return Policy($"policies/authenticationMethodsPolicy/authenticationMethodConfigurations/{id}", State(on), State(!on),
            Texts.T.Format(on ? "advice.tenant.plan-method-on" : "advice.tenant.plan-method-off", MethodName(id.ToLowerInvariant())),
            on ? Texts.T["advice.tenant.plan-method-on-warning"] : Texts.T["advice.tenant.plan-method-off-warning"]);
    }
}
