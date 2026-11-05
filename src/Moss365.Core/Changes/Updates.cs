namespace Moss365.Core.Changes;

/// <summary>Outcome of searching for a new version.</summary>
/// <param name="NewestVersion">The version of the latest release (without "v"), or null when it could not be
/// retrieved.</param>
/// <param name="Link">The page of the release (or of all releases).</param>
/// <param name="Message">Why there is no answer (e.g. a private repository), otherwise null.</param>
public sealed record UpdateInfo(string? NewestVersion, string Link, string? Message)
{
    public bool IsNewer(string current) => NewestVersion is not null && Versions.IsNewer(NewestVersion, current);
}

public interface IUpdateCheck
{
    /// <summary>Where all releases are (to copy or open).</summary>
    string ReleasesLink { get; }

    Task<UpdateInfo> CheckAsync(CancellationToken cancel);
}

public static class Versions
{
    /// <summary>"v0.5.1" compared to "0.5.0": true. Unreadable versions are never newer.</summary>
    public static bool IsNewer(string candidate, string current)
        => Read(candidate) is { } k && Read(current) is { } h && k > h;

    /// <summary>Reads "v1.2.3", "1.2" or "1.2.3+abc" (the build suffix does not count).</summary>
    public static Version? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim().TrimStart('v', 'V');
        var end = t.IndexOfAny(new[] { '+', '-', ' ' });
        if (end >= 0)
        {
            t = t[..end];
        }

        if (!Version.TryParse(t, out var v))
        {
            return null;
        }

        // Missing parts count as 0, so that 0.5 and 0.5.0 are equal
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
