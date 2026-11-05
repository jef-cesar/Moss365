using Moss365.Core.Inventory;

namespace Moss365.UI.Services;

public static class Searcher
{
    /// <summary>Searches in name, UPN, sAMAccountName, e-mail and department; all words must occur.</summary>
    public static IEnumerable<Person> Find(IEnumerable<Person> persons, string? text, bool withGuests)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in persons)
        {
            if (!withGuests && p.IsGuest)
            {
                continue;
            }

            if (words.Length == 0)
            {
                yield return p;
                continue;
            }

            var haystack = string.Join(' ', p.Name, p.Upn, p.Ad?.SamAccountName, p.Ad?.EmailAddress, p.Entra?.Mail, p.Department);
            if (words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                yield return p;
            }
        }
    }

    public static string Link(Person p) => "gebruiker/" + Uri.EscapeDataString(p.Key);
}
