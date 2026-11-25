using System.Globalization;
using System.Net;
using System.Text;
using Moss365.Core.Analysis;
using Moss365.Core.Changes;
using CheckStatus = Moss365.Core.Security.CheckStatus;

namespace Moss365.Core.Reporting;

/// <summary>A period of a report: a month, a quarter or chosen dates (both included).</summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To, string Name)
{
    public static ReportPeriod Month(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        return new(from, from.AddMonths(1).AddDays(-1), from.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("nl-BE")));
    }

    public static ReportPeriod Quarter(int year, int quarter)
    {
        var from = new DateOnly(year, (quarter - 1) * 3 + 1, 1);
        return new(from, from.AddMonths(3).AddDays(-1), $"Q{quarter} {year}");
    }

    public static ReportPeriod LastMonth(DateOnly today) => Month(today.AddMonths(-1).Year, today.AddMonths(-1).Month);

    public static ReportPeriod LastQuarter(DateOnly today)
    {
        var q = (today.Month - 1) / 3 + 1;
        return q == 1 ? Quarter(today.Year - 1, 4) : Quarter(today.Year, q - 1);
    }
}

/// <summary>A KPI at the start and at the end of the period (from the KPI history).</summary>
public sealed record KpiRow(string Key, string Title, int? Start, int? End, bool Attention)
{
    public int? Difference => Start is { } s && End is { } e ? e - s : null;
}

/// <summary>The changes of the period per kind.</summary>
public sealed record ChangeRow(string Action, int Succeeded, int Failed);

