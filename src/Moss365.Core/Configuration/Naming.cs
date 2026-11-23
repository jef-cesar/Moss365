using System.Text.Json.Serialization;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;

namespace Moss365.Core.Configuration;

/// <summary>How the part before the @ of a new account is made (R9).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalPartPattern
{
    /// <summary>an.peeters (the default)</summary>
    GivenDotSurname,
    /// <summary>anpeeters</summary>
    GivenSurname,
    /// <summary>a.peeters</summary>
    InitialDotSurname,
    /// <summary>apeeters</summary>
    InitialSurname,
    /// <summary>peeters.an</summary>
    SurnameDotGiven,
    /// <summary>peetersa</summary>
    SurnameInitial,
}

/// <summary>How the display name of a new account is made (R9).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DisplayNamePattern
{
    /// <summary>An Peeters (the default)</summary>
    GivenSurname,
    /// <summary>Peeters An</summary>
    SurnameGiven,
    /// <summary>PEETERS An</summary>
    SurnameUpperGiven,
    /// <summary>Peeters, An</summary>
    SurnameCommaGiven,
}

/// <summary>The naming of new accounts in this organisation (R9: per tenant, suggested from the data).</summary>
public sealed class NamingSettings
{
    public LocalPartPattern LocalPart { get; set; } = LocalPartPattern.GivenDotSurname;

    public DisplayNamePattern DisplayName { get; set; } = DisplayNamePattern.GivenSurname;

    public static string Example(LocalPartPattern p) => Local("An", "Peeters", p);

    public static string Example(DisplayNamePattern p) => Display("An", "Peeters", p);

    /// <summary>The part before the @ by the pattern (lower case, without accents and spaces).</summary>
    public static string Local(string givenName, string surname, LocalPartPattern pattern)
    {
        var g = NameRules.Normalise(givenName);
        var s = NameRules.Normalise(surname);
        if (g.Length == 0 || s.Length == 0)
        {
            return g.Length == 0 ? s : g;
        }

        var i = g[..1];
        return pattern switch
        {
            LocalPartPattern.GivenSurname => g + s,
            LocalPartPattern.InitialDotSurname => $"{i}.{s}",
            LocalPartPattern.InitialSurname => i + s,
            LocalPartPattern.SurnameDotGiven => $"{s}.{g}",
            LocalPartPattern.SurnameInitial => s + i,
            _ => $"{g}.{s}",
        };
    }

    public static string Display(string givenName, string surname, DisplayNamePattern pattern)
    {
        var g = givenName.Trim();
        var s = surname.Trim();
        if (g.Length == 0 || s.Length == 0)
        {
            return g.Length == 0 ? s : g;
        }

        return pattern switch
        {
            DisplayNamePattern.SurnameGiven => $"{s} {g}",
            DisplayNamePattern.SurnameUpperGiven => $"{s.ToUpperInvariant()} {g}",
            DisplayNamePattern.SurnameCommaGiven => $"{s}, {g}",
            _ => $"{g} {s}",
        };
    }

    /// <summary>
    /// The patterns most accounts follow (only accounts with a given name and a surname), with how many accounts match
    /// the winning pattern; null when there are too few accounts to say.
    /// </summary>
    public static (LocalPartPattern Local, int LocalCount, DisplayNamePattern Display, int DisplayCount, int Accounts)? Detect(InventoryData d)
    {
        var people = d.Persons.Where(p => !p.IsGuest && !d.Index.IsMailboxAccount(p))
            .Select(p => (Given: First(p.Ad?.GivenName, p.Entra?.GivenName), Surname: First(p.Ad?.Surname, p.Entra?.Surname),
                Upn: p.Upn, Display: First(p.Ad?.DisplayName, p.Entra?.DisplayName)))
            .Where(x => x.Given.Trim().Length > 0 && x.Surname.Trim().Length > 0 && x.Upn.Contains('@'))
            .ToList();
        if (people.Count < 3)
        {
            return null;
        }

        var local = Enum.GetValues<LocalPartPattern>()
            .Select(p => (Pattern: p, Count: people.Count(x => string.Equals(x.Upn.Split('@')[0].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'), Local(x.Given, x.Surname, p), StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Pattern).First();
        var display = Enum.GetValues<DisplayNamePattern>()
            .Select(p => (Pattern: p, Count: people.Count(x => string.Equals(x.Display.Trim(), Display(x.Given, x.Surname, p), StringComparison.Ordinal))))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Pattern).First();
        return (local.Pattern, local.Count, display.Pattern, display.Count, people.Count);
    }

    private static string First(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a : b ?? string.Empty;
}
