using Moss365.Core.Localization;
using Moss365.Core.Security;
using Moss365.Core.Setup;

namespace Moss365.UI.Services;

/// <summary>The security checks of R11 (0.34): read on demand, kept while the app is open.</summary>
public sealed partial class ManagementState
{
    private readonly Dictionary<string, SecurityCheck> _checks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, SecurityCheck> SecurityCheckResults => _checks;

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
            list.AddRange(new[] { ("ad-beveiliging", Texts.T["security.check.ad-security"]), ("gpo", Texts.T["security.check.group-policy-gpo"]) });
        }

        return list;
    }

    /// <summary>Runs one check (reads what it needs) and keeps the result.</summary>
    public async Task<SecurityCheck> RunSecurityCheckAsync(string key, int alertDays = 7, bool withRules = false, CancellationToken cancel = default)
    {
        var now = DateTimeOffset.Now;
        SecurityCheck check;
        try
        {
            check = key switch
            {
                "meldingen" => Alerts(alertDays),
                "doorsturen" => SecurityChecks.Forwarding(await ForwardingAsync(withRules, cancel), Core.Changes.CloudUserPlans.Domains(Data)),
                "app-geheimen" => SecurityChecks.AppCredentials(Graph() is { } g1 ? await g1.ReadAppsAsync(cancel) : null, now),
                "app-rechten" => SecurityChecks.AppPermissions(Graph() is { } g2 ? await g2.ReadAppsAsync(cancel) : null),
                "secure-score" => SecurityChecks.SecureScore(Graph() is { } g3 ? await g3.ReadSecureScoreAsync(cancel) : null),
                "rollen" => SecurityChecks.Roles(Graph() is { } g4 ? await g4.ReadRoleAssignmentsAsync(cancel) : null),
                "zwakke-mfa" => SecurityChecks.WeakMfa(Data),
                "risico" => SecurityChecks.Risky(Graph() is { } g5 ? await g5.ReadRiskyUsersAsync(cancel) : null),
                "ad-beveiliging" => SecurityChecks.AdSecurity(_factory?.Ad(EffectiveSettings, AdPassword) is { } a1 ? await a1.ReadSecurityAsync(cancel) : null, now),
                "gpo" => SecurityChecks.Gpo(_factory?.Ad(EffectiveSettings, AdPassword) is { } a2 ? await a2.ReadGpoAsync(cancel) : null),
                _ => SecurityCheck.Unknown(key, key, Texts.T["code.management-state-security-checks.unknown-check"]),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            check = SecurityCheck.Unknown(key, SecurityCheckList().FirstOrDefault(c => c.Key == key).Title ?? key, (Texts.T["code.management-state-security-checks.did-not-work"] + " ") + ex.Message);
        }

        _checks[key] = check;
        Changed?.Invoke();
        return check;
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
