using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Moss365.Connections.Graph;

public sealed class GraphFout : Exception
{
    public GraphFout(HttpStatusCode status, string code, string bericht) : base(bericht)
    {
        Status = status;
        Code = code;
    }

    public HttpStatusCode Status { get; }
    public string Code { get; }
}

/// <summary>Minimale Microsoft Graph-klant: GET met paginering, POST, PATCH en DELETE, met één nieuwe poging bij 429/503.</summary>
public sealed class GraphKlant
{
    public const string Basis = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;

    public GraphKlant(HttpClient http, Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _token = token;
    }

    public async Task<JsonElement> GetAsync(string pad, CancellationToken annuleren)
    {
        using var antwoord = await StuurAsync(HttpMethod.Get, pad, null, annuleren);
        return await LeesJsonAsync(antwoord, annuleren);
    }

    /// <summary>Alle items van een lijst, over alle pagina's (@odata.nextLink).</summary>
    public async Task<List<JsonElement>> GetAlleAsync(string pad, CancellationToken annuleren)
    {
        var uit = new List<JsonElement>();
        string? volgende = pad;
        while (volgende is not null)
        {
            var pagina = await GetAsync(volgende, annuleren);
            if (pagina.TryGetProperty("value", out var waarden))
            {
                uit.AddRange(waarden.EnumerateArray().Select(e => e.Clone()));
            }

            volgende = pagina.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        return uit;
    }

    public async Task PostAsync(string pad, object? inhoud, CancellationToken annuleren)
    {
        using var _ = await StuurAsync(HttpMethod.Post, pad, inhoud, annuleren);
    }

    /// <summary>POST met het antwoord als JSON (bv. de uitnodiging van een gast).</summary>
    public async Task<JsonElement> PostMetAntwoordAsync(string pad, object inhoud, CancellationToken annuleren)
    {
        using var antwoord = await StuurAsync(HttpMethod.Post, pad, inhoud, annuleren);
        return await LeesJsonAsync(antwoord, annuleren);
    }

    public async Task PatchAsync(string pad, object inhoud, CancellationToken annuleren)
    {
        using var _ = await StuurAsync(HttpMethod.Patch, pad, inhoud, annuleren);
    }

    public async Task PutAsync(string pad, object inhoud, CancellationToken annuleren)
    {
        using var _ = await StuurAsync(HttpMethod.Put, pad, inhoud, annuleren);
    }

    public async Task DeleteAsync(string pad, CancellationToken annuleren)
    {
        using var _ = await StuurAsync(HttpMethod.Delete, pad, null, annuleren);
    }

    private async Task<HttpResponseMessage> StuurAsync(HttpMethod methode, string pad, object? inhoud, CancellationToken annuleren)
    {
        for (var poging = 1; ; poging++)
        {
            using var verzoek = new HttpRequestMessage(methode, pad.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? pad : Basis + pad);
            verzoek.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(annuleren));
            verzoek.Headers.Add("ConsistencyLevel", "eventual");
            if (inhoud is not null)
            {
                verzoek.Content = new StringContent(JsonSerializer.Serialize(inhoud), Encoding.UTF8, "application/json");
            }

            var antwoord = await _http.SendAsync(verzoek, annuleren);
            if (antwoord.IsSuccessStatusCode)
            {
                return antwoord;
            }

            if (poging == 1 && antwoord.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wacht = antwoord.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                antwoord.Dispose();
                await Task.Delay(wacht > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wacht, annuleren);
                continue;
            }

            var tekst = await antwoord.Content.ReadAsStringAsync(annuleren);
            var status = antwoord.StatusCode;
            antwoord.Dispose();
            var (code, bericht) = LeesFout(tekst);
            throw new GraphFout(status, code, $"Graph {(int)status}: {bericht}");
        }
    }

    private static async Task<JsonElement> LeesJsonAsync(HttpResponseMessage antwoord, CancellationToken annuleren)
    {
        await using var stroom = await antwoord.Content.ReadAsStreamAsync(annuleren);
        using var doc = await JsonDocument.ParseAsync(stroom, cancellationToken: annuleren);
        return doc.RootElement.Clone();
    }

    private static (string Code, string Bericht) LeesFout(string tekst)
    {
        try
        {
            using var doc = JsonDocument.Parse(tekst);
            var fout = doc.RootElement.GetProperty("error");
            return (fout.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                fout.TryGetProperty("message", out var m) ? m.GetString() ?? tekst : tekst);
        }
        catch (Exception)
        {
            return (string.Empty, string.IsNullOrWhiteSpace(tekst) ? "(geen details)" : tekst);
        }
    }
}
