using Moss365.Core.Localization;

namespace Moss365.Core.Setup;

/// <summary>A part of Google Workspace (U40): the only scope of its API can also write, so it is asked only for a
/// tenant that turns the part on, although Moss365 only reads with it in G1.</summary>
public sealed record GooglePart(string Key, IReadOnlyList<string> Scopes, string Api)
{
    public string Name => Texts.T[$"setup.part.{Key}"];

    public string Explanation => Texts.T[$"setup.part.{Key}.help"];
}

/// <summary>The scopes Moss365 asks Google for (G1): the reading scopes always, those of a part only when it is on.</summary>
public static class GoogleParts
{
    public const string Licences = "google-licences";
    public const string GroupSettings = "google-group-settings";
    public const string Alerts = "google-alerts";

    /// <summary>Gmail settings of someone else (U34): forwarding, automatic reply, delegation, filters, signature. Google
    /// only allows them through a service account with domain-wide delegation; its key is kept encrypted in the tenant
    /// database.</summary>
    public const string GmailSettings = "gmail-settings";

    private const string Auth = "https://www.googleapis.com/auth/";

    /// <summary>
    /// The scopes of every sign-in (G2, as the base permissions of Microsoft 365): users (with their sessions and tokens),
    /// groups and calendar resources read and write, organisational units, admin roles and the logs only read, and who
    /// signed in. A change is always a plan with a confirmation, the role of the app and the change log.
    /// </summary>
    public static IReadOnlyList<string> BaseScopes { get; } = new[]
    {
        "openid", "email",
        Auth + "admin.directory.user",
        Auth + "admin.directory.user.security",
        Auth + "admin.directory.group",
        Auth + "admin.directory.orgunit.readonly",
        Auth + "admin.directory.resource.calendar",
        Auth + "admin.directory.rolemanagement.readonly",
        Auth + "admin.reports.audit.readonly",
    };

    /// <summary>The scopes the service account of the part Gmail settings gets in the domain-wide delegation (U34).</summary>
    public static IReadOnlyList<string> GmailScopes { get; } = new[] { Auth + "gmail.settings.basic", Auth + "gmail.settings.sharing" };

    public static IReadOnlyList<GooglePart> All { get; } = new GooglePart[]
    {
        new(Licences, new[] { Auth + "apps.licensing" }, "licensing.googleapis.com"),
        new(GroupSettings, new[] { Auth + "apps.groups.settings" }, "groupssettings.googleapis.com"),
        new(Alerts, new[] { Auth + "apps.alerts" }, "alertcenter.googleapis.com"),
        // The scopes of this part go to the service account, never to the sign-in of the administrator
        new(GmailSettings, Array.Empty<string>(), "gmail.googleapis.com"),
    };

    /// <summary>The engine refuses a change of a Google part that is off (as U25), also when a plan was made by hand.</summary>
    public static string? Refusal(Changes.Change w, IEnumerable<string>? on)
    {
        var part = w.ChangeAction switch
        {
            Changes.ChangeAction.GoogleAssignLicence or Changes.ChangeAction.GoogleRemoveLicence => Licences,
            Changes.ChangeAction.GoogleUpdateGroupSettings => GroupSettings,
            _ when w.ChangeAction.ToString().StartsWith("Gmail", StringComparison.Ordinal) => GmailSettings,
            _ => null,
        };
        return part is not null && !IsOn(on, part) ? Texts.T.Format("advice.part-off", Texts.T[$"setup.part.{part}"]) : null;
    }

    /// <summary>The APIs the project needs (the script turns them all on; a part only uses its own when it is on).</summary>
    public static IReadOnlyList<string> Apis { get; } = new[] { "admin.googleapis.com" }.Concat(All.Select(p => p.Api)).ToList();

    public static bool IsOn(IEnumerable<string>? on, string part) => PermissionParts.IsOn(on, part);

    /// <summary>The scopes for a tenant: the reading ones and those of the parts that are on, in a fixed order.</summary>
    public static IReadOnlyList<string> Scopes(IEnumerable<string>? on)
        => BaseScopes.Concat(All.Where(p => IsOn(on, p.Key)).SelectMany(p => p.Scopes)).Distinct(StringComparer.Ordinal).ToList();
}
