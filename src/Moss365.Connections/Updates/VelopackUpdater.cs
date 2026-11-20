using Moss365.Core.Changes;
using Velopack;
using UpdateInfo = Velopack.UpdateInfo;
using Velopack.Logging;
using Velopack.Sources;

namespace Moss365.Connections.Updates;

/// <summary>
/// Updates through Velopack (R7, decision U4): from the releases on GitHub, or from a shared folder for organisations
/// without access to GitHub. The preview channel also sees stable versions. Velopack checks the SHA-256 of every
/// download against the feed of the release; from 1.0 the packages are also signed (U8).
/// </summary>
public sealed class VelopackUpdater : IAppUpdater
{
    public const string RepositoryUrl = "https://github.com/jef-cesar/Moss365";

    private readonly Dictionary<string, (UpdateManager Manager, UpdateInfo Info)> _found = new(StringComparer.OrdinalIgnoreCase);

    public bool Installed
    {
        get
        {
            try
            {
                return new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.GetTempPath()))).IsInstalled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static IUpdateSource Source(UpdateSettings settings) => string.IsNullOrWhiteSpace(settings.Source)
        ? new GithubSource(RepositoryUrl, null, true)
        : new SimpleFileSource(new DirectoryInfo(settings.Source.Trim()));

    private UpdateManager Manager(UpdateSettings settings, string? exactly, bool downgrade)
        => new(new ChannelSource(Source(settings), settings.Feeds, exactly), new UpdateOptions
        {
            ExplicitChannel = settings.ChannelName,
            AllowVersionDowngrade = downgrade,
        });

    public async Task<AvailableVersion?> CheckAsync(UpdateSettings settings, CancellationToken cancel)
    {
        // A downgrade is only allowed when this version is not in the chosen channel (a preview, while stable is chosen)
        var current = CurrentVersion() ?? "0.0.0";
        var downgrade = settings.Channel == UpdateChannel.Stable && UpdateSettings.ChannelOf(current) == UpdateChannel.Preview;
        return await FindWith(Manager(settings, null, downgrade));
    }

    public async Task<AvailableVersion?> FindAsync(UpdateSettings settings, string version, CancellationToken cancel)
        => await FindWith(Manager(settings, version, downgrade: true));

    public async Task<IReadOnlyList<string>> OlderVersionsAsync(UpdateSettings settings, string current, CancellationToken cancel)
    {
        var source = new ChannelSource(Source(settings), settings.Feeds, null);
        var feed = await source.GetReleaseFeed(NullVelopackLogger.Instance, null, settings.ChannelName);
        return feed.Assets
            .Where(a => a.Type == VelopackAssetType.Full && Versions.IsNewer(current, a.Version.ToString()))
            .OrderByDescending(a => a.Version)
            .Select(a => a.Version.ToString())
            .Distinct()
            .ToList();
    }

    private async Task<AvailableVersion?> FindWith(UpdateManager manager)
    {
        var info = await manager.CheckForUpdatesAsync();
        if (info is null)
        {
            return null;
        }

        var target = info.TargetFullRelease;
        var version = target.Version.ToString();
        lock (_found)
        {
            _found[version] = (manager, info);
        }

        return new AvailableVersion(version, target.NotesMarkdown ?? string.Empty, info.IsDowngrade, string.IsNullOrEmpty(target.SHA256) ? null : target.SHA256);
    }

    public async Task DownloadAsync(AvailableVersion version, IProgress<int>? progress, CancellationToken cancel)
    {
        var (manager, info) = Found(version);

        // Velopack checks the checksum of the package against the feed and throws when it does not match
        await manager.DownloadUpdatesAsync(info, p => progress?.Report(p), cancel);
    }

    public void ApplyAndRestart(AvailableVersion version)
    {
        var (manager, info) = Found(version);
        manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    private (UpdateManager, UpdateInfo) Found(AvailableVersion version)
    {
        lock (_found)
        {
            return _found.TryGetValue(version.Version, out var f) ? f : throw new InvalidOperationException("Zoek eerst naar deze versie.");
        }
    }

    private static string? CurrentVersion()
    {
        var v = typeof(VelopackUpdater).Assembly.GetName().Version;
        return v is null ? null : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    /// <summary>
    /// The feeds of several channels as one (preview = preview and stable), optionally only one exact version (to go
    /// back to it).
    /// </summary>
    internal sealed class ChannelSource(IUpdateSource inner, IReadOnlyList<string> channels, string? exactly) : IUpdateSource
    {
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            var assets = new List<VelopackAsset>();
            foreach (var c in channels)
            {
                try
                {
                    assets.AddRange((await inner.GetReleaseFeed(logger, appId, c, stagingId, latestLocalRelease)).Assets);
                }
                catch (Exception) when (assets.Count > 0 || c != channels[^1])
                {
                    // A channel without releases yet (no feed file): the other channel still counts
                }
            }

            var list = assets.GroupBy(a => a.FileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First());
            if (exactly is not null)
            {
                list = list.Where(a => string.Equals(a.Version.ToString(), exactly, StringComparison.OrdinalIgnoreCase) && a.Type == VelopackAssetType.Full);
            }

            return new VelopackAssetFeed { Assets = list.ToArray() };
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
            => inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}
