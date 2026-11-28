using System.Net.Mail;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Linking;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Google;

/// <summary>The fields of a Google user as they have to become (G2); null = do not change.</summary>
public sealed record GoogleUserDesired(
    string? GivenName = null,
    string? FamilyName = null,
    string? OrgUnitPath = null,
    string? Department = null,
    string? EmployeeId = null,
    bool? Suspended = null);

/// <summary>
/// Where the Google users get their fields (U33): the platform at the start of their chain, the feed that carries them
/// (the provisioning of Entra or Directory Sync from AD), and whether the password comes along. A field that follows is
/// never written in Google itself: the synchronisation would overwrite it.
/// </summary>
public sealed record GoogleSource(Platform? FollowsFrom, GoogleFeed Feed, bool PasswordFollows)
{
    public static GoogleSource Itself { get; } = new(null, GoogleFeed.Itself, false);
}

/// <summary>The form of a new user in Google Workspace (G2).</summary>
public sealed class GoogleNewUserRequest
{
    public string GivenName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string OrgUnitPath { get; set; } = "/";
    public string Department { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
}

/// <summary>A new Google user as proposed: the address, the first password (only in the plan) and the plan.</summary>
public sealed record GoogleNewUserProposal(string Email, string Password, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<Change> Plan)
{
    public bool CanBeExecuted => Errors.Count == 0 && Plan.Count > 0;
}

/// <summary>
/// Plans for the users of Google Workspace (G2): a new user, the fields (names, department, employee id, the
/// organisational unit, suspended), aliases, a new password, signing out and revoking the tokens of apps. Every field
/// change keeps the previous values, so it can be undone; a user is never deleted. What follows from another platform
/// (the source of truth, U33) is refused here and changed at the source.
/// </summary>
public static class GooglePlans
{
    public const string GivenName = "givenName";
    public const string FamilyName = "familyName";
    public const string OrgUnitPath = "orgUnitPath";
    public const string Department = "department";
    public const string EmployeeId = "employeeId";
    public const string Suspended = "suspended";

    /// <summary>The fields in the order of the editor.</summary>
    public static IReadOnlyList<string> Fields { get; } = new[] { GivenName, FamilyName, Department, EmployeeId, OrgUnitPath, Suspended };

    private static readonly string[] FromEntra = { GivenName, FamilyName, Department, EmployeeId, Suspended };
    private static readonly string[] FromAd = { GivenName, FamilyName, Department, EmployeeId, Suspended, OrgUnitPath };

    public static GoogleSource Source(TenantSystems systems, InventoryData data, SourceSettings settings)
    {
        var from = SourceOfTruth.FollowsFrom(Platform.GoogleWorkspace, TruthKind.Users, systems, data, settings);
        return from is null
            ? GoogleSource.Itself
            : new GoogleSource(from, settings.GoogleUsers, settings.GoogleUsers == GoogleFeed.FromAd && settings.GooglePasswordSync == true);
    }

    /// <summary>
    /// The fields the synchronisation carries: the provisioning of Entra carries the names, the department, the employee
    /// id and whether the account is on; Directory Sync also the organisational unit (by its rules).
    /// </summary>
    public static IReadOnlyCollection<string> Followed(GoogleSource source)
        => source.FollowsFrom is null ? Array.Empty<string>() : source.Feed == GoogleFeed.FromAd ? FromAd : FromEntra;

    public static string FieldName(string field) => Texts.T["google.field." + field.ToLowerInvariant()];

    /// <summary>The domains of the users: the domain of the tenant first, then the most used.</summary>
    public static IReadOnlyList<string> Domains(InventoryData data, string? configured)
    {
        var used = data.GoogleUsers.Select(u => DomainOf(u.PrimaryEmail)).Where(d => d.Length > 0)
            .GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => g.Key);
        return new[] { (configured ?? string.Empty).Trim().ToLowerInvariant() }.Where(d => d.Length > 0).Concat(used).Distinct().ToList();
    }

