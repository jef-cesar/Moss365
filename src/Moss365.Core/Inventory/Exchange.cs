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

    /// <summary>R21: hidden from the address book (HiddenFromAddressListsEnabled); null when not read.</summary>
    public bool? Hidden { get; init; }

    /// <summary>R21: only senders inside the organisation (RequireSenderAuthenticationEnabled); null when not read.</summary>
    public bool? OnlyInternalSenders { get; init; }
    public IReadOnlyList<string> ManagedBy { get; init; } = Array.Empty<string>();
    public int? CountMembers { get; init; }
    public IReadOnlyList<ExoMember> Members { get; init; } = Array.Empty<ExoMember>();

    public string TypeText => TypeTextOf(Type);

    /// <summary>The kind of an Exchange group (its RecipientTypeDetails) in the language of the interface.</summary>
    public static string TypeTextOf(string type) => type switch
    {
        "MailUniversalSecurityGroup" => Localization.Texts.T["group.kind.mail-security"],
        "MailUniversalDistributionGroup" => Localization.Texts.T["group.kind.distribution"],
        "RoomList" => "Room list",
        _ => type,
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

    /// <summary>R21: a copy of what is sent as or on behalf of the mailbox stays in its own Sent Items; null when not read.</summary>
    public bool? SentItemsCopy { get; init; }

    /// <summary>R21: who may send on behalf of the mailbox (GrantSendOnBehalfTo), as Exchange names them.</summary>
    public IReadOnlyList<string> SendOnBehalf { get; init; } = Array.Empty<string>();

    public bool IsShared => string.Equals(Type, "SharedMailbox", StringComparison.OrdinalIgnoreCase);

    public string TypeText => Type switch
    {
        "SharedMailbox" => Localization.Texts.T["mailbox.kind.shared"],
        "SchedulingMailbox" => Localization.Texts.T["mailbox.kind.scheduling"],
        "DiscoveryMailbox" => "Discovery",
        "EquipmentMailbox" => Localization.Texts.T["mailbox.kind.equipment"],
        _ => Type,
    };

    /// <summary>The part of the address before the @; the access group is called SG-SM-&lt;this
    /// part&gt;-Full.</summary>
    public string LocalPart => Smtp.Split('@')[0];
}

/// <summary>A permission on a shared mailbox. Trustee is as Exchange returns it (address, name or alias).</summary>
public sealed record MailboxPermission(string Mailbox, string Permission, string Trustee);

/// <summary>A mail contact of Exchange Online (R21): someone outside the organisation in the address book.</summary>
public sealed record MailContact
{
    public string DisplayName { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;

    /// <summary>The address outside the organisation where mail goes (ExternalEmailAddress, without "SMTP:").</summary>
    public string External { get; init; } = string.Empty;

    /// <summary>The primary address in the address book (usually the same as <see cref="External"/>): the identity.</summary>
    public string Smtp { get; init; } = string.Empty;
    public bool Hidden { get; init; }
    public bool IsDirSynced { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
}
