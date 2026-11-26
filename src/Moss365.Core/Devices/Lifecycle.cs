using System.Text.Json;
using System.Text.RegularExpressions;

namespace Moss365.Core.Devices;

public enum SupportStatus
{
    Unknown,
    Supported,
    EndingSoon,
    Ended,
}

/// <summary>Whether a system is still supported, and until when.</summary>
public sealed record SupportInfo(SupportStatus Status, DateOnly? End);

/// <summary>
/// The life cycle of systems (R17): the end of support per system and build comes from a data file that ships with
/// every update (<c>Devices/lifecycle.json</c>), instead of a fixed list in the code. AD gives the system with its
/// edition ("Windows 10 Enterprise", "10.0 (19045)"); Intune only "Windows" and the build, so a build that a long-term
/// edition (LTSC) also uses stays unknown there.
/// </summary>
public static partial class Lifecycle
{
    /// <summary>Support that ends within this many days is "ending soon".</summary>
    public const int SoonDays = 180;

    private sealed record Entry(string Match, string? Edition, string? Build, string? End);

    private sealed record LifecycleFile(string Updated, List<Entry> Systems);

    private static readonly Lazy<IReadOnlyList<Entry>> Entries = new(Load);

    [GeneratedRegex(@"(?<![\d])([12]\d{4})(?![\d])")]
    private static partial Regex BuildNumber();

    private static IReadOnlyList<Entry> Load()
    {
        using var stream = typeof(Lifecycle).Assembly.GetManifestResourceStream("Moss365.Lifecycle.json")
                           ?? throw new InvalidOperationException("Moss365.Lifecycle.json");
        return JsonSerializer.Deserialize<LifecycleFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Systems ?? new List<Entry>();
    }

    public static SupportInfo Of(string os, string version, DateTimeOffset now)
    {
        var text = $"{os} {version}";
        var build = BuildNumber().Match(text) is { Success: true } m ? m.Groups[1].Value : null;

        // Intune says only "Windows": the build tells Windows 10 from Windows 11
        var name = os.Trim();
        var editionKnown = !name.Equals("Windows", StringComparison.OrdinalIgnoreCase);
        if (!editionKnown && build is not null)
        {
            name = int.Parse(build, System.Globalization.CultureInfo.InvariantCulture) >= 22000 ? "Windows 11" : "Windows 10";
        }

        foreach (var e in Entries.Value)
        {
            if (!name.Contains(e.Match, StringComparison.OrdinalIgnoreCase) || (e.Build is not null && e.Build != build))
            {
                continue;
            }

            if (e.Edition is not null)
            {
                if (!editionKnown)
                {
                    // This build is also a long-term edition: without the edition it cannot be told
                    if (e.Build is not null)
                    {
                        return new SupportInfo(SupportStatus.Unknown, null);
                    }

                    continue;
                }

                if (!name.Contains(e.Edition, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (!DateOnly.TryParse(e.End, System.Globalization.CultureInfo.InvariantCulture, out var end))
            {
                return new SupportInfo(SupportStatus.Unknown, null);
            }

            var today = DateOnly.FromDateTime(now.LocalDateTime);
            var status = end < today ? SupportStatus.Ended : end <= today.AddDays(SoonDays) ? SupportStatus.EndingSoon : SupportStatus.Supported;
            return new SupportInfo(status, end);
        }

        return new SupportInfo(SupportStatus.Unknown, null);
    }
}
