using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Moss365.Core.Wijzigingen;

namespace Moss365.Connections.Updates;

/// <summary>
/// Vraagt de laatste release op bij GitHub (zonder aanmelding). Bij een privé-repo antwoordt GitHub dan "niet gevonden";
/// de app toont in dat geval enkel de link naar de releases.
/// </summary>
public sealed class GitHubUpdates : IUpdateControle
{
    private readonly HttpClient _http;
    private readonly string _repo;

    public GitHubUpdates(HttpClient http, string repo = "jef-cesar/Moss365")
    {
        _http = http;
        _repo = repo;
    }

    public string ReleasesLink => $"https://github.com/{_repo}/releases/latest";

    public async Task<UpdateInfo> ControleerAsync(CancellationToken annuleren)
    {
        try
        {
            using var verzoek = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repo}/releases/latest");
            verzoek.Headers.UserAgent.Add(new ProductInfoHeaderValue("Moss365", "1.0"));
            verzoek.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var antwoord = await _http.SendAsync(verzoek, annuleren);
            if (antwoord.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateInfo(null, ReleasesLink, "De releases zijn niet openbaar (privé-repo): automatisch controleren kan niet.");
            }

            if (!antwoord.IsSuccessStatusCode)
            {
                return new UpdateInfo(null, ReleasesLink, $"GitHub antwoordde {(int)antwoord.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(await antwoord.Content.ReadAsStringAsync(annuleren));
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
