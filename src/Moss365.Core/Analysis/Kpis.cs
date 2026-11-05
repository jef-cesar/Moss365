using System.Globalization;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Core.Analysis;

/// <summary>Which list the rows behind a KPI are on.</summary>
public enum KpiList
{
    Users,
    Mailboxes,
    Groups,
    Rooms,
    ChangeLog,
    CaPolicies,
    CaCheck,
}

/// <summary>
/// A KPI: a number on a tile or bar, and the list with exactly the rows behind that number. The number is always the
/// count of rows of that list (<see cref="Kpis.Count"/>), so tile and list never differ. With a parameter (e.g.
/// <c>afdeling:Vrije tijd</c>) it is one KPI per value.
/// </summary>
public sealed record Kpi(string Key, string Label, KpiList Items, bool Attention = false)
{
    public string Page => Items switch
    {
        KpiList.Users => "gebruikers",
        KpiList.Mailboxes => "postvakken",
        KpiList.Groups => "groepen",
        KpiList.Rooms => "rooms",
        KpiList.ChangeLog => "logboek",
        _ => "conditional-access",
    };

    /// <summary>The page with the filter already applied.</summary>
    public string Link => $"{Page}?kpi={Uri.EscapeDataString(Key)}";

    /// <summary>The value after the colon (for a KPI with a parameter), otherwise empty.</summary>
    public string Value => Key.IndexOf(':') is var i and >= 0 ? Key[(i + 1)..] : string.Empty;
}

/// <summary>
/// The data the KPIs compute from, with what is expensive (the Conditional Access check) computed once. Create a new
/// source when the data changes.
/// </summary>
public sealed class KpiSource
{
    private IReadOnlyList<Person>? _staff;
    private IReadOnlyList<Person>? _active;
    private IReadOnlyList<CaFinding>? _ca;

    public KpiSource(InventoryData data, OrganisationConfig config, DateTime now)
    {
        Data = data;
        Config = config;
        Now = now;
    }

    public InventoryData Data { get; }
    public OrganisationConfig Config { get; }
    public DateTime Now { get; }

    /// <summary>Employees: no guests, no accounts behind shared mailboxes and rooms.</summary>
    public IReadOnlyList<Person> Staff => _staff ??= Data.Persons.Where(p => !p.IsGuest && !Data.Index.IsMailboxAccount(p)).ToList();

    public IReadOnlyList<Person> Active => _active ??= Staff.Where(p => p.Enabled).ToList();

    public IReadOnlyList<CaFinding> CaFindings => _ca ??= Data.ConditionalAccess is { Message: null } ? CaCheck.Check(Data, Config, Now) : Array.Empty<CaFinding>();
}

/// <summary>All KPIs of the app in one place: the label, the list they link to and which rows belong to it.</summary>
public static class Kpis
{
    // Accounts
    public static readonly Kpi Active = new("actief", "actieve medewerkers", KpiList.Users);
    public static readonly Kpi Disabled = new("uitgeschakeld", "uitgeschakeld", KpiList.Users);
    public static readonly Kpi Guests = new("gasten", "gastgebruikers", KpiList.Users);
    public static readonly Kpi AdAndEntra = new("ad-en-entra", "AD + Entra", KpiList.Users);
    public static readonly Kpi OnlyAd = new("alleen-ad", "alleen AD", KpiList.Users);
    public static readonly Kpi OnlyEntra = new("alleen-entra", "alleen Entra", KpiList.Users);
    public static readonly Kpi MailboxAccounts = new("postvakaccounts", "postvak- en roomaccounts (niet meegeteld)", KpiList.Users);

