using Moss365.Core.Localization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Moss365.Core.Changes;

namespace Moss365.Connections.Updates;

/// <summary>
/// Asks GitHub for the latest release (without signing in). For a private repository GitHub then answers "not found";
/// in that case the app shows only the link to the releases.
/// </summary>
public sealed class GitHubUpdates : IUpdateCheck
{
    private readonly HttpClient _http;
    private readonly string _repo;

    public GitHubUpdates(HttpClient http, string repo = "jef-cesar/Moss365")
    {
        _http = http;
        _repo = repo;
    }

    public string ReleasesLink => $"https://github.com/{_repo}/releases/latest";

    public async Task<UpdateInfo> CheckAsync(UpdateChannel channel, CancellationToken cancel)
    {
        try
        {
            // Not releases/latest: GitHub leaves prereleases out of that (the preview channel, and every release before 0.30.1)
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases?per_page=30");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Moss365", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var answer = await _http.SendAsync(request, cancel);
            if (answer.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateInfo(null, ReleasesLink, Texts.T["code.git-hub-updates.the-releases-are-not-public"]);
            }

            if (!answer.IsSuccessStatusCode)
            {
                return new UpdateInfo(null, ReleasesLink, $"GitHub antwoordde {(int)answer.StatusCode}.");
            }

            return Newest(await answer.Content.ReadAsStringAsync(cancel), channel) ?? new UpdateInfo(null, ReleasesLink, Texts.T["code.git-hub-updates.no-release-in-this-channel"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateInfo(null, ReleasesLink, Texts.T.Format("code.git-hub-updates.github-is-not-reachable", ex.Message));
        }
    }

    /// <summary>The newest release of the channel in GitHub's list (drafts never count).</summary>
    internal UpdateInfo? Newest(string json, UpdateChannel channel)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray()
            .Where(r => !(r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True))
            .Select(r => (Tag: (r.TryGetProperty("tag_name", out var t) ? t.GetString() : null)?.TrimStart('v', 'V'),
                Link: r.TryGetProperty("html_url", out var l) ? l.GetString() : null))
            .Where(r => r.Tag is not null && Versions.Read(r.Tag) is not null
                        && (channel == UpdateChannel.Preview || UpdateSettings.ChannelOf(r.Tag) == UpdateChannel.Stable))
            .OrderByDescending(r => Versions.Read(r.Tag))
            .ThenBy(r => r.Tag!.Contains('-'))
            .Select(r => new UpdateInfo(r.Tag, r.Link ?? ReleasesLink, null))
            .FirstOrDefault();
    }
}
