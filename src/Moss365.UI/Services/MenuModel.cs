namespace Moss365.UI.Services;

/// <summary>One item of the menu: the address and the text.</summary>
public sealed record MenuItem(string Href, string Text);

/// <summary>A group of the menu.</summary>
public sealed record MenuGroup(string Name, IReadOnlyList<MenuItem> Items);

/// <summary>
/// The menu (0.21): grouped by subject, with one item for all settings. The settings have their own side bar on the
/// settings screen. Pages the profile does not allow, or that do not fit the systems of the tenant, are left out by
/// the layout.
/// </summary>
public static class MenuModel
{
    public const string SettingsHref = "instellingen";

    public static IReadOnlyList<MenuGroup> Main { get; } = new[]
    {
        new MenuGroup("Mensen", new MenuItem[]
        {
            new("gebruikers", "Gebruikers"), new("gasten", "Gastgebruikers"), new("nieuw", "Nieuwe gebruiker"), new("uitstroom", "Uitstroom"),
            new("functieprofielen", "Functieprofielen"), new("spiegel", "Spiegelgebruiker"), new("wachtwoorden", "Wachtwoord verloopt"),
            new("sleutels", "BitLocker en LAPS"), new("gastenverlenging", "Gasten verlengen"),
        }),
        new MenuGroup("Groepen en toegang", new MenuItem[]
        {
            new("groepen", "Groepen"), new("postvakken", "Gedeelde postvakken"), new("rooms", "Rooms"), new("samenwerking", "Teams en SharePoint"), new("logonscripts", "Logonscripts"),
        }),
        new MenuGroup("Beveiliging", new MenuItem[]
        {
            new("beveiliging", "Overzicht beveiliging"), new("controles", "Beveiligingscontroles"), new("conditional-access", "Conditional Access"),
        }),
        new MenuGroup("Inzicht", new MenuItem[]
        {
            new("statistieken", "Statistieken"), new("licenties", "Licenties en kosten"), new("toestellen", "Toestellen"), new("rapporten", "Rapporten"), new("geschiedenis", "Geschiedenis"), new("vergelijk", "AD tegenover Entra"), new("logboek", "Logboek"),
        }),
        new MenuGroup("Werk", new MenuItem[]
        {
            new("opkuis", "Opkuisronde"), new("aanvragen", "Aanvragen"), new("scripts", "Scripts"), new("gepland", "Gepland"), new("opvolging", "Opvolging"),
        }),
    };

    /// <summary>The side bar of the settings screen.</summary>
    public static IReadOnlyList<MenuGroup> Settings { get; } = new[]
    {
        new MenuGroup("Deze tenant", new MenuItem[]
        {
            new("organisatie", "Organisatie"), new("verbindingen", "Verbindingen"), new("inrichten", "Tenant inrichten"), new("databank", "Databank en versleuteling"),
        }),
        new MenuGroup("Alle tenants", new MenuItem[] { new("tenants", "Tenants"), new("tenantvergelijking", "Tenants vergelijken") }),
        new MenuGroup("Deze pc en de app", new MenuItem[] { new("controle", "Controle van deze pc"), new("updates", "Versie en updates") }),
    };

    /// <summary>The parts of the organisation settings (one page each).</summary>
    public static IReadOnlyList<MenuItem> OrganisationParts { get; } = new MenuItem[]
    {
        new("organisatie", "Bedrijf en naamgeving"), new("organisatie/diensten", "Diensten en afdelingen"), new("organisatie/kantoren", "Kantoren"),
        new("organisatie/groepen", "Nieuwe groepen"), new("organisatie/functieprofielen", "Functieprofielen"), new("organisatie/rollen", "Rollen in Moss365"),
        new("organisatie/noodaccounts", "Noodaccounts"),
    };

    /// <summary>Every page of the menu and the settings, for searching with Ctrl+K.</summary>
    public static IEnumerable<MenuItem> AllPages => new[] { new MenuItem(string.Empty, "Overzicht") }
        .Concat(Main.SelectMany(g => g.Items))
        .Concat(Settings.SelectMany(g => g.Items).Where(i => i.Href != "organisatie"))
        .Concat(OrganisationParts.Select(p => p with { Text = "Organisatie: " + p.Text }));

    /// <summary>The first part of an address (without query, anchor and details), as the menu knows it.</summary>
    public static string Section(string path)
    {
        var first = path.Split('?', '#')[0].Trim('/').Split('/')[0].ToLowerInvariant();
        return first switch
        {
            "gebruiker" => "gebruikers",
            "groep" => "groepen",
            "postvak" => "postvakken",
            "room" => "rooms",
            "ca-beleid" => "conditional-access",
            _ => first,
        };
    }

    public static bool IsSettings(string path) => Settings.SelectMany(g => g.Items).Any(i => i.Href == Section(path)) || Section(path) == SettingsHref;

    /// <summary>The group of the main menu that holds this page, or null.</summary>
    public static string? GroupOf(string path)
        => Main.FirstOrDefault(g => g.Items.Any(i => i.Href == Section(path)))?.Name;
}
