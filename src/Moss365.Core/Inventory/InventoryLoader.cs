namespace Moss365.Core.Inventory;

/// <summary>
/// Loads the output of Export-Inventaris.ps1. Per file the loader takes the newest inventory folder in which that file
/// exists, so that AD, Entra and Exchange may come from different runs.
/// </summary>
public static class InventoryLoader
{
    public const string AdUsers = "ad-gebruikers.csv";
    public const string AdGroups = "ad-groepen.csv";
    public const string EntraUsers = "entra-gebruikers.csv";
    public const string EntraGroups = "entra-groepen.csv";
    public const string EntraGroupMembers = "entra-groepsleden.csv";
    public const string EntraLicences = "entra-licenties.csv";
    public const string ExoRooms = "exo-rooms-overzicht.csv";
    public const string AdGroupMembers = "ad-groepsleden.csv";
    public const string ExoGroups = "exo-groepen.csv";
    public const string ExoGroupMembers = "exo-groepsleden.csv";
    public const string ExoMailboxes = "exo-postvakken-niet-gebruikers.csv";
    public const string ExoMailboxPermissions = "exo-gedeelde-postvakrechten.csv";
    public const string EntraMfa = "entra-mfa.csv";
    public const string EntraRoles = "entra-rollen.csv";

    /// <summary>Conditional Access (JSON, written by the app; Export-Inventaris.ps1 does not create it).</summary>
    public const string EntraCa = "entra-ca.json";

    private static readonly string[] Files =
    {
        AdUsers, AdGroups, AdGroupMembers, EntraUsers, EntraGroups, EntraGroupMembers, EntraLicences,
        ExoRooms, ExoGroups, ExoGroupMembers, ExoMailboxes, ExoMailboxPermissions, EntraMfa, EntraRoles,
    };

