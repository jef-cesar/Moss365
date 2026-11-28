using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Setup;

namespace Moss365.Core.Google;

/// <summary>
/// The key of the service account of the part "Gmail settings" (U34), as Google gives it in a JSON file. The private
/// key never leaves the app: it is kept encrypted in the tenant database and only used to sign a token; never shown or
/// logged.
/// </summary>
public sealed record GmailKey(string ClientEmail, string ClientId, string KeyId, string ProjectId, string PrivateKeyPem, string TokenUri)
{
    /// <summary>The only place Google issues tokens: an assertion is never sent elsewhere.</summary>
    public const string GoogleTokenUri = "https://oauth2.googleapis.com/token";

    public GmailKeyInfo Info => new(ClientEmail, ClientId, KeyId, ProjectId);

    /// <summary>The key from the JSON file of Google, or the reason it cannot be used.</summary>
    public static (GmailKey? Key, string? Error) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
            if (S("type") != "service_account")
            {
                return (null, Texts.T["gmail.key.not-service-account"]);
            }

            var key = new GmailKey(S("client_email"), S("client_id"), S("private_key_id"), S("project_id"), S("private_key"), S("token_uri") is { Length: > 0 } uri ? uri : GoogleTokenUri);
            if (key.ClientEmail.Length == 0 || key.ClientId.Length == 0 || !key.PrivateKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                return (null, Texts.T["gmail.key.incomplete"]);
            }

            return key.TokenUri != GoogleTokenUri ? (null, Texts.T["gmail.key.token-uri"]) : (key, null);
        }
        catch (JsonException)
        {
            return (null, Texts.T["gmail.key.not-json"]);
        }
    }
}

/// <summary>What may be shown of the key: who the service account is, never the private key.</summary>
public sealed record GmailKeyInfo(string ClientEmail, string ClientId, string KeyId, string ProjectId);

public sealed record GmailForwarding(bool Enabled, string Address, string Disposition);

/// <summary>The automatic reply of Gmail; start and end in milliseconds since 1970 (UTC), as the Gmail API gives them.</summary>
public sealed record GmailVacation(bool Enabled, string Subject, string Text, bool OnlyInternal, long? Start, long? End);

/// <summary>A filter of Gmail: its id, the criteria and the action as Gmail gives them (JSON), and in words.</summary>
public sealed record GmailFilter(string Id, string Json, string Summary, bool Forwards);

/// <summary>The Gmail settings of one user (G2, part "Gmail settings"), read when the page of the person asks; never
/// in a snapshot.</summary>
public sealed record GmailSettings(
    GmailForwarding Forwarding,
    GmailVacation Vacation,
    IReadOnlyList<string> Delegates,
    IReadOnlyList<GmailFilter> Filters,
    string Signature,
    IReadOnlyList<string> ForwardingAddresses);

/// <summary>The automatic reply as it has to become.</summary>
public sealed record GmailVacationDesired(bool On, string Subject, string Text, bool OnlyInternal, DateOnly? Start, DateOnly? End);

/// <summary>
/// Plans for the Gmail settings of a user (G2, part "Gmail settings", U34): forwarding, the automatic reply, delegates,
/// filters and the signature, each with what was before, so it can be undone (a filter that was removed comes back).
/// Every change goes through the service account of the tenant; the engine refuses it with the part off.
/// </summary>
public static class GmailPlans
{
    public static IReadOnlyList<string> Dispositions { get; } = new[] { "leaveInInbox", "markRead", "archive", "trash" };

    public static string DispositionName(string d) => Texts.T.Text("gmail.disposition." + d.ToLowerInvariant(), d);

