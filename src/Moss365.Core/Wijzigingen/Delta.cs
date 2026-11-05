using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Past de AD-wijzigingen sinds de vorige lezing toe op die vorige lezing. Gewijzigde objecten (uSNChanged) komen vers
/// uit AD; de rest blijft, behalve wat verwijderd of verplaatst is (dat volgt uit de lijst van alle bestaande objecten).
/// Lidmaatschappen: een gewijzigde groep heeft zijn verse ledenlijst; het "lid van" van gebruikers wordt daarop aangepast.
/// </summary>
public static class AdDelta
{
    /// <param name="basis">De vorige lezing.</param>
    /// <param name="gebruikers">Gebruikers die veranderd zijn sinds de vorige lezing (vers gelezen, met memberOf).</param>
    /// <param name="groepen">Groepen die veranderd zijn (vers gelezen, met hun leden).</param>
    /// <param name="bestaandeGebruikers">Alle gebruikers die nu bestaan: sAMAccountName naar DN.</param>
    /// <param name="bestaandeGroepen">Alle groepen die nu bestaan: sAMAccountName naar DN.</param>
    public static (List<AdGebruiker> Gebruikers, List<AdGroep> Groepen) Toepassen(
        AdBasis basis,
        IReadOnlyList<AdGebruiker> gebruikers,
        IReadOnlyList<AdGroep> groepen,
        IReadOnlyDictionary<string, string> bestaandeGebruikers,
        IReadOnlyDictionary<string, string> bestaandeGroepen)
    {
        var ic = StringComparer.OrdinalIgnoreCase;

        // Verplaatst of hernoemd (oude DN naar nieuwe) en verwijderd (oude DN)
        var nieuweDn = new Dictionary<string, string>(ic);
        var weg = new HashSet<string>(ic);
        foreach (var (sam, dn) in basis.Gebruikers.Select(u => (u.SamAccountName, u.DistinguishedName)).Concat(basis.Groepen.Select(g => (g.SamAccountName, g.DistinguishedName))))
        {
            var bestaand = bestaandeGebruikers.TryGetValue(sam, out var u) ? u : bestaandeGroepen.TryGetValue(sam, out var g) ? g : null;
            if (bestaand is null)
            {
                weg.Add(dn);
            }
            else if (!string.Equals(bestaand, dn, StringComparison.OrdinalIgnoreCase))
            {
                nieuweDn[dn] = bestaand;
            }
        }

        string? Nu(string dn) => weg.Contains(dn) ? null : nieuweDn.TryGetValue(dn, out var n) ? n : dn;
        List<string> Herschrijf(IEnumerable<string> dns) => dns.Select(Nu).Where(d => d is not null).Select(d => d!).Distinct(ic).ToList();

        // Groepen
        var vers = groepen.GroupBy(g => g.SamAccountName, ic).ToDictionary(g => g.Key, g => g.First(), ic);
        var oudeLeden = basis.Groepen.ToDictionary(g => g.SamAccountName, g => Herschrijf(g.Leden), ic);
        var nieuweGroepen = new List<AdGroep>();
        foreach (var g in basis.Groepen)
        {
            if (vers.ContainsKey(g.SamAccountName) || !bestaandeGroepen.ContainsKey(g.SamAccountName))
            {
                continue;
            }

            var dn = Nu(g.DistinguishedName)!;
            var leden = oudeLeden[g.SamAccountName];
            nieuweGroepen.Add(g with
            {
                DistinguishedName = dn,
                OuPad = DnHulp.OuPadVanObject(dn),
                ManagedBy = g.ManagedBy.Length == 0 ? g.ManagedBy : Nu(g.ManagedBy) ?? string.Empty,
                Leden = leden,
                AantalLeden = g.LedenBekend ? leden.Count : g.AantalLeden,
            });
        }

        nieuweGroepen.AddRange(vers.Values);

        // "Lid van" aanpassen voor wie in een gewijzigde groep bij- of weggekomen is
        var erbij = new Dictionary<string, HashSet<string>>(ic);
        var eraf = new Dictionary<string, HashSet<string>>(ic);
        void Noteer(Dictionary<string, HashSet<string>> d, string lid, string groep)
        {
            if (!d.TryGetValue(lid, out var s))
            {
                d[lid] = s = new HashSet<string>(ic);
            }

            s.Add(groep);
        }

        foreach (var g in vers.Values)
        {
            var oud = oudeLeden.TryGetValue(g.SamAccountName, out var o) ? o : new List<string>();
            foreach (var lid in g.Leden.Except(oud, ic))
            {
                Noteer(erbij, lid, g.DistinguishedName);
            }

            foreach (var lid in oud.Except(g.Leden, ic))
            {
                Noteer(eraf, lid, g.DistinguishedName);
            }
        }

        // Gebruikers
        var versGebruikers = gebruikers.GroupBy(u => u.SamAccountName, ic).ToDictionary(g => g.Key, g => g.First(), ic);
        var nieuweGebruikers = new List<AdGebruiker>();
        foreach (var u in basis.Gebruikers)
        {
            if (versGebruikers.ContainsKey(u.SamAccountName) || !bestaandeGebruikers.ContainsKey(u.SamAccountName))
            {
                continue;
            }

            var dn = Nu(u.DistinguishedName)!;
            var lidVan = Herschrijf(u.MemberOf);
            if (eraf.TryGetValue(dn, out var min))
            {
                lidVan.RemoveAll(min.Contains);
            }

            if (erbij.TryGetValue(dn, out var plus))
            {
                lidVan.AddRange(plus.Where(p => !lidVan.Contains(p, ic)));
            }

            nieuweGebruikers.Add(u with { DistinguishedName = dn, OuPad = DnHulp.OuPadVanObject(dn), MemberOf = lidVan });
        }

        nieuweGebruikers.AddRange(versGebruikers.Values);
        return (nieuweGebruikers, nieuweGroepen);
    }
}