    // Points of attention (active employees)
    public static readonly Kpi Inactive90 = new("inactief-90", "niet aangemeld in 90 dagen", KpiList.Users, Attention: true);
    public static readonly Kpi Inactive180 = new("inactief-180", "niet aangemeld in 180 dagen", KpiList.Users, Attention: true);
    public static readonly Kpi NeverSignedIn = new("nooit-aangemeld", "nooit aangemeld (of onbekend)", KpiList.Users, Attention: true);
    public static readonly Kpi PasswordExpiresNever = new("wachtwoord-verloopt-nooit", "wachtwoord verloopt nooit", KpiList.Users, Attention: true);
    public static readonly Kpi Privileged = new("bevoorrecht", "in een bevoorrechte AD-groep", KpiList.Users, Attention: true);
    public static readonly Kpi PrivilegedAll = new("bevoorrecht-alle", "in een bevoorrechte AD-groep (ook uitgeschakeld)", KpiList.Users);

    // Security
    public static readonly Kpi WithoutMfa = new("zonder-mfa", "actieve medewerkers zonder MFA", KpiList.Users, Attention: true);
    public static readonly Kpi AdministratorsWithoutMfa = new("beheerders-zonder-mfa", "beheerders zonder MFA", KpiList.Users, Attention: true);
    public static readonly Kpi WithAdminRole = new("beheerrol", "accounts met een Entra-beheerrol", KpiList.Users);
    public static readonly Kpi GlobalAdministrators = new("global-admin", "Global Administrators", KpiList.Users);
    public static readonly Kpi OutsideMfaPolicy = new("buiten-mfa-beleid", "medewerkers buiten elk MFA-beleid", KpiList.Users, Attention: true);

    // Shared mailboxes
    public static readonly Kpi SignInAllowed = new("kunnen-aanmelden", "gedeelde postvakken waarmee je kan aanmelden", KpiList.Mailboxes, Attention: true);
    public static readonly Kpi WithDirectPermissions = new("rechtstreeks", "met rechtstreekse rechten op personen", KpiList.Mailboxes, Attention: true);
    public static readonly Kpi WithoutAccessGroup = new("zonder-toegangsgroep", "zonder toegangsgroep", KpiList.Mailboxes, Attention: true);

    // Groups and rooms
    public static readonly Kpi DynamicGroups = new("dynamisch", "dynamisch", KpiList.Groups);
    public static readonly Kpi EmptyGroups = new("zonder-leden", "zonder leden", KpiList.Groups, Attention: true);
    public static readonly Kpi RoomsWithDeviation = new("afwijkend", "wijken af van de standaard", KpiList.Rooms, Attention: true);

    // Change log (7 days)
    public static readonly Kpi Succeeded7 = new("gelukt-7", "wijzigingen gelukt (7 dagen)", KpiList.ChangeLog);
    public static readonly Kpi Failed7 = new("mislukt-7", "mislukt (7 dagen)", KpiList.ChangeLog, Attention: true);

    // Conditional Access
    public static readonly Kpi CaOn = new("ca-aan", "beleiden aan", KpiList.CaPolicies);
    public static readonly Kpi CaReport = new("ca-rapport", "alleen rapport", KpiList.CaPolicies);
    public static readonly Kpi CaOff = new("ca-uit", "uit", KpiList.CaPolicies);
    public static readonly Kpi CaProblems = new("ca-problemen", "problemen in de controle", KpiList.CaCheck, Attention: true);
    public static readonly Kpi CaAttention = new("ca-aandacht", "punten om na te kijken", KpiList.CaCheck, Attention: true);

    /// <summary>The fixed KPIs (without a parameter).</summary>
    public static IReadOnlyList<Kpi> Fixed { get; } = typeof(Kpis).GetFields()
        .Where(f => f.IsStatic && f.FieldType == typeof(Kpi))
        .Select(f => (Kpi)f.GetValue(null)!)
        .ToList();

    // KPIs with a parameter: one per value
    public static Kpi Source(string source) => new($"bron:{source}", source, KpiList.Users);
    public static Kpi Division(string division) => new($"afdeling:{division}", division, KpiList.Users);
    public static Kpi Licence(string sku) => new($"licentie:{sku}", sku, KpiList.Users);
    /// <summary>Per MFA method, by the readable name (<see cref="SecurityStatistics.MethodName"/>).</summary>
    public static Kpi Method(string name) => new($"methode:{name}", name, KpiList.Users);
    public static Kpi PermissionModel(string model) => new($"rechtenmodel:{model}", model, KpiList.Mailboxes);

