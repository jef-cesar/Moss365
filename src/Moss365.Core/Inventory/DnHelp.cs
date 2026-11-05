using System.Text.RegularExpressions;

namespace Moss365.Core.Inventory;

/// <summary>Helper functions for distinguished names (the same logic as Get-OuPad/Get-ParentDN in
/// Export-Inventaris.ps1).</summary>
public static partial class DnHelp
{
    [GeneratedRegex(@"(?<!\\),")]
    private static partial Regex Separation();

    /// <summary>"CN=Jan,OU=TD,OU=Personeel,DC=x,DC=be" to "Personeel/TD/Jan".</summary>
    public static string OuPath(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        var names = Separation().Split(dn)
            .Where(d => d.StartsWith("OU=", StringComparison.OrdinalIgnoreCase) || d.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(d => d[3..].Replace("\\,", ","))
            .Reverse();
        return string.Join('/', names);
    }

    /// <summary>Path of the container the object is in: "CN=Jan,OU=TD,DC=x" to "TD".</summary>
    public static string OuPathOfObject(string? dn) => OuPath(Parent(dn));

    public static string Parent(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        var share = Separation().Split(dn, 2);
        return share.Length < 2 ? string.Empty : share[1];
    }

    /// <summary>The name (first component) of a DN: "CN=gg_ICT,OU=GROEPEN,..." to "gg_ICT".</summary>
    public static string Name(string? dn)
    {
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        var first = Separation().Split(dn, 2)[0];
        var is_ = first.IndexOf('=');
        return (is_ >= 0 ? first[(is_ + 1)..] : first).Replace("\\,", ",");
    }

    /// <summary>True when the OU path is equal to, or falls under, one of the given paths.</summary>
    public static bool FallsUnder(string ouPath, IEnumerable<string> paths)
        => paths.Any(p => string.Equals(ouPath, p, StringComparison.OrdinalIgnoreCase)
                          || ouPath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
}
