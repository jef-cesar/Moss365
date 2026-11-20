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

    /// <summary>The newest release in the channel (the preview channel also sees stable versions).</summary>
    Task<UpdateInfo> CheckAsync(UpdateChannel channel, CancellationToken cancel);
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

/// <summary>Which releases an installation follows (R7): stable versions, or also previews (0.17.0-preview.1).</summary>
public enum UpdateChannel
{
    Stable,
    Preview,
}

/// <summary>How the app updates itself: the channel, and a custom source (a shared folder) instead of GitHub.</summary>
public sealed record UpdateSettings(UpdateChannel Channel, string? Source = null)
{
    /// <summary>The name of the channel in the release feed (releases.stable.json, releases.preview.json).</summary>
    public string ChannelName => Name(Channel);

    public static string Name(UpdateChannel channel) => channel == UpdateChannel.Preview ? "preview" : "stable";

    /// <summary>The channel a version belongs to: a version with a suffix (0.17.0-preview.1) is a preview.</summary>
    public static UpdateChannel ChannelOf(string version) => version.Contains('-') ? UpdateChannel.Preview : UpdateChannel.Stable;

    /// <summary>
    /// Until 1.0 every installation follows the previews too (there is no stable 1.x yet); from 1.0 stable is the
    /// default.
    /// </summary>
    public static UpdateChannel DefaultChannel(string appVersion) => Versions.Read(appVersion) is { Major: >= 1 } ? UpdateChannel.Stable : UpdateChannel.Preview;

    /// <summary>Following the preview channel means: previews and stable versions; the stable channel: stable only.</summary>
    public IReadOnlyList<string> Feeds => Channel == UpdateChannel.Preview ? new[] { "preview", "stable" } : new[] { "stable" };
}

/// <summary>A version the app can install: newer, or older (going back, or switching to the stable channel).</summary>
/// <param name="Sha256">The fingerprint of the package, as published with the release; the download is checked
/// against it.</param>
public sealed record AvailableVersion(string Version, string Notes, bool Downgrade, string? Sha256 = null);

/// <summary>
/// Updating the app itself (R7, decision U4: Velopack). Only an installation made with Setup.exe can update itself; a
/// copy started from the zip only gets the link to the releases.
/// </summary>
public interface IAppUpdater
{
    /// <summary>Installed with Setup.exe, so it can update itself.</summary>
    bool Installed { get; }

    /// <summary>The newest version in the channel (null: this is the newest). An older version when the channel was
    /// switched to one whose newest version is lower.</summary>
    Task<AvailableVersion?> CheckAsync(UpdateSettings settings, CancellationToken cancel);

    /// <summary>Older versions in the channel, newest first (to go back to).</summary>
    Task<IReadOnlyList<string>> OlderVersionsAsync(UpdateSettings settings, string current, CancellationToken cancel);

    /// <summary>Exactly this version (to go back to an older one), or null when the channel does not have it.</summary>
    Task<AvailableVersion?> FindAsync(UpdateSettings settings, string version, CancellationToken cancel);

    /// <summary>Downloads the version and checks its fingerprint; throws when it does not match.</summary>
    Task DownloadAsync(AvailableVersion version, IProgress<int>? progress, CancellationToken cancel);

    /// <summary>Closes the app, installs the downloaded version and starts it again.</summary>
    void ApplyAndRestart(AvailableVersion version);
}