    // For the charts of Statistics (BiStatistics)
    public static Kpi SignInEvent(string disk) => new($"aanmelding:{disk}", disk switch
    {
        "0-30" => "aangemeld in de laatste 30 dagen",
        "30-90" => "laatst aangemeld 30 tot 90 dagen geleden",
        "90-180" => "laatst aangemeld 90 tot 180 dagen geleden",
        "180+" => "laatst aangemeld meer dan 180 dagen geleden",
        _ => "nooit aangemeld (of onbekend)",
    }, KpiList.Users);

    public static Kpi Password(string disk) => new($"wachtwoord:{disk}", disk switch
    {
        "0-90" => "wachtwoord jonger dan 90 dagen",
        "90-365" => "wachtwoord 90 dagen tot 1 jaar oud",
        "365-730" => "wachtwoord 1 tot 2 jaar oud",
        "730+" => "wachtwoord ouder dan 2 jaar",
        _ => "leeftijd van het wachtwoord onbekend",
    }, KpiList.Users);

    public static Kpi MfaStatus(string status) => new($"mfa:{status}", status switch
    {
        "ja" => "met MFA",
        "nee" => "zonder MFA",
        _ => "MFA onbekend",
    }, KpiList.Users);

    public static Kpi Office(string office) => new($"kantoor:{office}", office, KpiList.Users);
    public static Kpi GuestDomainKpi(string domain) => new($"gastdomein:{domain}", $"gasten van {domain}", KpiList.Users);
    public static Kpi Created(string month) => new($"aangemaakt:{month}", $"aangemaakt in {month}", KpiList.Users);

    /// <summary>All accounts (also disabled) of a division: the filter above the charts.</summary>
    public static Kpi DivisionAll(string division) => new($"afdeling-alle:{division}", $"afdeling {division}", KpiList.Users);

    public const string NoOffice = "(geen kantoor)";

    public static string OfficeOf(Person p)
        => p.Ad?.Office is { Length: > 0 } o ? o.Trim() : p.Entra?.OfficeLocation is { Length: > 0 } e ? e.Trim() : NoOffice;

    /// <summary>The domain of the own organisation of a guest (from the e-mail address or the UPN with
    /// #EXT#).</summary>
    public static string GuestDomain(Person p)
    {
        if (p.Entra?.Mail is { } mail && mail.IndexOf('@') is var at and > 0 && !mail.Contains("#EXT#", StringComparison.OrdinalIgnoreCase))
        {
            return mail[(at + 1)..].ToLowerInvariant();
        }

        var upn = p.Upn;
        var ext = upn.IndexOf("#EXT#", StringComparison.OrdinalIgnoreCase);
        if (ext > 0 && upn.LastIndexOf('_', ext) is var dash and > 0)
        {
            return upn[(dash + 1)..ext].ToLowerInvariant();
        }

        return "(onbekend)";
    }

    private static DateTime? Date(string? text)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    public static Kpi GroupKind(string kind) => new($"groepsoort:{kind}", kind, KpiList.Groups);

    /// <summary>The KPI for a key from the address bar, or null when it does not exist.</summary>
    public static Kpi? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (Fixed.FirstOrDefault(k => k.Key == key) is { } fixedValue)
        {
            return fixedValue;
        }

        // Composite (e.g. a bar with the division filter): the rows that are in every part
        if (key.Contains('|', StringComparison.Ordinal))
        {
            var share = key.Split('|').Select(d => d.Contains('|', StringComparison.Ordinal) ? null : Find(d)).ToList();
            return share.All(d => d is { Items: KpiList.Users }) && share.Count > 1
                ? new Kpi(key, string.Join(" · ", share.Select(d => d!.Label)), KpiList.Users, share[0]!.Attention)
                : null;
        }

        var i = key.IndexOf(':');
        if (i <= 0 || i == key.Length - 1)
        {
            return null;
        }

