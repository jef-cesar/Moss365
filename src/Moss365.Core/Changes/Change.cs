using System.Text.Json.Serialization;

namespace Moss365.Core.Changes;

public enum TargetSystem
{
    ActiveDirectory,
    Graph,
    Exchange,

    /// <summary>Google Workspace (G2): the Directory, Groups Settings, Licensing and (with its part) Gmail API.</summary>
    Google,
}

public enum ChangeAction
{
    // On-premises AD (Target = DN of the user, except when creating)
    AdAddGroupMember,
    AdRemoveGroupMember,
    AdSetAttributes,
    AdCreateUser,
    AdDisableAccount,
    AdMove,

    // Microsoft Graph (Target = id or UPN of the user)
    GraphAddGroupMember,
    GraphRemoveGroupMember,
    GraphSetAttributes,
    GraphAssignLicence,
    GraphRemoveLicences,
    GraphRevokeSessions,

    // Exchange Online (Target = UPN or SMTP address)
    ExchangeAddGroupMember,
    ExchangeRemoveGroupMember,
    ExchangeRoomCalendarProcessing,
    ExchangeRoomPlace,
    ExchangeCalendarPermission,
    ExchangeConvertToSharedMailbox,

    // New in 0.3: always append at the end (older follow-up tasks saved the value as a number)
    GraphSetGroupAttributes,      // Target = group id; parameters displayName, description
    GraphAddOwner,         // Target = group id; parameter lid = id or UPN
    GraphRemoveOwner,
    ExchangeUpdateGroup,         // Target = group; DisplayName, PrimarySmtpAddress
    ExchangeGroupManager,         // Target = group; Toevoegen or Verwijderen
    ExchangeUpdateMailbox,       // Target = mailbox; DisplayName, verbergen, doorsturen
    ExchangeAutoReply,           // Target = mailbox; AutoReplyState, InternalMessage, ExternalMessage
    ExchangeAddMailboxPermission,  // Target = mailbox; Recht (FullAccess/SendAs), Trustee
    ExchangeRemoveMailboxPermission,
    ExchangeCreateSharedMailbox, // Target = address; Name, DisplayName, Alias
    ExchangeCreateGroup,          // Target = address; Name, DisplayName, Alias (mail-enabled security group)

    // New in 0.4
    GraphInviteGuest,            // Target = e-mail address; naam, bericht (True/False)
    GraphRemoveGuest,           // Target = id; guest accounts only (can be restored in Entra for 30 days)

    // New in 0.5
    AdSetPassword,          // Target = DN; wijzigenBijAanmelding (True/False); the password is in Secret
    AdUnlockAccount,          // Target = DN
    AdEnableAccount,           // Target = DN (undo of disabling)

    // New in 0.8
    AdCreateGroup,                // Target = DN of the new group; ou, cn, sAMAccountName, groupType, description, managedBy
    GraphCreateGroup,             // Target = alias or address; displayName, mailNickname, soort (Beveiliging/M365), description, eigenaar, leden

    // New in 0.8.1
    GraphSetManager,             // Target = user id; managerId (empty = no manager); Previous managerId

    // New in 0.9: managing Conditional Access (json = what Graph gets; when changing only the fields that change)
    GraphCreateCaPolicy,             // Target = "nieuw"; json = the full policy
    GraphUpdateCaPolicy,          // Target = policy id; json = partial JSON; Previous json = the same fields from before
    GraphCreateCaLocation,            // Target = "nieuw"; json = the named location
    GraphUpdateCaLocation,         // Target = location id; json = the location; Previous json = the location from before

    // New in 0.14: setting up a tenant (the wizard, R5). appName = the name of the app registration ("Moss365")
    GraphCreateApplication,       // Target = appName; json = the full application
    GraphUpdateApplication,       // Target = object id of the application; json = the fields that change; Previous json
    GraphCreateServicePrincipal,  // Target = appName; the enterprise app, with "assignment required" on
    GraphUpdateServicePrincipal,  // Target = id of the service principal; json; Previous json
    GraphGrantConsent,            // Target = appName; resourceId, scope, grantId (empty = new); Previous scope
    GraphAssignAppRole,           // Target = id of the user or group, or the name of a group made in the same plan; appName, appRoleId, kind (user/group)

