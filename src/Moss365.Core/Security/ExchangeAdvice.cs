using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Security;

/// <summary>An outbound spam policy of Exchange Online with its mode for automatic forwarding (On, Off, Automatic).</summary>
public sealed record ForwardingPolicy(string Name, string Mode, bool Default);

/// <summary>SPF, DMARC (DNS) and DKIM (Exchange) of one accepted domain.</summary>
public sealed record MailDomain(string Domain, IReadOnlyList<string> Spf, IReadOnlyList<string> Dmarc, bool? DkimEnabled, string Selector1, string Selector2)
{
    public bool Microsoft => Domain.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A rule of the preset security policies (Standard or Strict) with its state.</summary>
public sealed record PresetRule(string Name, string State);

/// <summary>The preset security policies: Exchange Online Protection, and Defender for Office 365 where licensed.</summary>
public sealed record PresetReading(IReadOnlyList<PresetRule> Eop, IReadOnlyList<PresetRule> Atp, bool AtpAvailable);

/// <summary>What the advice for Exchange Online reads (R16).</summary>
public sealed record ExchangeOrganisationReading(
    bool SmtpAuthOff,
    IReadOnlyList<string> SmtpAuthMailboxes,
    IReadOnlyList<ForwardingPolicy> Forwarding,
    bool? ExternalTag,
    bool? AuditLog,
    IReadOnlyList<MailDomain> Domains,
    PresetReading? Presets);

/// <summary>Who sent mail with SMTP AUTH in the last 30 days (the usage report of Microsoft 365).</summary>
public sealed record SmtpAuthUsage(IReadOnlyList<string> Users, bool Concealed);

/// <summary>
/// The advice for Exchange Online that is carried out as a plan (R16, chapter 10): SMTP AUTH (guided: whoever still uses
/// it keeps an exception), automatic forwarding to outside, the "external" tag in Outlook, the audit log, the preset
/// security policies, and SPF, DKIM and DMARC per domain. Every change goes through the fixed commands of Exchange.ps1.
/// </summary>
public static class ExchangeAdvice
{
    public const string SmtpAuth = "smtp-auth";
    public const string Forwarding = "forwarding";
    public const string ExternalTag = "external-tag";
    public const string Audit = "audit";
    public const string Preset = "preset";
    public const string Dkim = "dkim";

    public const string StandardPreset = "Standard Preset Security Policy";
    public const string StrictPreset = "Strict Preset Security Policy";

    public static readonly IReadOnlyList<string> Fixes = new[] { SmtpAuth, Forwarding, ExternalTag, Audit, Preset, Dkim };

    /// <summary>The settings <see cref="ChangeAction.ExchangeOrganisationSetting"/> may change (Exchange.ps1 refuses any other).</summary>
    public static readonly IReadOnlyList<string> Settings = new[] { "SmtpAuth", "SmtpAuthPostvak", "Doorsturen", "ExternLabel", "Auditlog", "Dkim", "Preset" };

