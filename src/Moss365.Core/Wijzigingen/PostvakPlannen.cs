using Moss365.Core.Inventaris;
using static Moss365.Core.Wijzigingen.GroepPlannen;

namespace Moss365.Core.Wijzigingen;

/// <summary>Gewenste instellingen van een postvak; null = niet wijzigen. Een leeg doorstuuradres zet doorsturen uit.</summary>
public sealed class PostvakGewenst
{
    public string? Weergavenaam { get; set; }
    public bool? Verborgen { get; set; }
    public string? DoorsturenNaar { get; set; }
    public bool? DoorsturenEnBewaren { get; set; }

    /// <summary>"Enabled" of "Disabled"; null = niet wijzigen.</summary>
    public string? AutoAntwoord { get; set; }

    public string? AutoAntwoordIntern { get; set; }
    public string? AutoAntwoordExtern { get; set; }
}

/// <summary>Een nieuw gedeeld postvak met zijn toegangsgroep.</summary>
public sealed class NieuwPostvakAanvraag
{
    public string Weergavenaam { get; set; } = string.Empty;

    /// <summary>Deel van het adres voor de @, bv. "info" of "jeugddienst".</summary>
    public string LokaalDeel { get; set; } = string.Empty;

    public string Domein { get; set; } = string.Empty;
    public List<ObjectRef> Leden { get; set; } = new();
}

