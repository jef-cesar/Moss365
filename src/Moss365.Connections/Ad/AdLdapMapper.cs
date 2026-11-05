using System.Globalization;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Connections.Ad;

/// <summary>Converts LDAP attributes (name to values) to the model. Pure, so testable without AD.</summary>
public static class AdLdapMapper
{
    public const int UacDisabled = 0x2;
    public const int UacPasswordNeverExpires = 0x10000;
    public const int UacNormalAccount = 0x200;

    /// <summary>Bit in msDS-User-Account-Control-Computed: the account is locked now.</summary>
    public const int UacLocked = 0x10;

    public static readonly string[] UserAttributes =
    {
        "sAMAccountName", "userPrincipalName", "displayName", "givenName", "sn", "title", "department", "company",
        "physicalDeliveryOfficeName", "l", "description", "mail", "userAccountControl", "distinguishedName", "whenCreated",
        "lastLogonTimestamp", "pwdLastSet", "scriptPath", "memberOf", "telephoneNumber", "mobile", "manager",
        "msDS-User-Account-Control-Computed", "lockoutTime",
    };

    public static readonly string[] GroupAttributes = { "name", "sAMAccountName", "distinguishedName", "groupType", "description", "member", "displayName", "mail", "managedBy" };

    public static AdUser ToUser(IReadOnlyDictionary<string, object?[]> k)
    {
        var uac = Number(k, "userAccountControl") ?? 0;
        var dn = Text(k, "distinguishedName");
        return new AdUser
        {
            SamAccountName = Text(k, "sAMAccountName"),
            UserPrincipalName = Text(k, "userPrincipalName"),
            DisplayName = Text(k, "displayName"),
            GivenName = Text(k, "givenName"),
            Surname = Text(k, "sn"),
            Title = Text(k, "title"),
            Department = Text(k, "department"),
            Company = Text(k, "company"),
            Office = Text(k, "physicalDeliveryOfficeName"),
            City = Text(k, "l"),
            Description = Text(k, "description"),
            EmailAddress = Text(k, "mail"),
            Enabled = (uac & UacDisabled) == 0,
            PasswordNeverExpires = (uac & UacPasswordNeverExpires) != 0,
            LockedOut = Locked(k),
            DistinguishedName = dn,
            OuPath = DnHelp.OuPathOfObject(dn),
            WhenCreated = Date(k, "whenCreated"),
            LastLogonDate = Date(k, "lastLogonTimestamp"),
            PasswordLastSet = Date(k, "pwdLastSet"),
            ScriptPath = Text(k, "scriptPath"),
            MemberOr = Items(k, "memberOf"),
            Phone = Text(k, "telephoneNumber"),
            Mobile = Text(k, "mobile"),
            Manager = Text(k, "manager"),
        };
    }

    public static AdGroup ToGroup(IReadOnlyDictionary<string, object?[]> k)
    {
        var type = Number(k, "groupType") ?? 0;
        var dn = Text(k, "distinguishedName");
        return new AdGroup
        {
            Name = Text(k, "name"),
            SamAccountName = Text(k, "sAMAccountName"),
            DistinguishedName = dn,
            OuPath = DnHelp.OuPathOfObject(dn),
            Category = (type & unchecked((int)0x80000000)) != 0 ? "Security" : "Distribution",
            Scope = (type & 0x2) != 0 ? "Global" : (type & 0x4) != 0 ? "DomainLocal" : (type & 0x8) != 0 ? "Universal" : string.Empty,
            Description = Text(k, "description"),
            DisplayName = Text(k, "displayName"),
            Mail = Text(k, "mail"),
            ManagedBy = Text(k, "managedBy"),
            Members = Items(k, "member"),
            MembersKnown = true,
            CountMembers = k.TryGetValue("member", out var members) ? members.Length : 0,
        };
    }

    /// <summary>
    /// Locked according to the computed attribute; if that is missing, lockoutTime (which stays set until the next
    /// sign-in, even when the lockout has already expired).
    /// </summary>
    private static bool Locked(IReadOnlyDictionary<string, object?[]> k)
    {
        if (Number(k, "msDS-User-Account-Control-Computed") is { } computed)
        {
            return (computed & UacLocked) != 0;
        }

        return k.TryGetValue("lockoutTime", out var w) && w.Length > 0 && w[0] switch
        {
            long l => l > 0,
            int i => i > 0,
            _ => false,
        };
    }

    /// <summary>The state of the domain controller from RootDSE (dsServiceName and highestCommittedUSN).</summary>
    public static AdSnapshot? ToSnapshot(object? domainController, object? usn)
    {
        var dc = Convert.ToString(domainController, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(dc) || !long.TryParse(Convert.ToString(usn, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? null
            : new AdSnapshot(dc, n);
    }

    /// <summary>
    /// Reading only the changes is possible when it is the same domain controller (USNs count per domain controller)
    /// and its counter did not jump back (e.g. after a restore).
    /// </summary>
    public static bool CanContinueFrom(AdSnapshot previous, AdSnapshot now)
        => string.Equals(previous.DomainController, now.DomainController, StringComparison.OrdinalIgnoreCase) && now.HighestUsn >= previous.HighestUsn;

    /// <summary>Puts a value safely in an LDAP filter (RFC 4515): \ * ( ) and NUL are escaped.</summary>
    public static string FilterValue(string value)
    {
        var output = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            output.Append(c switch
            {
                '\\' => "\\5c",
                '*' => "\\2a",
                '(' => "\\28",
                ')' => "\\29",
                '\0' => "\\00",
                _ => c.ToString(),
            });
        }

        return output.ToString();
    }

    /// <summary>DNS name "CONTOSO.LOCAL" to DN "DC=CONTOSO,DC=LOCAL".</summary>
    public static string DomainToDn(string domain)
        => string.Join(',', domain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(d => "DC=" + d));

    /// <summary>An ADsPath may not contain a plain slash; in a DN it must be escaped.</summary>
    public static string LdapPath(string server, string dn)
        => string.IsNullOrWhiteSpace(server) ? $"LDAP://{dn.Replace("/", "\\/")}" : $"LDAP://{server.Trim()}/{dn.Replace("/", "\\/")}";

    private static string Text(IReadOnlyDictionary<string, object?[]> k, string name)
        => k.TryGetValue(name, out var w) && w.Length > 0 && w[0] is not null ? Convert.ToString(w[0], CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> Items(IReadOnlyDictionary<string, object?[]> k, string name)
        => k.TryGetValue(name, out var w) ? w.Where(x => x is not null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToList() : Array.Empty<string>();

    private static int? Number(IReadOnlyDictionary<string, object?[]> k, string name)
    {
        if (!k.TryGetValue(name, out var w) || w.Length == 0 || w[0] is null)
        {
            return null;
        }

        return w[0] switch
        {
            int i => i,
            long l => unchecked((int)l),
            _ => int.TryParse(Convert.ToString(w[0], CultureInfo.InvariantCulture), out var n) ? n : null,
        };
    }

    /// <summary>LDAP returns dates as DateTime (whenCreated) or as a FILETIME number (lastLogonTimestamp,
    /// pwdLastSet).</summary>
    private static string Date(IReadOnlyDictionary<string, object?[]> k, string name)
    {
        if (!k.TryGetValue(name, out var w) || w.Length == 0 || w[0] is null)
        {
            return string.Empty;
        }

        DateTime? date = w[0] switch
        {
            DateTime d => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d,
            long ft when ft > 0 && ft < DateTime.MaxValue.ToFileTimeUtc() => DateTime.FromFileTimeUtc(ft).ToLocalTime(),
            _ => null,
        };
        return date?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
