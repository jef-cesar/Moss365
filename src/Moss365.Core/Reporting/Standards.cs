using Moss365.Core.Security;

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

    public static IReadOnlyList<StandardControl> All { get; } = new[]
    {
        new StandardControl(Nis2, "21.2.a", "Risicoanalyse en beleid voor de beveiliging van informatiesystemen", new[] { "secure-score", "gpo", "ad-beveiliging" }, "Secure Score en de AD- en GPO-controle, bewaard in de momentopnames."),
        new StandardControl(Nis2, "21.2.b", "Incidenten behandelen", new[] { "meldingen", "risico", "doorsturen" }, "Meldingen uit de momentopnames, risicovolle gebruikers en doorsturen naar buiten."),
        new StandardControl(Nis2, "21.2.d", "Beveiliging van de toeleveringsketen", new[] { "app-rechten", "app-geheimen" }, "Apps met brede rechten en hun geheimen."),
        new StandardControl(Nis2, "21.2.f", "De doeltreffendheid van de maatregelen beoordelen", new[] { "secure-score" }, "De trend van Secure Score en van de KPI's (rapport per periode)."),
        new StandardControl(Nis2, "21.2.i", "Toegangsbeheer en beheer van bedrijfsmiddelen", new[] { "rollen", "ad-beveiliging", "meldingen" }, "Beheerrollen, bevoorrechte groepen, het logboek van elke wijziging."),
        new StandardControl(Nis2, "21.2.j", "Multifactorauthenticatie", new[] { "zwakke-mfa" }, "MFA-methoden per gebruiker en de Conditional Access-controle."),
        new StandardControl(Cis, "5", "Accountbeheer", new[] { "meldingen", "ad-beveiliging" }, "Inactieve en uitgeschakelde accounts (KPI's), het logboek van in- en uitstroom."),
        new StandardControl(Cis, "6", "Beheer van toegangsrechten", new[] { "rollen", "zwakke-mfa" }, "Beheerrollen, PIM en MFA."),
        new StandardControl(Cis, "4", "Veilige configuratie", new[] { "gpo", "secure-score" }, "Groepsbeleid en Secure Score."),
        new StandardControl(Cis, "8", "Beheer van auditlogs", new[] { "meldingen" }, "Het logboek in Moss365 en de momentopnames van elke lezing."),
        new StandardControl(Cis, "15", "Beheer van dienstverleners", new[] { "app-rechten", "app-geheimen" }, "Apps van derden met hun rechten."),
    };

    /// <summary>Every measure with the worst result of its checks; a check that did not run is unknown.</summary>
    public static IReadOnlyList<StandardRow> Evaluate(IReadOnlyDictionary<string, SecurityCheck> results, IEnumerable<string>? available = null)
    {
        var known = available?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Select(c =>
        {
            var checks = c.Checks.Where(k => known is null || known.Contains(k)).ToList();
            var list = checks.Select(k => results.TryGetValue(k, out var r) ? (k, r.Status, r.Summary) : (k, CheckStatus.Unknown, "niet uitgevoerd")).ToList();
            var status = list.Count == 0 ? CheckStatus.Unknown
                : list.Any(x => x.Item2 == CheckStatus.Problem) ? CheckStatus.Problem
                : list.Any(x => x.Item2 == CheckStatus.Attention) ? CheckStatus.Attention
                : list.Any(x => x.Item2 == CheckStatus.Unknown) ? CheckStatus.Unknown
                : CheckStatus.Ok;
            return new StandardRow(c, status, list);
        }).ToList();
    }
}