    /// <summary>All inventory folders under the base folder, newest first (the name contains yyyyMMdd-HHmm).</summary>
    public static IReadOnlyList<string> SearchFolders(string baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder) || !Directory.Exists(baseFolder))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(baseFolder, "Inventaris-*")
            .OrderByDescending(m => Path.GetFileName(m), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Loads from the given folders; the first folder that contains a file wins.</summary>
    public static InventoryData Load(IEnumerable<string> foldersNewestFirst)
        => Load(foldersNewestFirst, (folder, file) => File.Exists(Path.Combine(folder, file)) ? File.ReadAllText(Path.Combine(folder, file), System.Text.Encoding.UTF8) : null);

    /// <summary>The prefix of the inventory folders in the tenant database.</summary>
    public const string StorePrefix = "inventory";

    /// <summary>The inventory folders in the tenant database (<c>inventory/Inventaris-...</c>), newest first.</summary>
    public static IReadOnlyList<string> SearchFolders(Storage.IFileStore store)
        => store.List(StorePrefix + "/")
            .Select(f => f.Path.Split('/'))
            .Where(p => p.Length == 3 && p[1].StartsWith("Inventaris-", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{StorePrefix}/{p[1]}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(m => m, StringComparer.Ordinal)
            .ToList();

    /// <summary>Loads from inventory folders in the tenant database; the first folder that contains a file wins.</summary>
    public static InventoryData Load(Storage.IFileStore store, IEnumerable<string> foldersNewestFirst)
        => Load(foldersNewestFirst, (folder, file) => store.ReadText($"{folder}/{file}"));

    /// <summary>
    /// Copies inventory folders from disk (the output of Export-Inventaris.ps1, or the folders of an older version) into
    /// the tenant database: the newest ones that are not there yet. Returns how many folders were copied.
    /// </summary>
    public static int ImportFolders(Storage.IFileStore store, string baseFolder, int newest = 5)
    {
        var present = SearchFolders(store).Select(m => m[(m.IndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var folder in SearchFolders(baseFolder).Take(newest).Where(m => !present.Contains(Path.GetFileName(m))))
        {
            foreach (var file in Files.Append(EntraCa).Where(file => File.Exists(Path.Combine(folder, file))))
            {
                store.Write($"{StorePrefix}/{Path.GetFileName(folder)}/{file}", File.ReadAllBytes(Path.Combine(folder, file)));
            }

            count++;
        }

        return count;
    }

    private static InventoryData Load(IEnumerable<string> foldersNewestFirst, Func<string, string, string?> read)
    {
        var folders = foldersNewestFirst.ToList();
        var sources = new List<SourceFile>();
        var tables = new Dictionary<string, List<Dictionary<string, string>>>();

        foreach (var file in Files)
        {
            string? text = null;
            var folder = folders.FirstOrDefault(m => (text = read(m, file)) is not null);
            var rows = text is null ? new List<Dictionary<string, string>>() : CsvReader.Read(text.TrimStart('\uFEFF'));
            tables[file] = rows;
            sources.Add(new SourceFile(file, folder, rows.Count));
        }

        var data = Build(tables, sources);
        string? caText = null;
        var caFolder = folders.FirstOrDefault(m => (caText = read(m, EntraCa)) is not null);
        if (caFolder is null || caText is null)
        {
            return data;
        }

        var ca = CaStorage.FromJson(caText);
        return data with
        {
            ConditionalAccess = ca,
            Sources = sources.Append(new SourceFile(EntraCa, caFolder, ca?.Policies.Count ?? 0)).ToList(),
        };
    }

    /// <summary>Builds the model from tables already read (file name to rows). Also used by tests.</summary>
    public static InventoryData Build(IReadOnlyDictionary<string, List<Dictionary<string, string>>> tables, IReadOnlyList<SourceFile>? sources = null)
    {
        List<Dictionary<string, string>> T(string name) => tables.TryGetValue(name, out var t) ? t : new();

        var adUsers = T(AdUsers).Select(MakeAdUser).Where(g => g.SamAccountName.Length > 0).ToList();
        var adMembers = T(AdGroupMembers)
            .Where(r => K(r, "GroepDN").Length > 0 && K(r, "LidDN").Length > 0)
            .GroupBy(r => K(r, "GroepDN"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => K(r, "LidDN")).ToList(), StringComparer.OrdinalIgnoreCase);
        var membersKnown = tables.ContainsKey(AdGroupMembers) && T(AdGroupMembers).Count > 0;
        var adGroups = T(AdGroups).Select(r => MakeAdGroup(r, adMembers, membersKnown))
            .Where(g => g.DistinguishedName.Length > 0)
            .GroupBy(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var entraUsers = T(EntraUsers).Select(MakeEntraUser).Where(g => g.Id.Length > 0).ToList();
        var cloudGroups = T(EntraGroups).Select(MakeCloudGroup)
            .Where(g => g.Id.Length > 0)
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var membership = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var memberNames = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in T(EntraGroupMembers))
        {
            var memberId = W(row, "LidId");
            var groupId = W(row, "GroepId");
            if (memberId.Length == 0 || groupId.Length == 0)
            {
                continue;
            }

            var memberType = K(row, "LidType");
            if (memberType.Length > 0 && !memberType.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                memberNames[memberId] = (memberType, W(row, "Lid"));
            }

            if (!membership.TryGetValue(memberId, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                membership[memberId] = set;
            }

            set.Add(groupId);
        }

        var exoMembers = T(ExoGroupMembers)
            .GroupBy(r => W(r, "Groep").Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExoMember>)g.Select(r => new ExoMember(K(r, "Lid"), W(r, "LidNaam"))).ToList(), StringComparer.OrdinalIgnoreCase);

        return Compose(adUsers, adGroups.Values.ToList(), entraUsers, cloudGroups.Values.ToList(), membership,
            T(ExoRooms).Select(MakeRoom).Where(r => r.Smtp.Length > 0).ToList(),
            T(EntraLicences).Select(MakeLicence).Where(l => l.SkuPartNumber.Length > 0).ToList(),
            sources ?? Array.Empty<SourceFile>(), "Inventaris", string.Empty) with
        {
            CloudMemberNames = memberNames,
            ExoGroups = T(ExoGroups).Select(r => MakeExoGroup(r, exoMembers)).Where(g => g.DisplayName.Length > 0).ToList(),
            Mailboxes = T(ExoMailboxes).Select(MakeMailbox)
                .Where(p => p.Smtp.Length > 0 && !p.Type.Equals("RoomMailbox", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            MailboxPermissions = T(ExoMailboxPermissions)
                .Select(r => new MailboxPermission(K(r, "Postvak"), K(r, "Recht"), K(r, "Adres").Length > 0 ? K(r, "Adres") : K(r, "Gebruiker")))
                .Where(r => r.Mailbox.Length > 0 && r.Trustee.Length > 0)
                .SelectMany(r => r.Permission.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => r with { Permission = x }))
                .ToList(),
            Security = T(EntraMfa).Count + T(EntraRoles).Count == 0 ? null : new SecurityData(
                T(EntraMfa).Select(r => new MfaRegistration(K(r, "Id"), K(r, "UserPrincipalName"), B(r, "IsMfaRegistered"), B(r, "IsMfaCapable"),
                    K(r, "Methods").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), K(r, "DefaultMethod"), B(r, "IsAdmin")))
                    .Where(m => m.UserId.Length > 0).ToList(),
                T(EntraRoles).Select(r => new RoleMember(K(r, "Rol"), K(r, "LidId"), W(r, "LidNaam"), K(r, "LidUpn"), K(r, "LidSoort"), K(r, "RoleTemplateId")))
                    .Where(r => r.Role.Length > 0).ToList(),
                T(EntraMfa).Count == 0 ? "Niet in de inventaris." : null,
                T(EntraRoles).Count == 0 ? "Niet in de inventaris." : null),
        };
    }

    /// <summary>Puts the model together from separate parts; used by the inventory and by live loading.</summary>
    public static InventoryData Compose(
        IReadOnlyList<AdUser> adUsers,
        IReadOnlyList<AdGroup> adGroups,
        IReadOnlyList<EntraUser> entraUsers,
        IReadOnlyList<CloudGroup> cloudGroups,
        IReadOnlyDictionary<string, HashSet<string>> membership,
        IReadOnlyList<Room> rooms,
        IReadOnlyList<Licence> licences,
        IReadOnlyList<SourceFile> sources,
        string origin,
        string domainDn) => new()
    {
        AdUsers = adUsers,
        EntraUsers = entraUsers,
        AdGroupsByDn = adGroups.Where(g => g.DistinguishedName.Length > 0)
            .GroupBy(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
        CloudGroups = cloudGroups.Where(g => g.Id.Length > 0)
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
        CloudMembership = membership.ToDictionary(k => k.Key, k => (IReadOnlySet<string>)k.Value, StringComparer.OrdinalIgnoreCase),
        Persons = LinkPersons(adUsers, entraUsers),
        Rooms = rooms.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
        Licences = licences,
        Sources = sources,
        Origin = origin,
        DomainDn = domainDn,
    };

    /// <summary>Links AD and Entra accounts via sAMAccountName (onPremisesSamAccountName in Entra).</summary>
    public static IReadOnlyList<Person> LinkPersons(IReadOnlyList<AdUser> ad, IReadOnlyList<EntraUser> entra)
    {
        var entraOnSam = entra
            .Where(e => e.OnPremisesSyncEnabled && e.OnPremisesSamAccountName.Length > 0)
            .GroupBy(e => e.OnPremisesSamAccountName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var persons = new List<Person>(ad.Count + entra.Count);
        foreach (var a in ad)
        {
            entraOnSam.TryGetValue(a.SamAccountName, out var e);
            if (e is not null)
            {
                used.Add(e.Id);
            }

            persons.Add(new Person { Key = "ad:" + a.SamAccountName.ToLowerInvariant(), Ad = a, Entra = e });
        }

        foreach (var e in entra.Where(e => !used.Contains(e.Id)))
        {
            persons.Add(new Person { Key = "entra:" + e.Id.ToLowerInvariant(), Entra = e });
        }

        return persons.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AdUser MakeAdUser(Dictionary<string, string> r)
    {
        var dn = K(r, "DistinguishedName");
        var ouDn = K(r, "OU");
        return new AdUser
        {
            SamAccountName = K(r, "SamAccountName"),
            UserPrincipalName = W(r, "UserPrincipalName"),
            DisplayName = W(r, "DisplayName"),
            GivenName = W(r, "GivenName"),
            Surname = W(r, "Surname"),
            Title = W(r, "Title"),
            Department = W(r, "Department"),
            Company = W(r, "Company"),
            Office = W(r, "Office"),
            City = W(r, "City"),
            Description = W(r, "Description"),
            EmailAddress = W(r, "EmailAddress"),
            Enabled = B(r, "Enabled"),
            DistinguishedName = dn,
            OuPath = ouDn.Length > 0 ? DnHelp.OuPath(ouDn) : DnHelp.OuPathOfObject(dn),
            WhenCreated = W(r, "whenCreated"),
            LastLogonDate = W(r, "LastLogonDate"),
            PasswordLastSet = W(r, "PasswordLastSet"),
            PasswordNeverExpires = B(r, "PasswordNeverExpires"),
            LockedOut = B(r, "LockedOut"),
            ScriptPath = W(r, "ScriptPath"),
            MemberOr = Items(r, "MemberOf"),
            Phone = W(r, "OfficePhone"),
            Mobile = W(r, "MobilePhone"),
            Manager = W(r, "Manager"),
        };
    }

    private static AdGroup MakeAdGroup(Dictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<string>> members, bool membersKnown)
    {
        var dn = K(r, "DistinguishedName");
        var ou = K(r, "OU");
        var managedBy = K(r, "ManagedBy");
        return new AdGroup
        {
            Mail = K(r, "Mail"),
            DisplayName = W(r, "DisplayName"),
            ManagedBy = managedBy.Contains('=') ? managedBy : string.Empty,
            Members = members.GetValueOrDefault(dn) ?? Array.Empty<string>(),
            MembersKnown = membersKnown,
            Name = W(r, "Name"),
            SamAccountName = K(r, "SamAccountName"),
            DistinguishedName = dn,
            OuPath = ou.Length > 0 ? DnHelp.OuPath(ou) : DnHelp.OuPathOfObject(dn),
            Category = W(r, "Category"),
            Scope = W(r, "Scope"),
            Description = W(r, "Description"),
            CountMembers = int.TryParse(K(r, "AantalLeden"), out var n) ? n : null,
        };
    }

    private static EntraUser MakeEntraUser(Dictionary<string, string> r) => new()
    {
        Id = K(r, "Id"),
        UserPrincipalName = W(r, "UserPrincipalName"),
        DisplayName = W(r, "DisplayName"),
        GivenName = W(r, "GivenName"),
        Surname = W(r, "Surname"),
        Mail = W(r, "Mail"),
        MailNickname = W(r, "MailNickname"),
        AccountEnabled = B(r, "AccountEnabled"),
        UserType = W(r, "UserType"),
        Department = W(r, "Department"),
        JobTitle = W(r, "JobTitle"),
        OfficeLocation = W(r, "OfficeLocation"),
        Phone = W(r, "BusinessPhone"),
        Mobile = W(r, "MobilePhone"),
        ManagerId = W(r, "ManagerId"),
        CompanyName = W(r, "CompanyName"),
        City = W(r, "City"),
        UsageLocation = W(r, "UsageLocation"),
        CreatedDateTime = W(r, "CreatedDateTime"),
        OnPremisesSyncEnabled = B(r, "OnPremisesSyncEnabled"),
        OnPremisesSamAccountName = K(r, "OnPremisesSamAccountName"),
        OnPremisesDistinguishedName = W(r, "OnPremisesDistinguishedName"),
        Licences = Items(r, "Licenties"),
        LastSignIn = W(r, "LaatsteAanmelding"),
    };

    private static CloudGroup MakeCloudGroup(Dictionary<string, string> r) => new()
    {
        Id = K(r, "Id"),
        DisplayName = W(r, "DisplayName"),
        Description = W(r, "Description"),
        Mail = K(r, "Mail"),
        OnPremisesSamAccountName = K(r, "OnPremisesSamAccountName"),
        Type = K(r, "Type") switch
        {
            "M365" => CloudGroupType.Microsoft365,
            "Beveiliging" => CloudGroupType.Security,
            "MailBeveiliging" => CloudGroupType.MailSecurity,
            "Distributie" => CloudGroupType.Distribution,
            _ => CloudGroupType.Unknown,
        },
        FromAd = string.Equals(K(r, "Bron"), "AD", StringComparison.OrdinalIgnoreCase),
        Dynamic = B(r, "Dynamisch"),
        MembershipRule = W(r, "Lidmaatschapsregel"),
        GroupLicence = B(r, "GroepLicentie"),
        CountMembers = int.TryParse(K(r, "AantalLeden"), out var n) ? n : null,
    };

    /// <summary>Raw value, not trimmed: spelling variants such as a trailing space must stay visible.</summary>
    private static Room MakeRoom(Dictionary<string, string> r) => new()
    {
        Smtp = K(r, "Smtp"),
        DisplayName = W(r, "DisplayName"),
        Alias = K(r, "Alias"),
        Building = W(r, "Gebouw"),
        Floor = K(r, "Verdieping"),
        FloorLabel = W(r, "VerdiepingLabel"),
        Capacity = I(r, "Capaciteit"),
        Hidden = B(r, "Hidden"),
        AutomateProcessing = K(r, "AutomateProcessing"),
        AllBookInPolicy = B(r, "AllBookInPolicy"),
        AllRequestInPolicy = B(r, "AllRequestInPolicy"),
        BookInPolicy = W(r, "BookInPolicy"),
        ResourceDelegates = W(r, "ResourceDelegates"),
        BookingWindowInDays = I(r, "BookingWindowInDays"),
        MaximumDurationInMinutes = I(r, "MaximumDurationInMinutes"),
        AllowConflicts = B(r, "AllowConflicts"),
        AllowRecurringMeetings = B(r, "AllowRecurringMeetings"),
        AddOrganizerToSubject = B(r, "AddOrganizerToSubject"),
        DeleteSubject = B(r, "DeleteSubject"),
        CalendarPermissions = W(r, "AgendaRechten"),
    };

    private static ExoGroup MakeExoGroup(Dictionary<string, string> r, IReadOnlyDictionary<string, IReadOnlyList<ExoMember>> members) => new()
    {
        Type = K(r, "Type"),
        DisplayName = W(r, "DisplayName").Trim(),
        Name = K(r, "Name"),
        Alias = K(r, "Alias"),
        Smtp = K(r, "PrimarySmtpAddress"),
        IsDirSynced = B(r, "IsDirSynced"),
        MemberJoinRestriction = K(r, "MemberJoinRestriction"),
        ManagedBy = K(r, "ManagedBy") is "ja" or "nee" ? Array.Empty<string>() : Items(r, "ManagedBy"),
        CountMembers = I(r, "AantalLeden"),
        Members = members.GetValueOrDefault(W(r, "DisplayName").Trim()) ?? Array.Empty<ExoMember>(),
    };

    private static Mailbox MakeMailbox(Dictionary<string, string> r) => new()
    {
        Type = K(r, "Type"),
        DisplayName = W(r, "DisplayName"),
        Alias = K(r, "Alias"),
        Smtp = K(r, "PrimarySmtpAddress"),
        Hidden = B(r, "Hidden"),
        IsDirSynced = B(r, "IsDirSynced"),
        WhenCreated = W(r, "WhenCreated"),
        Forwarding = K(r, "Doorsturen") is "geen" ? string.Empty : K(r, "Doorsturen"),
        ForwardingTo = K(r, "DoorsturenNaar"),
        SaveForwardingAnd = K(r, "DoorsturenEnBewaren").Length > 0 ? B(r, "DoorsturenEnBewaren") : null,
        AutoReply = K(r, "AutoAntwoord"),
        AutoReplyInternal = W(r, "AutoAntwoordIntern"),
        AutoReplyExternal = W(r, "AutoAntwoordExtern"),
    };

    private static Licence MakeLicence(Dictionary<string, string> r)
        => new(K(r, "SkuId"), K(r, "SkuPartNumber"), I(r, "Toegewezen") ?? 0, I(r, "Totaal") ?? 0);

    private static int? I(Dictionary<string, string> r, string column) => int.TryParse(K(r, column), out var n) ? n : null;

    private static string W(Dictionary<string, string> r, string column) => r.TryGetValue(column, out var v) ? v : string.Empty;

    /// <summary>Trimmed value for keys and technical fields.</summary>
    private static string K(Dictionary<string, string> r, string column) => W(r, column).Trim();

    private static bool B(Dictionary<string, string> r, string column) => string.Equals(K(r, column), "True", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Items(Dictionary<string, string> r, string column)
        => W(r, column).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
