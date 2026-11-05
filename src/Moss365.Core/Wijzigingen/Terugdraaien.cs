using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>Een plan dat uitgevoerde wijzigingen terugdraait, en wat niet terug te draaien is (met de reden).</summary>
public sealed record OmkeerUitkomst(IReadOnlyList<Wijziging> Plan, IReadOnlyList<string> NietOmkeerbaar);

/// <summary>
/// Maakt van gelukte regels uit het logboek het omgekeerde plan: een lid weer toevoegen of verwijderen, een recht teruggeven,
/// een account weer in- of uitschakelen, terug naar de vorige OU, en kenmerken terug naar hun vorige waarde (als die in het
/// logboek staat). Het plan volgt de omgekeerde volgorde, zodat elke stap dezelfde toestand terugvindt als bij het origineel.
/// Niet omkeerbaar: aanmaken, gasten verwijderen, wachtwoorden, sessies, licenties en postvakken omzetten.
/// </summary>
public static class Terugdraaien
{
    private static readonly Dictionary<Actie, Actie> Tegendeel = new()
    {
        [Actie.AdGroepLidToevoegen] = Actie.AdGroepLidVerwijderen,
        [Actie.AdGroepLidVerwijderen] = Actie.AdGroepLidToevoegen,
        [Actie.GraphGroepLidToevoegen] = Actie.GraphGroepLidVerwijderen,
        [Actie.GraphGroepLidVerwijderen] = Actie.GraphGroepLidToevoegen,
        [Actie.ExchangeGroepLidToevoegen] = Actie.ExchangeGroepLidVerwijderen,
        [Actie.ExchangeGroepLidVerwijderen] = Actie.ExchangeGroepLidToevoegen,
        [Actie.GraphEigenaarToevoegen] = Actie.GraphEigenaarVerwijderen,
        [Actie.GraphEigenaarVerwijderen] = Actie.GraphEigenaarToevoegen,
        [Actie.ExchangePostvakRechtToevoegen] = Actie.ExchangePostvakRechtVerwijderen,
        [Actie.ExchangePostvakRechtVerwijderen] = Actie.ExchangePostvakRechtToevoegen,
        [Actie.AdAccountUitschakelen] = Actie.AdAccountInschakelen,
        [Actie.AdAccountInschakelen] = Actie.AdAccountUitschakelen,
    };

    /// <summary>Kenmerken die met hun vorige waarde teruggezet kunnen worden.</summary>
    private static readonly HashSet<Actie> Kenmerken = new()
    {
        Actie.AdKenmerkenZetten, Actie.GraphKenmerkenZetten, Actie.GraphGroepKenmerkenZetten,
        Actie.ExchangeGroepInstellen, Actie.ExchangePostvakInstellen, Actie.ExchangeAutoAntwoord, Actie.GraphManagerZetten,
        Actie.GraphCaBeleidWijzigen, Actie.GraphCaLocatieWijzigen,
    };

    /// <summary>Kan deze regel ongedaan gemaakt worden? Zo nee, waarom niet.</summary>
    public static string? WaaromNiet(LogRegel r)
    {
        if (r.Status != nameof(ResultaatStatus.Gelukt))
        {
            return "niet gelukt, dus niets terug te draaien";
        }

        if (WasAlZo(r))
        {
            return "er veranderde niets (" + r.Melding.TrimEnd('.') + ")";
        }

        if (!Enum.TryParse<Actie>(r.Actie, out var actie))
        {
            return "onbekende actie";
        }

        if (Tegendeel.ContainsKey(actie) || actie == Actie.AdVerplaatsen || actie == Actie.ExchangeGroepBeheerder)
        {
            return null;
        }

        if (Kenmerken.Contains(actie))
        {
            return r.Vorige is { Count: > 0 } ? null : "de vorige waarden staan niet in het logboek (wijziging van voor versie 0.5)";
        }

        return actie switch
        {
            Actie.AdGebruikerAanmaken or Actie.ExchangeGedeeldPostvakAanmaken or Actie.ExchangeGroepAanmaken or Actie.GraphCaLocatieMaken
                => "aangemaakte objecten verwijdert de app niet",
            Actie.GraphCaBeleidMaken => "zet het beleid uit bij Conditional Access > Beheren (de app verwijdert geen beleiden)",
            Actie.GraphGastUitnodigen => "verwijder de gast via Gastgebruikers",
            Actie.GraphGastVerwijderen => "terugzetten via Entra > Verwijderde gebruikers (30 dagen)",
            Actie.AdWachtwoordInstellen => "een vorig wachtwoord kan niet terug",
            Actie.GraphSessiesIntrekken => "afgemelde sessies kunnen niet terug",
            Actie.ExchangeNaarGedeeldPostvak => "zet het postvak zelf terug om in Exchange (licentie nodig)",
            Actie.GraphLicentieToekennen or Actie.GraphLicentiesVerwijderen => "licenties wijzig je via de gebruiker of Entra",
            Actie.AdAccountOntgrendelen => "een account vergrendel je niet opnieuw",
            _ => "deze wijziging kan de app niet terugdraaien",
        };
    }

