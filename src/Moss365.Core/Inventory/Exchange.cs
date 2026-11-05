namespace Moss365.Core.Inventory;

/// <summary>A group as Exchange Online knows it: distribution list, mail-enabled security group or room list.</summary>
public sealed record ExoGroup
{
    public string Type { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Internal name in Exchange; the group sometimes appears as a trustee by this name.</summary>
    public string Name { get; init; } = string.Empty;

    public string Alias { get; init; } = string.Empty;
    public string Smtp { get; init; } = string.Empty;
    public bool IsDirSynced { get; init; }
    public string MemberJoinRestriction { get; init; } = string.Empty;
    public IReadOnlyList<string> ManagedBy { get; init; } = Array.Empty<string>();
    public int? CountMembers { get; init; }
    public IReadOnlyList<ExoMember> Members { get; init; } = Array.Empty<ExoMember>();

    public string TypeText => Type switch
    {
        "MailUniversalSecurityGroup" => "Mail-beveiligingsgroep",
        "MailUniversalDistributionGroup" => "Distributielijst",
        "RoomList" => "Room list",
        _ => Type,
    };
}

/// <summary>A member of an Exchange group: address and display name.</summary>
public sealed record ExoMember(string Smtp, string Name);

/// <summary>A mailbox that is not a user mailbox: shared, scheduling (Bookings) or discovery.</summary>
public sealed record Mailbox
{
    public string Type { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string Smtp { get; init; } = string.Empty;
    public bool Hidden { get; init; }
    public bool IsDirSynced { get; init; }
    public string WhenCreated { get; init; } = string.Empty;

    /// <summary>"intern", "extern" or empty (inventory); live the forwarding address itself.</summary>
    public string Forwarding { get; init; } = string.Empty;

    public string ForwardingTo { get; init; } = string.Empty;
    public bool? SaveForwardingAnd { get; init; }

    /// <summary>Enabled, Disabled or Scheduled; empty when not read (live only).</summary>
    public string AutoReply { get; init; } = string.Empty;

    public string AutoReplyInternal { get; init; } = string.Empty;
    public string AutoReplyExternal { get; init; } = string.Empty;

    public bool IsShared => string.Equals(Type, "SharedMailbox", StringComparison.OrdinalIgnoreCase);

    public string TypeText => Type switch
    {
        "SharedMailbox" => "Gedeeld postvak",
        "SchedulingMailbox" => "Planning (Bookings)",
        "DiscoveryMailbox" => "Discovery",
        "EquipmentMailbox" => "Apparatuur",
        _ => Type,
    };

    /// <summary>The part of the address before the @; the access group is called SG-SM-&lt;this
    /// part&gt;-Full.</summary>
    public string LocalPart => Smtp.Split('@')[0];
}

/// <summary>A permission on a shared mailbox. Trustee is as Exchange returns it (address, name or alias).</summary>
public sealed record MailboxPermission(string Mailbox, string Permission, string Trustee);
