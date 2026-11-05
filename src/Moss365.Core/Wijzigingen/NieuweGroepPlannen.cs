using Moss365.Core.Analyse;
using Moss365.Core.Configuratie;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>Welke groep je aanmaakt; dat bepaalt ook langs welke weg.</summary>
public enum NieuweGroepSoort
{
    /// <summary>AD-beveiligingsgroep in de groepen-OU (gesynchroniseerd naar Entra).</summary>
    AdBeveiliging,

    /// <summary>Beveiligingsgroep enkel in de cloud (Graph).</summary>
    CloudBeveiliging,

    /// <summary>Microsoft 365-groep: gedeelde mailbox, agenda, Teams mogelijk (Graph).</summary>
    Microsoft365,

    /// <summary>Mail-beveiligingsgroep (Exchange), bv. voor toegang tot een gedeeld postvak.</summary>
    MailBeveiliging,

    /// <summary>Distributielijst (Exchange).</summary>
    Distributie,
}

public sealed class NieuweGroepAanvraag
{
    public NieuweGroepSoort Soort { get; set; } = NieuweGroepSoort.AdBeveiliging;
    public string Naam { get; set; } = string.Empty;
    public string Omschrijving { get; set; } = string.Empty;

    /// <summary>E-mailadres (enkel Microsoft 365-groep, mail-beveiligingsgroep en distributielijst).</summary>
    public string Mail { get; set; } = string.Empty;

    /// <summary>AD: Global (standaard) of Universal.</summary>
    public string Bereik { get; set; } = "Global";

    /// <summary>Sleutel van de gebruiker die beheerder (AD, Exchange) of eigenaar (cloud) wordt; leeg = niemand.</summary>
    public string Beheerder { get; set; } = string.Empty;

    /// <summary>Sleutels van de eerste leden.</summary>
    public List<string> Leden { get; set; } = new();
}

/// <summary>
/// Een nieuwe groep aanmaken (0.8, #13), in één plan: aanmaken, dan beheerder of eigenaar en de eerste leden (die stappen
/// hangen af van het aanmaken). Controles: de naam en het adres bestaan nog niet, geen bevoorrechte naam, geen naam van
/// een dynamische groep, en een waarschuwing als de naam niet volgens de naamgeving is.
/// </summary>
public static class NieuweGroepPlannen
{
    // AD groupType: beveiligingsgroep (0x80000000) + Global (2) of Universal (8)
    public const int GlobalBeveiliging = unchecked((int)0x80000002);
    public const int UniversalBeveiliging = unchecked((int)0x80000008);

    /// <summary>Het voorvoegsel dat de naamgeving voor deze soort voorstelt.</summary>
    public static string Voorvoegsel(NieuweGroepSoort soort) => soort switch
    {
        NieuweGroepSoort.AdBeveiliging or NieuweGroepSoort.CloudBeveiliging => "gg_",
        NieuweGroepSoort.Microsoft365 => "WKG_",
        NieuweGroepSoort.MailBeveiliging => "SG-",
        _ => string.Empty,
    };

    public static bool MetMail(NieuweGroepSoort soort) => soort is NieuweGroepSoort.Microsoft365 or NieuweGroepSoort.MailBeveiliging or NieuweGroepSoort.Distributie;

