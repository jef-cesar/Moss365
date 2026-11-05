using Moss365.Core.Storage;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Moss365.Core.Scripts;

/// <summary>Kind of a variable: decides the field in the form and the type in the param() block.</summary>
public enum VariableKind
{
    Text,
    Number,
    YesNo,
    Date,
    Choice,
    User,
    Group,
    Mailbox,
    Room,
    Items,
    File,
    Folder,
}

/// <summary>Which services the startup connects to before the script runs.</summary>
[Flags]
public enum ScriptConnection
{
    No = 0,
    Exchange = 1,
    Graph = 2,
    Ad = 4,
    Local = 8,
}

/// <summary>One variable of a script: what the form asks and passes on as a parameter.</summary>
public sealed record ScriptVariable(
    string Name,
    VariableKind Kind = VariableKind.Text,
    bool Required = false,
    string Question = "",
    string? Default = null,
    IReadOnlyList<string>? Options = null)
{
    public string Label => Question.Length > 0 ? Question : Name;

    /// <summary>The PowerShell type in the param() block.</summary>
    public string PsType => Kind switch
    {
        VariableKind.Number => "int",
        VariableKind.YesNo => "switch",
        VariableKind.Date => "datetime",
        VariableKind.Items => "string[]",
        _ => "string",
    };
}

/// <summary>
/// The header of a script in the library: a comment block <c>&lt;#MOSS365 ... #&gt;</c> at the top, with name,
/// description, connections, whether it changes something, and the variables. The param() block follows from the
/// variables.
/// </summary>
public sealed record ScriptHeader
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ScriptConnection Connection { get; init; } = ScriptConnection.Local;
    public bool Writes { get; init; }

    /// <summary>"7", "5.1" or empty (then PowerShell 7 if present).</summary>
    public string PowerShell { get; init; } = string.Empty;

    public int DurationMinutes { get; init; } = 30;
    public IReadOnlyList<ScriptVariable> Variables { get; init; } = Array.Empty<ScriptVariable>();

    /// <summary>The header came from a MOSS365 block (otherwise derived from the param() block).</summary>
    public bool FromBlock { get; init; }
}

/// <summary>Reads and writes the header and the param() block of a script. No PowerShell needed.</summary>
public static class ScriptText
{
    private const string BlockStart = "<#MOSS365";

    public static ScriptHeader Read(string text, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var parameters = ReadParameters(text);
        var block = SearchBlock(text);
        if (block is null)
        {
            // No header: derive everything from the param() block and the help at the top
            return new ScriptHeader
            {
                Name = name,
                Description = Synopsis(text),
                Connection = AdviseConnection(text),
                Writes = Regex.IsMatch(text, @"SupportsShouldProcess\s*=\s*\$true", RegexOptions.IgnoreCase),
                Variables = parameters.Select(p => new ScriptVariable(p.Name, p.Kind, false, p.Name, Options: p.Options)).ToList(),
            };
        }

        var header = new ScriptHeader { Name = name, FromBlock = true };
        var variables = new List<ScriptVariable>();
        foreach (var rule in text.Substring(block.Value.Start + BlockStart.Length, block.Value.Length - BlockStart.Length - 2).Split('\n'))
        {
            var r = rule.Trim();
            var dp = r.IndexOf(':');
            if (dp <= 0)
            {
                continue;
            }

            var key = r[..dp].Trim().ToLowerInvariant();
            var value = r[(dp + 1)..].Trim();
            header = key switch
            {
                "naam" => header with { Name = value },
                "omschrijving" => header with { Description = header.Description.Length == 0 ? value : header.Description + " " + value },
                "verbinding" => header with { Connection = ReadConnection(value) },
                "schrijft" => header with { Writes = value.StartsWith("j", StringComparison.OrdinalIgnoreCase) },
                "powershell" => header with { PowerShell = value is "7" or "5.1" ? value : string.Empty },
                "duur" => header with { DurationMinutes = int.TryParse(value, out var d) && d > 0 ? Math.Min(d, 24 * 60) : 30 },
                _ => header,
            };
            if (key == "variabele" && ReadVariable(value) is { } v)
            {
                variables.Add(v);
            }
        }

        return header with { Variables = variables };
    }