    // New in 0.31: a user in the cloud (tenant without AD)
    GraphCreateUser,              // Target = UPN; displayName, mailNickname, givenName, surname, jobTitle, department, officeLocation, companyName, city, usageLocation; the password is in Secret

    // New in 0.32: logon scripts in NETLOGON
    AdWriteLogonScript,           // Target = path within NETLOGON; inhoud = the new content; Previous inhoud = the content before

    // New in 0.33: helpdesk
    GraphCreateTemporaryAccessPass, // Target = user id; minuten, eenmalig; the pass is put in Secret after running (never logged)
    GraphSendMail,                // Target = addresses (;); onderwerp, tekst; sent by the signed-in administrator

    // New in 0.39: security advice (R16)
    AdSetAccountFlag,             // Target = DN; vlag (a name in SecurityAdvice.AccountFlags), aan (True/False); Previous aan
    IntuneDeviceAction,           // Target = managed device id; actie (DevicePlans.Actions), toestel, eigendom (company/personal), naam (rename); Previous naam

    // New in 0.40: compliance and the clean-up of devices (R17, U18, U25, U26)
    IntuneCreateCompliancePolicy, // Target = name of the policy; json = the policy (a template), platform
    GraphSetDeviceEnabled,        // Target = object id of the device in Entra; aan (True/False), deviceId; Previous aan
    GraphDeleteDevice,            // Target = object id of the device in Entra; deviceId, sleutels (BitLocker keys that go with it)
    IntuneDeleteDevice,           // Target = managed device id; toestel, eigendom
    IntuneSetComplianceSettings,  // Target = deviceManagement; json = the compliance settings (all of them, as read, one changed); Previous json
    AdHardening,                  // Target = DN; wat (AdHardening.Kinds), waarde (encryptionTypes, machineAccountQuota), beschermd (adminCount); Previous
    GraphUpdatePolicy,            // Target = a policy of TenantAdvice.PolicyTargets; json = the fields that change; Previous json
    GraphRoleEligibility,         // Target = principal id; rolId, rol, naar (eligible/active); Previous naar
    ExchangeOrganisationSetting,  // Target = setting or object; instelling (ExchangeAdvice.Settings), waarde, identity; Previous waarde

    // New in 0.41: the primary address of a user from AD (R21)
    AdSetProxyAddresses,          // Target = DN; mail, proxyAddresses (the whole list, separated by |; absent = unchanged); Previous both
    ExchangeCreateContact,        // Target = the external address; Name, DisplayName, ExternalEmailAddress, Alias, FirstName, LastName, Company, Title, Phone
    ExchangeUpdateContact,        // Target = the address of the contact; the fields that change (ContactInstellen); Previous the same fields
    ExchangeRoomWorkingHours,     // Target = room address; WorkDays, WorkingHoursStartTime/EndTime (HH:mm), WorkingHoursTimeZone; Previous

