using System.Text.RegularExpressions;

namespace Moss365.Tests.Localization;

/// <summary>Finds text written in the screens instead of taken from a dictionary (R15).</summary>
public static class TextScanner
{
    private static readonly Regex Code = new(@"@code\s*\{.*", RegexOptions.Singleline);
    private static readonly Regex Comment = new(@"@\*.*?\*@", RegexOptions.Singleline);
    private static readonly Regex Node = new(@">([^<>@{}]*[A-Za-zÀ-ÿ][^<>@{}]*)<");
    private static readonly Regex Attribute = new(@"\b(?:title|aria-label|placeholder|alt|Label|Title|Context|Text|EmptyText|CountText|Description|Legend|WarningText|ColumnTitle|Hint)=""([^""@]*[A-Za-zÀ-ÿ][^""@]*)""");

    /// <summary>Code, file names and keys in the text are not translated.</summary>
    private static readonly Regex Literal = new(@"<(code|kbd)>.*?</\1>", RegexOptions.Singleline);

    /// <summary>C# between tags (a switch in the markup), not text.</summary>
    private static readonly Regex CSharp = new(@"\bbreak;|\bdefault:|\bcase\b[^:]*:|\bvar\s+\w+\s*=");

    /// <summary>Texts that are not words of the interface: product names and symbols.</summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Moss", "365", "The Moss Directory", "GitHub", "Ctrl+K", "AD", "Entra", "Moss365", "Have you tried turning the user off and on again?",
        "Moss365 - Have you tried turning the user off and on again?", "Tenant", "routeData",
    };

    private static readonly Regex Mixed = new(@">([^<>]*[@{}][^<>]*)<");
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
        var mixed = Operators(Literal.Replace(Comment.Replace(Code.Replace(razor, string.Empty), string.Empty), "<$1></$1>"));
        var words = Mixed.Matches(mixed).Select(m => m.Groups[1].Value)
            .SelectMany(t => Control.IsMatch(t) ? Pieces(System.Net.WebUtility.HtmlDecode(WithoutExpressions(WithoutHeaders(t)))) : new[] { System.Net.WebUtility.HtmlDecode(WithoutExpressions(t)) })
            .Select(t => Regex.Replace(t, @"\s+", " ").Trim())
            .Where(t => Regex.IsMatch(t, @"\p{L}{3,}") && !Allowed.Contains(t) && !CSharp.IsMatch(t) && !IsCode(t));
        return Plain(razor).Concat(words).Distinct().ToList();
    }

    /// <summary>Operators in C# expressions are not tags.</summary>
    private static string Operators(string s) => s
        .Replace("=>", "  ", StringComparison.Ordinal).Replace(">=", "  ", StringComparison.Ordinal).Replace("<=", "  ", StringComparison.Ordinal)
        .Replace(" > ", "   ", StringComparison.Ordinal).Replace(" < ", "   ", StringComparison.Ordinal);

    /// <summary>The text around Razor control flow (@if (...) { ... } else { ... }): the pieces between the code.</summary>
    private static readonly Regex Header = new(@"@?\b(?:if|foreach|for|while|switch|using|lock)\s*\(");

    /// <summary>Removes the conditions of Razor control flow (@if (...), else if (...), @foreach (...)).</summary>
    private static string WithoutHeaders(string text)
    {
        var s = text;
        for (var m = Header.Match(s); m.Success; m = Header.Match(s))
        {
            s = s[..m.Index] + "\n{" + s[Close(s, m.Index + m.Length - 1)..];
        }

        return s;
    }

    private static IEnumerable<string> Pieces(string text) => Regex.Split(text, @"[{};]|\belse\b|//[^\n]*")
        .Select(p => Regex.Replace(p, @"^\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)", string.Empty))
        .Where(p => !Regex.IsMatch(p, @"^\s*(break|continue|return)\b|[a-z]\.[A-Z]|\b[a-z]\w*\.[a-z]\w*\("));

    /// <summary>A piece that is C# rather than words (an assignment, a call, an operator).</summary>
    private static bool IsCode(string t) => Regex.IsMatch(t, @"^var\b|^\.\w|^[\w.]+\s*(=|\+\+|--|\+=|-=)|^\w+\([^)]*\)\s*$|==|!=|&&|\|\||\breturn\b|\bnew\b\s*\w*\(");

    private static IReadOnlyList<string> Plain(string razor)
    {
        // Operators in C# expressions are not tags
        var s = Operators(Literal.Replace(Comment.Replace(Code.Replace(razor, string.Empty), string.Empty), "<$1></$1>"));
        return Node.Matches(s).Select(m => m.Groups[1].Value).Concat(Attribute.Matches(s).Select(m => m.Groups[1].Value))
            .Select(t => Regex.Replace(System.Net.WebUtility.HtmlDecode(t), @"\s+", " ").Trim())
            .Where(t => t.Length > 1 && t.Any(char.IsLetter) && !Allowed.Contains(t) && !CSharp.IsMatch(t))
            .ToList();
    }
}
