using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Mail;

/// <summary>Where the mail of a person, a domain or a tenant lives (G0, U31).</summary>
public enum MailSystem
{
    Unknown,
    ExchangeOnline,
    ExchangeOnPremises,
    Gmail,
    Other,
    None,
}

/// <summary>One MX record of a domain: its preference (lower first) and the host.</summary>
public sealed record MxRecord(int Preference, string Host);

/// <summary>Reads the MX records of a domain through DNS (the system resolver of this PC).</summary>
public interface IMxLookup
{
    /// <summary>The MX records of a domain; empty when it has none. Throws when DNS cannot be asked.</summary>
    Task<IReadOnlyList<MxRecord>> MxAsync(string domain, CancellationToken cancel = default);
}

/// <summary>Where the mail of one domain really arrives, by its MX records, at the time of the check.</summary>
public sealed record MailRoute(string Domain, IReadOnlyList<string> Hosts, MailSystem System, string? Error = null);

/// <summary>
/// The document <c>postlocatie</c> of a tenant: the default of the tenant, a different system per person where needed
/// (co-existence, a migration), and the last MX check.
/// </summary>
public sealed record MailLocationSettings
{
    /// <summary>The default of the tenant; <see cref="MailSystem.Unknown"/> until it is chosen (then the proposal counts).</summary>
    public MailSystem Default { get; init; } = MailSystem.Unknown;

    /// <summary>A different system per person (the key of the person), chosen by hand.</summary>
    public IReadOnlyDictionary<string, MailSystem> Users { get; init; } = new Dictionary<string, MailSystem>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last MX check per domain.</summary>
    public IReadOnlyList<MailRoute> Routes { get; init; } = Array.Empty<MailRoute>();

    public DateTimeOffset? CheckedAt { get; init; }
}

/// <summary>Why a person's mail is where it is.</summary>
public enum MailLocationReason
{
    Default,
    Domain,
    ByHand,
}

public static class MailLocations
{
    public const string Document = "postlocatie";

    /// <summary>At most this many domains are checked (the ones with the most addresses).</summary>
    public const int MaxDomains = 20;

    /// <summary>The systems a tenant can choose: those of its systems, another system, or no mail at all.</summary>
    public static IReadOnlyList<MailSystem> Choices(TenantSystems systems)
    {
        var list = new List<MailSystem>();
        if (systems.HasMicrosoft365()) list.Add(MailSystem.ExchangeOnline);
        if (systems.HasAd()) list.Add(MailSystem.ExchangeOnPremises);
        if (systems.HasGoogle()) list.Add(MailSystem.Gmail);
        list.Add(MailSystem.Other);
        list.Add(MailSystem.None);
        return list;
    }

    /// <summary>The words for a system.</summary>
    public static string Name(MailSystem s) => s switch
    {
        MailSystem.ExchangeOnline => "Exchange Online",
        MailSystem.ExchangeOnPremises => Texts.T["platform.exchange-on-premises"],
        MailSystem.Gmail => "Gmail",
        MailSystem.Other => Texts.T["mail.other"],
        MailSystem.None => Texts.T["mail.none"],
        _ => Texts.T["mail.unknown"],
    };

    /// <summary>The platform label of a system, where it has one.</summary>
    public static Platform? PlatformOf(MailSystem s) => s switch
    {
        MailSystem.ExchangeOnline => Platform.ExchangeOnline,
        MailSystem.ExchangeOnPremises => Platform.ExchangeOnPremises,
        MailSystem.Gmail => Platform.Gmail,
        _ => null,
    };

    /// <summary>
    /// The system an MX host belongs to: Exchange Online (mail.protection.outlook.com), Gmail (the Google MX hosts), or
    /// another system (on-premises Exchange, a filter service or another provider; Moss365 cannot tell them apart).
    /// </summary>
    public static MailSystem Classify(IEnumerable<string> hosts)
    {
        var list = hosts.Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).Where(h => h.Length > 0).ToList();
        if (list.Count == 0)
        {
            return MailSystem.None;
        }

        // The preferred record decides (the list comes ordered by preference)
        var first = list[0];
        if (first.EndsWith(".mail.protection.outlook.com", StringComparison.Ordinal) || first.EndsWith(".mx.microsoft", StringComparison.Ordinal))
        {
            return MailSystem.ExchangeOnline;
        }

        if (first is "smtp.google.com" || first.EndsWith("aspmx.l.google.com", StringComparison.Ordinal) || first.EndsWith(".googlemail.com", StringComparison.Ordinal))
        {
            return MailSystem.Gmail;
        }

