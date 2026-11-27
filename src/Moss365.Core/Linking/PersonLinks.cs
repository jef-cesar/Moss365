using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Linking;

/// <summary>How two accounts of one person were linked (G0, U32).</summary>
public enum LinkRule
{
    /// <summary>AD and Entra: the synchronisation of Entra Connect (the anchor), as before G0.</summary>
    Anchor,

    /// <summary>The primary address of the Google account is the address or the UPN of the person.</summary>
    Address,

    /// <summary>The same employee id.</summary>
    EmployeeId,

    /// <summary>An alias of the Google account is the address or the UPN of the person.</summary>
    Alias,

    /// <summary>Linked by hand: wins over the rules.</summary>
    ByHand,
}

/// <summary>A link or an undone link made by hand, kept per tenant (document <c>koppelingen</c>).</summary>
public sealed record ManualLink(string GoogleId, string PersonKey, bool Linked, string By, DateTimeOffset At);

public sealed record LinkSettings
{
    public IReadOnlyList<ManualLink> Links { get; init; } = Array.Empty<ManualLink>();
}

/// <summary>What makes a case doubtful.</summary>
public enum DoubtKind
{
    /// <summary>One account fits two others by the same rule: Moss365 links neither.</summary>
    TwoCandidates,

    /// <summary>A link by hand that a rule contradicts (the rule would link another person).</summary>
    ContradictsRule,

    /// <summary>An AD account that is not synchronised while a cloud account has its address: two accounts for one person?</summary>
    SameAddressNotLinked,

    /// <summary>An account without a counterpart on the other platforms.</summary>
    NoCounterpart,
}

/// <summary>A doubtful case, for the list on the page Personen koppelen.</summary>
public sealed record LinkDoubt(DoubtKind Kind, string Subject, IReadOnlyList<string> Keys, string Detail);

/// <summary>
/// Links the accounts of one person over AD, Entra ID and Google Workspace (G0, U32). AD and Entra stay linked by the
/// synchronisation (the source decides: a link by hand cannot overrule Entra Connect). A Google account is linked by
/// fixed rules in this order: the primary address or the UPN, then the employee id, then an alias; a link made by hand
/// wins, and so does a link undone by hand. What fits two ways is not linked but listed.
/// </summary>
public static class PersonLinks
{
    public const string Document = "koppelingen";

    /// <summary>The Google accounts attached to the persons (from AD and Entra), with the doubtful cases.</summary>
    public static (IReadOnlyList<Person> Persons, IReadOnlyList<LinkDoubt> Doubts) Attach(IReadOnlyList<Person> persons, IReadOnlyList<GoogleUser> google, LinkSettings settings)
    {
        var doubts = new List<LinkDoubt>();
        var byKey = persons.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var byAddress = Index(persons.Where(p => !p.IsGuest), Addresses);
        var byEmployee = Index(persons.Where(p => !p.IsGuest), p => new[] { p.Ad?.EmployeeId, p.Entra?.EmployeeId });

        // The latest choice by hand per Google account and person counts
        var manual = settings.Links.GroupBy(l => (l.GoogleId.ToLowerInvariant(), l.PersonKey.ToLowerInvariant())).Select(g => g.OrderBy(l => l.At).Last()).ToList();

        var chosen = new Dictionary<string, (GoogleUser Google, LinkRule Rule)>(StringComparer.OrdinalIgnoreCase);
        var unattached = new List<GoogleUser>();
        foreach (var g in google)
        {
            var undone = manual.Where(l => !l.Linked && Same(l.GoogleId, g.Id)).Select(l => l.PersonKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (ruleKey, rule, ambiguous) = ByRules(g, byAddress, byEmployee, undone);
            if (manual.FirstOrDefault(l => l.Linked && Same(l.GoogleId, g.Id) && byKey.ContainsKey(l.PersonKey)) is { } hand)
            {
                if (ruleKey is not null && !Same(ruleKey, hand.PersonKey))
                {
                    doubts.Add(new LinkDoubt(DoubtKind.ContradictsRule, g.PrimaryEmail, new[] { hand.PersonKey, ruleKey },
                        Texts.T.Format("links.doubt.contradicts", Rule(rule!.Value), byKey[ruleKey].Name)));
                }

                Choose(hand.PersonKey, g, LinkRule.ByHand);
                continue;
            }

            if (ambiguous.Count > 1)
            {
                doubts.Add(new LinkDoubt(DoubtKind.TwoCandidates, g.PrimaryEmail, ambiguous,
                    Texts.T.Format("links.doubt.two", Rule(rule!.Value), string.Join(", ", ambiguous.Select(k => byKey[k].Name)))));
                unattached.Add(g);
                continue;
            }

            if (ruleKey is null)
            {
                unattached.Add(g);
                continue;
            }

            Choose(ruleKey, g, rule!.Value);
        }

        void Choose(string personKey, GoogleUser g, LinkRule rule)
        {
            if (chosen.TryGetValue(personKey, out var other))
            {
                // One person, two Google accounts: by hand wins, else the stronger rule; the same rule twice is doubtful
                if (other.Rule == rule && rule != LinkRule.ByHand)
                {
                    doubts.Add(new LinkDoubt(DoubtKind.TwoCandidates, byKey[personKey].Name, new[] { personKey },
                        Texts.T.Format("links.doubt.two-google", other.Google.PrimaryEmail, g.PrimaryEmail)));
                    chosen.Remove(personKey);
                    unattached.Add(other.Google);
                    unattached.Add(g);
                    return;
                }

                if (Strength(rule) <= Strength(other.Rule))
                {
                    unattached.Add(g);
                    return;
                }

                unattached.Add(other.Google);
            }

            chosen[personKey] = (g, rule);
        }

        var result = persons.Select(p => chosen.TryGetValue(p.Key, out var c)
                ? new Person { Key = p.Key, Ad = p.Ad, Entra = p.Entra, Google = c.Google, GoogleLink = c.Rule }
                : p)
            .ToList();
        var attachedIds = chosen.Values.Select(c => c.Google.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var g in unattached.Where(g => !attachedIds.Contains(g.Id)).DistinctBy(g => g.Id, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new Person { Key = "google:" + g.Id.ToLowerInvariant(), Google = g });
            if (persons.Any(p => !p.IsGuest) && !doubts.Any(d => d.Subject == g.PrimaryEmail))
            {
                doubts.Add(new LinkDoubt(DoubtKind.NoCounterpart, g.PrimaryEmail, new[] { "google:" + g.Id.ToLowerInvariant() }, Texts.T["links.doubt.no-counterpart"]));
            }
        }

        return (result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), doubts);
    }

