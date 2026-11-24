using System.Text.Json;

namespace Moss365.Core.Configuration;

/// <summary>
/// A job profile (R9, 0.22): what someone in a job gets. Groups (AD, cloud, Microsoft 365, mail) and shared mailboxes
/// by name, so that a profile can be exported to another tenant; licences by SKU; attributes that are filled in. Not
/// to be confused with the app's roles (who may do what in Moss365).
/// </summary>
public sealed class JobProfile
{
    /// <summary>Stays the same when the profile is renamed.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Goes up by one every time a saved profile changes (see <see cref="JobProfiles.BumpVersions"/>).</summary>
    public int Version { get; set; } = 1;

    /// <summary>Names of groups: the display name, or the sAMAccountName of an AD group.</summary>
    public List<string> Groups { get; set; } = new();

    /// <summary>E-mail addresses of shared mailboxes; access goes through their access group.</summary>
    public List<string> Mailboxes { get; set; } = new();

    /// <summary>SKU part numbers (e.g. SPE_E3).</summary>
    public List<string> Licences { get; set; } = new();

    /// <summary>Job title; also used to find who holds this job.</summary>
    public string? JobTitle { get; set; }

    public string? Department { get; set; }
    public string? Office { get; set; }

    /// <summary>Only in AD.</summary>
    public string? LogonScript { get; set; }

    /// <summary>The same profile without the version, to see whether the content changed.</summary>
    internal string Content()
    {
        var copy = (JobProfile)MemberwiseClone();
        copy.Version = 0;
        return JsonSerializer.Serialize(copy);
    }
}

public static class JobProfiles
{
    /// <summary>
    /// Raises the version of every profile whose content differs from the saved one; called when the organisation is
    /// saved. A new profile starts at version 1.
    /// </summary>
    public static void BumpVersions(OrganisationConfig? saved, OrganisationConfig current)
    {
        var before = (saved?.JobProfiles ?? new List<JobProfile>()).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in current.JobProfiles)
        {
            if (!before.TryGetValue(p.Id, out var old))
            {
                p.Version = Math.Max(1, p.Version);
            }
            else if (old.Content() != p.Content())
            {
                p.Version = Math.Max(p.Version, old.Version) + 1;
            }
            else
            {
                p.Version = old.Version;
            }
        }
    }

    public static JobProfile? Find(OrganisationConfig config, string? id)
        => config.JobProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
