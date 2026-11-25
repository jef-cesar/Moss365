using Moss365.Core.Changes;
using Moss365.Core.Reporting;
using Moss365.Core.Security;

namespace Moss365.UI.Services;

/// <summary>Reporting and working as a team (R14, 0.37): reports, guest renewal, requests and service health.</summary>
public sealed partial class ManagementState
{
    public ServiceHealthReading? ServiceHealthStatus { get; private set; }

    public string? ServiceHealthNote { get; private set; }

    public async Task ReadServiceHealthAsync(CancellationToken cancel = default)
    {
        ServiceHealthNote = null;
        if (!HasMicrosoft365 || Graph() is not { } graph)
        {
            ServiceHealthNote = "Meld je eerst aan bij Microsoft 365 (Verbindingen).";
        }
        else
        {
            try
            {
                ServiceHealthStatus = await graph.ReadServiceHealthAsync(cancel);
                if (ServiceHealthStatus is null)
                {
                    ServiceHealthNote = "De servicestatus is niet leesbaar (recht ServiceHealth.Read.All; voer Tenant inrichten opnieuw uit).";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ServiceHealthNote = "Servicestatus niet gelezen: " + ex.Message;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The report of a period, from the stored history of this tenant only.</summary>
    public PeriodReport BuildReport(ReportPeriod period)
    {
        var history = Database.ReadKpiSnapshot(period.From.AddDays(-400));
        var log = ChangeLog.Recent(100_000);
        var quality = DataQuality.PerDepartment(KpiSource);
        var standards = Standards.Evaluate(SecurityCheckResults, SecurityCheckList().Select(c => c.Key));
        return PeriodReport.Build(Tenant.Name, period, DateTimeOffset.Now, User, history, log, quality, standards);
    }

    /// <summary>Saves the report in the export folder of this tenant (goes to the access log).</summary>
    public string SaveReport(PeriodReport report, bool csv)
    {
        Directory.CreateDirectory(ExportFolder);
        var safe = string.Concat(report.Period.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        var target = Path.Combine(ExportFolder, $"rapport-{safe}.{(csv ? "csv" : "html")}");
        File.WriteAllText(target, csv ? report.ToCsv() : report.ToHtml(), new System.Text.UTF8Encoding(true));
        Access("Rapport opgeslagen", $"{report.Period.Name} ({(csv ? "CSV" : "HTML")})");
        return target;
    }

    public async Task RunAllSecurityChecksAsync(CancellationToken cancel = default)
    {
        foreach (var (key, _) in SecurityCheckList())
        {
            await RunSecurityCheckAsync(key, cancel: cancel);
        }
    }

    // ------------------------------------------------------------------ guest renewal

    public IReadOnlyList<GuestReview> GuestReviews() => GuestRenewal.Read(Database.ReadDocument(GuestRenewal.Document));

    public void SaveGuestReviews(IReadOnlyList<GuestReview> reviews)
    {
        if (IsHistoric)
        {
            throw new InvalidOperationException("Je bekijkt het verleden: niets wordt bewaard.");
        }

        Database.SaveDocument(GuestRenewal.Document, GuestRenewal.Write(reviews));
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ requests

    public IReadOnlyList<StaffRequest> Requests() => StaffRequests.Read(Database.ReadDocument(StaffRequests.Document));

    public void AddRequest(StaffRequest request)
    {
        if (IsHistoric)
        {
            throw new InvalidOperationException("Je bekijkt het verleden: niets wordt bewaard.");
        }

        Database.SaveDocument(StaffRequests.Document, StaffRequests.Write(StaffRequests.Add(Requests(), request with { RequestedBy = User, Created = DateTimeOffset.Now, Status = RequestStatus.Open })));
        Changed?.Invoke();
    }

    /// <summary>Approve or reject (needs the role for actions); the decision goes to the change log.</summary>
    public StaffRequest? DecideRequest(Guid id, bool approve, string reason = "")
    {
        if (IsHistoric || !Can(Core.Permissions.Permission.Actions))
        {
            throw new InvalidOperationException("Je mag aanvragen niet goedkeuren of weigeren.");
        }

        var now = DateTimeOffset.Now;
        var updated = StaffRequests.Decide(Requests(), id, approve, User, now, reason);
        Database.SaveDocument(StaffRequests.Document, StaffRequests.Write(updated));
        var decided = updated.FirstOrDefault(r => r.Id == id);
        if (decided is not null)
        {
            ChangeLog.Write(StaffRequests.LogLine(decided, User, now));
        }

        Changed?.Invoke();
        return decided;
    }

    /// <summary>Fills in the form of Nieuwe gebruiker with an approved request.</summary>
    public void PrepareNewUser(StaffRequest request)
    {
        PageState.Forget("nieuw");
        PageState.Form("nieuw", "nieuw", "Nieuwe gebruiker", () => StaffRequests.ToNewUser(request));
    }
}