        var value = key[(i + 1)..];
        return key[..i] switch
        {
            "bron" => Source(value),
            "afdeling" => Division(value),
            "licentie" => Licence(value),
            "methode" => Method(value),
            "rechtenmodel" => PermissionModel(value),
            "groepsoort" => GroupKind(value),
            "aanmelding" when value is "0-30" or "30-90" or "90-180" or "180+" or "nooit" => SignInEvent(value),
            "wachtwoord" when value is "0-90" or "90-365" or "365-730" or "730+" or "onbekend" => Password(value),
            "mfa" when value is "ja" or "nee" or "onbekend" => MfaStatus(value),
            "kantoor" => Office(value),
            "gastdomein" => GuestDomainKpi(value),
            "aangemaakt" when DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => Created(value),
            "afdeling-alle" => DivisionAll(value),
            _ => null,
        };
    }

    /// <summary>The number on the tile: the count of rows in the list behind it.</summary>
    public static int Count(Kpi k, KpiSource b, IEnumerable<LogEntry>? changeLog = null) => k.Items switch
    {
        KpiList.Users => Persons(k, b).Count,
        KpiList.Mailboxes => Mailboxes(k, b).Count,
        KpiList.Groups => Groups(k, b).Count,
        KpiList.Rooms => Rooms(k, b).Count,
        KpiList.ChangeLog => LogLines(k, changeLog ?? Array.Empty<LogEntry>(), new DateTimeOffset(b.Now)).Count,
        KpiList.CaPolicies => Policies(k, b).Count,
        _ => Findings(k, b).Count,
    };

    // ---------------------------------------------------------------- persons

    public static IReadOnlyList<Person> Persons(Kpi k, KpiSource b)
    {
        if (k.Key.Contains('|', StringComparison.Ordinal))
        {
            var share = k.Key.Split('|').Select(Find).ToList();
            if (share.Any(x => x is null))
            {
                return Array.Empty<Person>();
            }

            var rest = share.Skip(1).Select(x => Persons(x!, b).ToHashSet()).ToList();
            return Persons(share[0]!, b).Where(p => rest.All(r => r.Contains(p))).ToList();
        }

        var d = b.Data;
        var now = b.Now;
        var security = d.Security;
        IEnumerable<Person> rows = k.Key switch
        {
            "actief" => b.Active,
            "uitgeschakeld" => b.Staff.Where(p => !p.Enabled),
            "gasten" => d.Persons.Where(p => p.IsGuest),
            "ad-en-entra" => b.Staff.Where(p => p.Ad is not null && p.Entra is not null),
            "alleen-ad" => b.Staff.Where(p => p.Ad is not null && p.Entra is null),
            "alleen-entra" => b.Staff.Where(p => p.Ad is null && p.Entra is not null),
            "postvakaccounts" => d.Persons.Where(p => !p.IsGuest && d.Index.IsMailboxAccount(p)),
            "inactief-90" => b.Active.Where(p => Statistic.LastSignIn(p) is { } l && l < now.AddDays(-90)),
            "inactief-180" => b.Active.Where(p => Statistic.LastSignIn(p) is { } l && l < now.AddDays(-180)),
            "nooit-aangemeld" => b.Active.Where(p => Statistic.LastSignIn(p) is null),
            "wachtwoord-verloopt-nooit" => b.Active.Where(p => p.Ad?.PasswordNeverExpires == true),
            "bevoorrecht" => b.Active.Where(p => d.AdGroupsOf(p).Any(MirrorUser.IsPrivileged)),
            "bevoorrecht-alle" => d.Persons.Where(p => p.Ad is not null && d.AdGroupsOf(p).Any(MirrorUser.IsPrivileged)),
            "zonder-mfa" => security is null ? Enumerable.Empty<Person>() : b.Active.Where(p => security.MfaOf(p.Entra?.Id) is { MfaRegistered: false }),
            "beheerders-zonder-mfa" => WithRole(d, r => SecurityData.IsCriticalRole(r.Role) && security!.MfaOf(r.MemberId) is { MfaRegistered: false }),
            "beheerrol" => WithRole(d, _ => true),
            "global-admin" => WithRole(d, r => r.Role == "Global Administrator"),
            "buiten-mfa-beleid" => b.CaFindings.FirstOrDefault(x => x.Subject == "Medewerkers zonder MFA-beleid")?.Persons ?? Enumerable.Empty<Person>(),
            _ when k.Key.StartsWith("bron:", StringComparison.Ordinal) => d.Persons.Where(p => p.Source == k.Value),
            _ when k.Key.StartsWith("aanmelding:", StringComparison.Ordinal) => b.Active.Where(p => Statistic.LastSignIn(p) is var l && k.Value switch
            {
                "0-30" => l >= now.AddDays(-30),
                "30-90" => l < now.AddDays(-30) && l >= now.AddDays(-90),
                "90-180" => l < now.AddDays(-90) && l >= now.AddDays(-180),
                "180+" => l < now.AddDays(-180),
                _ => l is null,
            }),
            _ when k.Key.StartsWith("wachtwoord:", StringComparison.Ordinal) => b.Active.Where(p => p.Ad is not null).Where(p => Date(p.Ad!.PasswordLastSet) is var w && k.Value switch
            {
                "0-90" => w >= now.AddDays(-90),
                "90-365" => w < now.AddDays(-90) && w >= now.AddDays(-365),
                "365-730" => w < now.AddDays(-365) && w >= now.AddDays(-730),
                "730+" => w < now.AddDays(-730),
                _ => w is null,
            }),
            _ when k.Key.StartsWith("mfa:", StringComparison.Ordinal) => b.Active.Where(p => (security?.MfaOf(p.Entra?.Id)) is var m && k.Value switch
            {
                "ja" => m is { MfaRegistered: true },
                "nee" => m is { MfaRegistered: false },
                _ => m is null,
            }),
            _ when k.Key.StartsWith("kantoor:", StringComparison.Ordinal) => b.Active.Where(p => OfficeOf(p) == k.Value),
            _ when k.Key.StartsWith("gastdomein:", StringComparison.Ordinal) => d.Persons.Where(p => p.IsGuest && GuestDomain(p) == k.Value),
            _ when k.Key.StartsWith("aangemaakt:", StringComparison.Ordinal) => b.Staff.Where(p => Date(p.Ad?.WhenCreated is { Length: > 0 } w ? w : p.Entra?.CreatedDateTime) is { } a
                && a.ToString("yyyy-MM", CultureInfo.InvariantCulture) == k.Value),
            _ when k.Key.StartsWith("afdeling-alle:", StringComparison.Ordinal) => d.Persons.Where(p => !p.IsGuest && (b.Config.DivisionFor(p.Department)?.Name ?? Statistic.NoDivision) == k.Value),
            _ when k.Key.StartsWith("afdeling:", StringComparison.Ordinal) => b.Active.Where(p => (b.Config.DivisionFor(p.Department)?.Name ?? Statistic.NoDivision) == k.Value),
            _ when k.Key.StartsWith("licentie:", StringComparison.Ordinal) => d.Persons.Where(p => p.Entra?.Licences.Contains(k.Value, StringComparer.OrdinalIgnoreCase) == true),
            _ when k.Key.StartsWith("methode:", StringComparison.Ordinal) => security is null ? Enumerable.Empty<Person>()
                : b.Active.Where(p => security.MfaOf(p.Entra?.Id) is { } m && m.Methods.Any(x => SecurityStatistics.MethodName(x) == k.Value)),
            _ => Enumerable.Empty<Person>(),
        };
        return rows.Distinct().ToList();
    }

    /// <summary>Users (no groups or apps) with an Entra admin role that meets the condition.</summary>
    private static IEnumerable<Person> WithRole(InventoryData d, Func<RoleMember, bool> condition)
    {
        if (d.Security is not { } b)
        {
            return Enumerable.Empty<Person>();
        }

        var ids = b.Roles.Where(r => r.MemberKind is "user" or "").Where(condition).Select(r => r.MemberId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return d.Persons.Where(p => p.Entra?.Id is { } id && ids.Contains(id));
    }

    // ---------------------------------------------------------------- mailboxes, groups, rooms

    /// <summary>How access to a shared mailbox is arranged.</summary>
    public static string PermissionModelOf(InventoryData d, Mailbox p)
    {
        var r = d.Index.PermissionsOn(p);
        return (r.Any(x => !x.Direct), r.Any(x => x.Direct)) switch
        {
            (true, false) => "Alleen via groep",
            (true, true) => "Groep en rechtstreeks",
            (false, true) => "Alleen rechtstreeks",
            _ => "Zonder rechten",
        };
    }

    public static IReadOnlyList<string> PermissionModels { get; } = new[] { "Alleen via groep", "Groep en rechtstreeks", "Alleen rechtstreeks", "Zonder rechten" };

    public static IReadOnlyList<Mailbox> Mailboxes(Kpi k, KpiSource b)
    {
        var d = b.Data;
        var shared = d.Mailboxes.Where(p => p.IsShared);
        return (k.Key switch
        {
            "kunnen-aanmelden" => shared.Where(p => d.Index.AccountOf(p.Smtp, p.Alias) is { Enabled: true }),
            "rechtstreeks" => shared.Where(p => d.Index.PermissionsOn(p).Any(r => r.Direct)),
            "zonder-toegangsgroep" => shared.Where(p => d.Index.AccessGroupOf(p) is null),
            _ when k.Key.StartsWith("rechtenmodel:", StringComparison.Ordinal) => shared.Where(p => PermissionModelOf(d, p) == k.Value),
            _ => Enumerable.Empty<Mailbox>(),
        }).ToList();
    }

    public static IReadOnlyList<Group> Groups(Kpi k, KpiSource b)
    {
        var groups = b.Data.Index.Groups;
        return (k.Key switch
        {
            "dynamisch" => groups.Where(g => g.Dynamic),
            "zonder-leden" => groups.Where(g => !g.Dynamic && (g.Cloud?.CountMembers ?? g.Exo?.CountMembers ?? g.Ad?.CountMembers) == 0),
            _ when k.Key.StartsWith("groepsoort:", StringComparison.Ordinal) => groups.Where(g => g.KindText == k.Value),
            _ => Enumerable.Empty<Group>(),
        }).ToList();
    }

    public static IReadOnlyList<Room> Rooms(Kpi k, KpiSource b) => k.Key switch
    {
        "afwijkend" => b.Data.Rooms.Where(r => PlanBuilder.RoomDeviations(r, b.Config.Rooms.Default).Count > 0).ToList(),
        _ => Array.Empty<Room>(),
    };

    // ---------------------------------------------------------------- change log and Conditional Access

    public static IReadOnlyList<LogEntry> LogLines(Kpi k, IEnumerable<LogEntry> rules, DateTimeOffset now)
    {
        var limit = now.AddDays(-7);
        return (k.Key switch
        {
            "gelukt-7" => rules.Where(r => r.Time >= limit && r.Succeeded),
            "mislukt-7" => rules.Where(r => r.Time >= limit && r.Failed),
            _ => Enumerable.Empty<LogEntry>(),
        }).ToList();
    }

    public static IReadOnlyList<CaPolicy> Policies(Kpi k, KpiSource b)
    {
        var policies = b.Data.ConditionalAccess is { Message: null } ca ? ca.Policies : Array.Empty<CaPolicy>();
        CaStatus? status = k.Key switch { "ca-aan" => CaStatus.On, "ca-rapport" => CaStatus.Report, "ca-uit" => CaStatus.Off, _ => null };
        return status is null ? Array.Empty<CaPolicy>() : policies.Where(x => x.Status == status).ToList();
    }

    public static IReadOnlyList<CaFinding> Findings(Kpi k, KpiSource b)
    {
        CaVerdict? verdict = k.Key switch { "ca-problemen" => CaVerdict.Problem, "ca-aandacht" => CaVerdict.Attention, _ => null };
        return verdict is null ? Array.Empty<CaFinding>() : b.CaFindings.Where(x => x.Verdict == verdict).ToList();
    }
}
