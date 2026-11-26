using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Security;

/// <summary>
/// What Moss365 explains about a check (R16, decision U16): the texts come from the dictionaries
/// (<c>advice.&lt;key&gt;.what</c>, <c>.why</c>, <c>.fix</c> and <c>.risk</c>), the attack technique (MITRE ATT&amp;CK) and the
/// links to Microsoft Learn are fixed. The weight counts in the hardening score (0: not counted).
/// </summary>
public sealed record AdviceInfo(string Key, int Weight, string? Technique, IReadOnlyList<string> Links)
{
    public string What => Texts.T[$"advice.{Key}.what"];

    public string Why => Texts.T[$"advice.{Key}.why"];

    public string Fix => Texts.T[$"advice.{Key}.fix"];

    public string Risk => Texts.T[$"advice.{Key}.risk"];

    /// <summary>The page of the technique at MITRE (T1558.003 gives techniques/T1558/003/).</summary>
    public string? TechniqueLink => Technique is { Length: > 0 } t ? $"https://attack.mitre.org/techniques/{t.Replace('.', '/')}/" : null;
}

/// <summary>A risk that is accepted for a check, or for one line of it (<see cref="Object"/>), until a date.</summary>
public sealed record AdviceException(string CheckKey, string Object, string Reason, string By, DateTimeOffset Since, DateOnly Until)
{
    public bool Active(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime) <= Until;

    public bool ForWholeCheck => Object.Length == 0;
}

/// <summary>One point of the hardening score (one per day).</summary>
public sealed record ScorePoint(DateOnly Date, int Score);

/// <summary>The last result of every check and the score per day, kept in the tenant database (document <see cref="SecurityAdvice.Document"/>).</summary>
public sealed record SecurityResults
{
    public IReadOnlyList<SecurityCheck> Last { get; init; } = Array.Empty<SecurityCheck>();

    public IReadOnlyList<AdviceException> Exceptions { get; init; } = Array.Empty<AdviceException>();

    public IReadOnlyList<ScorePoint> Scores { get; init; } = Array.Empty<ScorePoint>();
}

/// <summary>
/// Security advice (R16): the explanation of every check, risks accepted with a reason and an end date, and a hardening
/// score of our own over AD and Microsoft 365, computed locally from the results.
/// </summary>
public static class SecurityAdvice
{
    public const string Document = "beveiligingscontroles";

    /// <summary>The longest an accepted risk lasts; after that the advice comes back.</summary>
    public const int MaxExceptionDays = 366;

    /// <summary>The most lines kept per check (the rest is read again at the next check).</summary>
    public const int MaxStoredRows = 500;

