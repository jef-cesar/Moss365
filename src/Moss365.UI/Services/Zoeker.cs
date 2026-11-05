using Moss365.Core.Inventaris;

namespace Moss365.UI.Services;

public static class Zoeker
{
    /// <summary>Zoekt in naam, UPN, sAMAccountName, e-mail en dienst; alle woorden moeten voorkomen.</summary>
    public static IEnumerable<Persoon> Zoek(IEnumerable<Persoon> personen, string? tekst, bool metGasten)
    {
        var woorden = (tekst ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in personen)
        {
            if (!metGasten && p.IsGast)
            {
                continue;
            }

            if (woorden.Length == 0)
            {
                yield return p;
                continue;
            }

            var haystack = string.Join(' ', p.Naam, p.Upn, p.Ad?.SamAccountName, p.Ad?.EmailAddress, p.Entra?.Mail, p.Dienst);
            if (woorden.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                yield return p;
            }
        }
    }

    public static string Link(Persoon p) => "gebruiker/" + Uri.EscapeDataString(p.Sleutel);
}