/// <summary>
/// Plannen voor gedeelde postvakken. Toegang hoort via de groep SG-SM-&lt;naam&gt;-Full te lopen: die groep krijgt
/// FullAccess en SendAs, de mensen worden lid van de groep (zie docs/conventies.md).
/// </summary>
public static class PostvakPlannen
{
    /// <summary>
    /// Aanmelden met het account van een gedeeld postvak blokkeren of toestaan. Het postvak zelf blijft werken; enkel
    /// aanmelden met het account (wachtwoord) kan niet meer. Een gesynchroniseerd account wordt in AD uitgeschakeld (Entra
    /// volgt na de synchronisatie), een cloudaccount in Entra (accountEnabled). Ongedaan maken kan via het logboek.
    /// </summary>
    public static PlanUitkomst Aanmelden(InventarisData d, Postvak postvak, bool toestaan)
    {
        if (d.Index.AccountVan(postvak.Smtp, postvak.Alias) is not { } account)
        {
            return PlanUitkomst.Van(Array.Empty<Wijziging>(), new[] { $"Het account van {postvak.DisplayName} staat niet in de geladen gegevens (AD of Entra). Laad live." });
        }

        var waarschuwing = toestaan
            ? "Microsoft raadt aan om aanmelden met een gedeeld postvak te blokkeren: niemand hoort zich ermee aan te melden. Gebruik de toegangsgroep (SG-SM-...-Full)."
            : Analyse.Statistiek.LaatsteAanmelding(account) is { } laatst && (DateTime.Now - laatst).TotalDays <= 90
                ? $"Met dit account werd aangemeld op {laatst:dd/MM/yyyy}: gebruikt iemand het nog (bv. een scanner of toestel)?"
                : null;
        var wijziging = account.Ad is { } ad
            ? new Wijziging
            {
                Systeem = Systeem.ActiveDirectory,
                Actie = toestaan ? Actie.AdAccountInschakelen : Actie.AdAccountUitschakelen,
                Doel = ad.DistinguishedName,
                DoelNaam = postvak.DisplayName,
                Omschrijving = toestaan ? "Aanmelden toestaan: AD-account van het postvak inschakelen" : "Aanmelden blokkeren: AD-account van het postvak uitschakelen (het postvak blijft werken)",
                HuidigeWaarde = ad.Enabled ? "kan aanmelden" : "aanmelden geblokkeerd",
                NieuweWaarde = toestaan ? "kan aanmelden" : "aanmelden geblokkeerd",
                Waarschuwing = waarschuwing,
            }
            : account.Entra is { } entra
                ? new Wijziging
                {
                    Systeem = Systeem.Graph,
                    Actie = Actie.GraphKenmerkenZetten,
                    Doel = entra.Id,
                    DoelNaam = postvak.DisplayName,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = toestaan ? "true" : "false" },
                    Vorige = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = entra.AccountEnabled ? "true" : "false" },
                    Omschrijving = toestaan ? "Aanmelden toestaan (Entra)" : "Aanmelden blokkeren in Entra (het postvak blijft werken)",
                    HuidigeWaarde = entra.AccountEnabled ? "kan aanmelden" : "aanmelden geblokkeerd",
                    NieuweWaarde = toestaan ? "kan aanmelden" : "aanmelden geblokkeerd",
                    Waarschuwing = waarschuwing,
                }
                : null;
        if (wijziging is null)
        {
            return PlanUitkomst.Van(Array.Empty<Wijziging>(), new[] { "Geen account gevonden." });
        }

        return PlanUitkomst.Van(new[] { wijziging }, Array.Empty<string>());
    }

    public static readonly string[] Rechten = { "FullAccess", "SendAs" };

    /// <summary>Instellingen van het postvak: alleen wat verschilt met de gekende waarde.</summary>
    public static PlanUitkomst Instellingen(Postvak p, PostvakGewenst g)
    {
        var plan = new List<Wijziging>();
        var fouten = new List<string>();
        var instellen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oud = new List<string>();
        var nieuw = new List<string>();
        var vorige = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Zet(string naam, string huidig, string? waarde)
        {
            if (waarde is not null && !string.Equals(huidig, waarde, StringComparison.OrdinalIgnoreCase))
            {
                instellen[naam] = waarde;

                // Een onbekende ja/nee-waarde kan niet terug (leeg is daar geen geldige waarde)
                if (huidig.Length > 0 || naam is "DisplayName" or "ForwardingSmtpAddress")
                {
                    vorige[naam] = huidig;
                }

                oud.Add($"{naam}={(huidig.Length == 0 ? "(leeg)" : huidig)}");
                nieuw.Add($"{naam}={(waarde.Length == 0 ? "(leeg)" : waarde)}");
            }
        }

        Zet("DisplayName", p.DisplayName, Nieuw(p.DisplayName, g.Weergavenaam));
        Zet("HiddenFromAddressListsEnabled", p.Verborgen ? "True" : "False", g.Verborgen is null ? null : g.Verborgen.Value ? "True" : "False");
        if (g.DoorsturenNaar is not null)
        {
            var naar = g.DoorsturenNaar.Trim();
            if (naar.Length > 0 && !naar.Contains('@'))
            {
                fouten.Add($"'{naar}' is geen e-mailadres.");
            }
            else
            {
                Zet("ForwardingSmtpAddress", p.DoorsturenNaar, naar);
            }
        }

        Zet("DeliverToMailboxAndForward", p.DoorsturenEnBewaren is null ? string.Empty : p.DoorsturenEnBewaren.Value ? "True" : "False",
            g.DoorsturenEnBewaren is null ? null : g.DoorsturenEnBewaren.Value ? "True" : "False");

        if (instellen.Count > 0)
        {
            plan.Add(new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangePostvakInstellen,
                Doel = p.Smtp,
                DoelNaam = p.DisplayName,
                Parameters = instellen,
                Omschrijving = "Instellingen van het postvak aanpassen",
                Vorige = vorige,
                HuidigeWaarde = string.Join(", ", oud),
                NieuweWaarde = string.Join(", ", nieuw),
                Waarschuwing = instellen.TryGetValue("ForwardingSmtpAddress", out var f) && f.Length > 0 && !IsEigenDomein(f, p.Smtp)
                    ? "Doorsturen naar een ander domein: controleer of dat mag." : null,
            });
        }

        if (g.AutoAntwoord is { } stand)
        {
            if (stand is not ("Enabled" or "Disabled"))
            {
                fouten.Add("Automatisch antwoord kan enkel aan of uit.");
            }
            else
            {
                var ap = P(("AutoReplyState", stand));
                if (stand == "Enabled")
                {
                    ap["InternalMessage"] = g.AutoAntwoordIntern ?? p.AutoAntwoordIntern;
                    ap["ExternalMessage"] = g.AutoAntwoordExtern ?? p.AutoAntwoordExtern;
                    if (ap["InternalMessage"].Trim().Length == 0)
                    {
                        fouten.Add("Vul een tekst in voor het automatisch antwoord.");
                    }
                }

                var veranderd = !string.Equals(p.AutoAntwoord, stand, StringComparison.OrdinalIgnoreCase)
                                || (stand == "Enabled" && (ap["InternalMessage"] != p.AutoAntwoordIntern || ap["ExternalMessage"] != p.AutoAntwoordExtern));
                if (veranderd && fouten.Count == 0)
                {
                    plan.Add(new Wijziging
                    {
                        Systeem = Systeem.Exchange,
                        Actie = Actie.ExchangeAutoAntwoord,
                        Doel = p.Smtp,
                        DoelNaam = p.DisplayName,
                        Parameters = ap,
                        Omschrijving = stand == "Enabled" ? "Automatisch antwoord aanzetten" : "Automatisch antwoord uitzetten",
                        Vorige = p.AutoAntwoord.Length == 0 ? null : new(StringComparer.OrdinalIgnoreCase)
                        {
                            ["AutoReplyState"] = p.AutoAntwoord, ["InternalMessage"] = p.AutoAntwoordIntern, ["ExternalMessage"] = p.AutoAntwoordExtern,
                        },
                        HuidigeWaarde = p.AutoAntwoord.Length == 0 ? "(onbekend, niet live geladen)" : p.AutoAntwoord,
                        NieuweWaarde = stand,
                    });
                }
            }
        }

        return PlanUitkomst.Van(plan, fouten);
    }

    /// <summary>
    /// Rechtstreekse rechten naar de toegangsgroep verhuizen: wie rechtstreeks FullAccess of SendAs heeft, wordt lid van
    /// de groep en verliest het rechtstreekse recht. De groep krijgt eerst zelf beide rechten als die ontbreken.
    /// </summary>
    public static PlanUitkomst DirecteRechtenOpruimen(InventarisData data, Postvak p)
    {
        var index = data.Index;
        var groep = index.ToegangsgroepVan(p);
        if (groep is null)
        {
            return PlanUitkomst.Fout($"Er is nog geen toegangsgroep ({ObjectIndex.VerwachteToegangsgroep(p)}). Maak die eerst aan.");
        }

        var rechten = index.RechtenOp(p);
        var plan = new List<Wijziging>();
        plan.AddRange(GroepRechtenAanvullen(p, groep, rechten, null));

        var leden = index.LedenVan(groep).Select(r => r.Object).ToList();
        var toegevoegd = new Dictionary<string, Wijziging>(StringComparer.OrdinalIgnoreCase);
        var fouten = new List<string>();
        foreach (var r in rechten.Where(r => r.Direct))
        {
            Guid? na = null;
            if (r.Wie.Soort == ObjectSoort.Onbekend)
            {
                // Bv. een SID van een verwijderd account: enkel opruimen, en niet standaard aangevinkt
                plan.Add(RechtWeg(p, r, null, $"Onbekende rechthebbende {r.Trustee} (verwijderd account?)", geselecteerd: false));
                continue;
            }

            var alLid = leden.Any(l => l.IsZelfde(r.Wie));
            if (!alLid)
            {
                if (!toegevoegd.TryGetValue(r.Wie.Sleutel, out var lidWordt))
                {
                    var uitkomst = GroepPlannen.LidToevoegen(data, groep, r.Wie);
                    if (uitkomst.Plan.Count == 0)
                    {
                        fouten.AddRange(uitkomst.Fouten);
                        continue;
                    }

                    lidWordt = uitkomst.Plan[0];
                    toegevoegd[r.Wie.Sleutel] = lidWordt;
                    plan.Add(lidWordt);
                }

                na = lidWordt.Id;
            }

            plan.Add(RechtWeg(p, r, na, $"Rechtstreeks {r.Recht} van {r.Wie.Naam} weghalen (loopt voortaan via {groep.Naam})", geselecteerd: true));
        }

        if (plan.Count == 0 && fouten.Count == 0)
        {
            fouten.Add("Er zijn geen rechtstreekse rechten; alle toegang loopt al via de groep.");
        }

        return PlanUitkomst.Van(plan, fouten);
    }

    /// <summary>De toegangsgroep SG-SM-&lt;naam&gt;-Full aanmaken en FullAccess en SendAs geven.</summary>
    public static PlanUitkomst ToegangsgroepAanmaken(InventarisData data, Postvak p)
    {
        if (data.Index.ToegangsgroepVan(p) is { } bestaand)
        {
            return PlanUitkomst.Fout($"{bestaand.Naam} bestaat al en geeft toegang tot dit postvak.");
        }

        var naam = ObjectIndex.VerwachteToegangsgroep(p);
        var aanmaken = GroepAanmaken(naam, Domein(p.Smtp));
        return PlanUitkomst.Van(new[] { aanmaken }.Concat(GroepRechtenAanvullen(p, null, Array.Empty<RechtRegel>(), aanmaken, naam)));
    }

    /// <summary>Nieuw gedeeld postvak, de toegangsgroep, de rechten van de groep en de eerste leden.</summary>
    public static PlanUitkomst NieuwPostvak(InventarisData data, NieuwPostvakAanvraag a)
    {
        var fouten = new List<string>();
        var lokaal = a.LokaalDeel.Trim().ToLowerInvariant();
        var domein = a.Domein.Trim().TrimStart('@').ToLowerInvariant();
        if (a.Weergavenaam.Trim().Length == 0) fouten.Add("Vul een weergavenaam in.");
        if (lokaal.Length == 0 || lokaal.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) fouten.Add("Het adres mag enkel letters, cijfers, punt, streepje of underscore bevatten.");
        if (domein.Length == 0) fouten.Add("Kies een domein.");
        var smtp = $"{lokaal}@{domein}";
        if (fouten.Count == 0 && data.Index.Zoek(smtp) is { } bestaat)
        {
            fouten.Add($"{smtp} is al in gebruik ({bestaat.SoortTekst} {bestaat.Naam}).");
        }

        if (fouten.Count > 0)
        {
            return PlanUitkomst.Van(Array.Empty<Wijziging>(), fouten);
        }

        var postvak = new Wijziging
        {
            Systeem = Systeem.Exchange,
            Actie = Actie.ExchangeGedeeldPostvakAanmaken,
            Doel = smtp,
            DoelNaam = a.Weergavenaam.Trim(),
            Parameters = P(("Name", a.Weergavenaam.Trim()), ("DisplayName", a.Weergavenaam.Trim()), ("Alias", lokaal)),
            Omschrijving = "Gedeeld postvak aanmaken (zonder licentie, aanmelden geblokkeerd)",
            NieuweWaarde = smtp,
        };
        var groepNaam = $"SG-SM-{lokaal}-Full";
        // Keten: postvak, dan de groep, dan de rechten en leden. Mislukt een stap, dan worden de volgende overgeslagen.
        var groep = GroepAanmaken(groepNaam, domein, postvak.Id);
        var pv = new Postvak { Smtp = smtp, DisplayName = a.Weergavenaam.Trim(), Type = "SharedMailbox" };
        var plan = new List<Wijziging> { postvak, groep };
        plan.AddRange(GroepRechtenAanvullen(pv, null, Array.Empty<RechtRegel>(), groep, groepNaam));
        foreach (var lid in a.Leden.DistinctBy(l => l.Sleutel, StringComparer.OrdinalIgnoreCase))
        {
            var adres = Adres(data, lid);
            if (adres is null)
            {
                fouten.Add($"{lid.Naam} heeft geen e-mailadres en kan geen lid worden.");
                continue;
            }

            plan.Add(new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangeGroepLidToevoegen,
                Doel = adres,
                DoelNaam = lid.Naam,
                Parameters = P(("groep", groep.Doel), ("groepNaam", groepNaam)),
                Omschrijving = $"Lid maken van {groepNaam}: toegang tot {a.Weergavenaam.Trim()}",
                AfhankelijkVan = groep.Id,
            });
        }

        return PlanUitkomst.Van(plan, fouten);
    }

    // ---------------------------------------------------------------- hulpfuncties

    private static Wijziging GroepAanmaken(string naam, string domein, Guid? afhankelijkVan = null) => new()
    {
        AfhankelijkVan = afhankelijkVan,
        Systeem = Systeem.Exchange,
        Actie = Actie.ExchangeGroepAanmaken,
        Doel = $"{naam.ToLowerInvariant()}@{domein}",
        DoelNaam = naam,
        Parameters = P(("Name", naam), ("DisplayName", naam), ("Alias", naam)),
        Omschrijving = $"Mail-beveiligingsgroep {naam} aanmaken (lidmaatschap gesloten)",
        NieuweWaarde = $"{naam.ToLowerInvariant()}@{domein}",
    };

    /// <summary>FullAccess en SendAs voor de groep, voor zover die er nog niet zijn.</summary>
    private static IEnumerable<Wijziging> GroepRechtenAanvullen(Postvak p, Groep? groep, IReadOnlyList<RechtRegel> rechten, Wijziging? groepAanmaken, string? groepNaam = null)
    {
        var naam = groep?.Naam ?? groepNaam ?? string.Empty;
        var identiteit = groep is not null ? ExoIdentiteit(groep) : groepAanmaken!.Doel;
        foreach (var recht in Rechten)
        {
            if (groep is not null && rechten.Any(r => r.Recht.Equals(recht, StringComparison.OrdinalIgnoreCase) && r.Wie.Sleutel.Equals(groep.Sleutel, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return new Wijziging
            {
                Systeem = Systeem.Exchange,
                Actie = Actie.ExchangePostvakRechtToevoegen,
                Doel = p.Smtp,
                DoelNaam = p.DisplayName,
                Parameters = P(("Recht", recht), ("Trustee", identiteit)),
                Omschrijving = $"{naam} krijgt {recht} op het postvak",
                NieuweWaarde = $"{recht} voor {naam}",
                AfhankelijkVan = groepAanmaken?.Id,
            };
        }
    }

    private static Wijziging RechtWeg(Postvak p, RechtRegel r, Guid? na, string omschrijving, bool geselecteerd) => new()
    {
        Systeem = Systeem.Exchange,
        Actie = Actie.ExchangePostvakRechtVerwijderen,
        Doel = p.Smtp,
        DoelNaam = p.DisplayName,
        Parameters = P(("Recht", r.Recht), ("Trustee", r.Trustee)),
        Omschrijving = omschrijving,
        HuidigeWaarde = $"{r.Recht} voor {r.Wie.Naam}",
        NieuweWaarde = "(weg)",
        AfhankelijkVan = na,
        Geselecteerd = geselecteerd,
    };

    private static string Domein(string smtp) => smtp.Contains('@') ? smtp[(smtp.IndexOf('@') + 1)..] : string.Empty;

    private static bool IsEigenDomein(string adres, string postvakSmtp)
        => string.Equals(Domein(adres), Domein(postvakSmtp), StringComparison.OrdinalIgnoreCase);
}
