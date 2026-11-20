using Moss365.Connections.Updates;
using Moss365.Core.Changes;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace Moss365.Tests;

/// <summary>R7: channels and the release feed of the updates.</summary>
public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.17.0", UpdateChannel.Stable)]
    [InlineData("0.17.0-preview.1", UpdateChannel.Preview)]
    [InlineData("1.0.0", UpdateChannel.Stable)]
    public void TheChannelOfAVersion(string version, UpdateChannel channel) => Assert.Equal(channel, UpdateSettings.ChannelOf(version));

    [Fact]
    public void UntilOnePointZeroThePreviewsCountToo()
    {
        Assert.Equal(UpdateChannel.Preview, UpdateSettings.DefaultChannel("0.17.0"));
        Assert.Equal(UpdateChannel.Stable, UpdateSettings.DefaultChannel("1.0.0"));
        Assert.Equal(new[] { "preview", "stable" }, new UpdateSettings(UpdateChannel.Preview).Feeds);
        Assert.Equal(new[] { "stable" }, new UpdateSettings(UpdateChannel.Stable).Feeds);
        Assert.Equal("preview", new UpdateSettings(UpdateChannel.Preview).ChannelName);
    }

    /// <summary>A source with a feed per channel, like the files releases.stable.json and releases.preview.json.</summary>
    private sealed class FakeSource(Dictionary<string, string[]> feeds) : IUpdateSource
    {
        public List<string> Downloaded { get; } = new();

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (!feeds.TryGetValue(channel, out var versions))
            {
                throw new InvalidOperationException("no feed for " + channel);
            }

            return Task.FromResult(new VelopackAssetFeed
            {
                Assets = versions.Select(v => new VelopackAsset
                {
                    PackageId = "Moss365.Desktop",
                    Version = SemanticVersion.Parse(v),
                    Type = VelopackAssetType.Full,
                    FileName = $"Moss365.Desktop-{v}-full.nupkg",
                }).ToArray(),
            });
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            Downloaded.Add(releaseEntry.FileName);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ThePreviewChannelAlsoSeesStableVersions()
    {
        var inner = new FakeSource(new() { ["stable"] = new[] { "0.17.0", "0.18.0" }, ["preview"] = new[] { "0.18.0-preview.1", "0.19.0-preview.1" } });
        var preview = await new VelopackUpdater.ChannelSource(inner, new[] { "preview", "stable" }, null).GetReleaseFeed(NullVelopackLogger.Instance, null, "preview");
        Assert.Equal(4, preview.Assets.Length);

        var stable = await new VelopackUpdater.ChannelSource(inner, new[] { "stable" }, null).GetReleaseFeed(NullVelopackLogger.Instance, null, "stable");
        Assert.All(stable.Assets, a => Assert.DoesNotContain("-preview", a.Version.ToString(), StringComparison.Ordinal));

        // Going back: only that one version
        var back = await new VelopackUpdater.ChannelSource(inner, new[] { "preview", "stable" }, "0.17.0").GetReleaseFeed(NullVelopackLogger.Instance, null, "preview");
        Assert.Equal("0.17.0", Assert.Single(back.Assets).Version.ToString());

        // A channel without releases yet does not break the other one
        var onlyStable = new FakeSource(new() { ["stable"] = new[] { "0.17.0" } });
        var feed = await new VelopackUpdater.ChannelSource(onlyStable, new[] { "preview", "stable" }, null).GetReleaseFeed(NullVelopackLogger.Instance, null, "preview");
        Assert.Single(feed.Assets);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new VelopackUpdater.ChannelSource(new FakeSource(new()), new[] { "stable" }, null)
            .GetReleaseFeed(NullVelopackLogger.Instance, null, "stable"));
    }
}