    /// <summary>The words of a filter: what it looks at and what it does; a filter that forwards is marked.</summary>
    public static GmailFilter Filter(string id, JsonElement criteria, JsonElement action)
    {
        string S(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
        var when = new[] { ("from", S(criteria, "from")), ("to", S(criteria, "to")), ("subject", S(criteria, "subject")), ("query", S(criteria, "query")) }
            .Where(p => p.Item2.Length > 0).Select(p => Texts.T.Format("gmail.filter." + p.Item1, p.Item2));
        var labels = action.ValueKind == JsonValueKind.Object && action.TryGetProperty("addLabelIds", out var add) && add.ValueKind == JsonValueKind.Array
            ? add.EnumerateArray().Select(l => l.GetString() ?? string.Empty).ToList()
            : new List<string>();
        var forward = S(action, "forward");
        var does = new List<string>();
        if (forward.Length > 0) does.Add(Texts.T.Format("gmail.filter.forward", forward));
        if (labels.Contains("TRASH")) does.Add(Texts.T["gmail.filter.trash"]);
        if (action.ValueKind == JsonValueKind.Object && action.TryGetProperty("removeLabelIds", out var remove) && remove.ValueKind == JsonValueKind.Array
            && remove.EnumerateArray().Any(l => l.GetString() == "INBOX"))
        {
            does.Add(Texts.T["gmail.filter.skip-inbox"]);
        }

        if (labels.Any(l => l != "TRASH")) does.Add(Texts.T.Format("gmail.filter.label", labels.Count(l => l != "TRASH")));
        var json = JsonSerializer.Serialize(new Dictionary<string, JsonElement> { ["criteria"] = criteria.Clone(), ["action"] = action.Clone() });
        return new GmailFilter(id, json, $"{string.Join(", ", when)} → {(does.Count > 0 ? string.Join(", ", does) : Texts.T["gmail.filter.nothing"])}", forward.Length > 0);
    }

    public static PlanOutcome Forwarding(GoogleUser user, GmailSettings current, string address, string disposition, IReadOnlyList<string> domains)
    {
        var to = address.Trim().ToLowerInvariant();
        if (to.Length > 0 && (!MailAddress.TryCreate(to, out var mail) || !string.Equals(mail.Address, to, StringComparison.OrdinalIgnoreCase)))
        {
            return PlanOutcome.Error(Texts.T.Format("google.alias.invalid", address));
        }

        if (!Dispositions.Contains(disposition))
        {
            return PlanOutcome.Error(Texts.T.Format("attribute.invalid", Texts.T["gmail.forwarding.disposition"]));
        }

        if (string.Equals(to, user.PrimaryEmail, StringComparison.OrdinalIgnoreCase))
        {
            return PlanOutcome.Error(Texts.T["gmail.forwarding.self"]);
        }

        var before = current.Forwarding.Enabled ? current.Forwarding.Address : string.Empty;
        if (before == to && (to.Length == 0 || current.Forwarding.Disposition == disposition))
        {
            return PlanOutcome.Of(Array.Empty<Change>());
        }

        var outside = to.Length > 0 && !domains.Contains(to[(to.IndexOf('@') + 1)..]);
        return PlanOutcome.Of(new[]
        {
            Step(user, ChangeAction.GmailSetForwarding,
                to.Length == 0 ? Texts.T.Format("gmail.plan.forwarding-off", user.PrimaryEmail) : Texts.T.Format("gmail.plan.forwarding", user.PrimaryEmail, to),
                new() { ["adres"] = to, ["disposition"] = disposition },
                new() { ["adres"] = before, ["disposition"] = current.Forwarding.Disposition is { Length: > 0 } d ? d : "leaveInInbox" },
                outside ? Texts.T["gmail.forwarding.outside"] : to.Length > 0 && !current.ForwardingAddresses.Contains(to, StringComparer.OrdinalIgnoreCase) ? Texts.T["gmail.forwarding.new-address"] : null,
                before.Length > 0 ? before : Texts.T["gmail.off"], to.Length > 0 ? to : Texts.T["gmail.off"]),
        });
    }

    public static PlanOutcome Vacation(GoogleUser user, GmailSettings current, GmailVacationDesired desired)
    {
        if (desired.On && desired.Subject.Trim().Length == 0 && desired.Text.Trim().Length == 0)
        {
            return PlanOutcome.Error(Texts.T["gmail.vacation.empty"]);
        }

        if (desired.Start is { } s && desired.End is { } e && e < s)
        {
            return PlanOutcome.Error(Texts.T["gmail.vacation.end-before-start"]);
        }

        // The start of the first day and the end of the last day, in the time zone of this PC
        static string Ms(DateOnly? d, bool end) => d is { } day
            ? new DateTimeOffset(day.ToDateTime(end ? TimeOnly.MaxValue : TimeOnly.MinValue, DateTimeKind.Local)).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        static string Of(long? ms) => ms?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aan"] = desired.On.ToString(),
            ["onderwerp"] = desired.Subject.Trim(),
            ["tekst"] = desired.Text.Trim(),
            ["alleenIntern"] = desired.OnlyInternal.ToString(),
            ["start"] = desired.On ? Ms(desired.Start, false) : string.Empty,
            ["einde"] = desired.On ? Ms(desired.End, true) : string.Empty,
        };
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aan"] = current.Vacation.Enabled.ToString(),
            ["onderwerp"] = current.Vacation.Subject,
            ["tekst"] = current.Vacation.Text,
            ["alleenIntern"] = current.Vacation.OnlyInternal.ToString(),
            ["start"] = Of(current.Vacation.Start),
            ["einde"] = Of(current.Vacation.End),
        };
        if (parameters.All(p => previous[p.Key] == p.Value) || (!desired.On && !current.Vacation.Enabled))
        {
            return PlanOutcome.Of(Array.Empty<Change>());
        }

