using System.Text.Json.Serialization;

namespace Moss365.Core.Changes;

public enum TargetSystem
{
    ActiveDirectory,
    Graph,
    Exchange,
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
