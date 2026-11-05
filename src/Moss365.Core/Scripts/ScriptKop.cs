using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Moss365.Core.Scripts;

/// <summary>Soort van een variabele: bepaalt het veld in het formulier en het type in het param()-blok.</summary>
public enum VariabeleSoort
{
    Tekst,
    Getal,
    JaNee,
    Datum,
    Keuze,
    Gebruiker,
    Groep,
    Postvak,
    Room,
    Lijst,
    Bestand,
    Map,
}

/// <summary>Met welke diensten de opstart verbindt voor het script draait.</summary>
[Flags]
public enum ScriptVerbinding
{
    Geen = 0,
    Exchange = 1,
    Graph = 2,
    Ad = 4,
    Lokaal = 8,
}

/// <summary>Eén variabele van een script: wat het formulier vraagt en als parameter doorgeeft.</summary>
public sealed record ScriptVariabele(
    string Naam,
    VariabeleSoort Soort = VariabeleSoort.Tekst,
    bool Verplicht = false,
    string Vraag = "",
    string? Standaard = null,
    IReadOnlyList<string>? Opties = null)
{
    public string Label => Vraag.Length > 0 ? Vraag : Naam;

    /// <summary>Het PowerShell-type in het param()-blok.</summary>
    public string PsType => Soort switch
    {
        VariabeleSoort.Getal => "int",
        VariabeleSoort.JaNee => "switch",
        VariabeleSoort.Datum => "datetime",
        VariabeleSoort.Lijst => "string[]",
        _ => "string",
    };
}

/// <summary>
/// De kop van een script in de bibliotheek: een commentaarblok <c>&lt;#MOSS365 ... #&gt;</c> bovenaan, met naam,
/// omschrijving, verbindingen, of het iets wijzigt, en de variabelen. Het param()-blok volgt uit de variabelen.
/// </summary>
public sealed record ScriptKop
{
    public string Naam { get; init; } = string.Empty;
    public string Omschrijving { get; init; } = string.Empty;
    public ScriptVerbinding Verbinding { get; init; } = ScriptVerbinding.Lokaal;
    public bool Schrijft { get; init; }

    /// <summary>"7", "5.1" of leeg (dan PowerShell 7 als die er is).</summary>
    public string PowerShell { get; init; } = string.Empty;

    public int DuurMinuten { get; init; } = 30;
    public IReadOnlyList<ScriptVariabele> Variabelen { get; init; } = Array.Empty<ScriptVariabele>();

    /// <summary>De kop kwam uit een MOSS365-blok (anders afgeleid uit het param()-blok).</summary>
    public bool UitBlok { get; init; }
}

/// <summary>Leest en schrijft de kop en het param()-blok van een script. Geen PowerShell nodig.</summary>
public static class ScriptTekst
{
    private const string BlokStart = "<#MOSS365";

    public static ScriptKop Lees(string tekst, string bestandsnaam)
    {
        var naam = Path.GetFileNameWithoutExtension(bestandsnaam);
        var parameters = LeesParameters(tekst);
        var blok = ZoekBlok(tekst);
        if (blok is null)
        {
            // Geen kop: alles afleiden uit het param()-blok en de help bovenaan
            return new ScriptKop
            {
                Naam = naam,
                Omschrijving = Synopsis(tekst),
                Verbinding = RaadVerbinding(tekst),
                Schrijft = Regex.IsMatch(tekst, @"SupportsShouldProcess\s*=\s*\$true", RegexOptions.IgnoreCase),
                Variabelen = parameters.Select(p => new ScriptVariabele(p.Naam, p.Soort, false, p.Naam, Opties: p.Opties)).ToList(),
            };
        }

        var kop = new ScriptKop { Naam = naam, UitBlok = true };
        var variabelen = new List<ScriptVariabele>();
        foreach (var regel in tekst.Substring(blok.Value.Start + BlokStart.Length, blok.Value.Lengte - BlokStart.Length - 2).Split('\n'))
        {
            var r = regel.Trim();
            var dp = r.IndexOf(':');
            if (dp <= 0)
            {
                continue;
            }

            var sleutel = r[..dp].Trim().ToLowerInvariant();
            var waarde = r[(dp + 1)..].Trim();
            kop = sleutel switch
            {
                "naam" => kop with { Naam = waarde },
                "omschrijving" => kop with { Omschrijving = kop.Omschrijving.Length == 0 ? waarde : kop.Omschrijving + " " + waarde },
                "verbinding" => kop with { Verbinding = LeesVerbinding(waarde) },
                "schrijft" => kop with { Schrijft = waarde.StartsWith("j", StringComparison.OrdinalIgnoreCase) },
                "powershell" => kop with { PowerShell = waarde is "7" or "5.1" ? waarde : string.Empty },
                "duur" => kop with { DuurMinuten = int.TryParse(waarde, out var d) && d > 0 ? Math.Min(d, 24 * 60) : 30 },
                _ => kop,
            };
            if (sleutel == "variabele" && LeesVariabele(waarde) is { } v)
            {
                variabelen.Add(v);
            }
        }

        return kop with { Variabelen = variabelen };
    }

