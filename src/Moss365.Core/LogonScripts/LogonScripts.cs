using System.Text.RegularExpressions;
using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.LogonScripts;

/// <summary>A script in the NETLOGON share; Name is the path within the share (e.g. "logon.bat" or "dienst\x.bat").</summary>
public sealed record LogonScriptFile(string Name, long Size, DateTimeOffset Modified);

/// <summary>A logon or logoff script assigned through a Group Policy (scripts.ini in SYSVOL); shown, never changed.</summary>
public sealed record GpoScript(string PolicyId, string Kind, string Script, string Parameters);

/// <summary>The NETLOGON share of the domain (and the policies in SYSVOL). Writing goes through a plan (AD executor).</summary>
public interface ILogonScriptShare
{
    /// <summary>Where the scripts are, e.g. \\contoso.local\NETLOGON.</summary>
    string Location { get; }

    IReadOnlyList<LogonScriptFile> List();

    string Read(string name);

    IReadOnlyList<GpoScript> GpoScripts();
}

public sealed record DriveMapping(string Letter, string Path, string? Condition);

/// <summary>What a script does, as far as the app can tell: drives, printers, other scripts, programs.</summary>
public sealed record ScriptExplanation(IReadOnlyList<string> Lines, IReadOnlyList<DriveMapping> Drives, IReadOnlyList<string> Printers, IReadOnlyList<string> Calls);

/// <summary>A script and who has it in scriptPath; File is null for a reference to a script that does not exist.</summary>
public sealed record ScriptUse(string Name, LogonScriptFile? File, IReadOnlyList<Person> Users);

/// <summary>
/// Logon scripts (R9, 0.32): who has which script, references to scripts that do not exist, scripts nobody uses, what a
/// script does in plain language, and the difference between two versions.
/// </summary>
public static class LogonScriptAnalysis
{
    /// <summary>The script as a path within NETLOGON: "\\server\NETLOGON\x.bat" and "x.bat" are the same.</summary>
    public static string Normalise(string? scriptPath)
    {
        var p = (scriptPath ?? string.Empty).Trim().Trim('"').Replace('/', '\\');
        var i = p.IndexOf("\\netlogon\\", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            p = p[(i + "\\netlogon\\".Length)..];
        }

        return p.TrimStart('\\').ToLowerInvariant();
    }

    /// <summary>Every script with its users, and every scriptPath that points to a script that is not there.</summary>
    public static IReadOnlyList<ScriptUse> Uses(IReadOnlyList<LogonScriptFile> files, InventoryData d)
    {
        var byScript = d.Persons.Where(p => p.Ad is { ScriptPath.Length: > 0 })
            .GroupBy(p => Normalise(p.Ad!.ScriptPath))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Person>)g.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        var output = files.Select(f => new ScriptUse(f.Name, f, byScript.GetValueOrDefault(Normalise(f.Name)) ?? Array.Empty<Person>())).ToList();
        var known = files.Select(f => Normalise(f.Name)).ToHashSet();
        output.AddRange(byScript.Where(k => !known.Contains(k.Key)).Select(k => new ScriptUse(k.Value[0].Ad!.ScriptPath.Trim(), null, k.Value)));
        return output.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly Regex NetUse = new(@"net\s+use\s+([A-Za-z]:)\s+""?(\\\\[^""\s]+)""?", RegexOptions.IgnoreCase);
    private static readonly Regex NetUseDelete = new(@"net\s+use\s+([A-Za-z]:|\*)\s+/del", RegexOptions.IgnoreCase);
    private static readonly Regex VbsDrive = new(@"MapNetworkDrive\s*\(?\s*""([A-Za-z]:)""\s*,\s*""(\\\\[^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex PsDriveName = new(@"New-PSDrive\b.*?-Name\s+[""']?([A-Za-z])[""']?", RegexOptions.IgnoreCase);
    private static readonly Regex PsDriveRoot = new(@"-Root\s+[""']?(\\\\[^""'\s]+)", RegexOptions.IgnoreCase);
    private static readonly Regex PrinterUi = new(@"printui(?:\.dll)?,?\s*PrintUIEntry\b.*?/in\b.*?/n\s*""?(\\\\[^""\s]+)", RegexOptions.IgnoreCase);
    private static readonly Regex VbsPrinter = new(@"AddWindowsPrinterConnection\s*\(?\s*""(\\\\[^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex PsPrinter = new(@"Add-Printer\b.*?-ConnectionName\s+[""']?(\\\\[^""'\s]+)", RegexOptions.IgnoreCase);
    private static readonly Regex Call = new(@"^\s*(?:call\s+|cscript(?:\.exe)?\s+(?://\w+\s+)*|wscript(?:\.exe)?\s+|powershell(?:\.exe)?\s+.*?-File\s+)""?([^""\s]+\.(?:bat|cmd|vbs|ps1|kix))""?", RegexOptions.IgnoreCase);
    private static readonly Regex Start = new(@"^\s*start\s+(?:""[^""]*""\s+)?""?([^""\s]+\.exe)""?", RegexOptions.IgnoreCase);
    private static readonly Regex IfMember = new(@"(?:ifmember(?:\.exe)?\s+""?([^""\r\n]+?)""?\s*$)|(?:IsMember\s*\(\s*""([^""]+)""\s*\))", RegexOptions.IgnoreCase);

    public static ScriptExplanation Explain(string content)
    {
        var lines = new List<string>();
        var drives = new List<DriveMapping>();
        var printers = new List<string>();
        var calls = new List<string>();
        string? condition = null;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::", StringComparison.Ordinal)
                || line.StartsWith('\'') || line.StartsWith('#'))
            {
                continue;
            }

            if (IfMember.Match(line) is { Success: true } member)
            {
                condition = (member.Groups[1].Success ? member.Groups[1].Value : member.Groups[2].Value).Trim();
            }

            Match m;
            if ((m = NetUseDelete.Match(line)).Success)
            {
                lines.Add(m.Groups[1].Value == "*" ? "Verwijdert alle bestaande schijfkoppelingen" : $"Verwijdert de koppeling {m.Groups[1].Value.ToUpperInvariant()}");
            }
            else if ((m = NetUse.Match(line)).Success || (m = VbsDrive.Match(line)).Success)
            {
                Drive(m.Groups[1].Value, m.Groups[2].Value);
            }
            else if ((m = PsDriveName.Match(line)).Success && PsDriveRoot.Match(line) is { Success: true } root)
            {
                Drive(m.Groups[1].Value + ":", root.Groups[1].Value);
            }
            else if ((m = PrinterUi.Match(line)).Success || (m = VbsPrinter.Match(line)).Success || (m = PsPrinter.Match(line)).Success)
            {
                printers.Add(m.Groups[1].Value);
                lines.Add($"Voegt de printer {m.Groups[1].Value} toe" + When());
            }
            else if ((m = Call.Match(line)).Success)
            {
                calls.Add(m.Groups[1].Value);
                lines.Add($"Start het script {m.Groups[1].Value}" + When());
            }
            else if ((m = Start.Match(line)).Success)
            {
                lines.Add($"Start het programma {m.Groups[1].Value}" + When());
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("De app herkent geen schijven, printers of andere scripts in dit script.");
        }

        return new ScriptExplanation(lines, drives, printers, calls);

        string When() => condition is null ? string.Empty : $" (voor leden van {condition})";

        void Drive(string letter, string path)
        {
            var l = letter.ToUpperInvariant();
            drives.Add(new DriveMapping(l, path, condition));
            lines.Add($"Koppelt {l} aan {path}" + When());
        }
    }

