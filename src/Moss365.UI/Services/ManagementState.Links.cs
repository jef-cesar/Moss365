using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Linking;
using Moss365.Core.Localization;
using Moss365.Core.Permissions;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>Linking persons over AD, Entra ID and Google Workspace (G0, U32): the links by hand and the doubtful cases.</summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions LinkJson = new JsonSerializerOptions().WithLegacyNames();
    private LinkSettings? _links;
    private IReadOnlyList<LinkDoubt> _googleDoubts = Array.Empty<LinkDoubt>();

    /// <summary>The links and undone links made by hand (document <c>koppelingen</c>).</summary>
    public LinkSettings LinkSettings
    {
        get
        {
            if (_links is null)
            {
                try
                {
                    _links = Database.ReadDocument(PersonLinks.Document) is { } json ? JsonSerializer.Deserialize<LinkSettings>(json, LinkJson) : null;
                }
                catch (JsonException)
                {
                    _links = null;
                }

                _links ??= new LinkSettings();
            }

            return _links;
        }
    }

    /// <summary>The doubtful cases: between AD and Entra (an account that is not synchronised while a cloud account has
    /// its address), and of the Google accounts.</summary>
    public IReadOnlyList<LinkDoubt> LinkDoubts()
    {
        var data = Data;
        var doubts = new List<LinkDoubt>();
        if (HasAd && HasMicrosoft365)
        {
            doubts.AddRange(PersonLinks.AdEntraDoubts(data.Persons));
        }

        if (HasGoogle)
        {
            doubts.AddRange(_googleDoubts);
        }

        return doubts;
    }

    /// <summary>Links a Google account to a person by hand, or undoes a link (role Settings); in the change log.</summary>
    public void LinkByHand(GoogleUser google, Person person, bool linked)
    {
        if (IsHistoric || !Can(Permission.Settings))
        {
            throw new InvalidOperationException(Texts.T["links.not-allowed"]);
        }

        var now = DateTimeOffset.Now;
        var links = LinkSettings.Links.Append(new ManualLink(google.Id, person.Key, linked, User, now)).ToList();
        var settings = LinkSettings with { Links = links };
        Database.SaveDocument(PersonLinks.Document, JsonSerializer.Serialize(settings, LinkJson));
        _links = settings;
        ChangeLog.Write(new LogEntry(now, User, Texts.T["menu.koppelingen"], "Moss365", "PersonLink", person.Key, person.Name,
            Texts.T.Format(linked ? "links.log.linked" : "links.log.unlinked", google.PrimaryEmail),
            new Dictionary<string, string> { ["google"] = google.Id, ["gekoppeld"] = linked ? "True" : "False" }, nameof(ResultStatus.Succeeded), string.Empty));
        Changed?.Invoke();
    }
}
