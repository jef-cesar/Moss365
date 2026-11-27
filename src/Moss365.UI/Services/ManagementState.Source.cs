using System.Text.Json;
using System.Text.Json.Serialization;
using Moss365.Core.Changes;
using Moss365.Core.Linking;
using Moss365.Core.Localization;
using Moss365.Core.Permissions;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>The source of truth of this tenant (G0, U33): what Moss365 sees, and what it asked.</summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions SourceJson = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }.WithLegacyNames();
    private SourceSettings? _source;

    /// <summary>The answers of this tenant (document <c>bron</c>).</summary>
    public SourceSettings SourceSettings
    {
        get
        {
            if (_source is null)
            {
                try
                {
                    _source = Database.ReadDocument(SourceOfTruth.Document) is { } json ? JsonSerializer.Deserialize<SourceSettings>(json, SourceJson) : null;
                }
                catch (JsonException)
                {
                    _source = null;
                }

                _source ??= new SourceSettings();
            }

            return _source;
        }
    }

    /// <summary>The chains of a kind of object: the source first, then what follows it.</summary>
    public IReadOnlyList<IReadOnlyList<Platform>> SourceChains(TruthKind kind) => SourceOfTruth.Chains(kind, Systems, Data, SourceSettings);

    /// <summary>The platform a platform follows for a kind of object, or null when it is a source itself.</summary>
    public Platform? FollowsFrom(Platform platform, TruthKind kind) => SourceOfTruth.FollowsFrom(platform, kind, Systems, Data, SourceSettings);

    /// <summary>Saves the answers (role Settings); in the change log with what they were.</summary>
    public void SaveSource(SourceSettings settings)
    {
        if (IsHistoric || !Can(Permission.Settings))
        {
            throw new InvalidOperationException(Texts.T["source.not-allowed"]);
        }

        var before = SourceSettings;
        Database.SaveDocument(SourceOfTruth.Document, JsonSerializer.Serialize(settings, SourceJson));
        _source = settings;
        ChangeLog.Write(new LogEntry(DateTimeOffset.Now, User, Texts.T["menu.bron"], "Moss365", "SourceOfTruth", Tenant.Id, Tenant.Name,
            Texts.T.Format("source.log", SourceOfTruth.Name(settings.GoogleUsers), SourceOfTruth.Name(settings.GoogleGroups)),
            new Dictionary<string, string> { ["gebruikers"] = settings.GoogleUsers.ToString(), ["groepen"] = settings.GoogleGroups.ToString(), ["wachtwoorden"] = settings.GooglePasswordSync?.ToString() ?? string.Empty },
            nameof(ResultStatus.Succeeded), string.Empty,
            Previous: new Dictionary<string, string> { ["gebruikers"] = before.GoogleUsers.ToString(), ["groepen"] = before.GoogleGroups.ToString(), ["wachtwoorden"] = before.GooglePasswordSync?.ToString() ?? string.Empty }));
        Changed?.Invoke();
    }
}