    private const int MaxScorePoints = 800;

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false }.WithLegacyNames();

    /// <summary>Every check with its weight, technique and links (the alerts are monitoring, not hardening: weight 0).</summary>
    public static IReadOnlyList<AdviceInfo> All { get; } = new AdviceInfo[]
    {
        new("meldingen", 0, "T1098", new[] { "https://learn.microsoft.com/entra/identity/role-based-access-control/security-planning" }),
        new("doorsturen", 2, "T1114.003", new[] { "https://learn.microsoft.com/defender-office-365/outbound-spam-policies-external-email-forwarding" }),
        new("app-geheimen", 1, "T1098.001", new[] { "https://learn.microsoft.com/entra/identity-platform/security-best-practices-for-app-registration" }),
        new("app-rechten", 2, "T1528", new[]
        {
            "https://learn.microsoft.com/entra/identity/enterprise-apps/configure-user-consent",
            "https://learn.microsoft.com/defender-office-365/detect-and-remediate-illicit-consent-grants",
        }),
        new("secure-score", 1, null, new[] { "https://learn.microsoft.com/defender-xdr/microsoft-secure-score" }),
        new("rollen", 3, "T1078.004", new[]
        {
            "https://learn.microsoft.com/entra/identity/role-based-access-control/best-practices",
            "https://learn.microsoft.com/entra/id-governance/privileged-identity-management/pim-configure",
        }),
        new("zwakke-mfa", 3, "T1111", new[] { "https://learn.microsoft.com/entra/identity/authentication/concept-authentication-methods" }),
        new("risico", 2, "T1078", new[] { "https://learn.microsoft.com/entra/id-protection/howto-identity-protection-investigate-risk" }),
        new("ad-beveiliging", 3, "T1558", new[]
        {
            "https://learn.microsoft.com/windows-server/identity/ad-ds/plan/security-best-practices/best-practices-for-securing-active-directory",
            "https://learn.microsoft.com/windows-server/identity/ad-ds/manage/forest-recovery-guide/ad-forest-recovery-reset-the-krbtgt-password",
        }),
        new("gpo", 1, "T1484.001", Array.Empty<string>()),
    };

    public static AdviceInfo? Info(string key) => All.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The account flags (userAccountControl) that the advice may turn off as a plan; the engine refuses any other.</summary>
    public static IReadOnlyDictionary<string, int> AccountFlags { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["PasswordNotRequired"] = 0x20,
        ["ReversibleEncryption"] = 0x80,
    };

    /// <summary>The lines of a check that can be solved as a plan: with a fix, not OK and not accepted.</summary>
    public static IReadOnlyList<CheckRow> Solvable(SecurityCheck check)
        => check.AcceptedUntil is not null
            ? Array.Empty<CheckRow>()
            : check.Rows.Where(r => r.Fix is { } f && AccountFlags.ContainsKey(f) && r.AcceptedUntil is null && r.Status != CheckStatus.Ok).ToList();

    /// <summary>
    /// The plan that solves the lines of a check (U16: low risk with undo, so a plan right away): the flag is turned off
    /// on the AD account, with its previous value for undo. A line whose account is not in the data read gives a message.
    /// </summary>
    public static PlanOutcome Solve(InventoryData data, SecurityCheck check)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var r in Solvable(check))
        {
            var fix = r.Fix!;
            var user = data.AdUsers.FirstOrDefault(u => string.Equals(u.SamAccountName, r.Object, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                errors.Add(Texts.T.Format("advice.solve.not-found", r.Object));
                continue;
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdSetAccountFlag,
                Target = user.DistinguishedName,
                TargetName = user.SamAccountName,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["vlag"] = fix, ["aan"] = "False" },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["aan"] = "True" },
                Description = Texts.T.Format("advice.solve.flag-off", FlagText(fix), user.SamAccountName),
                CurrentValue = Texts.T["device.enabled"],
                NewValue = Texts.T["device.disabled"],
                Warning = fix == "ReversibleEncryption" ? Texts.T["advice.solve.reversible-warning"] : null,
            });
        }

        return PlanOutcome.Of(plan, errors);
    }

    public static string FlagText(string flag) => flag switch
    {
        "PasswordNotRequired" => Texts.T["advice.flag.password-not-required"],
        "ReversibleEncryption" => Texts.T["advice.flag.reversible-encryption"],
        _ => flag,
    };

    /// <summary>
    /// The check as shown: a risk accepted for the whole check marks the check, one for a line marks that line. When the
    /// status of the check follows from its lines, it is computed again from the lines that are not accepted.
    /// </summary>
    public static SecurityCheck Apply(SecurityCheck check, IEnumerable<AdviceException> exceptions, DateTimeOffset now)
    {
        var active = exceptions.Where(e => e.Active(now) && string.Equals(e.CheckKey, check.Key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (active.Count == 0)
        {
            return check;
        }

        var rowDriven = check.Status == Worst(check.Rows);
        var rows = check.Rows.Select(r => active.FirstOrDefault(e => !e.ForWholeCheck && string.Equals(e.Object, r.Object, StringComparison.OrdinalIgnoreCase)) is { } e
            ? r with { AcceptedUntil = e.Until }
            : r).ToList();
        var status = rowDriven && check.Status != CheckStatus.Unknown ? Worst(rows.Where(r => r.AcceptedUntil is null)) : check.Status;
        var whole = active.Where(e => e.ForWholeCheck).OrderByDescending(e => e.Until).FirstOrDefault();
        return check with
        {
            Status = status,
            Rows = rows,
            AcceptedUntil = whole?.Until,
            AcceptedNote = whole is null ? null : Texts.T.Format("advice.exception.active", whole.Until.ToString("d", Texts.T.Culture), whole.By, whole.Reason),
        };
    }

    /// <summary>
    /// The hardening score (0 to 100) of the checks that gave a result: OK counts fully, attention half, a problem not; a
    /// risk accepted for the whole check counts as handled. Weighted per check; null when nothing was checked yet.
    /// </summary>
    public static int? Score(IEnumerable<SecurityCheck> checks)
    {
        double total = 0, max = 0;
        foreach (var c in checks)
        {
            var weight = Info(c.Key)?.Weight ?? 0;
            if (weight == 0 || (c.Status == CheckStatus.Unknown && c.AcceptedUntil is null))
            {
                continue;
            }

            max += weight;
            total += weight * (c.AcceptedUntil is not null ? 1 : c.Status switch { CheckStatus.Ok => 1, CheckStatus.Attention => 0.5, _ => 0 });
        }

        return max == 0 ? null : (int)Math.Round(100 * total / max, MidpointRounding.AwayFromZero);
    }

    /// <summary>A new accepted risk; refused without a reason, or with an end date that is not within a year.</summary>
    public static AdviceException Accept(string checkKey, string obj, string reason, string by, DateTimeOffset now, DateOnly until)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(Texts.T["advice.reason-required"], nameof(reason));
        }

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        if (until < today || until > today.AddDays(MaxExceptionDays))
        {
            throw new ArgumentException(Texts.T["advice.until-range"], nameof(until));
        }

        return new AdviceException(checkKey, obj.Trim(), reason.Trim(), by, now, until);
    }

    /// <summary>The results with this check as the last one (its lines capped) and today's score.</summary>
    public static SecurityResults WithResult(SecurityResults results, SecurityCheck check, DateTimeOffset now)
    {
        var kept = check with { Rows = check.Rows.Take(MaxStoredRows).ToList(), Time = now };
        var last = results.Last.Where(c => !string.Equals(c.Key, check.Key, StringComparison.OrdinalIgnoreCase)).Append(kept).ToList();
        return WithScore(results with { Last = last }, now);
    }

    /// <summary>The results with an accepted risk added, or (<paramref name="add"/> false) lifted, and today's score.</summary>
    public static SecurityResults WithException(SecurityResults results, AdviceException exception, bool add, DateTimeOffset now)
    {
        var others = results.Exceptions.Where(e => !(string.Equals(e.CheckKey, exception.CheckKey, StringComparison.OrdinalIgnoreCase)
                                                     && string.Equals(e.Object, exception.Object, StringComparison.OrdinalIgnoreCase)));
        return WithScore(results with { Exceptions = (add ? others.Append(exception) : others).ToList() }, now);
    }

    /// <summary>Today's point of the hardening score, from the last results with the accepted risks.</summary>
    public static SecurityResults WithScore(SecurityResults results, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var score = Score(results.Last.Select(c => Apply(c, results.Exceptions, now)));
        var points = results.Scores.Where(p => p.Date != today).ToList();
        if (score is { } s)
        {
            points.Add(new ScorePoint(today, s));
        }

        return results with { Scores = points.OrderBy(p => p.Date).TakeLast(MaxScorePoints).ToList() };
    }

    public static SecurityResults Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SecurityResults();
        }

        try
        {
            return JsonSerializer.Deserialize<SecurityResults>(json, Json) ?? new SecurityResults();
        }
        catch (JsonException)
        {
            return new SecurityResults();
        }
    }

    public static string Write(SecurityResults results) => JsonSerializer.Serialize(results, Json);

    /// <summary>The change log line of an accepted risk (evidence for an audit), or of lifting it.</summary>
    public static LogEntry LogLine(AdviceException e, string title, bool lifted, string user, DateTimeOffset now)
    {
        var what = e.ForWholeCheck ? title : $"{title}: {e.Object}";
        return new LogEntry(now, user, Texts.T["menu.controles"], "Moss365", "AcceptRisk", e.CheckKey, what,
            lifted ? Texts.T.Format("advice.exception.lifted", what) : Texts.T.Format("advice.exception.accepted", e.Until.ToString("d", Texts.T.Culture), what),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["controle"] = e.CheckKey, ["object"] = e.Object, ["tot"] = e.Until.ToString("yyyy-MM-dd") },
            "Succeeded", e.Reason);
    }

    private static CheckStatus Worst(IEnumerable<CheckRow> rows)
        => rows.Select(r => r.Status).Where(s => s != CheckStatus.Ok).DefaultIfEmpty(CheckStatus.Ok).Max();
}