    // New in 0.42: writing in Google Workspace (G2). Target = the id of the user, group or resource, unless said otherwise
    GoogleCreateUser,             // Target = the primary address; givenName, familyName, orgUnitPath, employeeId, department; password in Secret
    GoogleUpdateUser,             // the fields that change (givenName, familyName, orgUnitPath, suspended, employeeId, department); Previous
    GoogleSetPassword,            // password in Secret; changeAtNextLogin (True/False)
    GoogleSignOut,                // every web and device session of the user
    GoogleRevokeTokens,           // every token the user gave to an app
    GoogleAddAlias,               // alias
    GoogleRemoveAlias,            // alias
    GoogleAddGroupMember,         // Target = the address of the member; groep (id or address), rol (MEMBER, MANAGER, OWNER)
    GoogleRemoveGroupMember,      // Target = the address of the member; groep; Previous rol
    GoogleUpdateGroup,            // name, description; Previous
    GoogleUpdateGroupSettings,    // Target = the group address; whoCanJoin, whoCanPostMessage, whoCanViewMembership, allowExternalMembers; Previous (part)
    GoogleUpdateResource,         // resourceName, capacity, floorName, resourceDescription; Previous
    GoogleAssignLicence,          // Target = the address of the user; productId, skuId, sku (part)
    GoogleRemoveLicence,          // Target = the address of the user; productId, skuId, sku (part)
    GmailSetForwarding,           // Target = the address of the user; adres ("" = off), disposition; Previous (part Gmail settings)
    GmailSetVacation,             // Target = the address of the user; aan, onderwerp, tekst, alleenIntern, start, einde (ms); Previous
    GmailAddDelegate,             // Target = the address of the user; gemachtigde
    GmailRemoveDelegate,          // Target = the address of the user; gemachtigde
    GmailSetSignature,            // Target = the address of the user; handtekening (HTML); Previous
    GmailAddFilter,               // Target = the address of the user; json (criteria and action, as Gmail gives them)
    GmailRemoveFilter,            // Target = the address of the user; id; Previous json (to add it again)
    GoogleSetGroupMemberRole,     // Target = the address of the member; groep, rol (MEMBER, MANAGER, OWNER); Previous rol

    // Setting up the own Google Cloud project with gcloud (the wizard, U41). Target = the project id
    GcloudCreateProject,          // naam
    GcloudEnableServices,         // apis (separated by spaces)
    GcloudCreateServiceAccount,   // the service account of the part Gmail settings
    GcloudCreateServiceAccountKey, // its key comes back in Secret, is kept encrypted and never logged
}

/// <summary>
/// One planned change. A plan is a list of changes that the app shows first; the user chooses what is run. Parameters
/// are text (values of attributes, DNs, ids); secrets are kept apart and never logged.
/// </summary>
public sealed class Change
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required TargetSystem TargetSystem { get; init; }
    public required ChangeAction ChangeAction { get; init; }

    /// <summary>Technical identity of the target object (DN, id, UPN or SMTP address).</summary>
    public required string Target { get; set; }

    /// <summary>Readable name of the target object for the screen and the change log.</summary>
    public string TargetName { get; init; } = string.Empty;

    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What happens, in plain language.</summary>
    public required string Description { get; init; }

    public string? CurrentValue { get; init; }
    public string? NewValue { get; init; }

    /// <summary>A warning the user must read before confirming.</summary>
    public string? Warning { get; init; }

    /// <summary>Ticked by default in the preview.</summary>
    public bool Selected { get; set; } = true;

    /// <summary>This change is skipped when the change with this id failed or was not selected.</summary>
    public Guid? DependentOf { get; init; }

    /// <summary>
    /// The values of the changed attributes before the change (they go to the change log), so that the change can be
    /// undone. Never for secrets.
    /// </summary>
    public Dictionary<string, string>? Previous { get; init; }

    /// <summary>Secret value (e.g. the initial password). Never save or log it.</summary>
    [JsonIgnore]
    public string? Secret { get; set; }

    public string P(string key) => Parameters.TryGetValue(key, out var v) ? v : string.Empty;

    public string SystemText => TargetSystem switch
    {
        TargetSystem.ActiveDirectory => "AD",
        TargetSystem.Graph => "Entra (Graph)",
        TargetSystem.Google => "Google",
        _ => "Exchange",
    };
}

public enum ResultStatus
{
    Succeeded,
    Failed,
    Skipped,
}

public sealed record ChangeResult(Guid ChangeId, ResultStatus Status, string Message)
{
    public static ChangeResult Ok(Change w, string message = "") => new(w.Id, ResultStatus.Succeeded, message);
    public static ChangeResult Error(Change w, string message) => new(w.Id, ResultStatus.Failed, message);
    public static ChangeResult Skip(Change w, string message) => new(w.Id, ResultStatus.Skipped, message);
}

/// <summary>Runs changes for one system. One call gets a consecutive batch of that system.</summary>
public interface IExecutor
{
    TargetSystem TargetSystem { get; }

    Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel);
}