    /// <summary>
    /// Writes the header and the param() block into the script: the MOSS365 block is replaced or put at the top, and
    /// the param() block follows the variables. An existing parameter with the same name stays as it was (with its
    /// attributes), only the type is adjusted.
    /// </summary>
    public static string Write(ScriptHeader header, string text)
    {
        text = text.Replace("\r\n", "\n");
        var blockText = MakeBlock(header);
        var block = SearchBlock(text);
        text = block is { } b
            ? text[..b.Start] + blockText + text[(b.Start + b.Length)..]
            : blockText + "\n" + text.TrimStart('\n');

        var existing = SearchParam(text);
        var paramText = MakeParam(header, existing is { } p ? SplitsParameters(text.Substring(p.Within, p.WithinLength)) : new List<string>());
        if (existing is { } pb)
        {
            text = text[..pb.Start] + paramText + text[(pb.Start + pb.Length)..];
        }
        else
        {
            var after = SearchBlock(text)!.Value;
            var insert = after.Start + after.Length;

            // After the help at the top, if present (that must stay the first thing in the script)
            var help = Regex.Match(text[insert..], @"^\s*<#(?!MOSS365).*?#>", RegexOptions.Singleline);
            if (help.Success && help.Value.Contains(".SYNOPSIS", StringComparison.OrdinalIgnoreCase))
            {
                insert += help.Length;
            }

            text = text[..insert] + "\n" + paramText + "\n" + text[insert..].TrimStart('\n');
        }

        return text.Replace("\n", "\r\n");
    }

