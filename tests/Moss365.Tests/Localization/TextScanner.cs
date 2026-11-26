using System.Text.RegularExpressions;

namespace Moss365.Tests.Localization;

/// <summary>Finds text written in the screens instead of taken from a dictionary (R15).</summary>
public static class TextScanner
{
    private static readonly Regex Code = new(@"@code\s*\{.*", RegexOptions.Singleline);
    private static readonly Regex Comment = new(@"@\*.*?\*@", RegexOptions.Singleline);
    private static readonly Regex Node = new(@">([^<>@{}]*[A-Za-zÀ-ÿ][^<>@{}]*)<");
    private static readonly Regex Attribute = new(@"\b(?:title|aria-label|placeholder|alt|Label|Title|Context|Text|EmptyText|CountText|Description)=""([^""@]*[A-Za-zÀ-ÿ][^""@]*)""");

    /// <summary>Code, file names and keys in the text are not translated.</summary>
    private static readonly Regex Literal = new(@"<(code|kbd)>.*?</\1>", RegexOptions.Singleline);

    /// <summary>C# between tags (a switch in the markup), not text.</summary>
    private static readonly Regex CSharp = new(@"\bbreak;|\bcase\b[^:]*:|\bvar\s+\w+\s*=");

    /// <summary>Texts that are not words of the interface: product names and symbols.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Moss", "365", "The Moss Directory", "GitHub", "Ctrl+K", "AD", "Entra", "Moss365", "Have you tried turning the user off and on again?",
        "Moss365 - Have you tried turning the user off and on again?", "Tenant", "routeData",
    };

    private static readonly Regex Mixed = new(@">([^<>]*@[^<>]*)<");
    private static readonly Regex Control = new(@"[{}]|@(if|foreach|for|while|switch|else|code)\b");

    /// <summary>Removes the Razor expressions (@x.y, @(...), @T["..."]) from a piece of text, keeping the words around them.</summary>
    public static string WithoutExpressions(string text)
    {
        var output = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '@' && i + 1 < text.Length && text[i + 1] == '@')
            {
                output.Append('@');
                i += 2;
                continue;
            }

            if (text[i] != '@')
            {
                output.Append(text[i++]);
                continue;
            }

            var j = i + 1;
            if (j < text.Length && text[j] == '(')
            {
                j = Close(text, j);
            }
            else
            {
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '.' or '?' or '!'))
                {
                    j++;
                }

                while (j < text.Length && text[j] is '[' or '(')
                {
                    j = Close(text, j);
                    while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] is '_' or '.' or '?' or '!'))
                    {
                        j++;
                    }
                }
            }

            i = j;
        }

        return output.ToString();
    }

    private static int Close(string text, int open)
    {
        var depth = 0;
        for (var j = open; j < text.Length; j++)
        {
            if (text[j] is '(' or '[')
            {
                depth++;
            }
            else if (text[j] is ')' or ']' && --depth == 0)
            {
                return j + 1;
            }
        }

        return text.Length;
    }

    public static IReadOnlyList<string> Texts(string razor)
    {
        var mixed = Comment.Replace(Code.Replace(razor, string.Empty), string.Empty);
        var words = Mixed.Matches(mixed).Select(m => m.Groups[1].Value)
            .Where(t => !Control.IsMatch(t))
            .Select(t => Regex.Replace(System.Net.WebUtility.HtmlDecode(WithoutExpressions(t)), @"\s+", " ").Trim())
            .Where(t => Regex.IsMatch(t, @"\p{L}{3,}") && !Allowed.Contains(t) && !CSharp.IsMatch(t));
        return Plain(razor).Concat(words).ToList();
    }

    private static IReadOnlyList<string> Plain(string razor)
    {
        // Operators in C# expressions are not tags
        var s = Literal.Replace(Comment.Replace(Code.Replace(razor, string.Empty), string.Empty), "<$1></$1>")
            .Replace("=>", "  ", StringComparison.Ordinal).Replace(">=", "  ", StringComparison.Ordinal).Replace("<=", "  ", StringComparison.Ordinal)
            .Replace(" > ", "   ", StringComparison.Ordinal).Replace(" < ", "   ", StringComparison.Ordinal);
        return Node.Matches(s).Select(m => m.Groups[1].Value).Concat(Attribute.Matches(s).Select(m => m.Groups[1].Value))
            .Select(t => Regex.Replace(System.Net.WebUtility.HtmlDecode(t), @"\s+", " ").Trim())
            .Where(t => t.Length > 1 && t.Any(char.IsLetter) && !Allowed.Contains(t) && !CSharp.IsMatch(t))
            .ToList();
    }
}
