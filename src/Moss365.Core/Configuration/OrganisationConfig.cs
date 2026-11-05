using Moss365.Core.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Moss365.Core.Configuration;

/// <summary>Choice lists and fixed rules of the organisation (config/organisatie.json).</summary>
public sealed class OrganisationConfig
{
    [JsonPropertyName("_explanation")]
    public string? Explanation { get; set; }

    public string Company { get; set; } = "Contoso";

    /// <summary>DNS name of the AD domain, e.g. CONTOSO.LOCAL; the DN follows from it (DC=CONTOSO,DC=LOCAL).</summary>
    public string? AdDomain { get; set; }

    public List<string> SynchronisedOus { get; set; } = new();
    public List<Population> Populations { get; set; } = new();
    public List<Division> Divisions { get; set; } = new();
    public List<DepartmentMapping> Departments { get; set; } = new();
    public List<string> Offices { get; set; } = new();
    public List<DynamicRule> DynamicRules { get; set; } = new();
    public VolunteersRule? Volunteers { get; set; }
    public OffboardingRule? Offboarding { get; set; }
    public SharedMailboxesRule? SharedMailboxes { get; set; }
    public List<string> LogonScripts { get; set; } = new();
    public List<LicenceChoice> Licences { get; set; } = new();
    public RoomSettings Rooms { get; set; } = new();

    /// <summary>
    /// UPNs of the emergency accounts (break glass): cloud-only Global Administrators that stay outside all Conditional
    /// Access policies, so that nobody can lock themselves out.
    /// </summary>
    public List<string> EmergencyAccounts { get; set; } = new();

    /// <summary>
    /// Profiles required (0.8): whoever signs in without an app role (Moss365.Reader, ...) may only read. Off (default,
    /// the transition): without a role everyone may do everything, with a message.
    /// </summary>
    public bool ProfilesRequired { get; set; }

    /// <summary>How long the change log stays in the database (decision K4, to agree with the DPO); archive after
    /// that.</summary>
    public int ChangeLogSaveYears { get; set; } = 2;

    /// <summary>OU for new AD groups, as a path (e.g. GEMEENTEBESTUUR/GROEPEN).</summary>
    public string? GroupsOu { get; set; }

    /// <summary>Prefixes according to the naming of groups (gg_, AFD_, DNST_, SG-, WKG_); empty = that default
    /// list.</summary>
    public List<string> GroupPrefixes { get; set; } = new();

    /// <summary>DN of the domain, derived from AdDomain; empty when that is not filled in.</summary>
    [JsonIgnore]
    public string DomainDn => string.IsNullOrWhiteSpace(AdDomain)
        ? string.Empty
        : string.Join(',', AdDomain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(d => "DC=" + d));

    /// <summary>Converts an OU path ("GEMEENTEBESTUUR/GEBRUIKERS") to a DN within the domain.</summary>
    public string OuDn(string ouPath, string? domainDn = null)
    {
        var domain = string.IsNullOrWhiteSpace(domainDn) ? DomainDn : domainDn;
        var ous = Enumerable.Reverse(ouPath.Split('/', StringSplitOptions.RemoveEmptyEntries)).Select(o => "OU=" + o.Replace(",", "\\,"));
        return string.Join(',', ous.Append(domain).Where(s => s.Length > 0));
    }

    public Population? SearchPopulation(string? name)
        => Populations.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The department with this value (spaces and case do not count), or null.</summary>
    public DepartmentMapping? SearchDepartment(string? department)
    {
        var key = Normalise(department);
        return key.Length == 0 ? null : Departments.FirstOrDefault(d => Normalise(d.Department) == key);
    }

