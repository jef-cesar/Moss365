using Moss365.Core.Localization;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

/// <summary>Settings to connect live; no passwords (the app asks for those per session).</summary>
/// <summary>Keeps the (already encrypted) sign-in of Microsoft between two starts of the app (0.33).</summary>
public interface ITokenCacheStore
{
    byte[]? Load();

    void Save(byte[] data);

    void Clear();
}

/// <summary>The sign-in cache as a document in the tenant database (base64 of the encrypted bytes).</summary>
public sealed class DocumentTokenStore : ITokenCacheStore
{
    private readonly Storage.Database _db;

    public DocumentTokenStore(Storage.Database db) => _db = db;

    public byte[]? Load()
    {
        var text = _db.ReadDocument(Storage.Documents.SignInCache);
        try
        {
            return string.IsNullOrEmpty(text) ? null : Convert.FromBase64String(System.Text.Json.JsonSerializer.Deserialize<string>(text) ?? string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Save(byte[] data) => _db.SaveDocument(Storage.Documents.SignInCache, System.Text.Json.JsonSerializer.Serialize(Convert.ToBase64String(data)));

    public void Clear() => _db.DeleteDocument(Storage.Documents.SignInCache);
}

public sealed class ConnectionSettings
{
    /// <summary>Domain controller (name or IP), e.g. dc01.contoso.internal.</summary>
    public string AdServer { get; set; } = string.Empty;

    /// <summary>AD account, e.g. CONTOSO\name; empty = the Windows sign-in of the PC (only on a domain PC).</summary>
    public string AdUser { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    /// <summary>Application (client) ID of the app registration "Moss365".</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Admin account for Exchange Online (fills in the sign-in beforehand).</summary>
    public string ManagementUpn { get; set; } = string.Empty;

    /// <summary>Look for a new version on GitHub at startup.</summary>
    public bool SearchUpdates { get; set; } = true;

    /// <summary>Live loading: only what changed since the previous time (AD and Entra groups), when possible.</summary>
    public bool QuickLoad { get; set; } = true;

    /// <summary>Refresh every 15 minutes while the app is open (0.8): only when you are already signed in, never a
    /// sign-in link.</summary>
    public bool RefreshAuto { get; set; }

    /// <summary>The NETLOGON share when it is not \\domain\NETLOGON (0.32); empty = that default.</summary>
    public string NetlogonPath { get; set; } = string.Empty;

    /// <summary>Stay signed in on this PC (0.33): the sign-in is kept in the tenant database, encrypted with the
    /// Windows account; off by default.</summary>
    public bool RememberSignIn { get; set; }

    /// <summary>Where the encrypted sign-in is kept (set by the app, never saved with the settings).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ITokenCacheStore? TokenCache { get; set; }

    public bool AdFilled => AdServer.Trim().Length > 0;
    public bool GraphFilled => TenantId.Trim().Length > 0 && ClientId.Trim().Length > 0;
}

/// <summary>What a live AD read returns. Snapshot = from where the next read can ask for the changes only.</summary>
public sealed record AdData(IReadOnlyList<AdUser> Users, IReadOnlyList<AdGroup> Groups, string DomainDn,
    AdSnapshot? Snapshot = null, bool Quick = false);

/// <summary>
/// The state of one domain controller: its name (dsServiceName) and the highest USN. Changes count per domain
/// controller; with another domain controller the app reads everything again.
/// </summary>
public sealed record AdSnapshot(string DomainController, long HighestUsn);

/// <summary>The previous AD read, to ask for the changes only.</summary>
public sealed record AdBase(AdSnapshot Snapshot, IReadOnlyList<AdUser> Users, IReadOnlyList<AdGroup> Groups);

/// <summary>What a live Graph read returns. DeltaLink = from where the next read asks for the group changes
/// only.</summary>
public sealed record GraphData(
    IReadOnlyList<EntraUser> Users,
    IReadOnlyList<CloudGroup> Groups,
    IReadOnlyDictionary<string, HashSet<string>> Membership,
    IReadOnlyList<Licence> Licences,
    string? DeltaLink = null,
    bool Quick = false);

/// <summary>The previous Graph read of the groups, to ask for the changes only.</summary>
public sealed record GraphBase(string DeltaLink, IReadOnlyDictionary<string, CloudGroup> Groups, IReadOnlyDictionary<string, IReadOnlySet<string>> Membership);

public interface IAdConnection
{
    Task<AdData> ReadAsync(IProgress<string>? progress, CancellationToken cancel);

    /// <summary>With a base: only what changed (falls back to reading everything by itself when that is not
    /// possible).</summary>
    Task<AdData> ReadAsync(AdBase? baseline, IProgress<string>? progress, CancellationToken cancel) => ReadAsync(progress, cancel);

    /// <summary>Read one user again by sAMAccountName (stays valid when the account was moved); null = does not
    /// exist.</summary>
    Task<AdUser?> ReadUserAsync(string samAccountName, CancellationToken cancel);

    /// <summary>Read one group again by sAMAccountName, with the members; null = does not exist.</summary>
    Task<AdGroup?> ReadGroupAsync(string samAccountName, CancellationToken cancel);

    /// <summary>BitLocker keys and the LAPS password of a computer in AD (0.33), or null when the computer does not
    /// exist. Only call this when the access is written to the access log.</summary>
    Task<Helpdesk.AdComputerSecrets?> ComputerSecretsAsync(string computerName, CancellationToken cancel)
        => Task.FromResult<Helpdesk.AdComputerSecrets?>(null);

    /// <summary>The light security check of AD (0.34): SPN, delegation, password flags, krbtgt, adminCount, old systems.</summary>
    Task<Security.AdSecurityReading?> ReadSecurityAsync(CancellationToken cancel) => Task.FromResult<Security.AdSecurityReading?>(null);

    /// <summary>The computers in AD with their system and last sign-in (0.36).</summary>
    Task<IReadOnlyList<Devices.AdComputer>?> ReadComputersAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<Devices.AdComputer>?>(null);

    /// <summary>Group Policies and where they are linked (0.34), read only.</summary>
    Task<IReadOnlyList<Security.GpoInfo>?> ReadGpoAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<Security.GpoInfo>?>(null);
}

/// <summary>One Entra user with the ids of the groups it is a direct member of.</summary>
public sealed record GraphUserReading(EntraUser User, IReadOnlyList<string> GroupIds);

/// <summary>One cloud group with the ids of the users who are direct members.</summary>
public sealed record GraphGroupReading(CloudGroup Group, IReadOnlyList<string> MembersIds);

/// <summary>The result of reading a few Exchange objects again (rooms, mailboxes, groups).</summary>
public sealed record ExchangeObjects(IReadOnlyList<Room> Rooms, ExchangeData Data, IReadOnlyList<string> NotFound);

public interface IGraphConnection
{
    Task<GraphData> ReadAsync(IProgress<string>? progress, CancellationToken cancel);

    /// <summary>With a base: the groups via their changes (delta); falls back to reading everything by itself when that
    /// is not possible.</summary>
    Task<GraphData> ReadAsync(GraphBase? baseline, IProgress<string>? progress, CancellationToken cancel) => ReadAsync(progress, cancel);

    /// <summary>The id of the user with this UPN, or null when it does not exist (yet).</summary>
    Task<string?> FindUserIdAsync(string upn, CancellationToken cancel);

    /// <summary>The UPN of the signed-in administrator (for the change log), or null when not signed in yet.</summary>
    string? SignedInAs { get; }

    /// <summary>The app roles (profiles) from the sign-in, e.g. "Moss365.Helpdesk"; null when not signed in
    /// yet.</summary>
    IReadOnlyList<string>? AppRoles => null;

    /// <summary>Sign in (or extend the sign-in), so that the roles are known before anything runs.</summary>
    Task SignInAsync(CancellationToken cancel) => Task.CompletedTask;

    /// <summary>Is there a valid sign-in without showing anything (no sign-in link)? For automatic refresh.</summary>
    Task<bool> SilentSignedInAsync(CancellationToken cancel) => Task.FromResult(SignedInAs is not null);

    /// <summary>Read one user again (id or UPN), with its groups; null = does not exist (yet).</summary>
    Task<GraphUserReading?> ReadUserAsync(string idOrUpn, CancellationToken cancel);

    /// <summary>Read one group again, with the members; null = does not exist.</summary>
    Task<GraphGroupReading?> ReadGroupAsync(string id, CancellationToken cancel);

    /// <summary>Owners of a cloud group (not in the inventory).</summary>
    Task<IReadOnlyList<GraphOwner>> ReadOwnersAsync(string groupId, CancellationToken cancel);

    /// <summary>Devices in Entra by name (0.33, BitLocker and LAPS).</summary>
    Task<IReadOnlyList<Helpdesk.DeviceInfo>> FindDevicesAsync(string name, CancellationToken cancel)
        => Task.FromResult<IReadOnlyList<Helpdesk.DeviceInfo>>(Array.Empty<Helpdesk.DeviceInfo>());

    /// <summary>BitLocker keys of a device, or the key with this id (the first characters are enough), with the key
    /// itself. Only call this when the access is written to the access log.</summary>
    Task<IReadOnlyList<Helpdesk.BitLockerKey>> BitLockerKeysAsync(string? deviceId, string? keyId, CancellationToken cancel)
        => Task.FromResult<IReadOnlyList<Helpdesk.BitLockerKey>>(Array.Empty<Helpdesk.BitLockerKey>());

    /// <summary>The Windows LAPS password of a device in Entra, or null. Only call this when the access is written to
    /// the access log.</summary>
    Task<Helpdesk.LapsPassword?> LapsAsync(string deviceId, CancellationToken cancel) => Task.FromResult<Helpdesk.LapsPassword?>(null);

    /// <summary>Secrets and certificates of app registrations, application permissions and consents (0.34); null where
    /// that is not possible.</summary>
    Task<Security.AppsReading?> ReadAppsAsync(CancellationToken cancel) => Task.FromResult<Security.AppsReading?>(null);

    /// <summary>Microsoft service health and the message center (0.37); null where that cannot be read.</summary>
    Task<Reporting.ServiceHealthReading?> ReadServiceHealthAsync(CancellationToken cancel) => Task.FromResult<Reporting.ServiceHealthReading?>(null);

    /// <summary>Devices in Intune (0.36); null where that cannot be read (licence, permission).</summary>
    Task<IReadOnlyList<Devices.IntuneDevice>?> ReadIntuneDevicesAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<Devices.IntuneDevice>?>(null);

    /// <summary>Microsoft 365 groups and Teams (owners, activity) and SharePoint sites (storage, activity) (0.36).</summary>
    Task<Devices.CollaborationReading?> ReadCollaborationAsync(CancellationToken cancel) => Task.FromResult<Devices.CollaborationReading?>(null);

    /// <summary>Microsoft Secure Score with its history and the actions that gain the most (0.34).</summary>
    Task<Security.SecureScoreReading?> ReadSecureScoreAsync(CancellationToken cancel) => Task.FromResult<Security.SecureScoreReading?>(null);

    /// <summary>Admin role assignments: permanent, active and eligible (PIM) (0.34).</summary>
    Task<IReadOnlyList<Security.RoleAssignmentInfo>?> ReadRoleAssignmentsAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<Security.RoleAssignmentInfo>?>(null);

    /// <summary>Risky users (Entra ID P2); null when that cannot be read (0.34).</summary>
    Task<IReadOnlyList<Security.RiskyUser>?> ReadRiskyUsersAsync(CancellationToken cancel) => Task.FromResult<IReadOnlyList<Security.RiskyUser>?>(null);

    /// <summary>MFA registrations and members of admin roles; null where that is not possible.</summary>
    Task<SecurityData?> ReadSecurityAsync(IProgress<string>? progress, CancellationToken cancel)
        => Task.FromResult<SecurityData?>(null);

    /// <summary>Sign-ins of the last days (newest first), of one user or (id null) of everyone.</summary>
    Task<IReadOnlyList<SignInEvent>> ReadSignInsAsync(string? userId, int days, bool onlyFailed, CancellationToken cancel)
        => Task.FromResult<IReadOnlyList<SignInEvent>>(Array.Empty<SignInEvent>());

    /// <summary>Sign-ins of everyone across several pages (up to max), for the effect of Conditional Access.</summary>
    Task<IReadOnlyList<SignInEvent>> ReadSignInsAsync(int days, int max, CancellationToken cancel)
        => ReadSignInsAsync(null, days, false, cancel);

    /// <summary>
    /// The calendar of a room (or another mailbox) between from and to, with recurring appointments as separate items.
    /// Delegated (Calendars.Read.Shared): your own account needs read permission on that calendar. No permission:
    /// <see cref="NoPermissionError"/>.
    /// </summary>
    Task<IReadOnlyList<Appointment>> ReadCalendarAsync(string address, DateTime start, DateTime until, CancellationToken cancel)
        => Task.FromResult<IReadOnlyList<Appointment>>(Array.Empty<Appointment>());

    /// <summary>Who changed Conditional Access in the last days (audit log); no permission:
    /// <see cref="NoPermissionError"/>.</summary>
    Task<IReadOnlyList<CaAuditEntry>> ReadCaAuditAsync(int days, CancellationToken cancel)
        => Task.FromResult<IReadOnlyList<CaAuditEntry>>(Array.Empty<CaAuditEntry>());

    /// <summary>Conditional Access policies, named locations, strengths and role names; null when the connection cannot
    /// do that.</summary>
    Task<CaData?> ReadConditionalAccessAsync(IProgress<string>? progress, CancellationToken cancel)
        => Task.FromResult<CaData?>(null);
}

/// <summary>Your account may not read this (Graph 401, 403 or 404); the message comes from Microsoft.</summary>
public sealed class NoPermissionError(string message) : Exception(message);

public sealed record GraphOwner(string Id, string Name, string Upn);

/// <summary>What a live Exchange read of groups and mailboxes returns.</summary>
public sealed record ExchangeData(IReadOnlyList<ExoGroup> Groups, IReadOnlyList<Mailbox> Mailboxes, IReadOnlyList<MailboxPermission> Permissions);

public interface IExchangeConnection
{
    Task<IReadOnlyList<Room>> ReadRoomsAsync(IProgress<string>? progress, CancellationToken cancel);

    /// <summary>Shared mailboxes (with permissions, forwarding and automatic reply) and Exchange groups with
    /// members.</summary>
    Task<ExchangeData> ReadMailboxesAsync(IProgress<string>? progress, CancellationToken cancel);

    /// <summary>Forwarding on mailboxes and (with includeRules) inbox rules that forward (0.34).</summary>
    Task<IReadOnlyList<Security.ForwardingRule>> ReadForwardingAsync(bool includeRules, IProgress<string>? progress, CancellationToken cancel)
        => throw new NotSupportedException(Texts.T["code.connections.checking-forwarding-is-not-possible"]);

    /// <summary>Message trace (0.33): what was received by and/or sent from an address in the last days (at most 10).</summary>
    Task<IReadOnlyList<Helpdesk.MessageTraceRow>> MessageTraceAsync(string? sender, string? recipient, int days, CancellationToken cancel)
        => throw new NotSupportedException(Texts.T["code.connections.a-message-trace-is-not"]);

    /// <summary>Read a few rooms, mailboxes or groups again (by address), in one Exchange session.</summary>
    Task<ExchangeObjects> ReadObjectsAsync(IReadOnlyList<string> addresses, CancellationToken cancel);

    /// <summary>Rooms and mailboxes with groups in one Exchange session (one sign-in). An error in one part does not
    /// stop the other: it is then in <see cref="ExchangeReading"/>.</summary>
    async Task<ExchangeReading> ReadAllAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        IReadOnlyList<Room>? rooms = null;
        ExchangeData? mailboxes = null;
        string? roomsError = null, mailboxesError = null;
        try
        {
            rooms = await ReadRoomsAsync(progress, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            roomsError = ex.Message;
        }

        try
        {
            mailboxes = await ReadMailboxesAsync(progress, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            mailboxesError = ex.Message;
        }

        return new ExchangeReading(rooms, roomsError, mailboxes, mailboxesError);
    }
}

/// <summary>Rooms and mailboxes from one Exchange session; what failed is null with the error next to it.</summary>
public sealed record ExchangeReading(IReadOnlyList<Room>? Rooms, string? RoomsError, ExchangeData? Mailboxes, string? MailboxesError);

/// <summary>Creates connections and executors based on the settings and the AD password of this session.</summary>
public interface IConnectionFactory
{
    IAdConnection? Ad(ConnectionSettings settings, string? password);

    IGraphConnection? Graph(ConnectionSettings settings);

    IExchangeConnection? Exchange(ConnectionSettings settings);

    IReadOnlyList<IExecutor> Executors(ConnectionSettings settings, string? adPassword);

    /// <summary>Why Exchange could not use the same sign-in as Graph, or null.</summary>
    string? ExchangeMessage => null;

    /// <summary>The NETLOGON share of the domain (logon scripts, 0.32); null where that is not possible.</summary>
    LogonScripts.ILogonScriptShare? LogonScripts(ConnectionSettings settings) => null;

    /// <summary>Runs scripts from the library with the sign-in of the app; null where that is not possible.</summary>
    Core.Scripts.IScriptExecutor? Scripts(ConnectionSettings settings, string? adPassword) => null;

    /// <summary>Check of this PC (PowerShell, modules, network); null where that is not possible.</summary>
    ISystemCheck? Check => null;

    /// <summary>Searching for a new version of the app; null where that is not possible.</summary>
    IUpdateCheck? Updates => null;

    /// <summary>The wizard (R5): reads and sets up a tenant with the sign-in of Microsoft Graph PowerShell; null where
    /// that is not possible.</summary>
    Setup.ITenantSetupConnection? Setup(string tenantId) => null;

    /// <summary>The tenant ID of a domain (Microsoft's OpenID metadata).</summary>
    Task<Setup.TenantLookupResult> FindTenantAsync(string domainOrId, CancellationToken cancel)
        => Task.FromResult(new Setup.TenantLookupResult(null, Texts.T["code.connections.looking-up-is-not-available"]));

    /// <summary>The domain of this PC and its domain controller, or null (not joined to a domain, or not Windows).</summary>
    Setup.LocalDomain? FindLocalDomain() => null;

    /// <summary>Where Update library gets the scripts (R8): a shared folder, or (null) this repository on GitHub.</summary>
    Scripts.IScriptLibrarySource? ScriptLibrary(string? folder) => null;
}

public enum CheckStatus
{
    Good,
    Warning,
    Error,
}

/// <summary>
/// One check. When <see cref="Module"/> is filled in, the app can install the module in the PowerShell of
/// <see cref="PowerShell"/> (the path to powershell.exe or pwsh.exe).
/// </summary>
public sealed record CheckRule(string Group, string Section, CheckStatus Status, string Value, string Explanation,
    string? Solution = null, string? Module = null, string? PowerShell = null);

public interface ISystemCheck
{
    Task<IReadOnlyList<CheckRule>> CheckAsync(ConnectionSettings settings, IProgress<string>? progress, CancellationToken cancel);

    /// <summary>Installs a module for the current user; returns "Gelukt" or the error message.</summary>
    Task<string> InstallAsync(string powerShell, string module, CancellationToken cancel);
}
