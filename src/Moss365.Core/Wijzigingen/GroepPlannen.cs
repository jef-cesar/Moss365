using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>Een plan of de reden waarom het niet kan.</summary>
public sealed record PlanUitkomst(IReadOnlyList<Wijziging> Plan, IReadOnlyList<string> Fouten)
{
    public static PlanUitkomst Fout(string fout) => new(Array.Empty<Wijziging>(), new[] { fout });

    public static PlanUitkomst Van(IEnumerable<Wijziging> plan, IEnumerable<string>? fouten = null) => new(plan.ToList(), (fouten ?? Array.Empty<string>()).ToList());
}

/// <summary>Gewenste kenmerken van een groep; null = niet wijzigen.</summary>
public sealed class GroepGewenst
{
    public string? Weergavenaam { get; set; }
    public string? Omschrijving { get; set; }
    public string? Mail { get; set; }
}

/// <summary>
/// Plannen voor groepen: leden, naam en omschrijving, beheerders. De weg volgt uit de groep: een AD-groep (ook als ze
/// naar Entra gesynchroniseerd wordt) via AD, een cloudgroep via Graph, een mailgroep via Exchange. Dynamische groepen
/// worden nooit handmatig gevuld.
/// </summary>
public static class GroepPlannen
{
    public static PlanUitkomst LidToevoegen(InventarisData data, Groep g, ObjectRef lid, Guid? afhankelijkVan = null)
        => Lid(data, g, lid, toevoegen: true, afhankelijkVan);

    public static PlanUitkomst LidVerwijderen(InventarisData data, Groep g, ObjectRef lid)
        => Lid(data, g, lid, toevoegen: false, null);

