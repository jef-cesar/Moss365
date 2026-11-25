using System.Text.RegularExpressions;

namespace Moss365.Tests.Localization;

/// <summary>Finds text written in the screens instead of taken from a dictionary (R15).</summary>
public static class TextScanner
{
    private static readonly Regex Code = new(@"@code\s*\{.*", RegexOptions.Singleline);
    private static readonly Regex Comment = new(@"@\*.*?\*@", RegexOptions.Singleline);
    private static readonly Regex Node = new(@">([^<>@{}]*[A-Za-zÀ-ÿ][^<>@{}]*)<");
    private static readonly Regex Attribute = new(@"\b(?:title|aria-label|placeholder|alt)=""([^""@]*[A-Za-zÀ-ÿ][^""@]*)""");

    /// <summary>Texts that are not words of the interface: product names and symbols.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Moss", "365", "The Moss Directory", "GitHub", "Ctrl+K", "AD", "Entra", "Moss365", "Have you tried turning the user off and on again?",
        "Moss365 - Have you tried turning the user off and on again?", "Tenant",
    };

    public static IReadOnlyList<string> Texts(string razor)
    {
        // Operators in C# expressions are not tags
        var s = Comment.Replace(Code.Replace(razor, string.Empty), string.Empty)
            .Replace("=>", "  ", StringComparison.Ordinal).Replace(">=", "  ", StringComparison.Ordinal).Replace("<=", "  ", StringComparison.Ordinal)
            .Replace(" > ", "   ", StringComparison.Ordinal).Replace(" < ", "   ", StringComparison.Ordinal);
        return Node.Matches(s).Select(m => m.Groups[1].Value).Concat(Attribute.Matches(s).Select(m => m.Groups[1].Value))
            .Select(t => Regex.Replace(t.Replace("&rarr;", string.Empty).Replace("&larr;", string.Empty), @"\s+", " ").Trim())
            .Where(t => t.Length > 1 && t.Any(char.IsLetter) && !Allowed.Contains(t))
            .ToList();
    }
}
