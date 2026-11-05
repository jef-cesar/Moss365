using Moss365.Core.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Inventory;

/// <summary>State of a Conditional Access policy.</summary>
public enum CaStatus
{
    On,
    Off,

    /// <summary>Report-only: Entra measures what the policy would do, but enforces nothing.</summary>
    Report,
}

/// <summary>
/// One Conditional Access policy, translated from Graph. Ids of users, groups and roles stay ids; the screens look up
/// the names. Special values as in Graph: "All", "None", "GuestsOrExternalUsers", "Office365", "MicrosoftAdminPortals",
/// "AllTrusted".
/// </summary>
public sealed record CaPolicy
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public CaStatus Status { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Changed { get; init; }

    // Who
    public IReadOnlyList<string> UsersIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UsersOff { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GroupsIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> GroupsOff { get; init; } = Array.Empty<string>();

    /// <summary>Roles as roleTemplateId (as in Graph).</summary>
    public IReadOnlyList<string> RolesIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RolesOff { get; init; } = Array.Empty<string>();

    public bool GuestsIn { get; init; }
    public bool GuestsOff { get; init; }

    // What
    public IReadOnlyList<string> AppsIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AppsOff { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UserActions { get; init; } = Array.Empty<string>();

    // Conditions
    public IReadOnlyList<string> ClientApps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PlatformsIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PlatformsOff { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LocationsIn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LocationsOff { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SignInRisk { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UsersRisk { get; init; } = Array.Empty<string>();
    public bool DeviceFilter { get; init; }

    /// <summary>Transfer methods, e.g. "deviceCodeFlow", "authenticationTransfer".</summary>
    public IReadOnlyList<string> Transfer { get; init; } = Array.Empty<string>();

    // Consequence
    /// <summary>"OR" (one of) or "AND" (all).</summary>
    public string Operator { get; init; } = "OR";

    /// <summary>Built-in controls: block, mfa, compliantDevice, domainJoinedDevice, approvedApplication,
    /// compliantApplication, passwordChange.</summary>
    public IReadOnlyList<string> Checks { get; init; } = Array.Empty<string>();

    public string? StrengthId { get; init; }
    public string? StrengthName { get; init; }
    public bool TermsOfUse { get; init; }

    // Session
    public string? SignInFrequency { get; init; }
    public string? PersistentBrowser { get; init; }

    /// <summary>The policy as Graph returned it (for backup, comparing and restoring); empty when it does not come from
    /// Graph.</summary>
    public string Json { get; init; } = string.Empty;

    [JsonIgnore]
    public bool Blocks => Checks.Contains("block", StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool AsksMfa => Checks.Contains("mfa", StringComparer.OrdinalIgnoreCase) || StrengthId is not null;

    /// <summary>Phishing-resistant MFA (the built-in strength of Microsoft).</summary>
    [JsonIgnore]
    public bool AsksPhishingResistant => StrengthId == CaData.StrengthPhishingResistant;
}

/// <summary>A named location: IP ranges or countries.</summary>
public sealed record NamedLocation(string Id, string Name, bool IsIp, bool Trusted, IReadOnlyList<string> Values, bool UnknownCountries = false)
{
    /// <summary>The location as Graph returned it; empty when it does not come from Graph.</summary>
    public string Json { get; init; } = string.Empty;

    [JsonIgnore]
    public bool ContainsBelgium => !IsIp && Values.Contains("BE", StringComparer.OrdinalIgnoreCase);
}

/// <summary>An authentication strength (which combinations of methods are enough).</summary>
public sealed record AuthenticationStrength(string Id, string Name, bool BuiltIn, IReadOnlyList<string> Combinations);

/// <summary>
/// Everything about Conditional Access in the tenant. A message = (partly) not readable. GroupMembers: for every group
/// in a policy, all users who are members of it (also via a group in the group); null = not read.
/// </summary>
public sealed record CaData(
    IReadOnlyList<CaPolicy> Policies,
    IReadOnlyList<NamedLocation> Locations,
    IReadOnlyList<AuthenticationStrength> Strengths,
    IReadOnlyDictionary<string, string> Roles,
    bool? SecurityDefaults,
    string? Message = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GroupMembers = null)
{
    public const string RoleGlobalAdmin = "62e90394-69f5-4237-9190-012177145e10";
    public const string StrengthPhishingResistant = "00000000-0000-0000-0000-000000000004";

    public string RoleName(string templateId) => Roles.TryGetValue(templateId, out var n) ? n : templateId;

    public NamedLocation? Location(string id) => Locations.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One entry from the Entra audit log about Conditional Access: who changed which policy.</summary>
public sealed record CaAuditEntry(DateTimeOffset Time, string Who, string Activity, string TargetId, string TargetName, string Result);

/// <summary>How a policy turned out for one sign-in (appliedConditionalAccessPolicies).</summary>
public sealed record CaApplication(string PolicyId, string Name, string Result);

/// <summary>Saves Conditional Access as JSON in the inventory folder (entra-ca.json).</summary>
public static class CaStorage
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Converters = { new LegacyEnumConverter() },
    }.WithLegacyNames();

    public static string ToJson(CaData ca) => JsonSerializer.Serialize(ca, Options);

    /// <summary>Null when the file is empty or broken: Conditional Access is then simply "not loaded".</summary>
    public static CaData? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CaData>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
