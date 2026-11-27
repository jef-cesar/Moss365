using Moss365.Core.Localization;
using Moss365.Core.Storage;

namespace Moss365.Core.Changes;

/// <summary>
/// The platforms the screens name with a label (G0, U30): a name and a fixed colour, at every section, plan step and line
/// of the change log, so that it is always clear which platform something is about. The colour is never the only sign:
/// the name is always there. No logos (the trademarks of Microsoft and Google).
/// </summary>
public enum Platform
{
    ActiveDirectory,
    EntraId,
    ExchangeOnline,
    ExchangeOnPremises,
    Intune,
    GoogleWorkspace,
    Gmail,
}

public static class Platforms
{
    public static IReadOnlyList<Platform> All { get; } = Enum.GetValues<Platform>();

    /// <summary>The full name, for a tooltip and a sentence.</summary>
    public static string Name(Platform p) => p switch
    {
        Platform.ActiveDirectory => "Active Directory",
        Platform.EntraId => "Entra ID",
        Platform.ExchangeOnline => "Exchange Online",
        Platform.ExchangeOnPremises => Texts.T["platform.exchange-on-premises"],
        Platform.Intune => "Intune",
        Platform.GoogleWorkspace => "Google Workspace",
        Platform.Gmail => "Gmail",
        _ => p.ToString(),
    };

    /// <summary>The name on the label itself: short where the full name is long.</summary>
    public static string Short(Platform p) => p switch
    {
        Platform.ActiveDirectory => "AD",
        Platform.ExchangeOnPremises => Texts.T["platform.exchange-on-premises.short"],
        _ => Name(p),
    };

    /// <summary>The fixed colour of a platform: white text is readable on each (contrast 4.5:1 or more).</summary>
    public static string Colour(Platform p) => p switch
    {
        Platform.ActiveDirectory => "#5b4b8a",
        Platform.EntraId => "#1565c0",
        Platform.ExchangeOnline => "#0e7490",
        Platform.ExchangeOnPremises => "#6b4f2a",
        Platform.Intune => "#a23b72",
        Platform.GoogleWorkspace => "#2e7d32",
        Platform.Gmail => "#b3261e",
        _ => "#5b5b5b",
    };

    /// <summary>The system of the tenant a platform belongs to.</summary>
    public static TenantSystems System(Platform p) => p switch
    {
        Platform.ActiveDirectory or Platform.ExchangeOnPremises => TenantSystems.ActiveDirectory,
        Platform.GoogleWorkspace or Platform.Gmail => TenantSystems.GoogleWorkspace,
        _ => TenantSystems.Microsoft365,
    };

    /// <summary>The platform of a change: by the first word of its action (Ad, Graph, Exchange, Intune).</summary>
    public static Platform Of(ChangeAction action)
    {
        var name = action.ToString();
        return name.StartsWith("Intune", StringComparison.Ordinal) ? Platform.Intune
            : name.StartsWith("Graph", StringComparison.Ordinal) ? Platform.EntraId
            : name.StartsWith("Exchange", StringComparison.Ordinal) ? Platform.ExchangeOnline
            : Platform.ActiveDirectory;
    }

    public static Platform Of(Change change) => Of(change.ChangeAction);

    /// <summary>
    /// The platform of a line of the change log, also of older lines (Dutch action names before 0.11); null for what
    /// Moss365 itself records (an accepted risk, a review, an update of the app).
    /// </summary>
    public static Platform? OfLog(string changeAction)
        => LegacyNames.TryParseEnum<ChangeAction>(changeAction, out var action) && Enum.IsDefined(action) ? Of(action) : null;

    /// <summary>The platform of a table of the snapshots (the CSV names of the inventory); null when it is not one.</summary>
    public static Platform? OfTable(string table)
    {
        var t = table.ToLowerInvariant();
        return t.StartsWith("ad-", StringComparison.Ordinal) || t.StartsWith("toestellen-ad", StringComparison.Ordinal) ? Platform.ActiveDirectory
            : t.StartsWith("entra-", StringComparison.Ordinal) || t.StartsWith("toestellen-entra", StringComparison.Ordinal) ? Platform.EntraId
            : t.StartsWith("exo-", StringComparison.Ordinal) ? Platform.ExchangeOnline
            : t.StartsWith("toestellen-intune", StringComparison.Ordinal) ? Platform.Intune
            : t.StartsWith("google-", StringComparison.Ordinal) ? Platform.GoogleWorkspace
            : t.StartsWith("gmail-", StringComparison.Ordinal) ? Platform.Gmail
            : null;
    }
}
