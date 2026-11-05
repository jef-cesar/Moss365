using System.Text;

namespace Moss365.Core.Inventory;

/// <summary>
/// Reads the CSV files of Export-Inventaris.ps1: separator ';', fields between quotes, "" within a field, line endings
/// within a field allowed, UTF-8 with or without BOM.
/// </summary>
public static class CsvReader
{
    public static List<Dictionary<string, string>> ReadFile(string path, char separation = ';')
        => Read(File.ReadAllText(path, Encoding.UTF8), separation);

    public static List<Dictionary<string, string>> Read(string text, char separation = ';')
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var rows = ParseRows(text, separation);
        var output = new List<Dictionary<string, string>>();
        if (rows.Count == 0)
        {
            return output;
        }

        var header = rows[0];
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Count == 1 && row[0].Length == 0)
            {
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var k = 0; k < header.Count; k++)
            {
                values[header[k]] = k < row.Count ? row[k] : string.Empty;
            }

            output.Add(values);
        }

        return output;
    }

    private static List<List<string>> ParseRows(string text, char separation)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var withinQuote = false;
        var hasContent = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (withinQuote)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        withinQuote = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    withinQuote = true;
                    hasContent = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    rows.Add(row);
                    row = new List<string>();
                    field.Clear();
                    hasContent = false;
                    break;
                default:
                    if (c == separation)
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else
                    {
                        field.Append(c);
                    }

                    hasContent = true;
                    break;
            }
        }

        if (hasContent || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
