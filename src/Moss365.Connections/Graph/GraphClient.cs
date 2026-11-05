using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Moss365.Connections.Graph;

public sealed class GraphError : Exception
{
    public GraphError(HttpStatusCode status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    public HttpStatusCode Status { get; }
    public string Code { get; }
}

/// <summary>Minimal Microsoft Graph client: GET with paging, POST, PATCH and DELETE, with one retry on
/// 429/503.</summary>
public sealed class GraphClient
{
    public const string Base = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;

    public GraphClient(HttpClient http, Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _token = token;
    }

    public async Task<JsonElement> GetAsync(string path, CancellationToken cancel)
    {
        using var answer = await SendAsync(HttpMethod.Get, path, null, cancel);
        return await ReadJsonAsync(answer, cancel);
    }

    /// <summary>All items of a list, across all pages (@odata.nextLink).</summary>
    public async Task<List<JsonElement>> GetAllAsync(string path, CancellationToken cancel)
    {
        var output = new List<JsonElement>();
        string? next = path;
        while (next is not null)
        {
            var currentPage = await GetAsync(next, cancel);
            if (currentPage.TryGetProperty("value", out var values))
            {
                output.AddRange(values.EnumerateArray().Select(e => e.Clone()));
            }

            next = currentPage.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        return output;
    }

    public async Task PostAsync(string path, object? content, CancellationToken cancel)
    {
        using var _ = await SendAsync(HttpMethod.Post, path, content, cancel);
    }

    /// <summary>POST with the response as JSON (e.g. the invitation of a guest).</summary>
    public async Task<JsonElement> PostWithResponseAsync(string path, object content, CancellationToken cancel)
    {
        using var answer = await SendAsync(HttpMethod.Post, path, content, cancel);
        return await ReadJsonAsync(answer, cancel);
    }

    public async Task PatchAsync(string path, object content, CancellationToken cancel)
    {
        using var _ = await SendAsync(HttpMethod.Patch, path, content, cancel);
    }

    public async Task PutAsync(string path, object content, CancellationToken cancel)
    {
        using var _ = await SendAsync(HttpMethod.Put, path, content, cancel);
    }

    public async Task DeleteAsync(string path, CancellationToken cancel)
    {
        using var _ = await SendAsync(HttpMethod.Delete, path, null, cancel);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? content, CancellationToken cancel)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path : Base + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(cancel));
            request.Headers.Add("ConsistencyLevel", "eventual");
            if (content is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json");
            }

            var answer = await _http.SendAsync(request, cancel);
            if (answer.IsSuccessStatusCode)
            {
                return answer;
            }

            if (attempt == 1 && answer.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = answer.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                answer.Dispose();
                await Task.Delay(wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait, cancel);
                continue;
            }

            var text = await answer.Content.ReadAsStringAsync(cancel);
            var status = answer.StatusCode;
            answer.Dispose();
            var (code, message) = ReadError(text);
            throw new GraphError(status, code, $"Graph {(int)status}: {message}");
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage answer, CancellationToken cancel)
    {
        await using var stream = await answer.Content.ReadAsStreamAsync(cancel);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        return doc.RootElement.Clone();
    }

    private static (string Code, string Message) ReadError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var error = doc.RootElement.GetProperty("error");
            return (error.TryGetProperty("code", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                error.TryGetProperty("message", out var m) ? m.GetString() ?? text : text);
        }
        catch (Exception)
        {
            return (string.Empty, string.IsNullOrWhiteSpace(text) ? "(geen details)" : text);
        }
    }
}
