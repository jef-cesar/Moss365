using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Vervangt één opnieuw gelezen object in de gegevens en werkt de relaties bij (memberOf van gebruikers, leden van
/// groepen, cloudlidmaatschap, postvakrechten). Zo toont de app na een wijziging meteen de nieuwe toestand zonder alles
/// opnieuw te laden.
/// </summary>
public static class ObjectVernieuwer
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>AD-gebruiker vervangen (null = bestaat niet meer); de ledenlijsten van de AD-groepen volgen zijn memberOf.</summary>
    public static InventarisData MetAdGebruiker(InventarisData d, string sam, AdGebruiker? nieuw)
    {
        var oud = d.AdGebruikers.FirstOrDefault(u => Cmp.Equals(u.SamAccountName, sam));
        var gebruikers = d.AdGebruikers.Where(u => !Cmp.Equals(u.SamAccountName, sam)).ToList();
        if (nieuw is not null)
        {
            gebruikers.Add(nieuw);
        }

        var weg = new[] { oud?.DistinguishedName, nieuw?.DistinguishedName }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToHashSet(Cmp);
        var lidVan = nieuw?.MemberOf.ToHashSet(Cmp) ?? new HashSet<string>(Cmp);
        var groepen = d.AdGroepenOpDn.Values.Select(g =>
        {
            if (!g.LedenBekend)
            {
                return g;
            }

            var leden = g.Leden.Where(l => !weg.Contains(l)).ToList();
            if (nieuw is not null && lidVan.Contains(g.DistinguishedName))
            {
                leden.Add(nieuw.DistinguishedName);
            }

            return leden.Count == g.Leden.Count && leden.SequenceEqual(g.Leden, Cmp) ? g : g with { Leden = leden, AantalLeden = leden.Count };
        }).ToList();

        return Herbouw(d, adGebruikers: gebruikers, adGroepen: groepen);
    }

    /// <summary>AD-groep vervangen (null = bestaat niet meer); memberOf van de gebruikers volgt de nieuwe ledenlijst.</summary>
    public static InventarisData MetAdGroep(InventarisData d, string sam, AdGroep? nieuw)
    {
        var oud = d.AdGroepenOpDn.Values.FirstOrDefault(g => Cmp.Equals(g.SamAccountName, sam));
        var groepen = d.AdGroepenOpDn.Values.Where(g => !Cmp.Equals(g.SamAccountName, sam)).ToList();
        if (nieuw is not null)
        {
            groepen.Add(nieuw);
        }

        var oudeDns = new[] { oud?.DistinguishedName, nieuw?.DistinguishedName }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToHashSet(Cmp);
        var leden = nieuw?.Leden.ToHashSet(Cmp) ?? new HashSet<string>(Cmp);
        var gebruikers = d.AdGebruikers.Select(u =>
        {
            var memberOf = u.MemberOf.Where(m => !oudeDns.Contains(m)).ToList();
            if (nieuw is not null && leden.Contains(u.DistinguishedName))
            {
                memberOf.Add(nieuw.DistinguishedName);
            }

            return memberOf.Count == u.MemberOf.Count && memberOf.SequenceEqual(u.MemberOf, Cmp) ? u : u with { MemberOf = memberOf };
        }).ToList();

        return Herbouw(d, adGebruikers: gebruikers, adGroepen: groepen);
    }

    /// <summary>Entra-gebruiker vervangen of toevoegen, met zijn rechtstreekse groepen. Oud = id of UPN waarmee hij gekend was.</summary>
    public static InventarisData MetEntraGebruiker(InventarisData d, string oud, GraphGebruikerLezing? nieuw)
    {
        var bestaand = d.EntraGebruikers.FirstOrDefault(e => Cmp.Equals(e.Id, oud) || Cmp.Equals(e.UserPrincipalName, oud));
        var weg = new[] { bestaand?.Id, nieuw?.Gebruiker.Id }.Where(x => !string.IsNullOrEmpty(x)).Cast<string>().ToHashSet(Cmp);
        var gebruikers = d.EntraGebruikers.Where(e => !weg.Contains(e.Id)).ToList();
        var lidmaatschap = Kopie(d.CloudLidmaatschap);
        foreach (var id in weg)
        {
            lidmaatschap.Remove(id);
        }

        if (nieuw is not null)
        {
            gebruikers.Add(nieuw.Gebruiker);
            lidmaatschap[nieuw.Gebruiker.Id] = nieuw.GroepIds.ToHashSet(Cmp);
        }

        return Herbouw(d, entraGebruikers: gebruikers, lidmaatschap: lidmaatschap);
    }

    /// <summary>Cloudgroep vervangen (null = bestaat niet meer), met de rechtstreekse leden.</summary>
    public static InventarisData MetCloudGroep(InventarisData d, string id, GraphGroepLezing? nieuw)
    {
        var groepen = d.CloudGroepen.Values.Where(g => !Cmp.Equals(g.Id, id)).ToList();
        var lidmaatschap = Kopie(d.CloudLidmaatschap);
        foreach (var set in lidmaatschap.Values)
        {
            set.Remove(id);
        }

        if (nieuw is not null)
        {
            groepen.Add(nieuw.Groep with { AantalLeden = nieuw.LedenIds.Count });
            foreach (var lid in nieuw.LedenIds)
            {
                if (!lidmaatschap.TryGetValue(lid, out var set))
                {
                    lidmaatschap[lid] = set = new HashSet<string>(Cmp);
                }

                set.Add(nieuw.Groep.Id);
            }
        }

        return Herbouw(d, cloudGroepen: groepen, lidmaatschap: lidmaatschap);
    }

    /// <summary>Opnieuw gelezen rooms, postvakken (met hun rechten) en Exchange-groepen vervangen; niet gevonden = weg.</summary>
    public static InventarisData MetExchange(InventarisData d, ExchangeObjecten o)
    {
        var weg = o.NietGevonden.ToHashSet(Cmp);
        var nieuweRooms = o.Rooms.ToDictionary(r => r.Smtp, Cmp);
        var nieuwePostvakken = o.Gegevens.Postvakken.ToDictionary(p => p.Smtp, Cmp);
        var nieuweGroepen = o.Gegevens.Groepen.Where(g => g.Smtp.Length > 0).ToDictionary(g => g.Smtp, Cmp);

        var rooms = d.Rooms.Where(r => !weg.Contains(r.Smtp) && !nieuweRooms.ContainsKey(r.Smtp)).Concat(nieuweRooms.Values)
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var postvakken = d.Postvakken.Where(p => !weg.Contains(p.Smtp) && !nieuwePostvakken.ContainsKey(p.Smtp)).Concat(nieuwePostvakken.Values)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        var rechten = d.PostvakRechten.Where(r => !weg.Contains(r.Postvak) && !nieuwePostvakken.ContainsKey(r.Postvak)).Concat(o.Gegevens.Rechten).ToList();
        var groepen = d.ExoGroepen.Where(g => !weg.Contains(g.Smtp) && !nieuweGroepen.ContainsKey(g.Smtp)).Concat(nieuweGroepen.Values).ToList();

        return d with { Rooms = rooms, Postvakken = postvakken, PostvakRechten = rechten, ExoGroepen = groepen };
    }

    private static Dictionary<string, HashSet<string>> Kopie(IReadOnlyDictionary<string, IReadOnlySet<string>> bron)
        => bron.ToDictionary(k => k.Key, k => new HashSet<string>(k.Value, Cmp), Cmp);

    /// <summary>Stelt de gegevens opnieuw samen (personen koppelen) en behoudt de Exchange-gegevens.</summary>
    private static InventarisData Herbouw(
        InventarisData d,
        IReadOnlyList<AdGebruiker>? adGebruikers = null,
        IReadOnlyList<AdGroep>? adGroepen = null,
        IReadOnlyList<EntraGebruiker>? entraGebruikers = null,
        IReadOnlyList<CloudGroep>? cloudGroepen = null,
        Dictionary<string, HashSet<string>>? lidmaatschap = null)
        => InventarisLader.Samenstellen(
            adGebruikers ?? d.AdGebruikers,
            adGroepen ?? d.AdGroepenOpDn.Values.ToList(),
            entraGebruikers ?? d.EntraGebruikers,
            cloudGroepen ?? d.CloudGroepen.Values.ToList(),
            lidmaatschap ?? Kopie(d.CloudLidmaatschap),
            d.Rooms, d.Licenties, d.Bronnen, d.Herkomst, d.DomeinDn) with
        {
            CloudLidNamen = d.CloudLidNamen,
            ExoGroepen = d.ExoGroepen,
            Postvakken = d.Postvakken,
            PostvakRechten = d.PostvakRechten,
            Beveiliging = d.Beveiliging,
            ConditionalAccess = d.ConditionalAccess,
        };
}