    private static PlanUitkomst Lid(InventarisData data, Groep g, ObjectRef lid, bool toevoegen, Guid? afhankelijkVan)
    {
        if (g.Dynamisch)
        {
            return PlanUitkomst.Fout($"{g.Naam} is een dynamische groep: de leden volgen uit de regel, niet uit handmatige wijzigingen.");
        }

        if (toevoegen && g.Ad is { } adg && Spiegelgebruiker.IsBevoorrecht(adg))
        {
            return PlanUitkomst.Fout($"{g.Naam} is een bevoorrechte groep; leden toevoegen doe je bewust buiten deze app.");
        }

        var werkwoord = toevoegen ? "Lid maken van" : "Uit";
        switch (g.Route)
        {
            case SchrijfRoute.ActiveDirectory:
            {
                var dn = AdDn(data, lid);
                if (dn is null)
                {
                    return PlanUitkomst.Fout($"{lid.Naam} heeft geen AD-object; {g.Naam} is een AD-groep (ook in de cloud gaat het lidmaatschap via AD).");
                }

                return PlanUitkomst.Van(new[]
                {
                    new Wijziging
                    {
                        Systeem = Systeem.ActiveDirectory,
                        Actie = toevoegen ? Actie.AdGroepLidToevoegen : Actie.AdGroepLidVerwijderen,
                        Doel = dn,
                        DoelNaam = lid.Naam,
                        Parameters = P(("groepDn", g.Ad!.DistinguishedName), ("groep", g.Ad.Name)),
                        Omschrijving = toevoegen ? $"Lid maken van AD-groep {g.Naam}" : $"Uit AD-groep {g.Naam} halen",
                        AfhankelijkVan = afhankelijkVan,
                    },
                });
            }

            case SchrijfRoute.Graph:
            {
                var id = EntraId(data, lid);
                if (id is null)
                {
                    return PlanUitkomst.Fout($"{lid.Naam} bestaat niet in Entra.");
                }

                return PlanUitkomst.Van(new[]
                {
                    new Wijziging
                    {
                        Systeem = Systeem.Graph,
                        Actie = toevoegen ? Actie.GraphGroepLidToevoegen : Actie.GraphGroepLidVerwijderen,
                        Doel = id,
                        DoelNaam = lid.Naam,
                        Parameters = P(("groepId", g.Cloud!.Id), ("groep", g.Naam)),
                        Omschrijving = toevoegen ? $"Lid maken van cloudgroep {g.Naam}" : $"Uit cloudgroep {g.Naam} halen",
                        AfhankelijkVan = afhankelijkVan,
                    },
                });
            }

            case SchrijfRoute.Exchange:
            {
                var adres = Adres(data, lid);
                if (adres is null)
                {
                    return PlanUitkomst.Fout($"{lid.Naam} heeft geen e-mailadres; alleen ontvangers kunnen lid zijn van een mailgroep.");
                }

                return PlanUitkomst.Van(new[]
                {
                    new Wijziging
                    {
                        Systeem = Systeem.Exchange,
                        Actie = toevoegen ? Actie.ExchangeGroepLidToevoegen : Actie.ExchangeGroepLidVerwijderen,
                        Doel = adres,
                        DoelNaam = lid.Naam,
                        Parameters = P(("groep", ExoIdentiteit(g)), ("groepNaam", g.Naam)),
                        Omschrijving = $"{werkwoord} {g.Naam} (Exchange)" + (toevoegen ? string.Empty : " halen")
                                       + (toevoegen && g.Naam.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase) ? ": toegang tot een gedeeld postvak" : string.Empty),
                        AfhankelijkVan = afhankelijkVan,
                    },
                });
            }

            default:
                return PlanUitkomst.Fout($"Voor {g.Naam} is geen schrijfweg bekend.");
        }
    }

    /// <summary>Weergavenaam, omschrijving en (mailgroepen) e-mailadres; alleen wat verschilt.</summary>
    public static PlanUitkomst Kenmerken(Groep g, GroepGewenst gewenst)
    {
        var fouten = new List<string>();
        var plan = new List<Wijziging>();
        var naam = Nieuw(g.Naam, gewenst.Weergavenaam);
        var omschrijving = Nieuw(g.Omschrijving, gewenst.Omschrijving);
        var mail = Nieuw(g.Mail, gewenst.Mail);

        switch (g.Route)
        {
            case SchrijfRoute.ActiveDirectory:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (naam is not null) p["displayName"] = naam;
                if (omschrijving is not null) p["description"] = omschrijving;
                if (mail is not null) fouten.Add("Het e-mailadres van een AD-groep wijzig je niet hier (het domein heeft geen Exchange-schema).");
                if (p.Count > 0)
                {
                    plan.Add(new Wijziging
                    {
                        Systeem = Systeem.ActiveDirectory,
                        Actie = Actie.AdKenmerkenZetten,
                        Doel = g.Ad!.DistinguishedName,
                        DoelNaam = g.Naam,
                        Parameters = p,
                        Omschrijving = "Naam en/of omschrijving van de AD-groep aanpassen" + (g.Cloud is null ? string.Empty : " (Entra volgt na de synchronisatie)"),
                        HuidigeWaarde = Tekst(("displayName", g.Ad.DisplayName.Length > 0 ? g.Ad.DisplayName : g.Ad.Name), ("description", g.Ad.Description), p),
                        NieuweWaarde = Tekst(p),
                        Vorige = Vorig(p, ("displayName", g.Ad.DisplayName), ("description", g.Ad.Description)),
                    });
                }

                break;
            }

            case SchrijfRoute.Graph:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (naam is not null) p["displayName"] = naam;
                if (omschrijving is not null) p["description"] = omschrijving;
                if (mail is not null) fouten.Add("Het e-mailadres van een Microsoft 365-groep wijzig je in Exchange; dat zit nog niet in de app.");
                if (p.Count > 0)
                {
                    plan.Add(new Wijziging
                    {
                        Systeem = Systeem.Graph,
                        Actie = Actie.GraphGroepKenmerkenZetten,
                        Doel = g.Cloud!.Id,
                        DoelNaam = g.Naam,
                        Parameters = p,
                        Omschrijving = "Naam en/of omschrijving van de cloudgroep aanpassen",
                        HuidigeWaarde = Tekst(("displayName", g.Cloud.DisplayName), ("description", g.Cloud.Description), p),
                        NieuweWaarde = Tekst(p),
                        Vorige = Vorig(p, ("displayName", g.Cloud.DisplayName), ("description", g.Cloud.Description)),
                    });
                }

                break;
            }

            case SchrijfRoute.Exchange:
            {
                var p = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (naam is not null) p["DisplayName"] = naam;
                if (mail is not null) p["PrimarySmtpAddress"] = mail;
                if (omschrijving is not null) fouten.Add("Een mailgroep heeft in Exchange geen omschrijving die de app kan zetten; de omschrijving blijft ongewijzigd.");
                if (p.Count > 0)
                {
                    plan.Add(new Wijziging
                    {
                        Systeem = Systeem.Exchange,
                        Actie = Actie.ExchangeGroepInstellen,
                        Doel = ExoIdentiteit(g),
                        DoelNaam = g.Naam,
                        Parameters = p,
                        Omschrijving = "Naam en/of e-mailadres van de mailgroep aanpassen",
                        HuidigeWaarde = Tekst(("DisplayName", g.Naam), ("PrimarySmtpAddress", g.Mail), p),
                        NieuweWaarde = Tekst(p),
                        Vorige = Vorig(p, ("DisplayName", g.Naam), ("PrimarySmtpAddress", g.Mail)),
                    });
                }

                break;
            }

            default:
                fouten.Add($"{g.Naam} is dynamisch of heeft geen gekende schrijfweg.");
                break;
        }

        return PlanUitkomst.Van(plan, fouten);
    }

    /// <summary>Beheerder (AD managedBy, Exchange ManagedBy) of eigenaar (cloudgroep) toevoegen of verwijderen.</summary>
    public static PlanUitkomst Beheerder(InventarisData data, Groep g, ObjectRef wie, bool toevoegen)
    {
        if (g.Ad is not null)
        {
            var dn = AdDn(data, wie);
            if (toevoegen && dn is null)
            {
                return PlanUitkomst.Fout($"{wie.Naam} heeft geen AD-object en kan geen beheerder van een AD-groep zijn.");
            }

            return PlanUitkomst.Van(new[]
            {
                new Wijziging
                {
                    Systeem = Systeem.ActiveDirectory,
                    Actie = Actie.AdKenmerkenZetten,
                    Doel = g.Ad.DistinguishedName,
                    DoelNaam = g.Naam,
                    Parameters = P(("managedBy", toevoegen ? dn! : string.Empty)),
                    Vorige = P(("managedBy", g.Ad.ManagedBy)),
                    Omschrijving = toevoegen ? $"{wie.Naam} beheerder maken (managedBy; een AD-groep heeft er maar één)" : "Beheerder (managedBy) weghalen",
                    HuidigeWaarde = g.Ad.ManagedBy.Length > 0 ? DnHulp.Naam(g.Ad.ManagedBy) : "(geen)",
                    NieuweWaarde = toevoegen ? wie.Naam : "(geen)",
                },
            });
        }

        if (g.Route == SchrijfRoute.Exchange)
        {
            var adres = Adres(data, wie);
            if (adres is null)
            {
                return PlanUitkomst.Fout($"{wie.Naam} heeft geen e-mailadres.");
            }

            return PlanUitkomst.Van(new[]
            {
                new Wijziging
                {
                    Systeem = Systeem.Exchange,
                    Actie = Actie.ExchangeGroepBeheerder,
                    Doel = ExoIdentiteit(g),
                    DoelNaam = g.Naam,
                    Parameters = P((toevoegen ? "Toevoegen" : "Verwijderen", adres)),
                    Omschrijving = toevoegen ? $"{wie.Naam} beheerder maken" : $"{wie.Naam} als beheerder weghalen",
                    NieuweWaarde = wie.Naam,
                },
            });
        }

        if (g.Cloud is not null)
        {
            var id = EntraId(data, wie);
            if (id is null)
            {
                return PlanUitkomst.Fout($"{wie.Naam} bestaat niet in Entra.");
            }

            return PlanUitkomst.Van(new[]
            {
                new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = toevoegen ? Actie.GraphEigenaarToevoegen : Actie.GraphEigenaarVerwijderen,
                    Doel = g.Cloud.Id,
                    DoelNaam = g.Naam,
                    Parameters = P(("lid", id), ("lidNaam", wie.Naam)),
                    Omschrijving = toevoegen ? $"{wie.Naam} eigenaar maken" : $"{wie.Naam} als eigenaar weghalen",
                    NieuweWaarde = wie.Naam,
                },
            });
        }

        return PlanUitkomst.Fout("Deze groep heeft geen beheerders die de app kan wijzigen.");
    }

    // ---------------------------------------------------------------- hulpfuncties (ook voor de postvakplannen)

    internal static string ExoIdentiteit(Groep g) => FirstNonEmpty(g.Exo?.Smtp, g.Mail, g.Exo?.Alias, g.Naam);

    /// <summary>DN van een gebruiker of groep in AD.</summary>
    internal static string? AdDn(InventarisData data, ObjectRef r) => r.Soort switch
    {
        ObjectSoort.Gebruiker => data.ZoekPersoon(r.Sleutel)?.Ad?.DistinguishedName,
        ObjectSoort.Groep => data.Index.Groep(r.Sleutel)?.Ad?.DistinguishedName,
        _ => null,
    };

    /// <summary>Entra-id van een gebruiker, groep, postvak of room (het account achter het postvak).</summary>
    internal static string? EntraId(InventarisData data, ObjectRef r) => r.Soort switch
    {
        ObjectSoort.Gebruiker => data.ZoekPersoon(r.Sleutel)?.Entra?.Id,
        ObjectSoort.Groep => data.Index.Groep(r.Sleutel)?.Cloud?.Id,
        ObjectSoort.Postvak or ObjectSoort.Room => data.EntraGebruikers.FirstOrDefault(e =>
            string.Equals(e.UserPrincipalName, r.Sleutel, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Mail, r.Sleutel, StringComparison.OrdinalIgnoreCase))?.Id,
        _ => null,
    };

    /// <summary>E-mailadres (identiteit voor Exchange).</summary>
    internal static string? Adres(InventarisData data, ObjectRef r)
    {
        var adres = r.Soort switch
        {
            ObjectSoort.Gebruiker when data.ZoekPersoon(r.Sleutel) is { } p => FirstNonEmpty(p.Entra?.Mail, p.Ad?.EmailAddress, p.Upn),
            ObjectSoort.Groep => data.Index.Groep(r.Sleutel)?.Mail,
            ObjectSoort.Postvak or ObjectSoort.Room => r.Sleutel,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(adres) ? null : adres;
    }

    /// <summary>De vorige waarden van enkel de kenmerken die in het plan veranderen (voor ongedaan maken).</summary>
    internal static Dictionary<string, string> Vorig(IReadOnlyDictionary<string, string> nieuw, params (string Naam, string Waarde)[] huidig)
        => huidig.Where(h => nieuw.ContainsKey(h.Naam)).ToDictionary(h => h.Naam, h => h.Waarde ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    internal static Dictionary<string, string> P(params (string Naam, string Waarde)[] paren)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, w) in paren)
        {
            d[n] = w;
        }

        return d;
    }

    /// <summary>De nieuwe waarde als die (getrimd) verschilt van de huidige, anders null.</summary>
    internal static string? Nieuw(string huidig, string? gewenst)
    {
        if (gewenst is null)
        {
            return null;
        }

        var g = gewenst.Trim();
        return string.Equals(huidig.Trim(), g, StringComparison.Ordinal) ? null : g;
    }

    private static string Tekst(IReadOnlyDictionary<string, string> p) => string.Join(", ", p.Select(kv => $"{kv.Key}={kv.Value}"));

    private static string Tekst((string Naam, string Waarde) a, (string Naam, string Waarde) b, IReadOnlyDictionary<string, string> gewijzigd)
        => string.Join(", ", new[] { a, b }.Where(x => gewijzigd.ContainsKey(x.Naam)).Select(x => $"{x.Naam}={x.Waarde}"));

    internal static string FirstNonEmpty(params string?[] w) => w.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
}
