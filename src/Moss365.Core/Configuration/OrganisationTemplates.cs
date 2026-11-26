namespace Moss365.Core.Configuration;

/// <summary>A starting point for the settings of an organisation (R9).</summary>
public sealed record OrganisationTemplate(string Key, string Name, string Description, Func<OrganisationConfig> Make);

/// <summary>
/// Templates to start a tenant from (R9): divisions, kinds of users, group prefixes and naming. Applying one only adds
/// what is missing; what is already filled in stays as it is.
/// </summary>
public static class OrganisationTemplates
{
    public static IReadOnlyList<OrganisationTemplate> All => new[]
    {
        new OrganisationTemplate("lokaal-bestuur", Localization.Texts.T["template.local-government"], Localization.Texts.T["template.local-government.help"], () => new OrganisationConfig
        {
            Divisions = new() { D("Burger en welzijn"), D("Vrije tijd"), D("Grondgebiedzaken"), D("Interne zaken") },
            Populations = new() { P("Medewerkers"), P("Vrijwilligers", noLicence: true) },
            GroupPrefixes = new() { "gg_", "AFD_", "DNST_", "SG-" },
            Naming = new NamingSettings { LocalPart = LocalPartPattern.GivenDotSurname, DisplayName = DisplayNamePattern.GivenSurname },
        }),
        new OrganisationTemplate("school", Localization.Texts.T["template.school"], Localization.Texts.T["template.school.help"], () => new OrganisationConfig
        {
            Divisions = new() { D("Directie"), D("Secretariaat"), D("Leerkrachten"), D("ICT") },
            Populations = new() { P("Personeel"), P("Leerlingen") },
            GroupPrefixes = new() { "GRP_", "KLAS_", "SG-" },
            Naming = new NamingSettings { LocalPart = LocalPartPattern.GivenDotSurname, DisplayName = DisplayNamePattern.GivenSurname },
        }),
        new OrganisationTemplate("kleine-onderneming", Localization.Texts.T["template.small-business"], Localization.Texts.T["template.small-business.help"], () => new OrganisationConfig
        {
            Divisions = new() { D("Directie"), D("Verkoop"), D("Administratie"), D("IT") },
            Populations = new() { P("Medewerkers") },
            GroupPrefixes = new() { "SG-", "DL-" },
            Naming = new NamingSettings { LocalPart = LocalPartPattern.GivenDotSurname, DisplayName = DisplayNamePattern.GivenSurname },
        }),
    };

    /// <summary>Adds what the template has and the settings miss; returns what was added (for the screen).</summary>
    public static IReadOnlyList<string> Apply(OrganisationConfig c, OrganisationTemplate template)
    {
        var t = template.Make();
        var added = new List<string>();
        foreach (var d in t.Divisions.Where(d => !c.Divisions.Any(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase))))
        {
            c.Divisions.Add(d);
            added.Add(Localization.Texts.T.Format("template.added.division", d.Name));
        }

        foreach (var p in t.Populations.Where(p => !c.Populations.Any(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase))))
        {
            c.Populations.Add(p);
            added.Add(Localization.Texts.T.Format("template.added.population", p.Name));
        }

        foreach (var g in t.GroupPrefixes.Where(g => !c.GroupPrefixes.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            c.GroupPrefixes.Add(g);
            added.Add("voorvoegsel " + g);
        }

        return added;
    }

    private static Division D(string name) => new() { Name = name };

    private static Population P(string name, bool noLicence = false) => new() { Name = name, NoLicence = noLicence };
}
