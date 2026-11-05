namespace Moss365.Core.Inventory;

/// <summary>MFA registration of one Entra user (report userRegistrationDetails).</summary>
public sealed record MfaRegistration(
    string UserId,
    string Upn,
    bool MfaRegistered,
    bool MfaPossible,
    IReadOnlyList<string> Methods,
    string DefaultMethod,
    bool IsAdministrator);

/// <summary>One member of an (active) Entra admin role. RoleTemplateId is the same id as in Conditional
/// Access.</summary>
public sealed record RoleMember(string Role, string MemberId, string MemberName, string MemberUpn, string MemberKind, string RoleTemplateId = "");

/// <summary>
/// Security data from Entra. A message means that part could not be read (permissions, licence); the list is then empty
/// and the app shows "unknown", not "in order".
/// </summary>
public sealed record SecurityData(
    IReadOnlyList<MfaRegistration> Mfa,
    IReadOnlyList<RoleMember> Roles,
    string? MfaMessage = null,
    string? RolesMessage = null)
{
    public bool MfaKnown => MfaMessage is null;
    public bool RolesKnown => RolesMessage is null;

    public MfaRegistration? MfaOf(string? entraId)
        => entraId is null ? null : Mfa.FirstOrDefault(m => string.Equals(m.UserId, entraId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<RoleMember> RolesOf(string? entraId)
        => entraId is null ? Enumerable.Empty<RoleMember>() : Roles.Where(r => string.Equals(r.MemberId, entraId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The roles that cover the whole tenant; whoever has them deserves extra attention.</summary>
    public static bool IsCriticalRole(string role) => role is "Global Administrator" or "Privileged Role Administrator" or "Security Administrator"
        or "Exchange Administrator" or "SharePoint Administrator" or "User Administrator" or "Privileged Authentication Administrator"
        or "Conditional Access Administrator" or "Application Administrator" or "Cloud Application Administrator" or "Hybrid Identity Administrator";
}

/// <summary>One sign-in (auditLogs/signIns).</summary>
public sealed record SignInEvent(
    DateTimeOffset Time,
    string UserId,
    string Upn,
    string Name,
    string App,
    string Ip,
    string Location,
    int ErrorCode,
    string Reason,
    string Client,
    IReadOnlyList<CaApplication>? Ca = null)
{
    public bool Succeeded => ErrorCode == 0;
}
