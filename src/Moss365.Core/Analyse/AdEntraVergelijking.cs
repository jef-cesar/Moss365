using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Analyse;

public enum VerschilSoort
{
    OntbreektInEntra,
    StatusVerschilt,
    KenmerkVerschilt,
}

public sealed record Verschil(Persoon Persoon, VerschilSoort Soort, string Kenmerk, string AdWaarde, string EntraWaarde, string Toelichting)
{
    public string SoortTekst => Soort switch
    {
        VerschilSoort.OntbreektInEntra => "Ontbreekt in Entra",
        VerschilSoort.StatusVerschilt => "Status verschilt",
        _ => "Kenmerk verschilt",
    };
}

/// <summary>Vergelijkt on-prem AD met Entra ID voor de gesynchroniseerde populaties.</summary>
public static class AdEntraVergelijking
{
    /// <summary>De kenmerken die Entra Connect van AD naar Entra overneemt (AD-naam, Entra-naam).</summary>
    public static readonly IReadOnlyList<(string Kenmerk, Func<AdGebruiker, string> Ad, Func<EntraGebruiker, string> Entra)> Kenmerken = new[]
    {
        ("Weergavenaam", (Func<AdGebruiker, string>)(a => a.DisplayName), (Func<EntraGebruiker, string>)(e => e.DisplayName)),
        ("UPN", a => a.UserPrincipalName, e => e.UserPrincipalName),
        ("E-mail", a => a.EmailAddress, e => e.Mail),
        ("Dienst (Department)", a => a.Department, e => e.Department),
        ("Functie (Title)", a => a.Title, e => e.JobTitle),
        ("Bedrijf (Company)", a => a.Company, e => e.CompanyName),
        ("Kantoor (Office)", a => a.Office, e => e.OfficeLocation),
    };

    public static IReadOnlyList<Verschil> Vergelijk(InventarisData data, OrganisatieConfig config)
    {
        var uit = new List<Verschil>();
        if (!data.HeeftAd || !data.HeeftEntra)
        {
            return uit;
        }

        foreach (var p in data.Personen)
        {
            if (p.Ad is null)
            {
                continue;
            }

            var inSync = config.GesynchroniseerdeOus.Count == 0 || DnHulp.ValtOnder(p.Ad.OuPad, config.GesynchroniseerdeOus);
            if (p.Entra is null)
            {
                if (inSync && p.Ad.Enabled)
                {
                    uit.Add(new Verschil(p, VerschilSoort.OntbreektInEntra, "Account", p.Ad.UserPrincipalName, string.Empty, RedenOntbreekt(p.Ad)));
                }

                continue;
            }

            if (!p.Ad.Enabled && p.Entra.AccountEnabled)
            {
                uit.Add(new Verschil(p, VerschilSoort.StatusVerschilt, "Ingeschakeld", "uitgeschakeld", "ingeschakeld",
                    "Uitgeschakeld in AD maar nog actief in Entra (wacht op de volgende synchronisatie?)"));
            }

            foreach (var (kenmerk, adWaarde, entraWaarde) in Kenmerken)
            {
                var a = adWaarde(p.Ad);
                var e = entraWaarde(p.Entra);
                // Entra schrapt spaties vooraan en achteraan bij de synchronisatie: dat is geen echt verschil
                if (string.Equals(a.Trim(), e.Trim(), StringComparison.Ordinal))
                {
                    continue;
                }

                var toelichting = string.Equals(a.Trim(), e.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? "Alleen hoofdletters verschillen"
                    : string.Empty;
                uit.Add(new Verschil(p, VerschilSoort.KenmerkVerschilt, kenmerk, a, e, toelichting));
            }
        }

        return uit
            .OrderBy(v => v.Soort)
            .ThenBy(v => v.Persoon.Naam, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string RedenOntbreekt(AdGebruiker ad)
    {
        var redenen = new List<string>();
        var domein = ad.UserPrincipalName.Contains('@') ? ad.UserPrincipalName[(ad.UserPrincipalName.IndexOf('@') + 1)..] : string.Empty;
        if (domein.Length == 0)
        {
            redenen.Add("geen UPN");
        }
        else if (domein.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || domein.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            redenen.Add($"UPN-domein {domein} is niet routeerbaar");
        }

        if (string.IsNullOrWhiteSpace(ad.LastLogonDate))
        {
            redenen.Add("nooit aangemeld");
        }

        return redenen.Count == 0 ? "Oorzaak onbekend (synchronisatiefilter, recent aangemaakt?)" : string.Join("; ", redenen);
    }
}
