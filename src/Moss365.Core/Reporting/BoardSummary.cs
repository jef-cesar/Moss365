using System.Net;
using System.Text;
using Moss365.Core.Localization;
using Moss365.Core.Security;

namespace Moss365.Core.Reporting;

/// <summary>
/// The board summary (R19): one page for management and information security, with the overview of the tenant (the
/// four blocks, each number with its trend), the hardening score over time and the report of the last quarter. A
/// self-contained HTML page (no scripts, no outside files) that prints well as PDF; only numbers, no names of people.
/// </summary>
public static class BoardSummary
{
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    public static string ToHtml(string tenant, DateTimeOffset made, string madeBy, int days, OverviewLayout layout, IReadOnlyList<OverviewTile> tiles,
        IReadOnlyList<ScorePoint> scores, PeriodReport report)
    {
        var t = Texts.T;
        var b = new StringBuilder();
        b.Append("<!doctype html><html lang=\"").Append(t.Language.Code).Append("\"><head><meta charset=\"utf-8\"><title>").Append(E(t.Format("board.title", tenant)))
            .Append("</title><style>").Append(PeriodReport.Style).Append("</style></head><body>");
        b.Append("<h1>").Append(E(t.Format("board.title", tenant))).Append("</h1><p class=\"zacht\">").Append(E(t.Format("board.made", made.LocalDateTime, madeBy, days))).Append("</p>");
        foreach (var block in layout.Order.Where(x => !layout.Hidden.Contains(x)))
        {
            var rows = tiles.Where(x => x.Block == block && x.Value is not null).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            b.Append("<h2>").Append(E(t[$"overview.block.{block}"])).Append("</h2><table><tr><th>").Append(E(t["board.indicator"])).Append("</th><th class=\"n\">")
                .Append(E(t["board.now"])).Append("</th><th>").Append(E(t["board.trend"])).Append("</th></tr>");
            foreach (var x in rows)
            {
                b.Append("<tr><td>").Append(E(x.Label)).Append("</td><td class=\"n").Append(x.Let ? " aandacht" : string.Empty).Append("\">").Append(E(Overview.Number(x)))
                    .Append("</td><td class=\"zacht\">").Append(E(Overview.Trend(x, days))).Append("</td></tr>");
            }

            b.Append("</table>");
        }

        if (scores.Count > 0)
        {
            b.Append("<h2>").Append(E(t["board.scores"])).Append("</h2><table><tr><th>").Append(E(t["board.date"])).Append("</th><th class=\"n\">").Append(E(t["advice.score"])).Append("</th></tr>");
            foreach (var s in scores.OrderByDescending(s => s.Date).Take(12).Reverse())
            {
                b.Append("<tr><td>").Append(E(s.Date.ToString("d", t.Culture))).Append("</td><td class=\"n\">").Append(s.Score).Append("%</td></tr>");
            }

            b.Append("</table>");
        }

        report.AppendBody(b);
        b.Append("</body></html>");
        return b.ToString();
    }
}