/// <summary>Eén gewijzigde cloudgroep uit Graph-delta.</summary>
/// <param name="Id">Id van de groep.</param>
/// <param name="Verwijderd">De groep is verwijderd.</param>
/// <param name="Bijwerken">Zet de gewijzigde kenmerken op de vorige versie (null = nieuwe groep); null als enkel de leden veranderden.</param>
/// <param name="LedenErbij">Gebruikers die lid werden.</param>
/// <param name="LedenEraf">Gebruikers die geen lid meer zijn.</param>
public sealed record GroepDeltaItem(
    string Id,
    bool Verwijderd,
    Func<CloudGroep?, CloudGroep>? Bijwerken,
    IReadOnlyList<string> LedenErbij,
    IReadOnlyList<string> LedenEraf);

/// <summary>Past de wijzigingen aan cloudgroepen (Graph /groups/delta) toe op de vorige lezing.</summary>
public static class GraphDelta
{
    public static (List<CloudGroep> Groepen, Dictionary<string, HashSet<string>> Lidmaatschap) Toepassen(GraphBasis basis, IEnumerable<GroepDeltaItem> items)
    {
        var ic = StringComparer.OrdinalIgnoreCase;
        var groepen = new Dictionary<string, CloudGroep>(basis.Groepen, ic);

        // Per groep de leden (omgekeerd van het lidmaatschap per gebruiker)
        var leden = groepen.Keys.ToDictionary(id => id, _ => new HashSet<string>(ic), ic);
        foreach (var (gebruiker, ids) in basis.Lidmaatschap)
        {
            foreach (var id in ids)
            {
                if (!leden.TryGetValue(id, out var s))
                {
                    leden[id] = s = new HashSet<string>(ic);
                }

                s.Add(gebruiker);
            }
        }

        var gewijzigd = new HashSet<string>(ic);
        foreach (var item in items)
        {
            if (item.Verwijderd)
            {
                groepen.Remove(item.Id);
                leden.Remove(item.Id);
                continue;
            }

            groepen.TryGetValue(item.Id, out var bestaand);
            if (item.Bijwerken is not null)
            {
                groepen[item.Id] = item.Bijwerken(bestaand) with { Id = item.Id };
            }
            else if (bestaand is null)
            {
                // Alleen leden van een groep die we niet kennen: zonder kenmerken kan de app hem niet tonen
                continue;
            }

            if (!leden.TryGetValue(item.Id, out var set))
            {
                leden[item.Id] = set = new HashSet<string>(ic);
            }

            set.UnionWith(item.LedenErbij);
            set.ExceptWith(item.LedenEraf);
            gewijzigd.Add(item.Id);
        }

        foreach (var id in gewijzigd.Where(groepen.ContainsKey))
        {
            groepen[id] = groepen[id] with { AantalLeden = leden[id].Count };
        }

        var lidmaatschap = new Dictionary<string, HashSet<string>>(ic);
        foreach (var (id, s) in leden.Where(l => groepen.ContainsKey(l.Key)))
        {
            foreach (var gebruiker in s)
            {
                if (!lidmaatschap.TryGetValue(gebruiker, out var g))
                {
                    lidmaatschap[gebruiker] = g = new HashSet<string>(ic);
                }

                g.Add(id);
            }
        }

        return (groepen.Values.ToList(), lidmaatschap);
    }
}
