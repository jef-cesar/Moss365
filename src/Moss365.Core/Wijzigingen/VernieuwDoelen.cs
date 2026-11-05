using System.Text.RegularExpressions;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Welke objecten opnieuw gelezen moeten worden: na een uitgevoerd plan (de doelen en de groepen in de wijzigingen) of
/// op vraag van de beheerder (knop Vernieuwen). Per systeem de sleutel waarop gelezen wordt.
/// </summary>
public sealed partial class VernieuwDoelen
{
    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    /// <summary>sAMAccountName van AD-gebruikers (blijft geldig na verplaatsen).</summary>
    public HashSet<string> AdGebruikers { get; } = new(Cmp);

    public HashSet<string> AdGroepen { get; } = new(Cmp);

    /// <summary>Id of UPN van Entra-gebruikers.</summary>
    public HashSet<string> EntraGebruikers { get; } = new(Cmp);

    public HashSet<string> CloudGroepen { get; } = new(Cmp);

    /// <summary>Adressen van rooms, postvakken en Exchange-groepen.</summary>
    public HashSet<string> ExchangeAdressen { get; } = new(Cmp);

    /// <summary>Sleutels (<see cref="Sleutel"/>) van de objecten waar het om gaat, om ze als gewijzigd te markeren.</summary>
    public HashSet<string> Objecten { get; } = new(Cmp);

    /// <summary>Conditional Access opnieuw lezen (na een wijziging aan een beleid of een benoemde locatie).</summary>
    public bool ConditionalAccess { get; set; }

    public bool Leeg => !ConditionalAccess && AdGebruikers.Count + AdGroepen.Count + EntraGebruikers.Count + CloudGroepen.Count + ExchangeAdressen.Count == 0;

    public int Aantal => (ConditionalAccess ? 1 : 0) + AdGebruikers.Count + AdGroepen.Count + EntraGebruikers.Count + CloudGroepen.Count + ExchangeAdressen.Count;

    /// <summary>Sleutel van een object voor de status "gelezen om" en "gewijzigd".</summary>
    public static string Sleutel(ObjectRef r) => $"{r.Soort}:{r.Sleutel}".ToLowerInvariant();

    public static VernieuwDoelen Voor(InventarisData d, IEnumerable<ObjectRef> objecten)
    {
        var doelen = new VernieuwDoelen();
        foreach (var r in objecten)
        {
            doelen.Voeg(d, r);
        }

        return doelen;
    }

    /// <summary>De objecten die een uitgevoerd plan raakte (enkel de gelukte wijzigingen).</summary>
    public static VernieuwDoelen NaPlan(InventarisData d, IEnumerable<Wijziging> plan, IEnumerable<WijzigingResultaat> resultaten)
    {
        var gelukt = resultaten.Where(r => r.Status == ResultaatStatus.Gelukt).ToDictionary(r => r.WijzigingId);
        var doelen = new VernieuwDoelen();
        foreach (var w in plan.Where(w => gelukt.ContainsKey(w.Id)))
        {
            switch (w.Actie)
            {
                case Actie.AdGebruikerAanmaken:
                    // Nieuw account: nog niet in de gegevens, lezen op sAMAccountName
                    var sam = w.P("sAMAccountName");
                    if (sam.Length > 0)
                    {
                        doelen.AdGebruikers.Add(sam);
                        doelen.Objecten.Add($"gebruiker:ad:{sam}".ToLowerInvariant());
                    }

                    break;
                case Actie.GraphGastUitnodigen:
                    // De uitnodiging geeft het id van de nieuwe gast terug
                    if (Guid().Match(gelukt[w.Id].Melding) is { Success: true } id)
                    {
                        doelen.EntraGebruikers.Add(id.Value);
                    }

                    break;
                case Actie.ExchangeGedeeldPostvakAanmaken:
                case Actie.ExchangeGroepAanmaken:
                    doelen.ExchangeAdressen.Add(w.Doel);
                    break;
                case Actie.AdGroepAanmaken:
                    // Nieuwe groep: lezen op sAMAccountName (met de leden die er meteen bij kwamen)
                    if (w.P("sAMAccountName") is { Length: > 0 } groepSam)
                    {
                        doelen.AdGroepen.Add(groepSam);
                    }

                    break;
                case Actie.GraphGroepAanmaken:
                    // Graph geeft het id van de nieuwe groep terug
                    if (Guid().Match(gelukt[w.Id].Melding) is { Success: true } groepId)
                    {
                        doelen.CloudGroepen.Add(groepId.Value);
                    }

                    break;
                case Actie.GraphCaBeleidMaken:
                case Actie.GraphCaBeleidWijzigen:
                case Actie.GraphCaLocatieMaken:
                case Actie.GraphCaLocatieWijzigen:
                    doelen.ConditionalAccess = true;
                    continue;
                default:
                    doelen.VoegId(d, w.Doel);
                    break;
            }

            foreach (var p in new[] { "groepDn", "groepId", "groep" })
            {
                if (w.Parameters.TryGetValue(p, out var groep) && groep.Length > 0)
                {
                    doelen.VoegId(d, groep);
                }
            }
        }

        return doelen;
    }

    private void VoegId(InventarisData d, string id)
    {
        if (d.Index.Zoek(id) is { } r)
        {
            Voeg(d, r);
        }
        else if (id.Contains('@'))
        {
            // Onbekend adres (bv. net aangemaakt): Exchange zoekt het zelf op
            ExchangeAdressen.Add(id);
        }
    }

    private void Voeg(InventarisData d, ObjectRef r)
    {
        switch (r.Soort)
        {
            case ObjectSoort.Gebruiker when d.ZoekPersoon(r.Sleutel) is { } p:
                if (p.Ad is not null)
                {
                    AdGebruikers.Add(p.Ad.SamAccountName);
                }

                if (p.Entra is not null)
                {
                    EntraGebruikers.Add(p.Entra.Id);
                }
                else if (p.Upn.Length > 0)
                {
                    // Na de synchronisatie bestaat hij misschien al in Entra
                    EntraGebruikers.Add(p.Upn);
                }

                break;
            case ObjectSoort.Groep when d.Index.Groep(r.Sleutel) is { } g:
                if (g.Ad is not null)
                {
                    AdGroepen.Add(g.Ad.SamAccountName);
                }

                if (g.Cloud is not null)
                {
                    CloudGroepen.Add(g.Cloud.Id);
                }

                if (g.Exo is not null)
                {
                    ExchangeAdressen.Add(g.Exo.Smtp);
                }

                break;
            case ObjectSoort.Postvak:
            case ObjectSoort.Room:
                ExchangeAdressen.Add(r.Sleutel);
                break;
            default:
                return;
        }

        Objecten.Add(Sleutel(r));
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex Guid();
}