    /// <summary>
    /// Schrijft de kop en het param()-blok in het script: het MOSS365-blok wordt vervangen of bovenaan gezet, en het
    /// param()-blok volgt de variabelen. Een bestaande parameter met dezelfde naam blijft zoals hij was (met zijn
    /// attributen), enkel het type wordt aangepast.
    /// </summary>
    public static string Schrijf(ScriptKop kop, string tekst)
    {
        tekst = tekst.Replace("\r\n", "\n");
        var blokTekst = MaakBlok(kop);
        var blok = ZoekBlok(tekst);
        tekst = blok is { } b
            ? tekst[..b.Start] + blokTekst + tekst[(b.Start + b.Lengte)..]
            : blokTekst + "\n" + tekst.TrimStart('\n');

        var bestaand = ZoekParam(tekst);
        var paramTekst = MaakParam(kop, bestaand is { } p ? SplitsParameters(tekst.Substring(p.Binnen, p.BinnenLengte)) : new List<string>());
        if (bestaand is { } pb)
        {
            tekst = tekst[..pb.Start] + paramTekst + tekst[(pb.Start + pb.Lengte)..];
        }
        else
        {
            var na = ZoekBlok(tekst)!.Value;
            var invoeg = na.Start + na.Lengte;

            // Na de help bovenaan, als die er is (die moet het eerste in het script blijven)
            var help = Regex.Match(tekst[invoeg..], @"^\s*<#(?!MOSS365).*?#>", RegexOptions.Singleline);
            if (help.Success && help.Value.Contains(".SYNOPSIS", StringComparison.OrdinalIgnoreCase))
            {
                invoeg += help.Length;
            }

            tekst = tekst[..invoeg] + "\n" + paramTekst + "\n" + tekst[invoeg..].TrimStart('\n');
        }

        return tekst.Replace("\n", "\r\n");
    }

