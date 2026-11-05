using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Tests;

/// <summary>Made-up test data in the form of the CSV files of Export-Inventaris.ps1.</summary>
public static class TestData
{
    public const string Municipality = "OU=GEBRUIKERS,OU=GEMEENTEBESTUUR,DC=test,DC=internal";
    public const string SocialWelfare = "OU=GEBRUIKERS,OU=OCMW,DC=test,DC=internal";
    public const string Vpn = "OU=VPN_USERS,OU=ARCHIVE,DC=test,DC=internal";
    public const string Groups = "OU=GROEPEN,OU=GEMEENTEBESTUUR,DC=test,DC=internal";

    public static string Dn(string name, string ou) => $"CN={name},{ou}";

    public static Dictionary<string, string> AdUser(string sam, string ou, string upn, string department = "", bool enabled = true,
        string lastLogon = "2026-10-01 08:00:00", params string[] groups) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["OU"] = ou,
        ["SamAccountName"] = sam,
        ["UserPrincipalName"] = upn,
        ["DisplayName"] = sam.Replace('.', ' '),
        ["Department"] = department,
        ["EmailAddress"] = upn,
        ["Enabled"] = enabled ? "True" : "False",
        ["DistinguishedName"] = Dn(sam, ou),
        ["LastLogonDate"] = lastLogon,
        ["MemberOf"] = string.Join('|', groups.Select(g => Dn(g, Groups))),
    };

    public static Dictionary<string, string> AdGroup(string name, string scope = "Global", string? ou = null) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = name,
        ["SamAccountName"] = name,
        ["DistinguishedName"] = Dn(name, ou ?? Groups),
        ["OU"] = ou ?? Groups,
        ["Category"] = "Security",
        ["Scope"] = scope,
    };

    public static Dictionary<string, string> EntraUser(string id, string upn, string? sam = null, string department = "",
        bool enabled = true, string userType = "Member", string displayName = "") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = id,
        ["UserPrincipalName"] = upn,
        ["DisplayName"] = displayName.Length > 0 ? displayName : (sam ?? id).Replace('.', ' '),
        ["Mail"] = upn,
        ["AccountEnabled"] = enabled ? "True" : "False",
        ["UserType"] = userType,
        ["Department"] = department,
        ["OnPremisesSyncEnabled"] = sam is null ? "" : "True",
        ["OnPremisesSamAccountName"] = sam ?? "",
    };

    public static Dictionary<string, string> CloudGroup(string id, string name, string type, bool dynamicValue = false, string source = "Cloud", string rule = "",
        string mail = "", string onPremSam = "")
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["DisplayName"] = name,
            ["Type"] = type,
            ["Bron"] = source,
            ["Dynamisch"] = dynamicValue ? "True" : "False",
            ["Lidmaatschapsregel"] = rule,
            ["Mail"] = mail,
            ["OnPremisesSamAccountName"] = onPremSam,
        };

    public static Dictionary<string, string> AdMember(string groupValue, string memberDn, string? ou = null) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groep"] = groupValue,
        ["GroepDN"] = Dn(groupValue, ou ?? Groups),
        ["LidDN"] = memberDn,
    };

    public static Dictionary<string, string> ExoGroup(string name, string type, string smtp) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = type,
        ["DisplayName"] = name,
        ["Alias"] = name.ToLowerInvariant(),
        ["PrimarySmtpAddress"] = smtp,
        ["MemberJoinRestriction"] = "Closed",
        ["ManagedBy"] = "an.peeters@test.be",
    };

    public static Dictionary<string, string> ExoMember(string groupValue, string smtp) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Groep"] = groupValue,
        ["Lid"] = smtp,
        ["LidNaam"] = smtp.Split('@')[0],
    };

    public static Dictionary<string, string> Mailbox(string name, string smtp, string type = "SharedMailbox") => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Type"] = type,
        ["DisplayName"] = name,
        ["Alias"] = smtp.Split('@')[0],
        ["PrimarySmtpAddress"] = smtp,
        ["Hidden"] = "False",
    };

    public static Dictionary<string, string> Permission(string mailbox, string permission, string user) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Postvak"] = mailbox,
        ["Recht"] = permission,
        ["Gebruiker"] = user,
    };

    public static Dictionary<string, string> Member(string groupId, string memberId) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["GroepId"] = groupId,
        ["LidId"] = memberId,
        ["LidType"] = "user",
    };

    /// <summary>A small environment: two municipality users (reference and target), a social services user, a volunteer
    /// without Entra, ...</summary>
    public static InventoryData AppEnvironment() => InventoryLoader.Build(Files());

    /// <summary>Writes the made-up inventory as CSV files, as Export-Inventaris.ps1 creates them.</summary>
    public static void WriteInventory(string folder, Dictionary<string, List<Dictionary<string, string>>>? files = null)
    {
        Directory.CreateDirectory(folder);
        foreach (var (file, rows) in files ?? Files())
        {
            var columns = rows.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            static string Q(string? w) => "\"" + (w ?? string.Empty).Replace("\"", "\"\"") + "\"";
            var rules = new List<string> { string.Join(';', columns.Select(Q)) };
            rules.AddRange(rows.Select(r => string.Join(';', columns.Select(k => Q(r.GetValueOrDefault(k))))));
            File.WriteAllLines(Path.Combine(folder, file), rules);
        }
    }

    public static Dictionary<string, List<Dictionary<string, string>>> Files() => new()
    {
        [InventoryLoader.AdUsers] = new()
        {
            AdUser("an.peeters", Municipality, "an.peeters@test.be", "cultuur", groups: new[] { "gg_wifiIntern", "gg_rww_foxbeleid", "Domain Admins" }),
            AdUser("bo.janssens", Municipality, "bo.janssens@test.be", "cultuur ", groups: new[] { "gg_wifiIntern", "gg_eigen" }),
            AdUser("cas.vos", SocialWelfare, "Cas.Vos@test.be", "gezinszorg"),
            AdUser("dirk.intern", Municipality, "dirk.intern@TEST.INTERNAL", lastLogon: ""),
            AdUser("oud.account", Municipality, "oud.account@test.be", enabled: false),
            AdUser("vpn.leverancier", Vpn, "vpn.leverancier@test.be"),
        },
        [InventoryLoader.AdGroups] = new()
        {
            AdGroup("gg_wifiIntern"),
            AdGroup("gg_rww_foxbeleid"),
            AdGroup("gg_eigen"),
            AdGroup("Domain Admins", ou: "CN=Users,DC=test,DC=internal"),
        },
        [InventoryLoader.EntraUsers] = new()
        {
            EntraUser("e-an", "an.peeters@test.be", "an.peeters", "cultuur"),
            EntraUser("e-bo", "bo.janssens@test.be", "BO.JANSSENS", "cultuur"),
            EntraUser("e-cas", "cas.vos@test.be", "cas.vos", "gezinszorg"),
            EntraUser("e-oud", "oud.account@test.be", "oud.account", enabled: true),
            EntraUser("e-info", "info@test.onmicrosoft.com", displayName: "Info"),
            EntraUser("e-gast", "gast_example.net#EXT#@test.onmicrosoft.com", userType: "Guest", displayName: "Gast"),
        },
        [InventoryLoader.EntraGroups] = new()
        {
            CloudGroup("g-alle", "Alle gebruikers", "Beveiliging", dynamicValue: true, rule: "All Users"),
            CloudGroup("g-sm", "SG-SM-info-Full", "MailBeveiliging", mail: "sg-sm-info-full@test.be"),
            CloudGroup("g-team", "DNST_CULTUUR", "M365"),
            CloudGroup("g-dl", "Gemeentehuis", "Distributie", mail: "gemeentehuis@test.be"),
            CloudGroup("g-ad", "Gesynchroniseerd", "Beveiliging", source: "AD", onPremSam: "gg_rww_foxbeleid"),
            CloudGroup("g-afd", "AFD_VRIJETIJD", "M365"),
            CloudGroup("g-eigen", "WKG_Eigen", "M365"),
        },
        [InventoryLoader.EntraLicences] = new()
        {
            new(StringComparer.OrdinalIgnoreCase) { ["SkuId"] = "sku-spb", ["SkuPartNumber"] = "SPB", ["Toegewezen"] = "10", ["Totaal"] = "10" },
            new(StringComparer.OrdinalIgnoreCase) { ["SkuId"] = "sku-f3", ["SkuPartNumber"] = "DESKLESSPACK", ["Toegewezen"] = "5", ["Totaal"] = "8" },
        },
        [InventoryLoader.ExoRooms] = new()
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Smtp"] = "rm-vz-a@test.be", ["DisplayName"] = "Vergaderzaal A", ["Gebouw"] = "Gemeentehuis", ["Verdieping"] = "2", ["Capaciteit"] = "8",
                ["AutomateProcessing"] = "AutoAccept", ["BookingWindowInDays"] = "600", ["MaximumDurationInMinutes"] = "1440", ["AllowConflicts"] = "False",
                ["AllowRecurringMeetings"] = "True", ["AddOrganizerToSubject"] = "True", ["DeleteSubject"] = "True", ["AgendaRechten"] = "Default=LimitedDetails|Anonymous=None",
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Smtp"] = "rm-vz-b@test.be", ["DisplayName"] = "Vergaderzaal B", ["Gebouw"] = "Gemeentehuis", ["Verdieping"] = "2", ["Capaciteit"] = "8",
                ["AutomateProcessing"] = "AutoAccept", ["BookingWindowInDays"] = "180", ["MaximumDurationInMinutes"] = "1440", ["AllowConflicts"] = "False",
                ["AllowRecurringMeetings"] = "True", ["AddOrganizerToSubject"] = "True", ["DeleteSubject"] = "False", ["AgendaRechten"] = "Default=Reviewer|Anonymous=None",
            },
        },
        [InventoryLoader.AdGroupMembers] = new()
        {
            AdMember("gg_wifiIntern", Dn("an.peeters", Municipality)), AdMember("gg_wifiIntern", Dn("bo.janssens", Municipality)),
            AdMember("gg_rww_foxbeleid", Dn("an.peeters", Municipality)), AdMember("gg_eigen", Dn("bo.janssens", Municipality)),
            AdMember("gg_eigen", Dn("gg_rww_foxbeleid", Groups)),
            AdMember("Domain Admins", Dn("an.peeters", Municipality), "CN=Users,DC=test,DC=internal"),
        },
        [InventoryLoader.ExoGroups] = new()
        {
            ExoGroup("SG-SM-info-Full", "MailUniversalSecurityGroup", "sg-sm-info-full@test.be"),
            ExoGroup("Gemeentehuis", "MailUniversalDistributionGroup", "gemeentehuis@test.be"),
            ExoGroup("rl-gh", "RoomList", "rl-gh@test.be"),
        },
        [InventoryLoader.ExoGroupMembers] = new()
        {
            ExoMember("SG-SM-info-Full", "an.peeters@test.be"),
            ExoMember("Gemeentehuis", "an.peeters@test.be"),
            ExoMember("rl-gh", "rm-vz-a@test.be"),
        },
        [InventoryLoader.ExoMailboxes] = new()
        {
            Mailbox("Info", "info@test.be"),
            Mailbox("Boekhouding", "boekhouding@test.be"),
            Mailbox("Vergaderzaal A", "rm-vz-a@test.be", "RoomMailbox"),
            Mailbox("Afspraken", "afspraken@test.be", "SchedulingMailbox"),
        },
        [InventoryLoader.ExoMailboxPermissions] = new()
        {
            Permission("info@test.be", "FullAccess", "sg-sm-info-full@test.be"),
            Permission("info@test.be", "SendAs", "bo.janssens@test.be"),
            Permission("info@test.be", "FullAccess", "S-1-5-21-111-222-333-1001"),
            Permission("boekhouding@test.be", "FullAccess,SendAs", "an.peeters@test.be"),
        },
        [InventoryLoader.EntraGroupMembers] = new()
        {
            Member("g-alle", "e-an"), Member("g-alle", "e-bo"), Member("g-sm", "e-an"), Member("g-team", "e-an"), Member("g-team", "e-bo"),
            Member("g-dl", "e-an"), Member("g-ad", "e-an"), Member("g-eigen", "e-bo"),
        },
    };

    public static OrganisationConfig Config() => new()
    {
        Company = "Contoso",
        AdDomain = "TEST.INTERNAL",
        SynchronisedOus = new() { "GEMEENTEBESTUUR/GEBRUIKERS", "OCMW/GEBRUIKERS" },
        Populations = new()
        {
            new Population { Name = "Gemeente", Ou = "GEMEENTEBESTUUR/GEBRUIKERS", UpnDomain = "test.be", DefaultAdGroups = new() { "gg_wifiIntern", "bestaat_niet" } },
            new Population { Name = "Vrijwilliger", Ou = "GEMEENTEBESTUUR/GASTGEBRUIKERS", UpnDomain = "example.org", City = "intranet", NoLicence = true },
        },
        Offboarding = new OffboardingRule { Ou = "ARCHIVE/disabled_users" },
        Divisions = new() { new Division { Name = "Vrije tijd", Group = "AFD_VRIJETIJD" }, new Division { Name = "Burger en welzijn" } },
        Departments = new()
        {
            new DepartmentMapping { Department = "cultuur", Division = "Vrije tijd", LogonScript = "cultuur.bat" },
            new DepartmentMapping { Department = "gezinszorg", Division = null },
        },
        Offices = new() { "gemeentehuis", "bibliotheek" },
        Licences = new() { new LicenceChoice { Sku = "DESKLESSPACK", Name = "Microsoft 365 F3" }, new LicenceChoice { Sku = "SPB", Name = "Business Premium" } },
        DynamicRules = new() { new DynamicRule { Group = "Alle gebruikers", Rule = "alle gebruikers" } },
    };

    /// <summary>Made-up Conditional Access: MFA for everyone (except Info and WKG_Eigen), block legacy, administrators
    /// (report), block abroad.</summary>
    public static CaData Ca(DateTimeOffset now, bool groupMembers = true) => new(
        new[]
        {
            new CaPolicy
            {
                Id = "ca-mfa", Name = "MFA voor iedereen", Status = CaStatus.On, UsersIn = new[] { "All" }, UsersOff = new[] { "e-info" },
                GroupsOff = new[] { "g-eigen" }, AppsIn = new[] { "All" }, Checks = new[] { "mfa" }, Changed = now.AddDays(-100),
            },
            new CaPolicy
            {
                Id = "ca-legacy", Name = "Verouderd blokkeren", Status = CaStatus.On, UsersIn = new[] { "All" }, UsersOff = new[] { "e-info" },
                AppsIn = new[] { "All" }, ClientApps = new[] { "exchangeActiveSync", "other" }, Checks = new[] { "block" },
            },
            new CaPolicy
            {
                Id = "ca-beheer", Name = "Beheerders phishingbestendig", Status = CaStatus.Report, RolesIn = new[] { CaData.RoleGlobalAdmin },
                AppsIn = new[] { "All" }, StrengthId = CaData.StrengthPhishingResistant, StrengthName = "Phishingbestendige MFA", Changed = now.AddDays(-60),
            },
            new CaPolicy
            {
                Id = "ca-land", Name = "Buitenland blokkeren", Status = CaStatus.On, UsersIn = new[] { "All" }, AppsIn = new[] { "All" },
                LocationsIn = new[] { "All" }, LocationsOff = new[] { "loc-be" }, Checks = new[] { "block" },
            },
        },
        new[]
        {
            new NamedLocation("loc-be", "België", false, false, new[] { "BE" }),
            new NamedLocation("loc-kantoor", "Gemeentehuis", true, true, new[] { "203.0.113.0/24" }),
        },
        new[] { new AuthenticationStrength(CaData.StrengthPhishingResistant, "Phishingbestendige MFA", true, new[] { "fido2", "windowsHelloForBusiness" }) },
        new Dictionary<string, string> { [CaData.RoleGlobalAdmin] = "Global Administrator" },
        false,
        null,
        groupMembers ? new Dictionary<string, IReadOnlyList<string>> { ["g-eigen"] = new[] { "e-bo" } } : null);
}
