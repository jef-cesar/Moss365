using Moss365.Core.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Moss365.Core.Changes;

/// <summary>
/// Steps that have to wait until a new user is synchronised to Entra (licence, cloud groups). The app saves them and
/// runs them as soon as the user exists in Entra.
/// </summary>
public sealed class FollowUpTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public required string Upn { get; init; }
    public required string Description { get; init; }
    public List<Change> Changes { get; init; } = new();
    public DateTimeOffset? LastChecked { get; set; }
    public string? LastMessage { get; set; }
}

public sealed class FollowUpStorage
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new LegacyEnumConverter() },
    }.WithLegacyNames();

    private readonly Storage.ITextStorage _storage;

    public FollowUpStorage(string path) : this(new Storage.FileText(path))
    {
    }

    /// <summary>Since 0.8 in the database (document "opvolging").</summary>
    public FollowUpStorage(Storage.ITextStorage storage) => _storage = storage;

    public List<FollowUpTask> Read()
        => _storage.Read() is { } json ? JsonSerializer.Deserialize<List<FollowUpTask>>(json, Options) ?? new List<FollowUpTask>() : new List<FollowUpTask>();

    public void Save(IEnumerable<FollowUpTask> tasks) => _storage.Save(JsonSerializer.Serialize(tasks.ToList(), Options));

    public void Add(FollowUpTask task)
    {
        var tasks = Read();
        tasks.Add(task);
        Save(tasks);
    }
}
