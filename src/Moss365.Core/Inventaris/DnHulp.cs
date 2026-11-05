using System.Text.RegularExpressions;

namespace Moss365.Core.Inventaris;

/// <summary>Hulpfuncties voor distinguished names (zelfde logica als Get-OuPad/Get-ParentDN in Export-Inventaris.ps1).</summary>
public static partial class DnHulp
{
    [GeneratedRegex(@"(?<!\\),")]
    private static partial Regex Scheiding();

    /// <summary>"CN=Jan,OU=TD,OU=Personeel,DC=x,DC=be" naar "Personeel/TD/Jan".</summary>
    public static string OuPad(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        var namen = Scheiding().Split(dn)
            .Where(d => d.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || d.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(d => d[3..].Replace("\\,", ","))
            .Reverse();
        return string.Join('/', namen);
    }

    /// <summary>Pad van de container waarin het object staat: "CN=Jan,OU=TD,DC=x" naar "TD".</summary>
    public static string OuPadVanObject(string? dn) => OuPad(Ouder(dn));

    public static string Ouder(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        var delen = Scheiding().Split(dn, 2);
        return delen.Length < 2 ? string.Empty : delen[1];
    }

    /// <summary>De naam (eerste component) van een DN: "CN=gg_ICT,OU=GROEPEN,..." naar "gg_ICT".</summary>
    public static string Naam(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        var eerste = Scheiding().Split(dn, 2)[0];
        var is_ = eerste.IndexOf('=');
        return (is_ >= 0 ? eerste[(is_ + 1)..] : eerste).Replace("\\,", ",");
    }

    /// <summary>True als het OU-pad gelijk is aan, of onder, een van de opgegeven paden valt.</summary>
    public static bool ValtOnder(string ouPad, IEnumerable<string> paden)
        => paden.Any(p => string.Equals(ouPad, p, StringComparison.OrdinalIgnoreCase)
                          || ouPad.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
}