    public static PlanUitkomst Plan(InventarisData data, OrganisatieConfig config, NieuweGroepAanvraag a)
    {
        var fouten = new List<string>();
        var waarschuwingen = new List<string>();
        var naam = a.Naam.Trim();
        var mail = a.Mail.Trim().ToLowerInvariant();

        if (naam.Length == 0)
        {
            return PlanUitkomst.Fout("Geef de groep een naam.");
        }

        if (naam.IndexOfAny(new[] { '"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>', '@' }) >= 0)
        {
            fouten.Add("De naam bevat een teken dat niet mag in een groepsnaam (bv. / \\ : ; , @).");
        }

        if (data.Index.Groepen.Any(g => string.Equals(g.Naam, naam, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(g.Ad?.SamAccountName, naam, StringComparison.OrdinalIgnoreCase)))
        {
            fouten.Add($"Er bestaat al een groep {naam}.");
        }

        if (Spiegelgebruiker.IsBevoorrecht(new AdGroep { Name = naam }))
        {
            fouten.Add($"{naam} is een bevoorrechte naam; zo'n groep maak je bewust buiten deze app.");
        }

        if (config.IsDynamischeGroep(naam))
        {
            fouten.Add($"{naam} is een dynamische groep in de organisatie: die volgt uit regels en maak je niet hier.");
        }

        var voorvoegsels = config.GroepVoorvoegsels.Count > 0 ? config.GroepVoorvoegsels : new List<string> { "gg_", "AFD_", "DNST_", "SG-", "WKG_" };
        if (!voorvoegsels.Any(v => naam.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
        {
            waarschuwingen.Add($"De naam volgt de naamgeving niet (begint niet met {string.Join(", ", voorvoegsels)}).");
        }

        if (MetMail(a.Soort))
        {
            if (!mail.Contains('@') || mail.StartsWith('@') || mail.EndsWith('@'))
            {
                fouten.Add("Geef een geldig e-mailadres.");
            }
            else if (data.Index.Groepen.Any(g => string.Equals(g.Mail, mail, StringComparison.OrdinalIgnoreCase))
                     || data.Postvakken.Any(p => string.Equals(p.Smtp, mail, StringComparison.OrdinalIgnoreCase))
                     || data.Index.AccountVan(mail) is not null)
            {
                fouten.Add($"Het adres {mail} is al in gebruik.");
            }
        }

        var beheerder = a.Beheerder.Length == 0 ? null : data.ZoekPersoon(a.Beheerder);
        var leden = a.Leden.Select(data.ZoekPersoon).Where(p => p is not null).Cast<Persoon>().ToList();

        string? ouDn = null;
        if (a.Soort == NieuweGroepSoort.AdBeveiliging)
        {
            if (string.IsNullOrWhiteSpace(config.GroepenOu))
            {
                fouten.Add("Stel eerst bij Organisatie de OU voor nieuwe groepen in.");
            }
            else
            {
                ouDn = config.OuDn(config.GroepenOu, data.DomeinDn);
            }
        }

        if (fouten.Count > 0)
        {
            return new PlanUitkomst(Array.Empty<Wijziging>(), fouten);
        }

        var plan = new List<Wijziging>();
        var waarschuwing = waarschuwingen.Count == 0 ? null : string.Join(" ", waarschuwingen);
        switch (a.Soort)
        {
            case NieuweGroepSoort.AdBeveiliging:
            {
                var dn = $"CN={PlanBouwer.EscapeCn(naam)},{ouDn}";
                var p = GroepPlannen.P(("ou", ouDn!), ("cn", naam), ("sAMAccountName", naam),
                    ("groupType", (a.Bereik == "Universal" ? UniversalBeveiliging : GlobalBeveiliging).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                if (a.Omschrijving.Trim().Length > 0)
                {
                    p["description"] = a.Omschrijving.Trim();
                }

                if (beheerder?.Ad is { } b)
                {
                    p["managedBy"] = b.DistinguishedName;
                }
                else if (beheerder is not null)
                {
                    fouten.Add($"{beheerder.Naam} heeft geen AD-account en kan geen beheerder van een AD-groep zijn.");
                }

                var aanmaken = new Wijziging
                {
                    Systeem = Systeem.ActiveDirectory,
                    Actie = Actie.AdGroepAanmaken,
                    Doel = dn,
                    DoelNaam = naam,
                    Parameters = p,
                    Omschrijving = $"AD-beveiligingsgroep {naam} aanmaken ({a.Bereik}) in {config.GroepenOu}",
                    NieuweWaarde = dn,
                    Waarschuwing = waarschuwing,
                };
                plan.Add(aanmaken);
                foreach (var lid in leden)
                {
                    if (lid.Ad is null)
                    {
                        fouten.Add($"{lid.Naam} heeft geen AD-account en kan geen lid worden van een AD-groep.");
                        continue;
                    }

                    plan.Add(new Wijziging
                    {
                        Systeem = Systeem.ActiveDirectory,
                        Actie = Actie.AdGroepLidToevoegen,
                        Doel = lid.Ad.DistinguishedName,
                        DoelNaam = lid.Naam,
                        Parameters = GroepPlannen.P(("groepDn", dn), ("groep", naam)),
                        Omschrijving = $"Lid maken van de nieuwe AD-groep {naam}",
                        AfhankelijkVan = aanmaken.Id,
                    });
                }

                break;
            }

            case NieuweGroepSoort.CloudBeveiliging:
            case NieuweGroepSoort.Microsoft365:
            {
                var m365 = a.Soort == NieuweGroepSoort.Microsoft365;
                var alias = m365 ? mail.Split('@')[0] : new string(naam.Where(char.IsLetterOrDigit).ToArray());
                var zonderEntra = leden.Where(l => l.Entra is null).ToList();
                fouten.AddRange(zonderEntra.Select(l => $"{l.Naam} bestaat niet in Entra en kan geen lid worden van een cloudgroep."));
                if (beheerder is { Entra: null })
                {
                    fouten.Add($"{beheerder.Naam} bestaat niet in Entra en kan geen eigenaar zijn.");
                }

                var p = GroepPlannen.P(("displayName", naam), ("mailNickname", alias), ("soort", m365 ? "M365" : "Beveiliging"));
                if (a.Omschrijving.Trim().Length > 0)
                {
                    p["description"] = a.Omschrijving.Trim();
                }

                if (beheerder?.Entra is { } e)
                {
                    p["eigenaar"] = e.Id;
                }

                // Leden en eigenaar gaan mee in dezelfde opdracht: de groep bestaat nog niet, dus er is nog geen id
                var ledenIds = leden.Where(l => l.Entra is not null).Select(l => l.Entra!.Id).ToList();
                if (ledenIds.Count > 0)
                {
                    p["leden"] = string.Join(',', ledenIds);
                }

                if (ledenIds.Count + (p.ContainsKey("eigenaar") ? 1 : 0) > 20)
                {
                    fouten.Add("Een nieuwe cloudgroep kan bij het aanmaken maximaal 20 leden en eigenaars krijgen; voeg de rest daarna toe.");
                    break;
                }

                plan.Add(new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphGroepAanmaken,
                    Doel = m365 ? mail : alias,
                    DoelNaam = naam,
                    Parameters = p,
                    Omschrijving = (m365 ? $"Microsoft 365-groep {naam} aanmaken ({mail})" : $"Cloudbeveiligingsgroep {naam} aanmaken")
                                   + (ledenIds.Count > 0 ? $" met {ledenIds.Count} {(ledenIds.Count == 1 ? "lid" : "leden")}" : string.Empty)
                                   + (beheerder?.Entra is not null ? $", eigenaar {beheerder.Naam}" : string.Empty),
                    NieuweWaarde = naam,
                    Waarschuwing = waarschuwing,
                });
                break;
            }

            default:
            {
                var aanmaken = new Wijziging
                {
                    Systeem = Systeem.Exchange,
                    Actie = Actie.ExchangeGroepAanmaken,
                    Doel = mail,
                    DoelNaam = naam,
                    Parameters = GroepPlannen.P(("Name", naam), ("DisplayName", naam), ("Alias", mail.Split('@')[0]),
                        ("Type", a.Soort == NieuweGroepSoort.Distributie ? "Distribution" : "Security")),
                    Omschrijving = a.Soort == NieuweGroepSoort.Distributie
                        ? $"Distributielijst {naam} aanmaken ({mail})"
                        : $"Mail-beveiligingsgroep {naam} aanmaken ({mail}, lidmaatschap gesloten)",
                    NieuweWaarde = mail,
                    Waarschuwing = waarschuwing,
                };
                plan.Add(aanmaken);
                if (beheerder is not null)
                {
                    if (GroepPlannen.Adres(data, data.Index.Ref(beheerder)) is { } adres)
                    {
                        plan.Add(new Wijziging
                        {
                            Systeem = Systeem.Exchange,
                            Actie = Actie.ExchangeGroepBeheerder,
                            Doel = mail,
                            DoelNaam = naam,
                            Parameters = GroepPlannen.P(("Toevoegen", adres)),
                            Omschrijving = $"{beheerder.Naam} beheerder maken",
                            NieuweWaarde = beheerder.Naam,
                            AfhankelijkVan = aanmaken.Id,
                        });
                    }
                    else
                    {
                        fouten.Add($"{beheerder.Naam} heeft geen e-mailadres en kan geen beheerder zijn.");
                    }
                }

                foreach (var lid in leden)
                {
                    if (GroepPlannen.Adres(data, data.Index.Ref(lid)) is not { } adres)
                    {
                        fouten.Add($"{lid.Naam} heeft geen e-mailadres en kan geen lid worden van een mailgroep.");
                        continue;
                    }

                    plan.Add(new Wijziging
                    {
                        Systeem = Systeem.Exchange,
                        Actie = Actie.ExchangeGroepLidToevoegen,
                        Doel = adres,
                        DoelNaam = lid.Naam,
                        Parameters = GroepPlannen.P(("groep", mail), ("groepNaam", naam)),
                        Omschrijving = $"Lid maken van de nieuwe groep {naam} (Exchange)",
                        AfhankelijkVan = aanmaken.Id,
                    });
                }

                break;
            }
        }

        return new PlanUitkomst(plan, fouten);
    }
}