    /// <summary>The difference between two versions, line by line: "+ " added, "- " removed, "  " the same.</summary>
    public static IReadOnlyList<string> Diff(string before, string after)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        var n = a.Length;
        var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var output = new List<string>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                output.Add("  " + a[x]);
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                output.Add("- " + a[x++]);
            }
            else
            {
                output.Add("+ " + b[y++]);
            }
        }

        output.AddRange(a.Skip(x).Select(l => "- " + l));
        output.AddRange(b.Skip(y).Select(l => "+ " + l));
        return output;
    }
}

/// <summary>Plans for logon scripts: saving a new version of a script, and the logon script of users.</summary>
public static class LogonScriptPlans
{
    public const string Content = "inhoud";

    /// <summary>A new version of a script (or a new script); the previous version goes into the change log for undo.</summary>
    public static PlanOutcome Save(string name, string? before, string after)
    {
        name = name.Trim().Replace('/', '\\').TrimStart('\\');
        if (name.Length == 0 || name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || name.Contains(':'))
        {
            return PlanOutcome.Error("Geef het script een geldige naam binnen NETLOGON, bv. logon.bat.");
        }

        if (!Regex.IsMatch(name, @"\.(bat|cmd|vbs|ps1|kix)$", RegexOptions.IgnoreCase))
        {
            return PlanOutcome.Error("Een logonscript eindigt op .bat, .cmd, .vbs, .ps1 of .kix.");
        }

        var normalised = after.Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (before is not null && before.Replace("\r\n", "\n") == after.Replace("\r\n", "\n"))
        {
            return PlanOutcome.Error(Localization.Texts.T["plan.nothing-changed"]);
        }

        var diff = before is null ? Array.Empty<string>() : LogonScriptAnalysis.Diff(before, after);
        var added = diff.Count(l => l.StartsWith("+ ", StringComparison.Ordinal));
        var removed = diff.Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdWriteLogonScript,
                Target = name,
                TargetName = name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { [Content] = normalised },
                Previous = before is null ? null : new(StringComparer.OrdinalIgnoreCase) { [Content] = before },
                Description = before is null ? $"Nieuw logonscript {name} in NETLOGON" : $"Logonscript {name} bewaren",
                CurrentValue = before is null ? "(bestaat niet)" : $"{before.Replace("\r\n", "\n").Split('\n').Length} regels",
                NewValue = before is null ? $"{normalised.Split("\r\n").Length} regels" : $"+{added} / -{removed} regels",
                Warning = "Het script geldt meteen voor iedereen die het heeft, bij de volgende aanmelding.",
            },
        });
    }

    /// <summary>The logon script of the selected users ("" = none); only accounts in AD have one.</summary>
    public static PlanOutcome Assign(InventoryData d, OrganisationConfig c, IEnumerable<Person> persons, string script)
    {
        var list = persons.ToList();
        var withoutAd = list.Where(p => p.Ad is null).Select(p => $"{p.Name} heeft geen AD-account (een logonscript bestaat enkel in AD).").ToList();
        var outcome = BulkPlans.Attribute(d, c, list.Where(p => p.Ad is not null), nameof(UserDesired.LogonScript), script.Trim());
        return PlanOutcome.Of(outcome.Plan, withoutAd.Concat(outcome.Errors));
    }
}
