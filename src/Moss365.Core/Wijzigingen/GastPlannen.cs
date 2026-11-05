using System.Net.Mail;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Gastgebruikers (B2B) uitnodigen en verwijderen. Verwijderen kan enkel voor gasten: een medewerker gaat via uitstroom
/// en wordt nooit verwijderd. Een verwijderde gast staat nog 30 dagen bij de verwijderde gebruikers in Entra.
/// </summary>
public static class GastPlannen
{
    public static PlanUitkomst Uitnodigen(InventarisData d, string adres, string naam, bool bericht)
    {
        var fouten = new List<string>();
        adres = adres.Trim();
        if (!MailAddress.TryCreate(adres, out var mail) || mail.Address != adres)
        {
            fouten.Add($"'{adres}' is geen geldig e-mailadres.");
        }

        if (naam.Trim().Length == 0)
        {
            fouten.Add("Vul een naam in.");
        }

        // Een gast heeft als UPN adres_domein#EXT#@tenant; zijn mail is het adres zelf
        if (fouten.Count == 0 && d.EntraGebruikers.FirstOrDefault(e => string.Equals(e.Mail, adres, StringComparison.OrdinalIgnoreCase)
                                                                       || string.Equals(e.UserPrincipalName, adres, StringComparison.OrdinalIgnoreCase)) is { } bestaat)
        {
            fouten.Add($"{adres} bestaat al in Entra ({bestaat.DisplayName}{(bestaat.IsGast ? ", gast" : string.Empty)}).");
        }

        if (fouten.Count > 0)
        {
            return PlanUitkomst.Van(Array.Empty<Wijziging>(), fouten);
        }

        return PlanUitkomst.Van(new[]
        {
            new Wijziging
            {
                Systeem = Systeem.Graph,
                Actie = Actie.GraphGastUitnodigen,
                Doel = adres,
                DoelNaam = naam.Trim(),
                Parameters = GroepPlannen.P(("naam", naam.Trim()), ("bericht", bericht ? "True" : "False")),
                Omschrijving = bericht ? "Gast uitnodigen (met uitnodiging per e-mail)" : "Gast uitnodigen (zonder e-mail; deel zelf de link https://myapps.microsoft.com)",
                NieuweWaarde = adres,
            },
        });
    }

    public static PlanUitkomst Verwijderen(InventarisData d, IEnumerable<Persoon> gasten)
    {
        var plan = new List<Wijziging>();
        var fouten = new List<string>();
        foreach (var p in gasten)
        {
            if (p.Entra is not { IsGast: true } || p.Ad is not null)
            {
                fouten.Add($"{p.Naam} is geen gastaccount en wordt niet verwijderd (gebruik Uitstroom).");
                continue;
            }

            plan.Add(new Wijziging
            {
                Systeem = Systeem.Graph,
                Actie = Actie.GraphGastVerwijderen,
                Doel = p.Entra.Id,
                DoelNaam = p.Naam,
                Omschrijving = "Gastaccount verwijderen (30 dagen terug te zetten in Entra)",
                HuidigeWaarde = p.Entra.Mail.Length > 0 ? p.Entra.Mail : p.Entra.UserPrincipalName,
                NieuweWaarde = "(verwijderd)",
            });
        }

        return PlanUitkomst.Van(plan, fouten);
    }
}
