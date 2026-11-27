using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Devices;
using Moss365.Core.Licences;
using Moss365.Core.Localization;
using Moss365.Core.Reporting;
using Moss365.Core.Security;
using Moss365.Core.Storage;
using CheckStatus = Moss365.Core.Security.CheckStatus;

namespace Moss365.UI.Services;

/// <summary>
/// The overview (R19): the tiles of the four blocks, computed from the data, the stored results and the lists of the
/// tenant, each with the number of a period ago. The own tiles are kept per day with the KPIs (counts only).
/// </summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions OverviewJson = new JsonSerializerOptions().WithLegacyNames();

    /// <summary>The own tiles (not KPIs), whose counts are kept per day.</summary>
    private static readonly IReadOnlySet<string> OwnTiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "hardening", "advice-open", "devices-noncompliant", "devices-stale", "devices-outdated", "devices-unencrypted",
        "guests-due", "licence-waste", "data-quality", "reviews-due", "requests-open", "scheduled-due", "follow-up", "secrets-expiring", "passwords-expiring",
    };

    /// <summary>The tiles of the overview with their trend against the given number of days ago.</summary>
    public IReadOnlyList<OverviewTile> OverviewTiles(int days = 30) => ComputeOverview(days, forKeeping: false);

    /// <param name="forKeeping">Only the own tiles, to keep their counts after a read: the KPIs are kept already, and
    /// counting them here would fill the counts of the tiles too early (before the change log of the session).</param>
    private IReadOnlyList<OverviewTile> ComputeOverview(int days, bool forKeeping)
    {
        var reference = AsOf?.Time.LocalDateTime ?? DateTime.Now;
        var kept = Database.ReadKpiSnapshot(DateOnly.FromDateTime(reference.AddDays(-days - 7)));
        var then = DateOnly.FromDateTime(reference.AddDays(-days));
        var today = DateOnly.FromDateTime(reference);
        var tiles = new List<OverviewTile>();

        void Kpi(OverviewBlock block, Kpi k)
        {
            if (forKeeping)
            {
                return;
            }

            var count = KpiCount(k);
            tiles.Add(new OverviewTile(k.Key, block, count, k.Label, k.Link, k.Attention && count > 0, Overview.ValueAt(kept, k.Key, then))
            {
                History = Overview.HistoryOf(kept, k.Key, then, today),
            });
        }

        void Own(string key, OverviewBlock block, int? value, string label, string link, bool let, string unit = "", bool higherIsBetter = false)
            => tiles.Add(new OverviewTile(key, block, value, label, link, let, Overview.ValueAt(kept, Overview.CountPrefix + key, then), unit, higherIsBetter)
            {
                History = Overview.HistoryOf(kept, Overview.CountPrefix + key, then, today),
            });

        // Security: what is only kept now (the checks, the visit) is not shown for a date in the past
        if (!IsHistoric)
        {
            var results = SecurityCheckResults.Values.ToList();
            var score = HardeningScore;
            Own("hardening", OverviewBlock.Security, score, Texts.T["overview.hardening"], "controles", score is < 70, "%", higherIsBetter: true);
            var open = results.Count(c => c.Status is CheckStatus.Problem or CheckStatus.Attention);
            Own("advice-open", OverviewBlock.Security, results.Count == 0 ? null : open, Texts.T["overview.advice-open"], "controles", open > 0);
        }

        if (HasMicrosoft365)
        {
            Kpi(OverviewBlock.Security, Kpis.WithoutMfa);
            Kpi(OverviewBlock.Security, Kpis.AdministratorsWithoutMfa);
            Kpi(OverviewBlock.Security, Kpis.WithAdminRole);
        }

        if (HasAd)
        {
            Kpi(OverviewBlock.Security, Kpis.Privileged);
        }

        if (!forKeeping && !IsHistoric && PreviousVisit is not null && ChangesSinceLastVisit() is { } changes)
        {
            tiles.Add(new OverviewTile("since-visit", OverviewBlock.Security, changes.Count, Texts.T["overview.since-visit"], "#sinds-bezoek", changes.Count > 0));
        }

        // Devices: as far as they were read on Toestellen in this session
        if (!IsHistoric)
        {
            var read = AdComputers is not null || IntuneDevices is not null || EntraDevices is not null;
            var now = DateTimeOffset.Now;
            Own("devices-noncompliant", OverviewBlock.Devices, IntuneDevices?.Count(d => string.Equals(d.Compliance, "noncompliant", StringComparison.OrdinalIgnoreCase)),
                Texts.T["overview.devices-noncompliant"], "toestellen", IntuneDevices?.Any(d => string.Equals(d.Compliance, "noncompliant", StringComparison.OrdinalIgnoreCase)) == true);
            var stale = read ? DeviceList.Count(r => r.Findings.Contains("stale")) : (int?)null;
            Own("devices-stale", OverviewBlock.Devices, stale, Texts.T["overview.devices-stale"], "toestellen#een-lijst", stale > 0);
            var outdated = read
                ? (AdComputers ?? Array.Empty<AdComputer>()).Count(c => c.Enabled && Lifecycle.Of(c.OperatingSystem, c.Version, now).Status == SupportStatus.Ended)
                  + (IntuneDevices ?? Array.Empty<IntuneDevice>()).Count(d => Lifecycle.Of(d.OperatingSystem, d.Version, now).Status == SupportStatus.Ended)
                : (int?)null;
            Own("devices-outdated", OverviewBlock.Devices, outdated, Texts.T["overview.devices-outdated"], "toestellen", outdated > 0);
            var unencrypted = IntuneDevices?.Count(d => d.Encrypted == false);
            Own("devices-unencrypted", OverviewBlock.Devices, unencrypted, Texts.T["overview.devices-unencrypted"], "toestellen", unencrypted > 0);
        }

        // Hygiene and costs
        Kpi(OverviewBlock.Hygiene, Kpis.Inactive90);
        Kpi(OverviewBlock.Hygiene, Kpis.NeverSignedIn);
        if (HasMicrosoft365)
        {
            if (!IsHistoric)
            {
                var guests = GuestRenewal.Due(Data, GuestReviews(), DateTimeOffset.Now).Count;
                Own("guests-due", OverviewBlock.Hygiene, guests, Texts.T["overview.guests-due"], "gastenverlenging", guests > 0);
            }

            var waste = (int)Math.Round(LicenceAnalysis.Savings(LicenceAnalysis.Wasted(Data, reference), Config));
            Own("licence-waste", OverviewBlock.Hygiene, waste, Texts.T["overview.licence-waste"], "licenties", waste > 0, "€");
        }

        var quality = DataQuality.PerDepartment(KpiSource).LastOrDefault();
        Own("data-quality", OverviewBlock.Hygiene, quality is null || quality.People == 0 ? null : quality.Score, Texts.T["overview.data-quality"], "rapporten", quality?.Score < 80, "%", higherIsBetter: true);

        // Work: what is waiting for someone
        if (!IsHistoric)
        {
            var reviews = AccessReviewsDue().Count(r => r.Overdue);
            Own("reviews-due", OverviewBlock.Work, reviews, Texts.T["overview.reviews-due"], "toegangsreviews", reviews > 0);
            var requests = Requests().Count(r => r.Status == RequestStatus.Open);
            Own("requests-open", OverviewBlock.Work, requests, Texts.T["overview.requests-open"], "aanvragen", requests > 0);
            var scheduled = Scheduled.Read().Count(s => s.When <= DateTimeOffset.Now);
            Own("scheduled-due", OverviewBlock.Work, scheduled, Texts.T["overview.scheduled-due"], "gepland", scheduled > 0);
            var followUp = FollowUp.Read().Count;
            Own("follow-up", OverviewBlock.Work, followUp, Texts.T["overview.follow-up"], "opvolging", followUp > 0);
            if (HasAd && Config.PasswordMaxAgeDays > 0)
            {
                var expiring = Core.Helpdesk.HelpdeskTools.ExpiringPasswords(Data, Config.PasswordMaxAgeDays, 14, reference).Count;
                Own("passwords-expiring", OverviewBlock.Work, expiring, Texts.T["overview.passwords-expiring"], "wachtwoorden", expiring > 0);
            }
            if (HasMicrosoft365 && SecurityCheckResults.TryGetValue("app-geheimen", out var secrets))
            {
                var expiring = secrets.Rows.Count(r => r.Status is not CheckStatus.Ok);
                Own("secrets-expiring", OverviewBlock.Work, expiring, Texts.T["overview.secrets-expiring"], "controles", expiring > 0);
            }
        }

        Kpi(OverviewBlock.Work, Kpis.Failed7);
        return tiles;
    }

    /// <summary>After a read: today's counts of the own tiles, next to the KPIs (counts only, never names).</summary>
    public void SaveOverviewCounts()
    {
        try
        {
            Database.SaveKpiSnapshot(DateOnly.FromDateTime(DateTime.Now), Overview.CountsToKeep(ComputeOverview(30, forKeeping: true), OwnTiles));
        }
        catch (Exception ex)
        {
            Log.Warning("Overzicht bewaren mislukt: {Fout}", ex.Message);
        }
    }

    /// <summary>
    /// Saves the board summary (R19) in the export folder of this tenant: the overview with its trends, the hardening
    /// score over time and the report of the last quarter. Goes to the access log; returns the file.
    /// </summary>
    public string SaveBoardSummary(int days = 30)
    {
        var report = BuildReport(ReportPeriod.LastQuarter(DateOnly.FromDateTime(DateTime.Today)));
        var html = BoardSummary.ToHtml(Tenant.Name, DateTimeOffset.Now, User, days, OverviewLayout, OverviewTiles(days), HardeningScores, report);
        Directory.CreateDirectory(ExportFolder);
        var target = Path.Combine(ExportFolder, $"bestuursoverzicht-{DateTime.Now:yyyy-MM-dd}.html");
        File.WriteAllText(target, html, new System.Text.UTF8Encoding(true));
        Access("Bestuursoverzicht opgeslagen", Path.GetFileName(target));
        return target;
    }

    /// <summary>The order and the hidden blocks of this person, or the default of the role.</summary>
    public OverviewLayout OverviewLayout
    {
        get
        {
            var fallback = Overview.DefaultLayout(Roles);
            try
            {
                return Database.ReadDocument(Overview.LayoutDocument) is { Length: > 0 } json && JsonSerializer.Deserialize<OverviewLayout>(json, OverviewJson) is { } kept
                    ? Overview.Complete(kept, fallback)
                    : fallback;
            }
            catch (JsonException)
            {
                return fallback;
            }
        }
    }

    /// <summary>Keeps the layout of this person; null goes back to the default of the role.</summary>
    public void SaveOverviewLayout(OverviewLayout? layout)
    {
        if (layout is null)
        {
            Database.DeleteDocument(Overview.LayoutDocument);
        }
        else
        {
            Database.SaveDocument(Overview.LayoutDocument, JsonSerializer.Serialize(layout, OverviewJson));
        }

        Changed?.Invoke();
    }
}
