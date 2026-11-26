using Moss365.Core.Localization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Storage;

namespace Moss365.Core.Reporting;

public enum RequestKind
{
    Onboarding,
    Offboarding,
}

public enum RequestStatus
{
    Open,
    Approved,
    Rejected,
}

/// <summary>A request from HR or a department head: a new colleague, or a colleague who leaves.</summary>
public sealed record StaffRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public RequestKind Kind { get; init; }
    public RequestStatus Status { get; init; }
    public string RequestedBy { get; init; } = string.Empty;
    public DateTimeOffset Created { get; init; }
    public string GivenName { get; init; } = string.Empty;
    public string Surname { get; init; } = string.Empty;
    public string Department { get; init; } = string.Empty;
    public string JobTitle { get; init; } = string.Empty;
    public string JobProfile { get; init; } = string.Empty;
    public string Office { get; init; } = string.Empty;
    public DateOnly? Date { get; init; }

    /// <summary>For offboarding: the person who leaves.</summary>
    public string PersonKey { get; init; } = string.Empty;
    public string PersonName { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string DecidedBy { get; init; } = string.Empty;
    public DateTimeOffset? Decided { get; init; }
    public string Reason { get; init; } = string.Empty;

    [JsonIgnore]
    public string Title => Kind == RequestKind.Onboarding ? $"Nieuw: {GivenName} {Surname}".Trim() : Texts.T.Format("code.staff-requests.leaving", PersonName);
}

/// <summary>
/// Onboarding and offboarding as a request (R14, 0.37): the form is filled in by HR or the department head, the
/// administrator approves or rejects, and the plan follows from the job profile on the usual screens (Nieuwe gebruiker,
/// Uitstroom). Requests are stored in the tenant database (document "aanvragen"); a decision goes to the change log.
/// </summary>
public static class StaffRequests
{
    public const string Document = "aanvragen";

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    }.WithLegacyNames();

    public static IReadOnlyList<StaffRequest> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<StaffRequest>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<StaffRequest>>(json, Json) ?? new List<StaffRequest>();
        }
        catch (JsonException)
        {
            return Array.Empty<StaffRequest>();
        }
    }

    public static string Write(IEnumerable<StaffRequest> requests) => JsonSerializer.Serialize(requests.OrderBy(r => r.Created).ToList(), Json);

    public static IReadOnlyList<string> Validate(StaffRequest r, OrganisationConfig config)
    {
        var errors = new List<string>();
        if (r.Kind == RequestKind.Onboarding)
        {
            if (string.IsNullOrWhiteSpace(r.GivenName) || string.IsNullOrWhiteSpace(r.Surname)) errors.Add(Texts.T["code.staff-requests.fill-in-first-name-and"]);
            if (string.IsNullOrWhiteSpace(r.Department) && string.IsNullOrWhiteSpace(r.JobProfile)) errors.Add(Texts.T["code.staff-requests.choose-a-department-or-a"]);
            if (r.JobProfile.Length > 0 && JobProfiles.Find(config, r.JobProfile) is null) errors.Add(Texts.T.Format("code.staff-requests.job-profile-does-not-exist", r.JobProfile));
        }
        else if (string.IsNullOrWhiteSpace(r.PersonKey))
        {
            errors.Add(Texts.T["code.staff-requests.choose-who-is-leaving"]);
        }

        if (r.Date is null) errors.Add(r.Kind == RequestKind.Onboarding ? Texts.T["code.staff-requests.fill-in-the-start-date"] : Texts.T["code.staff-requests.fill-in-the-last-working"]);
        return errors;
    }

    /// <summary>The request as the form of Nieuwe gebruiker (the job profile fills in the rest there).</summary>
    public static NewUserRequest ToNewUser(StaffRequest r) => new()
    {
        GivenName = r.GivenName.Trim(),
        Surname = r.Surname.Trim(),
        Department = r.Department.Trim(),
        JobTitle = r.JobTitle.Trim(),
        Office = r.Office.Trim(),
        JobProfile = r.JobProfile.Length > 0 ? r.JobProfile : null,
    };

    public static IReadOnlyList<StaffRequest> Add(IReadOnlyList<StaffRequest> requests, StaffRequest r) => requests.Append(r).ToList();

    public static IReadOnlyList<StaffRequest> Decide(IReadOnlyList<StaffRequest> requests, Guid id, bool approve, string by, DateTimeOffset now, string reason = "")
        => requests.Select(r => r.Id == id && r.Status == RequestStatus.Open
            ? r with { Status = approve ? RequestStatus.Approved : RequestStatus.Rejected, DecidedBy = by, Decided = now, Reason = reason.Trim() }
            : r).ToList();

    /// <summary>The entry in the change log for a decision (no plan runs; the plan follows on the screens).</summary>
    public static LogEntry LogLine(StaffRequest r, string user, DateTimeOffset now) => new(now, user, "Aanvragen", "Moss365", "Request",
        r.Id.ToString(), r.Title, $"Aanvraag {(r.Status == RequestStatus.Approved ? "goedgekeurd" : "geweigerd")}: {r.Title}",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["aangevraagdDoor"] = r.RequestedBy, ["datum"] = r.Date?.ToString("yyyy-MM-dd") ?? string.Empty },
        "Succeeded", r.Reason);
}