    public static string MaakBlok(ScriptKop kop)
    {
        var sb = new StringBuilder();
        sb.Append(BlokStart).Append('\n');
        sb.Append("Naam: ").Append(Eenregel(kop.Naam)).Append('\n');
        if (kop.Omschrijving.Length > 0)
        {
            sb.Append("Omschrijving: ").Append(Eenregel(kop.Omschrijving)).Append('\n');
        }

        sb.Append("Verbinding: ").Append(VerbindingTekst(kop.Verbinding)).Append('\n');
        sb.Append("Schrijft: ").Append(kop.Schrijft ? "ja" : "nee").Append('\n');
        if (kop.PowerShell.Length > 0)
        {
            sb.Append("PowerShell: ").Append(kop.PowerShell).Append('\n');
        }

        if (kop.DuurMinuten != 30)
        {
            sb.Append("Duur: ").Append(kop.DuurMinuten.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var v in kop.Variabelen)
        {
            sb.Append("Variabele: ").Append(v.Naam).Append(" | ").Append(v.Soort).Append(" | ").Append(v.Verplicht ? "verplicht" : "optioneel")
                .Append(" | ").Append(Eenregel(v.Vraag).Replace("|", "/", StringComparison.Ordinal))
                .Append(" | ").Append(Eenregel(v.Standaard ?? string.Empty).Replace("|", "/", StringComparison.Ordinal))
                .Append(" | ").Append(string.Join(';', v.Opties ?? Array.Empty<string>()));
            sb.Append('\n');
        }

        sb.Append("#>");
        return sb.ToString();
    }

    public static string VerbindingTekst(ScriptVerbinding v)
    {
        var delen = new[] { (ScriptVerbinding.Exchange, "Exchange"), (ScriptVerbinding.Graph, "Graph"), (ScriptVerbinding.Ad, "AD"), (ScriptVerbinding.Lokaal, "Lokaal") }
            .Where(x => v.HasFlag(x.Item1)).Select(x => x.Item2).ToList();
        return delen.Count == 0 ? "Lokaal" : string.Join(", ", delen);
    }

    public static ScriptVerbinding LeesVerbinding(string tekst)
    {
        var v = ScriptVerbinding.Geen;
        foreach (var deel in tekst.Split(new[] { ',', '+', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            v |= deel.ToLowerInvariant() switch
            {
                "exchange" => ScriptVerbinding.Exchange,
                "graph" => ScriptVerbinding.Graph,
                "ad" => ScriptVerbinding.Ad,
                "lokaal" => ScriptVerbinding.Lokaal,
                _ => ScriptVerbinding.Geen,
            };
        }

        return v == ScriptVerbinding.Geen ? ScriptVerbinding.Lokaal : v;
    }

    /// <summary>Waarschuwingen bij het opslaan: dingen die in de app niet werken of niet horen.</summary>
    public static IReadOnlyList<string> Waarschuwingen(ScriptKop kop, string tekst)
    {
        var uit = new List<string>();
        var code = ZonderCommentaar(tekst);
        if (Regex.IsMatch(code, @"\bRead-Host\b", RegexOptions.IgnoreCase) && !code.Contains("function Get-ScriptWaarde", StringComparison.OrdinalIgnoreCase))
        {
            uit.Add("Read-Host werkt niet in de app: gebruik een variabele.");
        }

        if (Regex.IsMatch(code, @"\bConnect-(ExchangeOnline|MgGraph)\b", RegexOptions.IgnoreCase))
        {
            uit.Add("Connect-ExchangeOnline of Connect-MgGraph is niet nodig: de app verbindt zelf (de opdracht wordt in de app overgeslagen).");
        }

        if (kop.Schrijft && !Regex.IsMatch(code, @"SupportsShouldProcess\s*=\s*\$true", RegexOptions.IgnoreCase))
        {
            uit.Add("Dit script wijzigt iets, maar ondersteunt -WhatIf niet: het voorbeeld toont dan niet wat er zou gebeuren.");
        }

        var adressen = Regex.Matches(code, @"[A-Za-z0-9._%+-]+\.[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}").Select(m => m.Value)
            .Where(a => !a.StartsWith("voornaam.naam@", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        if (adressen.Count > 0)
        {
            uit.Add("Vaste e-mailadressen in de code (" + string.Join(", ", adressen.Take(3)) + "): maak er liever een variabele van.");
        }

        var namen = LeesParameters(tekst).Select(p => p.Naam).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in kop.Variabelen.Where(v => !Regex.IsMatch(v.Naam, @"^[A-Za-z][A-Za-z0-9_]*$")))
        {
            uit.Add($"De naam '{v.Naam}' kan geen PowerShell-parameter zijn (enkel letters, cijfers en _).");
        }

        foreach (var dubbel in kop.Variabelen.GroupBy(v => v.Naam, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            uit.Add($"De variabele '{dubbel.Key}' staat er meer dan eens (PowerShell kent geen verschil tussen hoofdletters en kleine letters).");
        }

        return uit;
    }

    // ------------------------------------------------------------------ param()-blok

    /// <summary>De parameters uit het param()-blok bovenaan: naam, soort (uit het type) en opties (uit ValidateSet).</summary>
    public static IReadOnlyList<(string Naam, VariabeleSoort Soort, IReadOnlyList<string>? Opties)> LeesParameters(string tekst)
    {
        var p = ZoekParam(tekst);
        if (p is null)
        {
            return Array.Empty<(string, VariabeleSoort, IReadOnlyList<string>?)>();
        }

        var uit = new List<(string, VariabeleSoort, IReadOnlyList<string>?)>();
        foreach (var deel in SplitsParameters(tekst.Substring(p.Value.Binnen, p.Value.BinnenLengte)))
        {
            var naam = Regex.Match(deel, @"\$(\w+)\s*(=|$)");
            if (!naam.Success)
            {
                continue;
            }

            var type = Regex.Matches(deel, @"\[([A-Za-z][\w\.]*(\[\])?)\]").Select(m => m.Groups[1].Value.ToLowerInvariant())
                .LastOrDefault(t => t is not ("parameter" or "cmdletbinding") && !t.StartsWith("validate", StringComparison.Ordinal) && !t.StartsWith("alias", StringComparison.Ordinal)) ?? "object";
            var set = Regex.Match(deel, @"ValidateSet\(([^)]*)\)", RegexOptions.IgnoreCase);
            IReadOnlyList<string>? opties = set.Success
                ? Regex.Matches(set.Groups[1].Value, @"'([^']*)'|""([^""]*)""").Select(m => m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value).ToList()
                : null;
            var soort = opties is { Count: > 0 } ? VariabeleSoort.Keuze : type switch
            {
                "int" or "int32" or "int64" or "long" or "double" or "decimal" => VariabeleSoort.Getal,
                "switch" or "bool" or "boolean" => VariabeleSoort.JaNee,
                "datetime" => VariabeleSoort.Datum,
                "string[]" or "object[]" or "array" => VariabeleSoort.Lijst,
                _ => VariabeleSoort.Tekst,
            };
            uit.Add((naam.Groups[1].Value, soort, opties));
        }

        return uit;
    }

    private static string MaakParam(ScriptKop kop, List<string> bestaand)
    {
        var sb = new StringBuilder();
        sb.Append(kop.Schrijft ? "[CmdletBinding(SupportsShouldProcess = $true)]" : "[CmdletBinding()]").Append('\n');
        sb.Append("param(");
        var regels = new List<string>();
        foreach (var v in kop.Variabelen)
        {
            var oud = bestaand.FirstOrDefault(b => Regex.IsMatch(b, @"\$" + Regex.Escape(v.Naam) + @"\s*(=|$)", RegexOptions.IgnoreCase));
            string regel;
            if (oud is not null)
            {
                // Type aanpassen, de rest (attributen, standaardwaarde) laten
                var t = Regex.Matches(oud, @"\[([A-Za-z][\w\.]*(\[\])?)\]").LastOrDefault(m => !m.Groups[1].Value.StartsWith("Validate", StringComparison.OrdinalIgnoreCase)
                                                                                         && !m.Groups[1].Value.Equals("Parameter", StringComparison.OrdinalIgnoreCase));
                regel = t is null ? oud : oud[..t.Index] + "[" + v.PsType + "]" + oud[(t.Index + t.Length)..];
                if (v.Soort == VariabeleSoort.Keuze && v.Opties is { Count: > 0 })
                {
                    regel = Regex.Replace(regel, @"\[ValidateSet\([^)]*\)\]\s*", string.Empty, RegexOptions.IgnoreCase);
                    regel = Keuzeset(v) + regel.TrimStart();
                }
            }
            else
            {
                regel = (v.Soort == VariabeleSoort.Keuze && v.Opties is { Count: > 0 } ? Keuzeset(v) : string.Empty) + "[" + v.PsType + "]$" + v.Naam;
            }

            regels.Add("    " + regel.Trim().Replace("\n", "\n    ", StringComparison.Ordinal));
        }

        if (regels.Count > 0)
        {
            sb.Append('\n').Append(string.Join(",\n", regels)).Append('\n');
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string Keuzeset(ScriptVariabele v)
        => "[ValidateSet(" + string.Join(", ", v.Opties!.Select(o => "'" + o.Replace("'", "''", StringComparison.Ordinal) + "'")) + ")]";

    /// <summary>De parameters in een param()-blok, gesplitst op komma's buiten haakjes, accolades en tekst.</summary>
    private static List<string> SplitsParameters(string binnen)
    {
        var uit = new List<string>();
        var diepte = 0;
        var start = 0;
        for (var i = 0; i < binnen.Length; i++)
        {
            var c = binnen[i];
            if (c is '\'' or '"')
            {
                i = EindeTekst(binnen, i);
                continue;
            }

            if (c == '#')
            {
                var eind = binnen.IndexOf('\n', i);
                i = eind < 0 ? binnen.Length : eind;
                continue;
            }

            if (c is '(' or '[' or '{')
            {
                diepte++;
            }
            else if (c is ')' or ']' or '}')
            {
                diepte--;
            }
            else if (c == ',' && diepte == 0)
            {
                uit.Add(binnen[start..i].Trim());
                start = i + 1;
            }
        }

        var rest = binnen[start..].Trim();
        if (rest.Length > 0)
        {
            uit.Add(rest);
        }

        return uit.Where(d => d.Contains('$')).ToList();
    }

    /// <summary>
    /// Het param()-blok van het script zelf (met het CmdletBinding-attribuut ervoor), als het vooraan staat: enkel
    /// commentaar, lege regels, using-regels en attributen ervoor.
    /// </summary>
    private static (int Start, int Lengte, int Binnen, int BinnenLengte)? ZoekParam(string tekst)
    {
        var i = 0;
        int? attribuutStart = null;
        while (i < tekst.Length)
        {
            var c = tekst[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '<' && i + 1 < tekst.Length && tekst[i + 1] == '#')
            {
                var eind = tekst.IndexOf("#>", i + 2, StringComparison.Ordinal);
                if (eind < 0)
                {
                    return null;
                }

                i = eind + 2;
            }
            else if (c == '#')
            {
                var eind = tekst.IndexOf('\n', i);
                i = eind < 0 ? tekst.Length : eind + 1;
            }
            else if (c == '[')
            {
                attribuutStart ??= i;
                var eind = Sluitend(tekst, i, '[', ']');
                if (eind < 0)
                {
                    return null;
                }

                i = eind + 1;
            }
            else if (tekst.AsSpan(i).StartsWith("using ", StringComparison.OrdinalIgnoreCase) || tekst.AsSpan(i).StartsWith("#requires", StringComparison.OrdinalIgnoreCase))
            {
                var eind = tekst.IndexOf('\n', i);
                i = eind < 0 ? tekst.Length : eind + 1;
            }
            else
            {
                var m = Regex.Match(tekst[i..], @"^param\s*\(", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    return null;
                }

                var open = i + m.Length - 1;
                var sluit = Sluitend(tekst, open, '(', ')');
                if (sluit < 0)
                {
                    return null;
                }

                var start = attribuutStart ?? i;
                return (start, sluit + 1 - start, open + 1, sluit - open - 1);
            }
        }

        return null;
    }

    private static int Sluitend(string tekst, int open, char o, char s)
    {
        var diepte = 0;
        for (var i = open; i < tekst.Length; i++)
        {
            var c = tekst[i];
            if (c is '\'' or '"')
            {
                i = EindeTekst(tekst, i);
            }
            else if (c == '#' && (i == 0 || tekst[i - 1] != '<'))
            {
                var eind = tekst.IndexOf('\n', i);
                i = eind < 0 ? tekst.Length : eind;
            }
            else if (c == o)
            {
                diepte++;
            }
            else if (c == s && --diepte == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int EindeTekst(string tekst, int start)
    {
        var q = tekst[start];
        for (var i = start + 1; i < tekst.Length; i++)
        {
            if (tekst[i] == '`')
            {
                i++;
            }
            else if (tekst[i] == q)
            {
                if (i + 1 < tekst.Length && tekst[i + 1] == q)
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return tekst.Length - 1;
    }

    // ------------------------------------------------------------------ hulp

    private static (int Start, int Lengte)? ZoekBlok(string tekst)
    {
        var start = tekst.IndexOf(BlokStart, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var eind = tekst.IndexOf("#>", start, StringComparison.Ordinal);
        return eind < 0 ? null : (start, eind + 2 - start);
    }

    private static ScriptVariabele? LeesVariabele(string waarde)
    {
        var d = waarde.Split('|').Select(x => x.Trim()).ToArray();
        if (d.Length == 0 || d[0].Length == 0)
        {
            return null;
        }

        var soort = d.Length > 1 && Enum.TryParse<VariabeleSoort>(d[1], true, out var s) ? s : VariabeleSoort.Tekst;
        var verplicht = d.Length > 2 && d[2].StartsWith("verplicht", StringComparison.OrdinalIgnoreCase);
        var opties = d.Length > 5 && d[5].Length > 0 ? d[5].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        return new ScriptVariabele(d[0], soort, verplicht, d.Length > 3 ? d[3] : string.Empty, d.Length > 4 && d[4].Length > 0 ? d[4] : null, opties);
    }

    private static string Synopsis(string tekst)
    {
        var m = Regex.Match(tekst, @"\.SYNOPSIS\s*\r?\n(.*?)(\r?\n\s*\r?\n|\r?\n\s*\.[A-Z]|#>)", RegexOptions.Singleline);
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim() : string.Empty;
    }

    private static ScriptVerbinding RaadVerbinding(string tekst)
    {
        var v = ScriptVerbinding.Geen;
        if (Regex.IsMatch(tekst, @"\b(Get|Set|Add|Remove|New)-(Mailbox|DistributionGroup|Recipient|CalendarProcessing|Place|EXO|MailboxFolder)|Connect-ExchangeOnline", RegexOptions.IgnoreCase))
        {
            v |= ScriptVerbinding.Exchange;
        }

        if (Regex.IsMatch(tekst, @"\b(Connect-MgGraph|Invoke-MgGraphRequest|Get-Mg\w+|Invoke-MossGraph)", RegexOptions.IgnoreCase))
        {
            v |= ScriptVerbinding.Graph;
        }

        if (Regex.IsMatch(tekst, @"\b(Get|Set|New|Remove)-AD\w+", RegexOptions.IgnoreCase))
        {
            v |= ScriptVerbinding.Ad;
        }

        return v == ScriptVerbinding.Geen ? ScriptVerbinding.Lokaal : v;
    }

    private static string ZonderCommentaar(string tekst)
        => Regex.Replace(Regex.Replace(tekst, @"<#.*?#>", string.Empty, RegexOptions.Singleline), @"(?m)^\s*#.*$", string.Empty);

    private static string Eenregel(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
