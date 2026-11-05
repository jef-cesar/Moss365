using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Opslag;

/// <summary>Eén back-up van Conditional Access (een bestand in de gegevensmap).</summary>
public sealed record CaBackupBestand(string Pad, DateTime Tijd, int Beleiden, int Locaties)
{
    public string Naam => Path.GetFileName(Pad);
}

public sealed record CaBackupInhoud(DateTime Tijd, IReadOnlyList<CaBeleid> Beleiden, IReadOnlyList<BenoemdeLocatie> Locaties);

/// <summary>
/// Back-ups van de Conditional Access-beleiden en benoemde locaties, zoals Graph ze gaf (JSON), in
/// <c>%LOCALAPPDATA%\Moss365\ca-backup</c>. Een nieuwe back-up enkel als er iets veranderde. Ze bevatten id's van
/// personen en groepen: lokaal houden, nooit in de repo.
/// </summary>
public static class CaBackup
{
    public const string MapNaam = "ca-backup";

    /// <summary>Zoveel back-ups blijven bewaard (de oudste gaan weg).</summary>
    public const int Bewaren = 365;

    private static readonly JsonSerializerOptions Opmaak = new() { WriteIndented = true };

    /// <summary>Bewaart een back-up als de beleiden of locaties anders zijn dan in de laatste. Geeft het pad, of null.</summary>
    public static string? Bewaar(CaGegevens ca, string map, DateTime tijd)
    {
        if (ca.Melding is not null || ca.Beleiden.Any(b => b.Json.Length == 0))
        {
            return null; // niet (volledig) gelezen uit Graph: geen back-up die later iets kwijt zou spelen
        }

        var inhoud = new JsonObject
        {
            ["beleiden"] = new JsonArray(ca.Beleiden.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => JsonNode.Parse(b.Json)).ToArray()),
            ["locaties"] = new JsonArray(ca.Locaties.Where(l => l.Json.Length > 0).OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => JsonNode.Parse(l.Json)).ToArray()),
        };
        var vergelijk = inhoud.ToJsonString();
        if (Lijst(map).FirstOrDefault() is { } laatste && Vergelijkbaar(laatste.Pad) == vergelijk)
        {
            return null;
        }

        Directory.CreateDirectory(map);
        inhoud["tijd"] = tijd.ToString("o", CultureInfo.InvariantCulture);
        var pad = Path.Combine(map, $"ca-{tijd:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(pad, inhoud.ToJsonString(Opmaak));
        foreach (var oud in Lijst(map).Skip(Bewaren))
        {
            File.Delete(oud.Pad);
        }

        return pad;
    }

    private static string Vergelijkbaar(string pad)
    {
        var node = JsonNode.Parse(File.ReadAllText(pad))!.AsObject();
        node.Remove("tijd");
        return new JsonObject { ["beleiden"] = node["beleiden"]?.DeepClone(), ["locaties"] = node["locaties"]?.DeepClone() }.ToJsonString();
    }

    /// <summary>De back-ups, nieuwste eerst.</summary>
    public static IReadOnlyList<CaBackupBestand> Lijst(string map)
    {
        if (!Directory.Exists(map))
        {
            return Array.Empty<CaBackupBestand>();
        }

        var uit = new List<CaBackupBestand>();
        foreach (var pad in Directory.GetFiles(map, "ca-*.json"))
        {
            try
            {
                var i = Lees(pad);
                uit.Add(new CaBackupBestand(pad, i.Tijd, i.Beleiden.Count, i.Locaties.Count));
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                // Een kapot bestand slaan we over
            }
        }

        return uit.OrderByDescending(b => b.Tijd).ThenByDescending(b => b.Pad, StringComparer.Ordinal).ToList();
    }

    public static CaBackupInhoud Lees(string pad)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(pad));
        var r = doc.RootElement;
        var tijd = r.TryGetProperty("tijd", out var t) ? DateTime.Parse(t.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : File.GetLastWriteTime(pad);
        var beleiden = r.TryGetProperty("beleiden", out var b) ? b.EnumerateArray().Select(CaJson.Lees).ToList() : new List<CaBeleid>();
        var locaties = r.TryGetProperty("locaties", out var l) ? l.EnumerateArray().Select(CaJson.LeesLocatie).ToList() : new List<BenoemdeLocatie>();
        return new CaBackupInhoud(tijd, beleiden, locaties);
    }
}