        return PlanOutcome.Of(new[]
        {
            Step(user, ChangeAction.GmailSetVacation,
                Texts.T.Format(desired.On ? "gmail.plan.vacation" : "gmail.plan.vacation-off", user.PrimaryEmail), parameters, previous, null,
                current.Vacation.Enabled ? Texts.T["gmail.on"] : Texts.T["gmail.off"], desired.On ? Texts.T["gmail.on"] : Texts.T["gmail.off"]),
        });
    }

    /// <summary>A delegate reads, sends and deletes mail for the user; Gmail only allows one of the organisation.</summary>
    public static PlanOutcome AddDelegate(GoogleUser user, GmailSettings current, string address, IReadOnlyList<string> domains)
    {
        var who = address.Trim().ToLowerInvariant();
        if (!MailAddress.TryCreate(who, out var mail) || !string.Equals(mail.Address, who, StringComparison.OrdinalIgnoreCase))
        {
            return PlanOutcome.Error(Texts.T.Format("google.alias.invalid", address));
        }

        if (!domains.Contains(who[(who.IndexOf('@') + 1)..]))
        {
            return PlanOutcome.Error(Texts.T["gmail.delegate.outside"]);
        }

        if (string.Equals(who, user.PrimaryEmail, StringComparison.OrdinalIgnoreCase) || current.Delegates.Contains(who, StringComparer.OrdinalIgnoreCase))
        {
            return PlanOutcome.Error(Texts.T.Format("gmail.delegate.already", who));
        }

        return PlanOutcome.Of(new[]
        {
            Step(user, ChangeAction.GmailAddDelegate, Texts.T.Format("gmail.plan.add-delegate", who, user.PrimaryEmail), new() { ["gemachtigde"] = who }, null,
                Texts.T["gmail.delegate.warning"]),
        });
    }

    public static Change RemoveDelegate(GoogleUser user, string address)
        => Step(user, ChangeAction.GmailRemoveDelegate, Texts.T.Format("gmail.plan.remove-delegate", address, user.PrimaryEmail), new() { ["gemachtigde"] = address }, null, null);

    public static PlanOutcome Signature(GoogleUser user, GmailSettings current, string html)
    {
        var text = html.Trim();
        if (text.Length > 10000)
        {
            return PlanOutcome.Error(Texts.T["gmail.signature.long"]);
        }

        return text == current.Signature.Trim()
            ? PlanOutcome.Of(Array.Empty<Change>())
            : PlanOutcome.Of(new[]
            {
                Step(user, ChangeAction.GmailSetSignature, Texts.T.Format("gmail.plan.signature", user.PrimaryEmail), new() { ["handtekening"] = text },
                    new() { ["handtekening"] = current.Signature }, null),
            });
    }

    /// <summary>A filter out; it is kept in the change log as it was, so that undo puts it back.</summary>
    public static Change RemoveFilter(GoogleUser user, GmailFilter filter)
        => Step(user, ChangeAction.GmailRemoveFilter, Texts.T.Format("gmail.plan.remove-filter", filter.Summary, user.PrimaryEmail), new() { ["id"] = filter.Id },
            new() { ["json"] = filter.Json }, filter.Forwards ? Texts.T["gmail.filter.forwarding-warning"] : null);

    /// <summary>The Gmail settings change through the part; null when it is on.</summary>
    public static string? PartOff(IReadOnlyCollection<string> parts) => parts.Contains(GoogleParts.GmailSettings) ? null : Texts.T["gmail.part-off"];

    private static Change Step(GoogleUser user, ChangeAction action, string description, Dictionary<string, string> parameters,
        Dictionary<string, string>? previous, string? warning, string? now = null, string? becomes = null) => new()
    {
        TargetSystem = TargetSystem.Google,
        ChangeAction = action,
        Target = user.PrimaryEmail,
        TargetName = user.Name.Length > 0 ? user.Name : user.PrimaryEmail,
        Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
        Previous = previous is null ? null : new Dictionary<string, string>(previous, StringComparer.OrdinalIgnoreCase),
        Description = description,
        Warning = warning,
        CurrentValue = now,
        NewValue = becomes,
    };
}