    private static Dictionary<string, string> Data(params (string Key, string Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>The check of the organisation settings of Exchange Online.</summary>
    public static SecurityCheck Check(ExchangeOrganisationReading? r, SmtpAuthUsage? usage)
    {
        var title = Texts.T["security.check.exchange"];
        if (r is null)
        {
            return SecurityCheck.Unknown("exchange-instellingen", title, Texts.T["code.security-checks.not-checked-yet-exchange-online"]);
        }

        var rows = new List<CheckRow>();
        if (!r.SmtpAuthOff)
        {
            var users = usage?.Users ?? Array.Empty<string>();
            rows.Add(new CheckRow("SMTP AUTH", usage is null ? Texts.T["advice.smtp.on-unknown"] : Texts.T.Format("advice.smtp.on", users.Count), CheckStatus.Attention)
            {
                Fix = SmtpAuth,
                FixData = Data(("gebruikers", string.Join(";", users)), ("uitzonderingen", string.Join(";", r.SmtpAuthMailboxes)),
                    ("verborgen", usage?.Concealed == true ? "True" : "False"), ("gelezen", usage is null ? "False" : "True")),
            });
        }

        foreach (var p in r.Forwarding.Where(p => p.Mode == "On"))
        {
            rows.Add(new CheckRow(p.Name, Texts.T[p.Default ? "advice.forwarding.default-on" : "advice.forwarding.policy-on"], p.Default ? CheckStatus.Problem : CheckStatus.Attention)
            {
                Fix = Forwarding,
                FixData = Data(("identity", p.Name), ("was", p.Mode)),
            });
        }

        if (r.ExternalTag == false)
        {
            rows.Add(new CheckRow(Texts.T["advice.external.object"], Texts.T["advice.external.off"], CheckStatus.Attention) { Fix = ExternalTag });
        }

        if (r.AuditLog == false)
        {
            rows.Add(new CheckRow(Texts.T["advice.audit.object"], Texts.T["advice.audit.off"], CheckStatus.Problem) { Fix = Audit });
        }

        if (r.Presets is { } presets)
        {
            var on = presets.Eop.Any(x => x.Name is StandardPreset or StrictPreset && x.State == "Enabled");
            var licence = presets.AtpAvailable ? string.Empty : " " + Texts.T["advice.preset.no-defender"];
            if (!on)
            {
                var standard = presets.Eop.FirstOrDefault(x => x.Name == StandardPreset);
                rows.Add(standard is null
                    ? new CheckRow(Texts.T["advice.preset.object"], Texts.T["advice.preset.missing"] + licence, CheckStatus.Attention)
                    : new CheckRow(Texts.T["advice.preset.object"], Texts.T["advice.preset.off"] + licence, CheckStatus.Attention) { Fix = Preset, FixData = Data(("identity", StandardPreset)) });
            }
        }

        var shown = rows.Count(x => x.Status != CheckStatus.Ok);
        return new SecurityCheck("exchange-instellingen", title, rows.Select(x => x.Status).DefaultIfEmpty(CheckStatus.Ok).Max(),
            shown == 0 ? Texts.T["advice.exchange.none"] : Texts.T.Format("code.security-checks.point-s-to-check", shown), rows);
    }

    /// <summary>SPF, DKIM and DMARC per own domain (the onmicrosoft.com domains are Microsoft's).</summary>
    public static SecurityCheck Domains(ExchangeOrganisationReading? r)
    {
        var title = Texts.T["security.check.mail-domains"];
        if (r is null || r.Domains.Count == 0)
        {
            return SecurityCheck.Unknown("maildomeinen", title, Texts.T["code.security-checks.not-checked-yet-exchange-online"]);
        }

        var rows = new List<CheckRow>();
        foreach (var d in r.Domains.Where(d => !d.Microsoft))
        {
            var (spfStatus, spf) = d.Spf.Count switch
            {
                0 => (CheckStatus.Problem, Texts.T["advice.spf.none"]),
                > 1 => (CheckStatus.Problem, Texts.T["advice.spf.double"]),
                _ when d.Spf[0].Contains("+all", StringComparison.OrdinalIgnoreCase) => (CheckStatus.Problem, Texts.T["advice.spf.all"]),
                _ when d.Spf[0].Contains("?all", StringComparison.OrdinalIgnoreCase) => (CheckStatus.Attention, Texts.T["advice.spf.neutral"]),
                _ when d.Spf[0].Contains("~all", StringComparison.OrdinalIgnoreCase) => (CheckStatus.Attention, Texts.T["advice.spf.soft"]),
                _ => (CheckStatus.Ok, Texts.T["advice.spf.ok"]),
            };
            rows.Add(new CheckRow($"{d.Domain} (SPF)", spf + (d.Spf.Count == 1 ? $" ({d.Spf[0]})" : string.Empty), spfStatus));

            var policy = d.Dmarc.Count == 1 ? Tag(d.Dmarc[0], "p") : null;
            rows.Add(new CheckRow($"{d.Domain} (DMARC)", d.Dmarc.Count switch
            {
                0 => Texts.T["advice.dmarc.none"],
                > 1 => Texts.T["advice.dmarc.double"],
                _ => policy is "quarantine" or "reject" ? Texts.T.Format("advice.dmarc.ok", policy) : Texts.T["advice.dmarc.monitor"],
            }, d.Dmarc.Count == 1 && policy is "quarantine" or "reject" ? CheckStatus.Ok : CheckStatus.Attention));

            if (d.DkimEnabled != true)
            {
                rows.Add(new CheckRow($"{d.Domain} (DKIM)",
                    d.Selector1.Length > 0 ? Texts.T.Format("advice.dkim.off-cnames", d.Domain, d.Selector1, d.Selector2) : Texts.T["advice.dkim.off"], CheckStatus.Attention)
                {
                    Fix = Dkim,
                    FixData = Data(("domein", d.Domain), ("selector1", d.Selector1), ("selector2", d.Selector2)),
                });
            }
            else
            {
                rows.Add(new CheckRow($"{d.Domain} (DKIM)", Texts.T["advice.dkim.on"], CheckStatus.Ok));
            }
        }

        var shown = rows.Count(x => x.Status != CheckStatus.Ok);
        return new SecurityCheck("maildomeinen", title, rows.Select(x => x.Status).DefaultIfEmpty(CheckStatus.Ok).Max(),
            shown == 0 ? Texts.T["advice.domains.none"] : Texts.T.Format("code.security-checks.point-s-to-check", shown), rows);
    }

    /// <summary>A tag of a DMARC record (p=, sp=, rua=), in lower case; null when it is not there.</summary>
    public static string? Tag(string record, string name)
        => record.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && string.Equals(p[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim().ToLowerInvariant()).FirstOrDefault();

    /// <summary>The plan for one line with a fix of this class (null when the fix is not one of them).</summary>
    public static PlanOutcome? Plan(CheckRow row)
    {
        if (row.Fix is not { } fix || !Fixes.Contains(fix))
        {
            return null;
        }

        var data = row.FixData ?? new Dictionary<string, string>();
        string P(string k) => data.TryGetValue(k, out var v) ? v : string.Empty;
        switch (fix)
        {
            case SmtpAuth:
            {
                // Guided (U16): who sent with SMTP AUTH in 30 days keeps it, with an exception per mailbox, until moved
                if (P("gelezen") != "True")
                {
                    return PlanOutcome.Error(Texts.T["advice.smtp.usage-unknown"]);
                }

                var users = P("gebruikers").Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (users.Length > 0 && P("verborgen") == "True")
                {
                    return PlanOutcome.Error(Texts.T.Format("advice.smtp.concealed", users.Length));
                }

                var already = P("uitzonderingen").Split(';', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var plan = new List<Change>();
                Guid? previous = null;
                foreach (var u in users.Where(u => !already.Contains(u)))
                {
                    var exception = Setting("SmtpAuthPostvak", u, "True", string.Empty, Texts.T.Format("advice.smtp.keep", u), null, previous);
                    plan.Add(exception);
                    previous = exception.Id;
                }

                plan.Add(Setting("SmtpAuth", string.Empty, "False", "True", Texts.T["advice.smtp.plan"],
                    users.Length == 0 ? Texts.T["advice.smtp.plan-warning"] : Texts.T.Format("advice.smtp.plan-warning-kept", users.Length, string.Join(", ", users.Take(10))), previous));
                return PlanOutcome.Of(plan);
            }

            case Forwarding:
                return PlanOutcome.Of(new[] { Setting("Doorsturen", P("identity"), "Off", P("was"), Texts.T.Format("advice.forwarding.plan", P("identity")), Texts.T["advice.forwarding.plan-warning"]) });
            case ExternalTag:
                return PlanOutcome.Of(new[] { Setting("ExternLabel", string.Empty, "True", "False", Texts.T["advice.external.plan"], null) });
            case Audit:
                return PlanOutcome.Of(new[] { Setting("Auditlog", string.Empty, "True", "False", Texts.T["advice.audit.plan"], Texts.T["advice.audit.plan-warning"]) });
            case Preset:
                return PlanOutcome.Of(new[] { Setting("Preset", P("identity"), "True", "False", Texts.T["advice.preset.plan"], Texts.T["advice.preset.plan-warning"]) });
            case Dkim:
                return PlanOutcome.Of(new[]
                {
                    Setting("Dkim", P("domein"), "True", "False", Texts.T.Format("advice.dkim.plan", P("domein")),
                        P("selector1").Length > 0 ? Texts.T.Format("advice.dkim.plan-warning", P("domein"), P("selector1"), P("selector2")) : Texts.T["advice.dkim.plan-warning-new"]),
                });
            default:
                return null;
        }
    }

    private static Change Setting(string setting, string identity, string value, string before, string description, string? warning, Guid? after = null) => new()
    {
        TargetSystem = TargetSystem.Exchange,
        ChangeAction = ChangeAction.ExchangeOrganisationSetting,
        Target = identity.Length > 0 ? identity : setting,
        TargetName = description,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["instelling"] = setting, ["waarde"] = value, ["identity"] = identity },
        Previous = new(StringComparer.OrdinalIgnoreCase) { ["waarde"] = before },
        Description = description,
        Warning = warning,
        DependentOf = after,
    };

    /// <summary>The engine refuses a setting Exchange.ps1 does not know (the script refuses it too).</summary>
    public static string? Refusal(Change w)
        => w.ChangeAction == ChangeAction.ExchangeOrganisationSetting && !Settings.Contains(w.P("instelling")) ? Texts.T["advice.policy.not-allowed"] : null;
}