    public static bool KanOmkeren(LogRegel r) => WaaromNiet(r) is null;

    /// <param name="regels">De gekozen regels; ze worden van nieuw naar oud teruggedraaid.</param>
    /// <param name="data">De huidige gegevens: om een verplaatst AD-account terug te vinden en bevoorrechte groepen te herkennen.</param>
    public static OmkeerUitkomst Plan(IEnumerable<LogRegel> regels, InventarisData? data = null)
    {
        var plan = new List<Wijziging>();
        var niet = new List<string>();

        // De DN van AD-accounts kan veranderen door een verplaatsing (ook door een stap in dit plan); volg die hier
        var huidigeDn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Dn(string dn)
        {
            if (huidigeDn.TryGetValue(dn, out var nu))
            {
                return nu;
            }

            nu = ZoekHuidigeDn(dn, data);
            huidigeDn[dn] = nu;
            return nu;
        }

        foreach (var r in regels.OrderByDescending(r => r.Tijd))
        {
            if (WaaromNiet(r) is { } reden)
            {
                niet.Add($"{r.Omschrijving} ({Naam(r)}): {reden}.");
                continue;
            }

            var actie = Enum.Parse<Actie>(r.Actie);
            var systeem = SysteemVan(actie);
            var parameters = new Dictionary<string, string>(r.Parameters, StringComparer.OrdinalIgnoreCase);
            var doel = r.Doel;
            if (systeem == Systeem.ActiveDirectory && doel.Contains('='))
            {
                doel = Dn(r.Doel);
            }

            Wijziging w;
            if (actie == Actie.AdVerplaatsen)
            {
                // Terug naar de OU van vroeger; het account staat nu in de doel-OU van toen
                var vorigeOu = DnHulp.Ouder(r.Doel);
                var rdn = r.Doel[..(r.Doel.Length - vorigeOu.Length)].TrimEnd(',');
                var nu = Dn($"{rdn},{r.Parameters.GetValueOrDefault("doelOu")}");
                w = Nieuw(r, Actie.AdVerplaatsen, nu, new() { ["doelOu"] = vorigeOu },
                    $"Terug naar {DnHulp.OuPad(vorigeOu)} verplaatsen", DnHulp.OuPadVanObject(nu), DnHulp.OuPad(vorigeOu));
                huidigeDn[r.Doel] = r.Doel;
            }
            else if (actie == Actie.ExchangeGroepBeheerder)
            {
                var omgekeerd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (parameters.TryGetValue("Toevoegen", out var toe) && toe.Length > 0)
                {
                    omgekeerd["Verwijderen"] = toe;
                }

                if (parameters.TryGetValue("Verwijderen", out var weg) && weg.Length > 0)
                {
                    omgekeerd["Toevoegen"] = weg;
                }

                foreach (var (k, v) in parameters.Where(p => p.Key is not ("Toevoegen" or "Verwijderen")))
                {
                    omgekeerd[k] = v;
                }

                w = Nieuw(r, actie, doel, omgekeerd, "Beheerder terugzetten: " + string.Join(", ",
                    omgekeerd.Where(p => p.Key is "Toevoegen" or "Verwijderen").Select(p => $"{p.Key.ToLowerInvariant()} {p.Value}")));
            }
            else if (Kenmerken.Contains(actie))
            {
                var vorige = new Dictionary<string, string>(r.Vorige!, StringComparer.OrdinalIgnoreCase);
                // Vaste parameters (bv. Identity bij Exchange) blijven; de kenmerken krijgen hun vorige waarde
                foreach (var (k, v) in vorige)
                {
                    parameters[k] = v;
                }

                w = Nieuw(r, actie, doel, parameters, "Terugzetten: " + r.Omschrijving,
                    string.Join(", ", vorige.Keys.Select(k => $"{k}: {r.Parameters.GetValueOrDefault(k)}")),
                    string.Join(", ", vorige.Select(p => $"{p.Key}: {(p.Value.Length == 0 ? "(leeg)" : p.Value)}")),
                    vorigeWaarden: vorige.Keys.ToDictionary(k => k, k => r.Parameters.GetValueOrDefault(k) ?? string.Empty, StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                var tegendeel = Tegendeel[actie];
                w = Nieuw(r, tegendeel, doel, parameters, Omschrijving(tegendeel, r));
                if (tegendeel == Actie.AdGroepLidToevoegen && AdGroep(parameters.GetValueOrDefault("groepDn") ?? string.Empty, data) is { } g
                    && Spiegelgebruiker.IsBevoorrecht(g))
                {
                    w = Nieuw(r, tegendeel, doel, parameters, Omschrijving(tegendeel, r),
                        waarschuwing: $"{g.Name} is een bevoorrechte groep: zet ze enkel terug als dat echt nodig is.", geselecteerd: false);
                }
            }

            plan.Add(w);
        }

        return new OmkeerUitkomst(plan, niet);
    }

    /// <summary>Was de wijziging een bevestiging van wat er al was ("Was al lid.")? Dan valt er niets terug te draaien.</summary>
    private static bool WasAlZo(LogRegel r)
        => r.Melding.StartsWith("Was al", StringComparison.OrdinalIgnoreCase)
           || r.Melding.StartsWith("Had geen", StringComparison.OrdinalIgnoreCase)
           || r.Melding.Contains("al niet meer", StringComparison.OrdinalIgnoreCase);

    /// <summary>De AD-groep met deze DN uit de gegevens, of (niet geladen) een groep met enkel naam en DN.</summary>
    private static AdGroep? AdGroep(string dn, InventarisData? data)
    {
        if (dn.Length == 0)
        {
            return null;
        }

        return data is not null && data.AdGroepenOpDn.TryGetValue(dn, out var g)
            ? g
            : new AdGroep { Name = DnHulp.Naam(dn), SamAccountName = DnHulp.Naam(dn), DistinguishedName = dn, OuPad = DnHulp.OuPadVanObject(dn) };
    }

    private static string ZoekHuidigeDn(string dn, InventarisData? data)
    {
        if (data is null || data.AdGebruikers.Any(a => string.Equals(a.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase)))
        {
            return dn;
        }

        // Verplaatst? Dan zelfde naam (CN) in een andere OU; enkel als dat eenduidig is
        var naam = DnHulp.Naam(dn);
        var kandidaten = data.AdGebruikers.Where(a => string.Equals(DnHulp.Naam(a.DistinguishedName), naam, StringComparison.OrdinalIgnoreCase)).ToList();
        return kandidaten.Count == 1 ? kandidaten[0].DistinguishedName : dn;
    }

    private static Systeem SysteemVan(Actie a) => a.ToString() switch
    {
        var t when t.StartsWith("Ad", StringComparison.Ordinal) => Systeem.ActiveDirectory,
        var t when t.StartsWith("Graph", StringComparison.Ordinal) => Systeem.Graph,
        _ => Systeem.Exchange,
    };

    private static string Naam(LogRegel r) => r.DoelNaam.Length > 0 ? r.DoelNaam : r.Doel;

    private static string Omschrijving(Actie a, LogRegel r)
    {
        var groep = r.Parameters.GetValueOrDefault("groep") ?? r.Parameters.GetValueOrDefault("groepNaam") ?? string.Empty;
        return a switch
        {
            Actie.AdGroepLidToevoegen or Actie.GraphGroepLidToevoegen or Actie.ExchangeGroepLidToevoegen => $"Terug lid maken van {groep}",
            Actie.AdGroepLidVerwijderen or Actie.GraphGroepLidVerwijderen or Actie.ExchangeGroepLidVerwijderen => $"Terug uit {groep} halen",
            Actie.GraphEigenaarToevoegen => $"Terug eigenaar maken: {r.Parameters.GetValueOrDefault("lid")}",
            Actie.GraphEigenaarVerwijderen => $"Eigenaar terug weghalen: {r.Parameters.GetValueOrDefault("lid")}",
            Actie.ExchangePostvakRechtToevoegen => $"{r.Parameters.GetValueOrDefault("Recht")} teruggeven aan {r.Parameters.GetValueOrDefault("Trustee")}",
            Actie.ExchangePostvakRechtVerwijderen => $"{r.Parameters.GetValueOrDefault("Recht")} terug intrekken van {r.Parameters.GetValueOrDefault("Trustee")}",
            Actie.AdAccountInschakelen => "AD-account terug inschakelen",
            Actie.AdAccountUitschakelen => "AD-account terug uitschakelen",
            _ => "Terugdraaien: " + r.Omschrijving,
        };
    }

    private static Wijziging Nieuw(LogRegel r, Actie actie, string doel, Dictionary<string, string> parameters, string omschrijving,
        string? huidig = null, string? nieuw = null, string? waarschuwing = null, bool geselecteerd = true, Dictionary<string, string>? vorigeWaarden = null) => new()
    {
        Systeem = SysteemVan(actie),
        Actie = actie,
        Doel = doel,
        DoelNaam = r.DoelNaam,
        Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
        Omschrijving = omschrijving,
        HuidigeWaarde = huidig,
        NieuweWaarde = nieuw,
        Waarschuwing = waarschuwing,
        Geselecteerd = geselecteerd,
        Vorige = vorigeWaarden,
    };
}