        return MailSystem.Other;
    }

    /// <summary>A domain name as DNS takes it: letters, digits, hyphens and dots, at most 253 characters.</summary>
    public static bool IsDomain(string domain)
        => domain.Length is > 2 and <= 253 && domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.')
           && domain.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') && !domain.Contains("..", StringComparison.Ordinal);

    /// <summary>The address of a person: the mail address of Entra, else of AD, else the UPN.</summary>
    public static string? AddressOf(Person p)
        => new[] { p.Entra?.Mail, p.Ad?.EmailAddress, p.Entra?.UserPrincipalName, p.Ad?.UserPrincipalName }
            .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && a.Contains('@'))?.Trim();

    public static string? DomainOf(string? address)
    {
        if (address is null)
        {
            return null;
        }

        var at = address.LastIndexOf('@');
        return at < 0 || at == address.Length - 1 ? null : address[(at + 1)..].Trim().ToLowerInvariant();
    }

    /// <summary>The domains of the tenant, with the most addresses first (and its own domain when known).</summary>
    public static IReadOnlyList<string> Domains(InventoryData data, string? tenantDomain)
    {
        var counted = data.Persons.Where(p => p.Entra?.IsGuest != true).Select(p => DomainOf(AddressOf(p))).Where(d => d is not null && IsDomain(d)).Select(d => d!)
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .Where(d => !d.EndsWith(".onmicrosoft.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tenantDomain is { } own && IsDomain(own.ToLowerInvariant()) && !counted.Contains(own.ToLowerInvariant()))
        {
            counted.Insert(0, own.ToLowerInvariant());
        }

        return counted.Take(MaxDomains).ToList();
    }

    /// <summary>
    /// The default proposed from what Moss365 sees: the system of the main domain (most addresses), or else from the
    /// systems of the tenant (Microsoft 365 first, then Google, then on-premises Exchange with AD).
    /// </summary>
    public static MailSystem ProposeDefault(IReadOnlyList<MailRoute> routes, TenantSystems systems)
    {
        var choices = Choices(systems);
        if (routes.FirstOrDefault(r => r.Error is null)?.System is { } main && main is not MailSystem.None and not MailSystem.Unknown
            && (choices.Contains(main) || main == MailSystem.Other))
        {
            return main;
        }

        return systems.HasMicrosoft365() ? MailSystem.ExchangeOnline : systems.HasGoogle() ? MailSystem.Gmail : MailSystem.ExchangeOnPremises;
    }

    /// <summary>The default that counts: the one chosen, else the proposal.</summary>
    public static MailSystem DefaultOf(MailLocationSettings settings, TenantSystems systems)
        => settings.Default != MailSystem.Unknown ? settings.Default : ProposeDefault(settings.Routes, systems);

    /// <summary>
    /// Where the mail of a person lives, and why: chosen by hand, else the system of the domain of the person's address
    /// when that domain arrives elsewhere than the default (a second domain in another system), else the default.
    /// </summary>
    public static (MailSystem System, MailLocationReason Reason) Of(Person p, MailLocationSettings settings, TenantSystems systems)
    {
        if (settings.Users.TryGetValue(p.Key, out var byHand) && byHand != MailSystem.Unknown)
        {
            return (byHand, MailLocationReason.ByHand);
        }

        var standard = DefaultOf(settings, systems);
        if (DomainOf(AddressOf(p)) is { } domain
            && settings.Routes.FirstOrDefault(r => r.Error is null && string.Equals(r.Domain, domain, StringComparison.OrdinalIgnoreCase)) is { } route
            && route.System is MailSystem.ExchangeOnline or MailSystem.Gmail && route.System != standard)
        {
            return (route.System, MailLocationReason.Domain);
        }

        return (standard, MailLocationReason.Default);
    }

    /// <summary>
    /// The domains whose mail arrives elsewhere than the default of the tenant: a warning on the page, unless it is
    /// meant (people of that domain chosen by hand in that system).
    /// </summary>
    public static IReadOnlyList<MailRoute> Differing(MailLocationSettings settings, TenantSystems systems)
    {
        var standard = DefaultOf(settings, systems);
        return settings.Routes.Where(r => r.Error is null && r.System is not MailSystem.Unknown && r.System != standard
                                          && !(standard == MailSystem.ExchangeOnPremises && r.System == MailSystem.Other)).ToList();
    }

    /// <summary>Checks the MX records of each domain; a domain that cannot be asked keeps its error, the rest goes on.</summary>
    public static async Task<IReadOnlyList<MailRoute>> CheckAsync(IMxLookup dns, IEnumerable<string> domains, CancellationToken cancel = default)
    {
        var routes = new List<MailRoute>();
        foreach (var domain in domains.Where(IsDomain))
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var records = await dns.MxAsync(domain, cancel);
                var hosts = records.OrderBy(r => r.Preference).ThenBy(r => r.Host, StringComparer.Ordinal).Select(r => r.Host.TrimEnd('.').ToLowerInvariant()).ToList();
                routes.Add(new MailRoute(domain, hosts, Classify(hosts)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                routes.Add(new MailRoute(domain, Array.Empty<string>(), MailSystem.Unknown, ex.Message));
            }
        }

        return routes;
    }
}