    /// <summary>
    /// Doubtful cases between AD and Entra, also without Google: an AD account that is not synchronised while a cloud
    /// account (not a guest) has its UPN or address. Two accounts for one person, or a synchronisation that broke.
    /// </summary>
    public static IReadOnlyList<LinkDoubt> AdEntraDoubts(IReadOnlyList<Person> persons)
    {
        var cloudOnly = Index(persons.Where(p => p.Ad is null && p.Entra is { IsGuest: false }), Addresses);
        var doubts = new List<LinkDoubt>();
        foreach (var p in persons.Where(p => p.Ad is not null && p.Entra is null))
        {
            var matches = Addresses(p).Where(a => a is not null).SelectMany(a => cloudOnly.TryGetValue(a!, out var keys) ? keys : new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (matches.Count > 0)
            {
                doubts.Add(new LinkDoubt(DoubtKind.SameAddressNotLinked, p.Name, new[] { p.Key }.Concat(matches).ToList(), Texts.T["links.doubt.same-address"]));
            }
        }

        return doubts;
    }

    /// <summary>The words for a rule.</summary>
    public static string Rule(LinkRule rule) => Texts.T["links.rule." + rule.ToString().ToLowerInvariant()];

    private static (string? Key, LinkRule? Rule, IReadOnlyList<string> Ambiguous) ByRules(GoogleUser g, Dictionary<string, List<string>> byAddress, Dictionary<string, List<string>> byEmployee, HashSet<string> undone)
    {
        var steps = new (LinkRule Rule, IEnumerable<string?> Values, Dictionary<string, List<string>> Index)[]
        {
            (LinkRule.Address, new[] { g.PrimaryEmail }, byAddress),
            (LinkRule.EmployeeId, new[] { g.EmployeeId }, byEmployee),
            (LinkRule.Alias, g.Aliases, byAddress),
        };
        foreach (var (rule, values, index) in steps)
        {
            var keys = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim().ToLowerInvariant())
                .SelectMany(v => index.TryGetValue(v, out var k) ? k : new List<string>())
                .Where(k => !undone.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (keys.Count == 1)
            {
                return (keys[0], rule, keys);
            }

            if (keys.Count > 1)
            {
                return (null, rule, keys);
            }
        }

        return (null, null, Array.Empty<string>());
    }

    private static int Strength(LinkRule rule) => rule switch
    {
        LinkRule.ByHand => 4,
        LinkRule.Address => 3,
        LinkRule.EmployeeId => 2,
        LinkRule.Alias => 1,
        _ => 0,
    };

    private static IEnumerable<string?> Addresses(Person p)
        => new[] { p.Entra?.Mail, p.Entra?.UserPrincipalName, p.Ad?.EmailAddress, p.Ad?.UserPrincipalName };

    private static Dictionary<string, List<string>> Index(IEnumerable<Person> persons, Func<Person, IEnumerable<string?>> values)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in persons)
        {
            foreach (var v in values(p).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim().ToLowerInvariant()).Distinct())
            {
                if (!index.TryGetValue(v, out var list))
                {
                    index[v] = list = new List<string>();
                }

                if (!list.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(p.Key);
                }
            }
        }

        return index;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
