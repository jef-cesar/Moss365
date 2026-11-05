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

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Moss365", "1.0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var answer = await _http.SendAsync(request, cancel);
            if (answer.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateInfo(null, ReleasesLink, "De releases zijn niet openbaar (privé-repo): automatisch controleren kan niet.");
            }

            if (!answer.IsSuccessStatusCode)
            {
                return new UpdateInfo(null, ReleasesLink, $"GitHub antwoordde {(int)answer.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(cancel));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var link = doc.RootElement.TryGetProperty("html_url", out var l) ? l.GetString() : null;
            return new UpdateInfo(tag?.TrimStart('v', 'V'), link ?? ReleasesLink, tag is null ? "Geen versie in het antwoord." : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateInfo(null, ReleasesLink, "GitHub is niet bereikbaar: " + ex.Message);
        }
    }
}