    public Division? DivisionFor(string? department)
    {
        var mapping = SearchDepartment(department);
        return mapping?.Division is null
            ? null
            : Divisions.FirstOrDefault(a => string.Equals(a.Name, mapping.Division, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsDynamicGroup(string groupName)
        => DynamicRules.Any(r => string.Equals(r.Group, groupName, StringComparison.OrdinalIgnoreCase));

    public static string Normalise(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed class Division
{
    public string Name { get; set; } = string.Empty;
    public string? Group { get; set; }
}

public sealed class DepartmentMapping
{
    public string Department { get; set; } = string.Empty;
    public string? Division { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogonScript { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Remark { get; set; }
}

/// <summary>A kind of user (municipality, social services, volunteer) with its OU, UPN domain and default
/// values.</summary>
public sealed class Population
{
    public string Name { get; set; } = string.Empty;
    public string Ou { get; set; } = string.Empty;
    public string UpnDomain { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    public bool NoLicence { get; set; }
    public List<string> DefaultAdGroups { get; set; } = new();
}

public sealed class LicenceChoice
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public sealed class RoomSettings
{
    public RoomDefault Default { get; set; } = new();
}

/// <summary>The desired settings for a meeting room; the app shows deviations.</summary>
public sealed class RoomDefault
{
    public string AutomateProcessing { get; set; } = "AutoAccept";
    public int BookingWindowInDays { get; set; } = 180;
    public int MaximumDurationInMinutes { get; set; } = 1440;
    public bool AllowConflicts { get; set; }
    public bool AllowRecurringMeetings { get; set; } = true;
    public bool AddOrganizerToSubject { get; set; } = true;
    public bool DeleteSubject { get; set; }
    public string CalendarPermissionDefault { get; set; } = "Reviewer";
}

public sealed class DynamicRule
{
    public string Group { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
}

public sealed class VolunteersRule
{
    public string? Ou { get; set; }
    public string? UpnDomain { get; set; }
    public string? City { get; set; }
}

public sealed class OffboardingRule
{
    public string? Ou { get; set; }
    public bool Delete { get; set; }
}

public sealed class SharedMailboxesRule
{
    public string? Group { get; set; }
    public List<string> Permissions { get; set; } = new();
}

/// <summary>
/// Reads and saves the configuration. The bundled version (default) is never overwritten; changes by the user go to an
/// own file that wins from then on.
/// </summary>
public static class ConfigStorage
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }.WithLegacyNames();

    /// <summary>Loads the user file if it exists, otherwise the default file, otherwise an empty
    /// configuration.</summary>
    public static (OrganisationConfig Config, string? Source) Load(string? defaultPath, string? usersPath)
    {
        foreach (var path in new[] { usersPath, defaultPath })
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<OrganisationConfig>(File.ReadAllText(path), Options) ?? new OrganisationConfig();
                return (config, path);
            }
        }

        return (new OrganisationConfig(), null);
    }

    /// <summary>
    /// Like <see cref="Load"/>, but a broken or empty own file falls back to the default file, with a warning the app
    /// shows (without kinds or departments you cannot, for example, create a new user).
    /// </summary>
    public static (OrganisationConfig Config, string? Source, string? Warning) LoadSafe(string? defaultPath, string? usersPath)
    {
        OrganisationConfig? defaultValue = null;
        string? defaultError = null;
        if (!string.IsNullOrWhiteSpace(defaultPath) && File.Exists(defaultPath))
        {
            try
            {
                defaultValue = JsonSerializer.Deserialize<OrganisationConfig>(File.ReadAllText(defaultPath), Options);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                defaultError = ex.Message;
            }
        }

        if (!string.IsNullOrWhiteSpace(usersPath) && File.Exists(usersPath))
        {
            try
            {
                var own = JsonSerializer.Deserialize<OrganisationConfig>(File.ReadAllText(usersPath), Options) ?? new OrganisationConfig();
                if (own.Populations.Count == 0 && defaultValue is { Populations.Count: > 0 })
                {
                    return (defaultValue, defaultPath,
                        $"Je eigen organisatie ({usersPath}) heeft geen soorten (gemeente, OCMW, ...); de app gebruikt de meegeleverde. Herstel of pas ze aan in het scherm Organisatie.");
                }

                return (own, usersPath, null);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                if (defaultValue is not null)
                {
                    return (defaultValue, defaultPath, $"Je eigen organisatie ({usersPath}) is niet leesbaar ({ex.Message}); de app gebruikt de meegeleverde.");
                }

                return (new OrganisationConfig(), null, $"Organisatie niet leesbaar: {usersPath} ({ex.Message}).");
            }
        }

        if (defaultValue is not null)
        {
            return (defaultValue, defaultPath, null);
        }

        return (new OrganisationConfig(), null, defaultError is not null
            ? $"De meegeleverde organisatie ({defaultPath}) is niet leesbaar: {defaultError}"
            : $"Geen organisatie gevonden (verwacht: {defaultPath}). Pak de volledige zip uit en start Moss365.exe uit die map (niet rechtstreeks vanuit de zip).");
    }

    public static void Save(OrganisationConfig config, string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // First to a temporary file, then replace: an interrupted write leaves no half file behind
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, To(config));
        File.Move(temporary, path, overwrite: true);
    }

    public static string To(OrganisationConfig config) => JsonSerializer.Serialize(config, Options);
}
