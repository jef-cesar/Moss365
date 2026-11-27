using Moss365.Core.Localization;
using Moss365.Core.Security;
using Moss365.Core.Setup;

namespace Moss365.UI.Services;

/// <summary>
/// The security checks of R11 (0.34), read on demand. Since R16 the last result of every check, the accepted risks and
/// the hardening score per day are kept in the tenant database (document <see cref="SecurityAdvice.Document"/>).
/// </summary>
public sealed partial class ManagementState
{
    private readonly Dictionary<string, SecurityCheck> _checks = new(StringComparer.OrdinalIgnoreCase);
    private SecurityResults? _securityResults;

    private SecurityResults StoredSecurity => _securityResults ??= SecurityAdvice.Read(Database.ReadDocument(SecurityAdvice.Document));

    /// <summary>
    /// The last result of every check (from this session, otherwise as kept), with the accepted risks applied: a line or
    /// a check whose risk is accepted no longer counts as attention or problem.
    /// </summary>
    public IReadOnlyDictionary<string, SecurityCheck> SecurityCheckResults
    {
        get
        {
            var now = DateTimeOffset.Now;
            var stored = StoredSecurity;
            var all = new Dictionary<string, SecurityCheck>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in stored.Last)
            {
                all[c.Key] = c;
            }

            foreach (var (key, c) in _checks)
            {
                all[key] = c;
            }

            var mine = SecurityCheckList().Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return all.Where(c => mine.Contains(c.Key)).ToDictionary(c => c.Key, c => SecurityAdvice.Apply(c.Value, stored.Exceptions, now), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The hardening score (0 to 100) of the checks of this tenant, or null when nothing was checked yet.</summary>
    public int? HardeningScore => SecurityAdvice.Score(SecurityCheckResults.Values);

    /// <summary>The hardening score per day, as kept.</summary>
    public IReadOnlyList<ScorePoint> HardeningScores => StoredSecurity.Scores;

    /// <summary>The accepted risks that still count (an expired one no longer does: the advice is back).</summary>
    public IReadOnlyList<AdviceException> AcceptedRisks => StoredSecurity.Exceptions.Where(e => e.Active(DateTimeOffset.Now)).ToList();

    /// <summary>Accepts the risk of a check, or of one line of it, until a date (role Administrator); in the change log.</summary>
    public void AcceptRisk(string checkKey, string obj, string reason, DateOnly until)
    {
        if (IsHistoric || !Can(Core.Permissions.Permission.Manage))
        {
            throw new InvalidOperationException(Texts.T["advice.not-allowed"]);
        }

        var now = DateTimeOffset.Now;
        var exception = SecurityAdvice.Accept(checkKey, obj, reason, User, now, until);
        SaveSecurity(SecurityAdvice.WithException(StoredSecurity, exception, add: true, now));
        ChangeLog.Write(SecurityAdvice.LogLine(exception, CheckTitle(checkKey), lifted: false, User, now));
        ForgetKpiCounts();
        Changed?.Invoke();
    }

    /// <summary>Lifts an accepted risk (role Administrator): the advice counts again; in the change log.</summary>
    public void LiftRisk(AdviceException exception)
    {
        if (IsHistoric || !Can(Core.Permissions.Permission.Manage))
        {
            throw new InvalidOperationException(Texts.T["advice.not-allowed"]);
        }

        var now = DateTimeOffset.Now;
        SaveSecurity(SecurityAdvice.WithException(StoredSecurity, exception, add: false, now));
        ChangeLog.Write(SecurityAdvice.LogLine(exception, CheckTitle(exception.CheckKey), lifted: true, User, now));
        ForgetKpiCounts();
        Changed?.Invoke();
    }

    private string CheckTitle(string key) => SecurityCheckList().FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)).Title ?? key;

    private void SaveSecurity(SecurityResults results)
    {
        _securityResults = results;
        Database.SaveDocument(SecurityAdvice.Document, SecurityAdvice.Write(results));
    }

    /// <summary>
    /// Keeps the result in the tenant database. A check that could not run (unknown) does not replace a real result kept
    /// earlier, so that the last real result and its date stay; the past is never written to.
    /// </summary>
    private void KeepResult(SecurityCheck check, DateTimeOffset now)
    {
        if (IsHistoric)
        {
            return;
        }

        if (check.Status == CheckStatus.Unknown && StoredSecurity.Last.Any(c => string.Equals(c.Key, check.Key, StringComparison.OrdinalIgnoreCase) && c.Status != CheckStatus.Unknown))
        {
            return;
        }

        SaveSecurity(SecurityAdvice.WithResult(StoredSecurity, check, now));
    }

