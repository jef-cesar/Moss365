using System.Text;

namespace Moss365.Core.Inventaris;

/// <summary>
/// Leest de CSV-bestanden van Export-Inventaris.ps1: scheidingsteken ';', velden tussen aanhalingstekens,
/// "" binnen een veld, regeleinden binnen een veld toegestaan, UTF-8 met of zonder BOM.
/// </summary>
public static class CsvLezer
{
    public static List<Dictionary<string, string>> LeesBestand(string pad, char scheiding = ';')
        => Lees(File.ReadAllText(pad, Encoding.UTF8), scheiding);

    public static List<Dictionary<string, string>> Lees(string tekst, char scheiding = ';')
    {
        if (tekst.Length > 0 && tekst[0] == '﻿')
        {
            tekst = tekst[1..];
        }

        var rijen = ParseRijen(tekst, scheiding);
        var uit = new List<Dictionary<string, string>>();
        if (rijen.Count == 0)
        {
            return uit;
        }

        var kop = rijen[0];
        for (var i = 1; i < rijen.Count; i++)
        {
            var rij = rijen[i];
            if (rij.Count == 1 && rij[0].Length == 0)
            {
                continue;
            }

            var waarden = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var k = 0; k < kop.Count; k++)
            {
                waarden[kop[k]] = k < rij.Count ? rij[k] : string.Empty;
            }

            uit.Add(waarden);
        }

        return uit;
    }

    private static List<List<string>> ParseRijen(string tekst, char scheiding)
    {
        var rijen = new List<List<string>>();
        var rij = new List<string>();
        var veld = new StringBuilder();
        var binnenAanhaling = false;
        var heeftInhoud = false;

        for (var i = 0; i < tekst.Length; i++)
        {
            var c = tekst[i];
            if (binnenAanhaling)
            {
                if (c == '"')
                {
                    if (i + 1 < tekst.Length && tekst[i + 1] == '"')
                    {
                        veld.Append('"');
                        i++;
                    }
                    else
                    {
                        binnenAanhaling = false;
                    }
                }
                else
                {
                    veld.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    binnenAanhaling = true;
                    heeftInhoud = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    rij.Add(veld.ToString());
                    rijen.Add(rij);
                    rij = new List<string>();
                    veld.Clear();
                    heeftInhoud = false;
                    break;
                default:
                    if (c == scheiding)
                    {
                        rij.Add(veld.ToString());
                        veld.Clear();
                    }
                    else
                    {
                        veld.Append(c);
                    }

                    heeftInhoud = true;
                    break;
            }
        }

        if (heeftInhoud || veld.Length > 0 || rij.Count > 0)
        {
            rij.Add(veld.ToString());
            rijen.Add(rij);
        }

        return rijen;
    }
}
