using Moss365.Core.Security;
using Moss365.Core.Localization;

namespace Moss365.Core.Reporting;

/// <summary>A measure of a standard and the checks of Moss365 that give evidence for it.</summary>
public sealed record StandardControl(string Standard, string Id, string Title, IReadOnlyList<string> Checks, string Evidence);

/// <summary>A measure with the result of its checks.</summary>
public sealed record StandardRow(StandardControl Control, CheckStatus Status, IReadOnlyList<(string Check, CheckStatus Status, string Summary)> Results);

/// <summary>
/// The security checks mapped to standards (R14, 0.37): NIS2 (article 21, the measures) and the CIS Controls v8.
/// A mapping gives evidence, not a certificate: the measures also need policy and people. The change log and the
/// snapshots are the evidence that changes are controlled and traceable.
/// </summary>
public static class Standards
{
    public const string Nis2 = "NIS2 (art. 21)";
    public const string Cis = "CIS Controls v8";

    public static IReadOnlyList<StandardControl> All => new[]
    {
        new StandardControl(Nis2, "21.2.a", Texts.T["standard.nis2.21.2.a"], new[] { "secure-score", "gpo", "ad-beveiliging" }, Texts.T["standard.nis2.21.2.a.evidence"]),
        new StandardControl(Nis2, "21.2.b", Texts.T["standard.nis2.21.2.b"], new[] { "meldingen", "risico", "doorsturen" }, Texts.T["standard.nis2.21.2.b.evidence"]),
        new StandardControl(Nis2, "21.2.d", Texts.T["standard.nis2.21.2.d"], new[] { "app-rechten", "app-geheimen" }, Texts.T["standard.nis2.21.2.d.evidence"]),
        new StandardControl(Nis2, "21.2.f", Texts.T["standard.nis2.21.2.f"], new[] { "secure-score" }, Texts.T["standard.nis2.21.2.f.evidence"]),
        new StandardControl(Nis2, "21.2.i", Texts.T["standard.nis2.21.2.i"], new[] { "rollen", "ad-beveiliging", "meldingen" }, Texts.T["standard.nis2.21.2.i.evidence"]),
        new StandardControl(Nis2, "21.2.j", Texts.T["standard.nis2.21.2.j"], new[] { "zwakke-mfa" }, Texts.T["standard.nis2.21.2.j.evidence"]),
        new StandardControl(Cis, "5", Texts.T["standard.cis.5"], new[] { "meldingen", "ad-beveiliging" }, Texts.T["standard.cis.5.evidence"]),
        new StandardControl(Cis, "6", Texts.T["standard.cis.6"], new[] { "rollen", "zwakke-mfa" }, Texts.T["standard.cis.6.evidence"]),
        new StandardControl(Cis, "4", Texts.T["standard.cis.4"], new[] { "gpo", "secure-score" }, Texts.T["standard.cis.4.evidence"]),
        new StandardControl(Cis, "8", Texts.T["standard.cis.8"], new[] { "meldingen" }, Texts.T["standard.cis.8.evidence"]),
        new StandardControl(Cis, "15", Texts.T["standard.cis.15"], new[] { "app-rechten", "app-geheimen" }, Texts.T["standard.cis.15.evidence"]),
    };

    /// <summary>Every measure with the worst result of its checks; a check that did not run is unknown.</summary>
    public static IReadOnlyList<StandardRow> Evaluate(IReadOnlyDictionary<string, SecurityCheck> results, IEnumerable<string>? available = null)
    {
        var known = available?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Select(c =>
        {
            var checks = c.Checks.Where(k => known is null || known.Contains(k)).ToList();
            var list = checks.Select(k => results.TryGetValue(k, out var r) ? (k, r.Status, r.Summary) : (k, CheckStatus.Unknown, Texts.T["cleanup.not-run"])).ToList();
            var status = list.Count == 0 ? CheckStatus.Unknown
                : list.Any(x => x.Item2 == CheckStatus.Problem) ? CheckStatus.Problem
                : list.Any(x => x.Item2 == CheckStatus.Attention) ? CheckStatus.Attention
                : list.Any(x => x.Item2 == CheckStatus.Unknown) ? CheckStatus.Unknown
                : CheckStatus.Ok;
            return new StandardRow(c, status, list);
        }).ToList();
    }
}