    /// <summary>The organisational units, the root first.</summary>
    public static IReadOnlyList<string> OrgUnits(InventoryData data)
        => new[] { "/" }.Concat(data.GoogleOrgUnits.Select(o => o.Path).Where(p => p.Length > 0).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Who has this address in Google already (a user or a group, as primary address or alias), or null.</summary>
    public static string? Holder(InventoryData data, string address)
    {
        bool Same(string a) => string.Equals(a, address, StringComparison.OrdinalIgnoreCase);
        return data.GoogleUsers.FirstOrDefault(u => Same(u.PrimaryEmail) || u.Aliases.Any(Same)) is { } user
            ? user.PrimaryEmail
            : data.GoogleGroups.FirstOrDefault(g => Same(g.Email) || g.Aliases.Any(Same))?.Email;
    }

    public static GoogleNewUserProposal NewUser(InventoryData data, OrganisationConfig config, GoogleNewUserRequest request,
        IReadOnlyList<string> domains, GoogleSource source, string? password = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        password ??= NameRules.InitialPassword();
        var given = request.GivenName.Trim();
        var family = request.FamilyName.Trim();
        if (given.Length == 0 || family.Length == 0)
        {
            errors.Add(Texts.T["code.plan-builder.first-name-and-last-name"]);
        }
        else if (given.Length > 60 || family.Length > 60)
        {
            errors.Add(Texts.T.Format("attribute.invalid", FieldName(given.Length > 60 ? GivenName : FamilyName)));
        }

        var domain = (request.Domain.Length > 0 ? request.Domain : domains.FirstOrDefault() ?? string.Empty).Trim().ToLowerInvariant();
        if (domain.Length == 0)
        {
            errors.Add(Texts.T["code.cloud-user-plans.choose-the-domain-of-the"]);
        }
        else if (!domains.Contains(domain))
        {
            errors.Add(Texts.T.Format("google.domain.unknown", domain));
        }

        var ou = request.OrgUnitPath.Trim() is { Length: > 0 } chosen ? chosen : "/";
        if (!OrgUnits(data).Contains(ou, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(Texts.T.Format("google.org-unit.unknown", ou));
        }

        if (source.FollowsFrom is { } from)
        {
            // Directory Sync and the provisioning of Entra may suspend or remove what is not in their source
            warnings.Add(Texts.T.Format("google.new.follows", Platforms.Name(from)));
        }

        var local = NamingSettings.Local(given, family, config.Naming.LocalPart);
        var email = string.Empty;
        if (local.Length > 0 && domain.Length > 0)
        {
            email = $"{local}@{domain}";
            if (Holder(data, email) is not null)
            {
                var free = Enumerable.Range(2, 8).Select(n => $"{local}{n}@{domain}").FirstOrDefault(a => Holder(data, a) is null);
                if (free is null)
                {
                    errors.Add(Texts.T.Format("google.new.taken", email));
                }
                else
                {
                    warnings.Add(Texts.T.Format("google.new.other-address", email, free));
                    email = free;
                }
            }
        }

        if (errors.Count > 0)
        {
            return new GoogleNewUserProposal(email, password, errors, warnings, Array.Empty<Change>());
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [GivenName] = given,
            [FamilyName] = family,
            [OrgUnitPath] = ou,
        };
        if (request.Department.Trim() is { Length: > 0 } department) parameters[Department] = department;
        if (request.EmployeeId.Trim() is { Length: > 0 } employee) parameters[EmployeeId] = employee;
        var change = new Change
        {
            TargetSystem = TargetSystem.Google,
            ChangeAction = ChangeAction.GoogleCreateUser,
            Target = email,
            TargetName = NameRules.ViewName(given, family),
            Parameters = parameters,
            Description = Texts.T.Format("google.plan.create-user", email, ou),
            NewValue = string.Join("; ", parameters.Select(p => $"{FieldName(p.Key)}: {p.Value}")),
            Secret = password,
        };
        return new GoogleNewUserProposal(email, password, errors, warnings, new[] { change });
    }

    /// <summary>The fields that change, as one change with the previous values; a field that follows is refused.</summary>
    public static PlanOutcome Edit(InventoryData data, GoogleUser user, GoogleUserDesired desired, GoogleSource source)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = new List<string>();
        var becomes = new List<string>();
        var errors = new List<string>();
        var followed = Followed(source);
        void Field(string key, string current, string? wanted, int max, string? shownNow = null, string? shownNew = null)
        {
            if (wanted is null || string.Equals(current, wanted.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            var value = wanted.Trim();
            if (followed.Contains(key))
            {
                errors.Add(Texts.T.Format("google.follows", FieldName(key), Platforms.Name(source.FollowsFrom!.Value)));
                return;
            }

            if (value.Length > max || (value.Length == 0 && key is GivenName or FamilyName))
            {
                errors.Add(Texts.T.Format("attribute.invalid", FieldName(key)));
                return;
            }

            parameters[key] = value;
            previous[key] = current;
            now.Add($"{FieldName(key)}: {Shown(shownNow ?? current)}");
            becomes.Add($"{FieldName(key)}: {Shown(shownNew ?? value)}");
        }

        Field(GivenName, user.GivenName, desired.GivenName, 60);
        Field(FamilyName, user.FamilyName, desired.FamilyName, 60);
        Field(Department, user.Department, desired.Department, 256);
        Field(EmployeeId, user.EmployeeId, desired.EmployeeId, 256);
        if (desired.OrgUnitPath is { } ou && !string.Equals(ou.Trim(), user.OrgUnitPath, StringComparison.OrdinalIgnoreCase))
        {
            if (OrgUnits(data).FirstOrDefault(o => string.Equals(o, ou.Trim(), StringComparison.OrdinalIgnoreCase)) is { } known)
            {
                Field(OrgUnitPath, user.OrgUnitPath, known, 1024);
            }
            else
            {
                errors.Add(Texts.T.Format("google.org-unit.unknown", ou));
            }
        }

        if (desired.Suspended is { } suspend && suspend != user.Suspended)
        {
            Field(Suspended, user.Suspended.ToString(), suspend.ToString(), 5, Status(user.Suspended), Status(suspend));
        }

        if (errors.Count > 0 || parameters.Count == 0)
        {
            return PlanOutcome.Of(Array.Empty<Change>(), errors);
        }

        var change = new Change
        {
            TargetSystem = TargetSystem.Google,
            ChangeAction = ChangeAction.GoogleUpdateUser,
            Target = user.Id,
            TargetName = user.PrimaryEmail,
            Parameters = parameters,
            Previous = previous,
            Description = parameters.Count == 1 && parameters.ContainsKey(Suspended)
                ? Texts.T.Format(desired.Suspended == true ? "google.plan.suspend" : "google.plan.unsuspend", user.PrimaryEmail)
                : Texts.T.Format("google.plan.update-user", user.PrimaryEmail),
            CurrentValue = string.Join("; ", now),
            NewValue = string.Join("; ", becomes),
            Warning = desired.Suspended == true && parameters.ContainsKey(Suspended) ? Texts.T["google.plan.suspend-warning"] : null,
        };
        return PlanOutcome.Of(new[] { change });
    }

    public static PlanOutcome AddAlias(InventoryData data, GoogleUser user, string alias, IReadOnlyList<string> domains)
    {
        var address = alias.Trim().ToLowerInvariant();
        if (!MailAddress.TryCreate(address, out var mail) || !string.Equals(mail.Address, address, StringComparison.OrdinalIgnoreCase))
        {
            return PlanOutcome.Error(Texts.T.Format("google.alias.invalid", alias));
        }

        if (!domains.Contains(DomainOf(address)))
        {
            return PlanOutcome.Error(Texts.T.Format("google.domain.unknown", DomainOf(address)));
        }

        if (Holder(data, address) is { } holder)
        {
            return PlanOutcome.Error(Texts.T.Format("google.alias.taken", address, holder));
        }

        return PlanOutcome.Of(new[] { Step(user, ChangeAction.GoogleAddAlias, Texts.T.Format("google.plan.add-alias", address, user.PrimaryEmail), ("alias", address)) });
    }

    public static Change RemoveAlias(GoogleUser user, string alias)
        => Step(user, ChangeAction.GoogleRemoveAlias, Texts.T.Format("google.plan.remove-alias", alias, user.PrimaryEmail), ("alias", alias));

    /// <summary>A new password (only in <see cref="Change.Secret"/>), refused when Directory Sync carries the passwords.</summary>
    public static PlanOutcome Password(GoogleUser user, string password, bool changeAtNextLogin, GoogleSource source)
    {
        if (source.PasswordFollows && source.FollowsFrom is { } from)
        {
            return PlanOutcome.Error(Texts.T.Format("google.password.follows", Platforms.Name(from)));
        }

        if (password.Length is < 8 or > 100)
        {
            return PlanOutcome.Error(Texts.T["google.password.length"]);
        }

        var change = Step(user, ChangeAction.GoogleSetPassword, Texts.T.Format("google.plan.password", user.PrimaryEmail), ("changeAtNextLogin", changeAtNextLogin.ToString()));
        change.Secret = password;
        return PlanOutcome.Of(new[] { change });
    }

    public static Change SignOut(GoogleUser user)
        => Step(user, ChangeAction.GoogleSignOut, Texts.T.Format("google.plan.sign-out", user.PrimaryEmail));

    public static Change RevokeTokens(GoogleUser user)
        => Step(user, ChangeAction.GoogleRevokeTokens, Texts.T.Format("google.plan.revoke-tokens", user.PrimaryEmail), warning: Texts.T["google.plan.revoke-tokens-warning"]);

    private static Change Step(GoogleUser user, ChangeAction action, string description, params (string Key, string Value)[] parameters)
        => Step(user, action, description, null, parameters);

    private static Change Step(GoogleUser user, ChangeAction action, string description, string? warning, params (string Key, string Value)[] parameters) => new()
    {
        TargetSystem = TargetSystem.Google,
        ChangeAction = action,
        Target = user.Id,
        TargetName = user.PrimaryEmail,
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
        Description = description,
        Warning = warning,
    };

    private static string Status(bool suspended) => Texts.T[suspended ? "google.user.suspended" : "google.user.active"];

    private static string Shown(string value) => value.Length > 0 ? value : Texts.T["edit-user.empty"];

    private static string DomainOf(string address) => address.Contains('@') ? address[(address.IndexOf('@') + 1)..].ToLowerInvariant() : string.Empty;
}