    /// <summary>The checks this tenant has (AD, Microsoft 365), in the order of the screen.</summary>
    public IReadOnlyList<(string Key, string Title)> SecurityCheckList()
    {
        var list = new List<(string, string)> { ("meldingen", Texts.T["code.management-state-security-checks.alerts-from-the-snapshots"]) };
        if (HasMicrosoft365)
        {
            list.AddRange(new[]
            {
                ("doorsturen", Texts.T["code.management-state-security-checks.forwarding-outside"]), ("app-geheimen", Texts.T["code.management-state-security-checks.secrets-and-certificates-of-apps"]), ("app-rechten", Texts.T["code.management-state-security-checks.broad-app-permissions-and-consents"]),
                ("secure-score", "Microsoft Secure Score"), ("rollen", Texts.T["code.management-state-security-checks.admin-roles-and-pim"]), ("zwakke-mfa", Texts.T["security.check.weak-mfa-methods"]), ("risico", Texts.T["security.check.risky-users"]),
            });
        }

        if (HasAd)
        {
            list.AddRange(new[] { ("ad-beveiliging", Texts.T["security.check.ad-security"]), ("ad-groepen", Texts.T["security.check.ad-groups"]), ("gpo", Texts.T["security.check.group-policy-gpo"]) });
        }

        return list;
    }

    /// <summary>
    /// Runs one check (reads what it needs) and keeps the result. Within a round (all checks after each other), what
    /// more than one check needs (the app registrations) is read once.
    /// </summary>
    public async Task<SecurityCheck> RunSecurityCheckAsync(string key, int alertDays = 7, bool withRules = false, CancellationToken cancel = default, SecurityCheckRound? round = null)
    {
        var now = DateTimeOffset.Now;
        SecurityCheck check;
        try
        {
            check = key switch
            {
                "meldingen" => Alerts(alertDays),
                "doorsturen" => SecurityChecks.Forwarding(await ForwardingAsync(withRules, cancel), Core.Changes.CloudUserPlans.Domains(Data)),
                "app-geheimen" => SecurityChecks.AppCredentials(await AppsAsync(round, cancel), now),
                "app-rechten" => SecurityChecks.AppPermissions(await AppsAsync(round, cancel)),
                "secure-score" => SecurityChecks.SecureScore(Graph() is { } g3 ? await g3.ReadSecureScoreAsync(cancel) : null),
                "rollen" => SecurityChecks.Roles(Graph() is { } g4 ? await g4.ReadRoleAssignmentsAsync(cancel) : null),
                "zwakke-mfa" => SecurityChecks.WeakMfa(Data),
                "risico" => SecurityChecks.Risky(Graph() is { } g5 ? await g5.ReadRiskyUsersAsync(cancel) : null),
                "ad-beveiliging" => SecurityChecks.AdSecurity(await AdSecurityAsync(round, cancel), now),
                "ad-groepen" => AdHardening.Groups(await AdSecurityAsync(round, cancel)),
                "gpo" => SecurityChecks.Gpo(_factory?.Ad(EffectiveSettings, AdPassword) is { } a2 ? await a2.ReadGpoAsync(cancel) : null),
                _ => SecurityCheck.Unknown(key, key, Texts.T["code.management-state-security-checks.unknown-check"]),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            check = SecurityCheck.Unknown(key, SecurityCheckList().FirstOrDefault(c => c.Key == key).Title ?? key, (Texts.T["code.management-state-security-checks.did-not-work"] + " ") + ex.Message);
        }

        check = check with { Time = now };
        _checks[key] = check;
        KeepResult(check, now);
        Changed?.Invoke();
        return check;
    }

    /// <summary>The app registrations: read once per round, or fresh for a check on its own.</summary>
    private Task<AppsReading?> AppsAsync(SecurityCheckRound? round, CancellationToken cancel)
    {
        if (Graph() is not { } graph)
        {
            return Task.FromResult<AppsReading?>(null);
        }

        return round is null ? graph.ReadAppsAsync(cancel) : (round.Apps ??= graph.ReadAppsAsync(cancel));
    }

    /// <summary>The security reading of AD: read once per round (two checks use it), or fresh for a check on its own.</summary>
    private Task<AdSecurityReading?> AdSecurityAsync(SecurityCheckRound? round, CancellationToken cancel)
    {
        if (!HasAd || _factory?.Ad(EffectiveSettings, AdPassword) is not { } ad)
        {
            return Task.FromResult<AdSecurityReading?>(null);
        }

        return round is null ? ad.ReadSecurityAsync(cancel) : (round.AdSecurity ??= ad.ReadSecurityAsync(cancel));
    }

    private async Task<IReadOnlyList<ForwardingRule>?> ForwardingAsync(bool withRules, CancellationToken cancel)
        => HasMicrosoft365 && _factory?.Exchange(EffectiveSettings) is { } exchange ? await exchange.ReadForwardingAsync(withRules, null, cancel) : null;

    private SecurityCheck Alerts(int days)
    {
        var since = DateTimeOffset.Now.AddDays(-days);
        var snapshot = Database.SnapshotAt(since);
        var before = snapshot is null ? null : DataAt(since);
        return SecurityChecks.Alerts(before, Data, snapshot?.Time);
    }
}

/// <summary>One round of checks after each other ("check everything"): what more than one check reads, is read once.</summary>
public sealed class SecurityCheckRound
{
    internal Task<AppsReading?>? Apps { get; set; }

    internal Task<AdSecurityReading?>? AdSecurity { get; set; }
}