    public static string MakeBlock(ScriptHeader header)
    {
        var sb = new StringBuilder();
        sb.Append(BlockStart).Append('\n');
        sb.Append("Naam: ").Append(SingleLine(header.Name)).Append('\n');
        if (header.Description.Length > 0)
        {
            sb.Append("Omschrijving: ").Append(SingleLine(header.Description)).Append('\n');
        }

        sb.Append("Verbinding: ").Append(ConnectionText(header.Connection)).Append('\n');
        sb.Append("Schrijft: ").Append(header.Writes ? "ja" : "nee").Append('\n');
        if (header.PowerShell.Length > 0)
        {
            sb.Append("PowerShell: ").Append(header.PowerShell).Append('\n');
        }

        if (header.DurationMinutes != 30)
        {
            sb.Append("Duur: ").Append(header.DurationMinutes.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var v in header.Variables)
        {
            sb.Append("Variabele: ").Append(v.Name).Append(" | ").Append(v.Kind).Append(" | ").Append(v.Required ? "verplicht" : "optioneel")
                .Append(" | ").Append(SingleLine(v.Question).Replace("|", "/", StringComparison.Ordinal))
                .Append(" | ").Append(SingleLine(v.Default ?? string.Empty).Replace("|", "/", StringComparison.Ordinal))
                .Append(" | ").Append(string.Join(';', v.Options ?? Array.Empty<string>()));
            sb.Append('\n');
        }

        sb.Append("#>");
        return sb.ToString();
    }

    public static string ConnectionText(ScriptConnection v)
    {
        var share = new[] { (ScriptConnection.Exchange, "Exchange"), (ScriptConnection.Graph, "Graph"), (ScriptConnection.Ad, "AD"), (ScriptConnection.Local, "Lokaal") }
            .Where(x => v.HasFlag(x.Item1)).Select(x => x.Item2).ToList();
        return share.Count == 0 ? "Lokaal" : string.Join(", ", share);
    }

    public static ScriptConnection ReadConnection(string text)
    {
        var v = ScriptConnection.No;
        foreach (var part in text.Split(new[] { ',', '+', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            v |= part.ToLowerInvariant() switch
            {
                "exchange" => ScriptConnection.Exchange,
                "graph" => ScriptConnection.Graph,
                "ad" => ScriptConnection.Ad,
                "lokaal" => ScriptConnection.Local,
                _ => ScriptConnection.No,
            };
        }

        return v == ScriptConnection.No ? ScriptConnection.Local : v;
    }

    /// <summary>Warnings when saving: things that do not work or do not belong in the app.</summary>
    public static IReadOnlyList<string> Warnings(ScriptHeader header, string text)
    {
        var output = new List<string>();
        var code = WithoutComment(text);
        if (Regex.IsMatch(code, @"\bRead-Host\b", RegexOptions.IgnoreCase) && !code.Contains("function Get-ScriptWaarde", StringComparison.OrdinalIgnoreCase))
        {
            output.Add("Read-Host werkt niet in de app: gebruik een variabele.");
        }

        if (Regex.IsMatch(code, @"\bConnect-(ExchangeOnline|MgGraph)\b", RegexOptions.IgnoreCase))
        {
            output.Add("Connect-ExchangeOnline of Connect-MgGraph is niet nodig: de app verbindt zelf (de opdracht wordt in de app overgeslagen).");
        }

        if (header.Writes && !Regex.IsMatch(code, @"SupportsShouldProcess\s*=\s*\$true", RegexOptions.IgnoreCase))
        {
            output.Add("Dit script wijzigt iets, maar ondersteunt -WhatIf niet: het voorbeeld toont dan niet wat er zou gebeuren.");
        }

        var addresses = Regex.Matches(code, @"[A-Za-z0-9._%+-]+\.[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}").Select(m => m.Value)
            .Where(a => !a.StartsWith("voornaam.naam@", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (addresses.Count > 0)
        {
            output.Add("Vaste e-mailadressen in de code (" + string.Join(", ", addresses.Take(3)) + "): maak er liever een variabele van.");
        }

        var names = ReadParameters(text).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in header.Variables.Where(v => !Regex.IsMatch(v.Name, @"^[A-Za-z][A-Za-z0-9_]*$")))
        {
            output.Add($"De naam '{v.Name}' kan geen PowerShell-parameter zijn (enkel letters, cijfers en _).");
        }

        foreach (var doubleValue in header.Variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            output.Add($"De variabele '{doubleValue.Key}' staat er meer dan eens (PowerShell kent geen verschil tussen hoofdletters en kleine letters).");
        }

        return output;
    }

    // ------------------------------------------------------------------ param() block

    /// <summary>The parameters from the param() block at the top: name, kind (from the type) and options (from
    /// ValidateSet).</summary>
    public static IReadOnlyList<(string Name, VariableKind Kind, IReadOnlyList<string>? Options)> ReadParameters(string text)
    {
        var p = SearchParam(text);
        if (p is null)
        {
            return Array.Empty<(string, VariableKind, IReadOnlyList<string>?)>();
        }

        var output = new List<(string, VariableKind, IReadOnlyList<string>?)>();
        foreach (var part in SplitsParameters(text.Substring(p.Value.Within, p.Value.WithinLength)))
        {
            var name = Regex.Match(part, @"\$(\w+)\s*(=|$)");
            if (!name.Success)
            {
                continue;
            }

            var type = Regex.Matches(part, @"\[([A-Za-z][\w\.]*(\[\])?)\]").Select(m => m.Groups[1].Value.ToLowerInvariant())
                .LastOrDefault(t => t is not ("parameter" or "cmdletbinding") && !t.StartsWith("validate", StringComparison.Ordinal) && !t.StartsWith("alias", StringComparison.Ordinal)) ?? "object";
            var set = Regex.Match(part, @"ValidateSet\(([^)]*)\)", RegexOptions.IgnoreCase);
            IReadOnlyList<string>? options = set.Success
                ? Regex.Matches(set.Groups[1].Value, @"'([^']*)'|""([^""]*)""").Select(m => m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value).ToList()
                : null;
            var kind = options is { Count: > 0 } ? VariableKind.Choice : type switch
            {
                "int" or "int32" or "int64" or "long" or "double" or "decimal" => VariableKind.Number,
                "switch" or "bool" or "boolean" => VariableKind.YesNo,
                "datetime" => VariableKind.Date,
                "string[]" or "object[]" or "array" => VariableKind.Items,
                _ => VariableKind.Text,
            };
            output.Add((name.Groups[1].Value, kind, options));
        }

        return output;
    }

    private static string MakeParam(ScriptHeader header, List<string> existing)
    {
        var sb = new StringBuilder();
        sb.Append(header.Writes ? "[CmdletBinding(SupportsShouldProcess = $true)]" : "[CmdletBinding()]").Append('\n');
        sb.Append("param(");
        var rules = new List<string>();
        foreach (var v in header.Variables)
        {
            var old = existing.FirstOrDefault(b => Regex.IsMatch(b, @"\$" + Regex.Escape(v.Name) + @"\s*(=|$)", RegexOptions.IgnoreCase));
            string rule;
            if (old is not null)
            {
                // Adjust the type, leave the rest (attributes, default value)
                var t = Regex.Matches(old, @"\[([A-Za-z][\w\.]*(\[\])?)\]").LastOrDefault(m => !m.Groups[1].Value.StartsWith("Validate", StringComparison.OrdinalIgnoreCase)
                                                                                         && !m.Groups[1].Value.Equals("Parameter", StringComparison.OrdinalIgnoreCase));
                rule = t is null ? old : old[..t.Index] + "[" + v.PsType + "]" + old[(t.Index + t.Length)..];
                if (v.Kind == VariableKind.Choice && v.Options is { Count: > 0 })
                {
                    rule = Regex.Replace(rule, @"\[ValidateSet\([^)]*\)\]\s*", string.Empty, RegexOptions.IgnoreCase);
                    rule = ChoiceSet(v) + rule.TrimStart();
                }
            }
            else
            {
                rule = (v.Kind == VariableKind.Choice && v.Options is { Count: > 0 } ? ChoiceSet(v) : string.Empty) + "[" + v.PsType + "]$" + v.Name;
            }

            rules.Add("    " + rule.Trim().Replace("\n", "\n    ", StringComparison.Ordinal));
        }

        if (rules.Count > 0)
        {
            sb.Append('\n').Append(string.Join(",\n", rules)).Append('\n');
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string ChoiceSet(ScriptVariable v)
        => "[ValidateSet(" + string.Join(", ", v.Options!.Select(o => "'" + o.Replace("'", "''", StringComparison.Ordinal) + "'")) + ")]";

    /// <summary>The parameters in a param() block, split on commas outside brackets, braces and text.</summary>
    private static List<string> SplitsParameters(string within)
    {
        var output = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < within.Length; i++)
        {
            var c = within[i];
            if (c is '\'' or '"')
            {
                i = EndText(within, i);
                continue;
            }

            if (c == '#')
            {
                var end = within.IndexOf('\n', i);
                i = end < 0 ? within.Length : end;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                output.Add(within[start..i].Trim());
                start = i + 1;
            }
        }

        var rest = within[start..].Trim();
        if (rest.Length > 0)
        {
            output.Add(rest);
        }

        return output.Where(d => d.Contains('$')).ToList();
    }

    /// <summary>
    /// The param() block of the script itself (with the CmdletBinding attribute before it), when it is at the start:
    /// only comments, empty lines, using lines and attributes before it.
    /// </summary>
    private static (int Start, int Length, int Within, int WithinLength)? SearchParam(string text)
    {
        var i = 0;
        int? attributeStart = null;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '<' && i + 1 < text.Length && text[i + 1] == '#')
            {
                var end = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }

                i = end + 2;
            }
            else if (c == '#')
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else if (c == '[')
            {
                attributeStart ??= i;
                var end = Closing(text, i, '[', ']');
                if (end < 0)
                {
                    return null;
                }

                i = end + 1;
            }
            else if (text.AsSpan(i).StartsWith("using ", StringComparison.OrdinalIgnoreCase) || text.AsSpan(i).StartsWith("#requires", StringComparison.OrdinalIgnoreCase))
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end + 1;
            }
            else
            {
                var m = Regex.Match(text[i..], @"^param\s*\(", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    return null;
                }

                var open = i + m.Length - 1;
                var close = Closing(text, open, '(', ')');
                if (close < 0)
                {
                    return null;
                }

                var start = attributeStart ?? i;
                return (start, close + 1 - start, open + 1, close - open - 1);
            }
        }

        return null;
    }

    private static int Closing(string text, int open, char o, char s)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\'' or '"')
            {
                i = EndText(text, i);
            }
            else if (c == '#' && (i == 0 || text[i - 1] != '<'))
            {
                var end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
            }
            else if (c == o)
            {
                depth++;
            }
            else if (c == s && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int EndText(string text, int start)
    {
        var q = text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                i++;
            }
            else if (text[i] == q)
            {
                if (i + 1 < text.Length && text[i + 1] == q)
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return text.Length - 1;
    }

    // ------------------------------------------------------------------ helpers

    private static (int Start, int Length)? SearchBlock(string text)
    {
        var start = text.IndexOf(BlockStart, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf("#>", start, StringComparison.Ordinal);
        return end < 0 ? null : (start, end + 2 - start);
    }

    private static ScriptVariable? ReadVariable(string value)
    {
        var d = value.Split('|').Select(x => x.Trim()).ToArray();
        if (d.Length == 0 || d[0].Length == 0)
        {
            return null;
        }

        var kind = d.Length > 1 && LegacyNames.TryParseEnum<VariableKind>(d[1], out var s) ? s : VariableKind.Text;
        var requiredValue = d.Length > 2 && d[2].StartsWith("verplicht", StringComparison.OrdinalIgnoreCase);
        var options = d.Length > 5 && d[5].Length > 0 ? d[5].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        return new ScriptVariable(d[0], kind, requiredValue, d.Length > 3 ? d[3] : string.Empty, d.Length > 4 && d[4].Length > 0 ? d[4] : null, options);
    }

    private static string Synopsis(string text)
    {
        var m = Regex.Match(text, @"\.SYNOPSIS\s*\r?\n(.*?)(\r?\n\s*\r?\n|\r?\n\s*\.[A-Z]|#>)", RegexOptions.Singleline);
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim() : string.Empty;
    }

    private static ScriptConnection AdviseConnection(string text)
    {
        var v = ScriptConnection.No;
        if (Regex.IsMatch(text, @"\b(Get|Set|Add|Remove|New)-(Mailbox|DistributionGroup|Recipient|CalendarProcessing|Place|EXO|MailboxFolder)|Connect-ExchangeOnline", RegexOptions.IgnoreCase))
        {
            v |= ScriptConnection.Exchange;
        }

        if (Regex.IsMatch(text, @"\b(Connect-MgGraph|Invoke-MgGraphRequest|Get-Mg\w+|Invoke-MossGraph)", RegexOptions.IgnoreCase))
        {
            v |= ScriptConnection.Graph;
        }

        if (Regex.IsMatch(text, @"\b(Get|Set|New|Remove)-AD\w+", RegexOptions.IgnoreCase))
        {
            v |= ScriptConnection.Ad;
        }

        return v == ScriptConnection.No ? ScriptConnection.Local : v;
    }

    private static string WithoutComment(string text)
        => Regex.Replace(Regex.Replace(text, @"<#.*?#>", string.Empty, RegexOptions.Singleline), @"(?m)^\s*#.*$", string.Empty);

    private static string SingleLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
