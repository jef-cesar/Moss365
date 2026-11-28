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

    private const string Auth = "https://www.googleapis.com/auth/";

    /// <summary>Only reading: users, groups, organisational units, calendar resources, admin roles, the logs, and who
    /// signed in.</summary>
    public static IReadOnlyList<string> BaseScopes { get; } = new[]
    {
        "openid", "email",
        Auth + "admin.directory.user.readonly",
        Auth + "admin.directory.group.readonly",
        Auth + "admin.directory.orgunit.readonly",
        Auth + "admin.directory.resource.calendar.readonly",
        Auth + "admin.directory.rolemanagement.readonly",
        Auth + "admin.reports.audit.readonly",
    };

    public static IReadOnlyList<GooglePart> All { get; } = new GooglePart[]
    {
        new(Licences, new[] { Auth + "apps.licensing" }, "licensing.googleapis.com"),
        new(GroupSettings, new[] { Auth + "apps.groups.settings" }, "groupssettings.googleapis.com"),
        new(Alerts, new[] { Auth + "apps.alerts" }, "alertcenter.googleapis.com"),
    };

    /// <summary>The APIs the project needs (the script turns them all on; a part only uses its own when it is on).</summary>
    public static IReadOnlyList<string> Apis { get; } = new[] { "admin.googleapis.com" }.Concat(All.Select(p => p.Api)).ToList();

    public static bool IsOn(IEnumerable<string>? on, string part) => PermissionParts.IsOn(on, part);

    /// <summary>The scopes for a tenant: the reading ones and those of the parts that are on, in a fixed order.</summary>
    public static IReadOnlyList<string> Scopes(IEnumerable<string>? on)
        => BaseScopes.Concat(All.Where(p => IsOn(on, p.Key)).SelectMany(p => p.Scopes)).Distinct(StringComparer.Ordinal).ToList();
}