/// <summary>
/// The monthly or quarterly report (R14, 0.37): indicators and their trend, the changes from the change log, data
/// quality and the checks mapped to standards. Built from the stored history only, so also for a period in the past;
/// saved as HTML (print as PDF) or CSV (Excel). Everything comes from one tenant.
/// </summary>
public sealed record PeriodReport(
    string Tenant,
    ReportPeriod Period,
    DateTimeOffset Made,
    string MadeBy,
    IReadOnlyList<KpiRow> Kpis,
    IReadOnlyList<ChangeRow> Changes,
    IReadOnlyList<QualityRow> Quality,
    IReadOnlyList<StandardRow> Standards,
    IReadOnlyList<string> Notes)
{
    /// <summary>The KPIs shown in a report (the fixed ones, in this order).</summary>
    public static IReadOnlyList<Kpi> ReportKpis => Analysis.Kpis.Fixed.Where(k => k.Items is KpiList.Users or KpiList.Mailboxes or KpiList.Groups or KpiList.CaCheck or KpiList.CaPolicies).ToList();

    public static PeriodReport Build(
        string tenant, ReportPeriod period, DateTimeOffset now, string user,
        IReadOnlyDictionary<string, IReadOnlyList<(DateOnly Date, int Value)>> history,
        IEnumerable<LogEntry> changeLog, IReadOnlyList<QualityRow> quality, IReadOnlyList<StandardRow> standards)
    {
        var notes = new List<string>();
        var kpis = new List<KpiRow>();
        foreach (var k in ReportKpis)
        {
            if (!history.TryGetValue(k.Key, out var values) || values.Count == 0)
            {
                continue;
            }

            var ordered = values.OrderBy(v => v.Date).ToList();
            int? At(DateOnly date, bool start)
            {
                var before = ordered.LastOrDefault(v => v.Date <= date);
                if (before.Date != default)
                {
                    return before.Value;
                }

                // No value yet at the start: the first one within the period
                var first = ordered.FirstOrDefault(v => v.Date >= period.From && v.Date <= period.To);
                return start && first.Date != default ? first.Value : null;
            }

            var startValue = At(period.From, true);
            var endValue = At(period.To, false);
            if (startValue is null && endValue is null)
            {
                continue;
            }

            kpis.Add(new KpiRow(k.Key, k.Label, startValue, endValue, k.Attention));
        }

        if (kpis.Count == 0)
        {
            notes.Add("Er zijn geen indicatoren bewaard voor deze periode (de geschiedenis begint bij de eerste lezing).");
        }

        var from = new DateTimeOffset(period.From.ToDateTime(TimeOnly.MinValue));
        var to = new DateTimeOffset(period.To.ToDateTime(TimeOnly.MaxValue));
        var changes = changeLog.Where(r => r.Time >= from && r.Time <= to)
            .GroupBy(r => r.ChangeAction, StringComparer.Ordinal)
            .Select(g => new ChangeRow(g.Key, g.Count(r => r.Succeeded), g.Count(r => r.Failed)))
            .OrderByDescending(c => c.Succeeded + c.Failed).ToList();
        if (period.To > DateOnly.FromDateTime(now.LocalDateTime))
        {
            notes.Add("De periode is nog niet voorbij: de cijfers zijn die van vandaag.");
        }

        return new PeriodReport(tenant, period, now, user, kpis, changes, quality, standards, notes);
    }

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    public static string StatusText(CheckStatus s) => s switch
    {
        CheckStatus.Ok => "OK",
        CheckStatus.Attention => "aandacht",
        CheckStatus.Problem => "probleem",
        _ => "onbekend",
    };

    /// <summary>A self-contained HTML page (no scripts, no outside files) that prints well as PDF.</summary>
    public string ToHtml()
    {
        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"nl\"><head><meta charset=\"utf-8\"><title>").Append(E($"Rapport {Tenant} {Period.Name}")).Append("</title><style>")
            .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:2em;color:#222}h1{font-size:1.6em}h2{margin-top:1.6em;border-bottom:1px solid #ccc}")
            .Append("table{border-collapse:collapse;width:100%;margin:.5em 0}th,td{border-bottom:1px solid #e3e3e3;padding:.3em .5em;text-align:left}")
            .Append("td.n,th.n{text-align:right}.zacht{color:#666}.probleem{color:#b00020;font-weight:600}.aandacht{color:#a15c00}.ok{color:#1b6e2a}")
            .Append("@media print{body{margin:0}}</style></head><body>");
        b.Append("<h1>").Append(E($"Rapport {Period.Name}")).Append("</h1><p class=\"zacht\">").Append(E($"{Tenant} - {Period.From:dd/MM/yyyy} tot {Period.To:dd/MM/yyyy} - gemaakt op {Made.LocalDateTime:dd/MM/yyyy HH:mm} door {MadeBy} met Moss365")).Append("</p>");
        foreach (var n in Notes)
        {
            b.Append("<p class=\"aandacht\">").Append(E(n)).Append("</p>");
        }

        b.Append("<h2>Indicatoren</h2><table><tr><th>Indicator</th><th class=\"n\">Begin</th><th class=\"n\">Einde</th><th class=\"n\">Verschil</th></tr>");
        foreach (var k in Kpis)
        {
            var cls = k.Attention && k.Difference > 0 ? " class=\"n aandacht\"" : " class=\"n\"";
            b.Append("<tr><td>").Append(E(k.Title)).Append("</td><td class=\"n\">").Append(k.Start).Append("</td><td class=\"n\">").Append(k.End)
                .Append("</td><td").Append(cls).Append('>').Append(k.Difference is { } d ? d.ToString("+0;-0;0", CultureInfo.InvariantCulture) : string.Empty).Append("</td></tr>");
        }

        b.Append("</table><h2>Wijzigingen</h2>");
        if (Changes.Count == 0)
        {
            b.Append("<p class=\"zacht\">Geen wijzigingen in deze periode.</p>");
        }
        else
        {
            b.Append("<table><tr><th>Soort</th><th class=\"n\">Gelukt</th><th class=\"n\">Mislukt</th></tr>");
            foreach (var c in Changes)
            {
                b.Append("<tr><td>").Append(E(c.Action)).Append("</td><td class=\"n\">").Append(c.Succeeded).Append("</td><td class=\"n\">").Append(c.Failed).Append("</td></tr>");
            }

            b.Append("<tr><th>Totaal</th><th class=\"n\">").Append(Changes.Sum(c => c.Succeeded)).Append("</th><th class=\"n\">").Append(Changes.Sum(c => c.Failed)).Append("</th></tr></table>");
        }

        b.Append("<h2>Datakwaliteit (actieve medewerkers)</h2><table><tr><th>Dienst</th><th class=\"n\">Mensen</th><th class=\"n\">Functie</th><th class=\"n\">Kantoor</th><th class=\"n\">Manager</th><th class=\"n\">Telefoon</th><th class=\"n\">Score</th></tr>");
        foreach (var q in Quality)
        {
            b.Append("<tr><td>").Append(E(q.Department)).Append("</td><td class=\"n\">").Append(q.People)
                .Append("</td><td class=\"n\">").Append(QualityRow.Percent(q.JobTitle, q.People)).Append("%</td><td class=\"n\">").Append(QualityRow.Percent(q.Office, q.People))
                .Append("%</td><td class=\"n\">").Append(QualityRow.Percent(q.Manager, q.People)).Append("%</td><td class=\"n\">").Append(QualityRow.Percent(q.Phone, q.People))
                .Append("%</td><td class=\"n\">").Append(q.Score).Append("%</td></tr>");
        }

        b.Append("</table><h2>Normen</h2><p class=\"zacht\">De controles van Moss365 als bewijs bij de maatregelen; dat vervangt geen beleid of audit. Het logboek en de momentopnames tonen dat elke wijziging gecontroleerd en terug te vinden is.</p>");
        b.Append("<table><tr><th>Norm</th><th>Maatregel</th><th>Resultaat</th><th>Bewijs</th></tr>");
        foreach (var s in Standards)
        {
            var status = StatusText(s.Status);
            b.Append("<tr><td>").Append(E($"{s.Control.Standard} {s.Control.Id}")).Append("</td><td>").Append(E(s.Control.Title))
                .Append("</td><td class=\"").Append(status).Append("\">").Append(E(status)).Append("</td><td class=\"zacht\">").Append(E(s.Control.Evidence)).Append("</td></tr>");
        }

        b.Append("</table></body></html>");
        return b.ToString();
    }

    /// <summary>The same as CSV (semicolons, for Excel in Dutch and other European settings).</summary>
    public string ToCsv()
    {
        static string C(object? v)
        {
            var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
            if (s.Length > 0 && "=+-@".Contains(s[0]) && !int.TryParse(s, out _))
            {
                s = "'" + s; // never a formula in Excel
            }

            return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        var b = new StringBuilder();
        void Line(params object?[] values) => b.Append(string.Join(';', values.Select(C))).Append("\r\n");
        Line("Rapport", Period.Name, Tenant, Period.From.ToString("yyyy-MM-dd"), Period.To.ToString("yyyy-MM-dd"));
        Line();
        Line("Indicator", "Begin", "Einde", "Verschil");
        foreach (var k in Kpis) Line(k.Title, k.Start, k.End, k.Difference);
        Line();
        Line("Wijziging", "Gelukt", "Mislukt");
        foreach (var c in Changes) Line(c.Action, c.Succeeded, c.Failed);
        Line();
        Line("Dienst", "Mensen", "Functie %", "Kantoor %", "Manager %", "Telefoon %", "Score %");
        foreach (var q in Quality) Line(q.Department, q.People, QualityRow.Percent(q.JobTitle, q.People), QualityRow.Percent(q.Office, q.People), QualityRow.Percent(q.Manager, q.People), QualityRow.Percent(q.Phone, q.People), q.Score);
        Line();
        Line("Norm", "Maatregel", "Resultaat");
        foreach (var s in Standards) Line($"{s.Control.Standard} {s.Control.Id}", s.Control.Title, StatusText(s.Status));
        return b.ToString();
    }
}
