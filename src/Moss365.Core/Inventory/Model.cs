namespace Moss365.Core.Inventory;

public sealed record AdUser
{
    public string SamAccountName { get; init; } = string.Empty;
    public string UserPrincipalName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string GivenName { get; init; } = string.Empty;
    public string Surname { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string Company { get; init; } = string.Empty;
    public string Office { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string EmailAddress { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string DistinguishedName { get; init; } = string.Empty;

    /// <summary>OU path of the container the account is in, e.g. "GEMEENTEBESTUUR/GEBRUIKERS".</summary>
    public string OuPath { get; init; } = string.Empty;

    public string WhenCreated { get; init; } = string.Empty;
    public string LastLogonDate { get; init; } = string.Empty;
    public string PasswordLastSet { get; init; } = string.Empty;
    public bool PasswordNeverExpires { get; init; }

    /// <summary>Locked after too many wrong passwords.</summary>
    public bool LockedOut { get; init; }

    public string ScriptPath { get; init; } = string.Empty;
    public IReadOnlyList<string> MemberOr { get; init; } = Array.Empty<string>();

    /// <summary>telephoneNumber (0.8.1).</summary>
    public string Phone { get; init; } = string.Empty;

    /// <summary>mobile (0.8.1).</summary>
    public string Mobile { get; init; } = string.Empty;

    /// <summary>DN of the manager (0.8.1).</summary>
    public string Manager { get; init; } = string.Empty;
}

public sealed record EntraUser
{
    public string Id { get; init; } = string.Empty;
    public string UserPrincipalName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string GivenName { get; init; } = string.Empty;
    public string Surname { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;
    public string MailNickname { get; init; } = string.Empty;
    public bool AccountEnabled { get; init; }
    public string UserType { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public string OfficeLocation { get; init; } = string.Empty;
    public string CompanyName { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string UsageLocation { get; init; } = string.Empty;
    public string CreatedDateTime { get; init; } = string.Empty;

    /// <summary>The first number of businessPhones (0.8.1).</summary>
    public string Phone { get; init; } = string.Empty;

    /// <summary>mobilePhone (0.8.1).</summary>
    public string Mobile { get; init; } = string.Empty;

    /// <summary>Object ID of the manager (0.8.1); empty when unknown or none.</summary>
    public string ManagerId { get; init; } = string.Empty;
    public bool OnPremisesSyncEnabled { get; init; }
    public string OnPremisesSamAccountName { get; init; } = string.Empty;
    public string OnPremisesDistinguishedName { get; init; } = string.Empty;
    public IReadOnlyList<string> Licences { get; init; } = Array.Empty<string>();
    public string LastSignIn { get; init; } = string.Empty;

    public bool IsGuest => string.Equals(UserType, "Guest", StringComparison.OrdinalIgnoreCase)
                          || UserPrincipalName.Contains("#EXT#", StringComparison.OrdinalIgnoreCase);
}

public sealed record AdGroup
{
    public string Name { get; init; } = string.Empty;
    public string SamAccountName { get; init; } = string.Empty;
    public string DistinguishedName { get; init; } = string.Empty;
    public string OuPath { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;

    /// <summary>DN of the manager (managedBy), empty when there is none.</summary>
    public string ManagedBy { get; init; } = string.Empty;

    public int? CountMembers { get; init; }

    /// <summary>DNs of the direct members (users, groups, computers).</summary>
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();

    /// <summary>False when the member list was not read (then the app derives the users from memberOf).</summary>
    public bool MembersKnown { get; init; }
}

public enum CloudGroupType
{
    Microsoft365,
    Security,
    MailSecurity,
    Distribution,
    Unknown,
}

public sealed record CloudGroup
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Mail { get; init; } = string.Empty;

    /// <summary>sAMAccountName of the AD group when the group is synchronised from AD.</summary>
    public string OnPremisesSamAccountName { get; init; } = string.Empty;

    public CloudGroupType Type { get; init; }

    /// <summary>True when the group is synchronised from on-premises AD.</summary>
    public bool FromAd { get; init; }

    public bool Dynamic { get; init; }
    public string MembershipRule { get; init; } = string.Empty;
    public bool GroupLicence { get; init; }
    public int? CountMembers { get; init; }

    public string TypeText => Type switch
    {
        CloudGroupType.Microsoft365 => "Microsoft 365-groep",
        CloudGroupType.Security => "Beveiligingsgroep",
        CloudGroupType.MailSecurity => "Mail-beveiligingsgroep",
        CloudGroupType.Distribution => "Distributielijst",
        _ => "Onbekend",
    };
}

/// <summary>A meeting room (room mailbox) with the settings the app manages.</summary>
public sealed record Room
{
    public string Smtp { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Alias { get; init; } = string.Empty;
    public string Building { get; init; } = string.Empty;
    public string Floor { get; init; } = string.Empty;
    public string FloorLabel { get; init; } = string.Empty;
    public int? Capacity { get; init; }
    public bool Hidden { get; init; }
    public string AutomateProcessing { get; init; } = string.Empty;
    public bool AllBookInPolicy { get; init; }
    public bool AllRequestInPolicy { get; init; }
    public string BookInPolicy { get; init; } = string.Empty;
    public string ResourceDelegates { get; init; } = string.Empty;
    public int? BookingWindowInDays { get; init; }
    public int? MaximumDurationInMinutes { get; init; }
    public bool AllowConflicts { get; init; }
    public bool AllowRecurringMeetings { get; init; }
    public bool AddOrganizerToSubject { get; init; }
    public bool DeleteSubject { get; init; }

    /// <summary>All calendar permissions as text, e.g. "Default=Reviewer|Anonymous=None|(2 anderen)".</summary>
    public string CalendarPermissions { get; init; } = string.Empty;

    /// <summary>The permission of the user Default (everyone in the organisation) on the calendar.</summary>
    public string DefaultCalendarPermission
    {
        get
        {
            foreach (var part in CalendarPermissions.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var is_ = part.IndexOf('=');
                if (is_ > 0 && (part[..is_].Equals("Default", StringComparison.OrdinalIgnoreCase) || part[..is_].Equals("Standaard", StringComparison.OrdinalIgnoreCase)))
                {
                    return part[(is_ + 1)..];
                }
            }

            return string.Empty;
        }
    }
}

/// <summary>A licence (SKU) of the tenant with the number of assigned and available units.</summary>
public sealed record Licence(string SkuId, string SkuPartNumber, int Assigned, int Total)
{
    public int Free => Math.Max(0, Total - Assigned);
}

/// <summary>A person or account: the link of an AD account and an Entra account (either can be missing).</summary>
public sealed class Person
{
    public required string Key { get; init; }
    public AdUser? Ad { get; init; }
    public EntraUser? Entra { get; init; }

    public string Name => FirstNonEmpty(Entra?.DisplayName, Ad?.DisplayName, Ad?.SamAccountName, Entra?.UserPrincipalName);
    public string Upn => FirstNonEmpty(Ad?.UserPrincipalName, Entra?.UserPrincipalName);
    public string Department => FirstNonEmpty(Ad?.Department, Entra?.Department).Trim();
    public bool IsGuest => Entra?.IsGuest == true && Ad is null;
    public bool Enabled => Ad?.Enabled ?? Entra?.AccountEnabled ?? false;

    public string Source => (Ad, Entra) switch
    {
        (not null, not null) => "AD + Entra",
        (not null, null) => "Alleen AD",
        (null, not null) => Entra.IsGuest ? "Gast" : "Alleen Entra",
        _ => "?",
    };

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w)) ?? string.Empty;
}

/// <summary>Where each source file came from (the app chooses the newest inventory folder per file).</summary>
public sealed record SourceFile(string File, string? Folder, int Rows);

public sealed record InventoryData
{
    public IReadOnlyList<AdUser> AdUsers { get; init; } = Array.Empty<AdUser>();
    public IReadOnlyList<EntraUser> EntraUsers { get; init; } = Array.Empty<EntraUser>();
    public IReadOnlyDictionary<string, AdGroup> AdGroupsByDn { get; init; } = new Dictionary<string, AdGroup>();
    public IReadOnlyDictionary<string, CloudGroup> CloudGroups { get; init; } = new Dictionary<string, CloudGroup>();

    /// <summary>Entra user id to the ids of the cloud groups it is a direct member of.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> CloudMembership { get; init; } = new Dictionary<string, IReadOnlySet<string>>();

    public IReadOnlyList<Person> Persons { get; init; } = Array.Empty<Person>();
    public IReadOnlyList<Room> Rooms { get; init; } = Array.Empty<Room>();
    public IReadOnlyList<Licence> Licences { get; init; } = Array.Empty<Licence>();
    public IReadOnlyList<SourceFile> Sources { get; init; } = Array.Empty<SourceFile>();

    /// <summary>Distribution lists, mail-enabled security groups and room lists from Exchange Online, with their
    /// members.</summary>
    public IReadOnlyList<ExoGroup> ExoGroups { get; init; } = Array.Empty<ExoGroup>();

    /// <summary>Mailboxes that are not user mailboxes (shared, scheduling, ...), without the rooms.</summary>
    public IReadOnlyList<Mailbox> Mailboxes { get; init; } = Array.Empty<Mailbox>();

    /// <summary>FullAccess and SendAs permissions on shared mailboxes.</summary>
    public IReadOnlyList<MailboxPermission> MailboxPermissions { get; init; } = Array.Empty<MailboxPermission>();

    /// <summary>Members of cloud groups that are not users (devices, contacts): id to (kind, name).</summary>
    public IReadOnlyDictionary<string, (string Kind, string Name)> CloudMemberNames { get; init; } = new Dictionary<string, (string, string)>();

    // An own index per instance (also after "with"); a field would be copied along with "with"
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InventoryData, ObjectIndex> Indexes = new();

    /// <summary>Lookups and relations across AD, Entra and Exchange; built on first use.</summary>
    public ObjectIndex Index => Indexes.GetValue(this, d => new ObjectIndex(d));

    /// <summary>MFA registrations and admin roles from Entra; null when they are not loaded.</summary>
    public SecurityData? Security { get; init; }

    /// <summary>Conditional Access policies, named locations and strengths from Entra; null when they are not
    /// loaded.</summary>
    public CaData? ConditionalAccess { get; init; }

    /// <summary>"Inventaris" or "Live" (straight from AD, Graph and Exchange).</summary>
    public string Origin { get; init; } = "Inventaris";

    /// <summary>DN of the AD domain when it is known (live), otherwise empty.</summary>
    public string DomainDn { get; init; } = string.Empty;

    /// <summary>Finds the AD group with this sAMAccountName (for synchronised cloud groups).</summary>
    public AdGroup? FindAdGroupBySam(string sam)
        => AdGroupsByDn.Values.FirstOrDefault(g => string.Equals(g.SamAccountName, sam, StringComparison.OrdinalIgnoreCase));

    public AdGroup? FindAdGroupByName(string name)
        => AdGroupsByDn.Values.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)
                                                    || string.Equals(g.SamAccountName, name, StringComparison.OrdinalIgnoreCase));

    public CloudGroup? FindCloudGroupByName(string name)
        => CloudGroups.Values.FirstOrDefault(g => string.Equals(g.DisplayName, name, StringComparison.OrdinalIgnoreCase));

    public bool HasAd => AdUsers.Count > 0;
    public bool HasEntra => EntraUsers.Count > 0;

    public Person? SearchPerson(string key)
        => Persons.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<AdGroup> AdGroupsOf(Person person)
    {
        if (person.Ad is null)
        {
            yield break;
        }

        foreach (var dn in person.Ad.MemberOr)
        {
            if (AdGroupsByDn.TryGetValue(dn, out var groupValue))
            {
                yield return groupValue;
            }
            else
            {
                yield return new AdGroup { Name = DnHelp.Name(dn), DistinguishedName = dn, OuPath = DnHelp.OuPathOfObject(dn) };
            }
        }
    }

    public IEnumerable<CloudGroup> CloudGroupsOf(Person person)
    {
        if (person.Entra is null || !CloudMembership.TryGetValue(person.Entra.Id, out var ids))
        {
            yield break;
        }

        foreach (var id in ids)
        {
            if (CloudGroups.TryGetValue(id, out var groupValue))
            {
                yield return groupValue;
            }
        }
    }
}
