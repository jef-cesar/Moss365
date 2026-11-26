using System.Text.Json;
using Moss365.Core.Security;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>When this tenant was last opened (R18), for "since your last visit".</summary>
public sealed record VisitRecord(DateTimeOffset Last);

/// <summary>Since your last visit (R18): the visit before this one, and what changed outside Moss365 since then.</summary>
public sealed partial class ManagementState
{
    private const string VisitDocument = "laatste-bezoek";
    private static readonly JsonSerializerOptions VisitJson = new JsonSerializerOptions().WithLegacyNames();
    private bool _visitStarted;

    /// <summary>The visit before this one (null at the first visit, or before <see cref="BeginVisit"/>).</summary>
    public DateTimeOffset? PreviousVisit { get; private set; }

    /// <summary>
    /// The start screen marks the visit, once per session: the visit before it stays known for this session. Reading
    /// without a window is no visit (it never calls this).
    /// </summary>
    public void BeginVisit()
    {
        if (_visitStarted || IsHistoric)
        {
            return;
        }

        _visitStarted = true;
        try
        {
            PreviousVisit = Database.ReadDocument(VisitDocument) is { } json ? JsonSerializer.Deserialize<VisitRecord>(json, VisitJson)?.Last : null;
        }
        catch (JsonException)
        {
            PreviousVisit = null;
        }

        Database.SaveDocument(VisitDocument, JsonSerializer.Serialize(new VisitRecord(DateTimeOffset.Now), VisitJson));
    }

    /// <summary>What changed outside Moss365 since the previous visit, as far as the snapshots and the logs kept
    /// locally know; null at the first visit.</summary>
    public IReadOnlyList<VisitChange>? ChangesSinceLastVisit()
    {
        if (PreviousVisit is not { } since || IsHistoric)
        {
            return null;
        }

        var until = DateTimeOffset.Now.AddMinutes(5);
        return SinceLastVisit.Compute(since, HasAd ? DataAt(since) : null, Data, Database.ReadAudits(since, until, 2000), Database.ReadSignIns(since, until, 2000),
            ChangeLog.Recent(2000).Where(r => r.Time >= since - SinceLastVisit.SameChange));
    }
}
