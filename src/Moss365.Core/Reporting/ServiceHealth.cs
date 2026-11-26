using Moss365.Core.Localization;

namespace Moss365.Core.Reporting;

/// <summary>The state of one Microsoft 365 service.</summary>
public sealed record ServiceStatus(string Service, string Status);

/// <summary>An open incident or advisory of Microsoft.</summary>
public sealed record ServiceIssue(string Id, string Title, string Service, string Status, string Classification, DateTimeOffset? Start, DateTimeOffset? LastUpdate);

/// <summary>A message of the message center.</summary>
public sealed record ServiceMessage(string Id, string Title, string Category, string Services, DateTimeOffset? Start, DateTimeOffset? ActionRequiredBy);

public sealed record ServiceHealthReading(IReadOnlyList<ServiceStatus> Services, IReadOnlyList<ServiceIssue> Issues, IReadOnlyList<ServiceMessage> Messages, string? Note = null);

/// <summary>Microsoft service health (R14, 0.37): "is it us or Microsoft?", on the start screen.</summary>
public static class ServiceHealth
{
    public static bool Healthy(string status) => status is "serviceOperational" or "serviceRestored" or "falsePositive" or "postIncidentReviewPublished" or "resolved";

    public static string StatusText(string status) => status switch
    {
        "serviceOperational" => Texts.T["code.service-health.healthy"],
        "investigating" => Texts.T["code.service-health.being-investigated"],
        "restoringService" => Texts.T["code.service-health.being-restored"],
        "verifyingService" => Texts.T["code.service-health.restoration-being-checked"],
        "serviceRestored" => "hersteld",
        "postIncidentReviewPublished" => "hersteld (verslag gepubliceerd)",
        "serviceDegradation" => "verminderd",
        "serviceInterruption" => "onderbroken",
        "extendedRecovery" => Texts.T["code.service-health.extended-recovery"],
        "falsePositive" => "vals alarm",
        "investigationSuspended" => "onderzoek gestopt",
        "resolved" => "opgelost",
        "mitigatedExternal" or "mitigated" => "beperkt",
        "confirmed" => "bevestigd",
        "reported" => "gemeld",
        _ => status,
    };

    public static string ClassificationText(string classification) => classification switch
    {
        "incident" => "incident",
        "advisory" => "melding",
        _ => classification,
    };

    /// <summary>One line for the start screen.</summary>
    public static string Summary(ServiceHealthReading reading)
    {
        var bad = reading.Services.Where(s => !Healthy(s.Status)).ToList();
        var incidents = reading.Issues.Count(i => i.Classification == "incident");
        if (bad.Count == 0 && reading.Issues.Count == 0)
        {
            return Texts.T["health.all-ok"];
        }

        var parts = new List<string>();
        if (bad.Count > 0) parts.Add(Texts.T.Format("health.services", bad.Count, string.Join(", ", bad.Select(b => b.Service))));
        if (incidents > 0) parts.Add(Texts.T.Format("health.incidents", incidents));
        if (reading.Issues.Count - incidents > 0) parts.Add(Texts.T.Format("health.advisories", reading.Issues.Count - incidents));
        return string.Join("; ", parts) + ".";
    }

    /// <summary>Messages that ask for an action before a date (the soonest first).</summary>
    public static IReadOnlyList<ServiceMessage> ActionRequired(ServiceHealthReading reading, DateTimeOffset now)
        => reading.Messages.Where(m => m.ActionRequiredBy is { } d && d >= now.AddDays(-1)).OrderBy(m => m.ActionRequiredBy).ToList();
}
