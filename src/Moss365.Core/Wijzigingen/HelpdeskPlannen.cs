using Moss365.Core.Analyse;
using Moss365.Core.Inventaris;

namespace Moss365.Core.Wijzigingen;

/// <summary>
/// Dagelijkse helpdeskacties op een AD-account: wachtwoord opnieuw instellen, ontgrendelen en weer inschakelen. Het
/// wachtwoord zit enkel in <see cref="Wijziging.Geheim"/> en komt nooit in het logboek.
/// </summary>
public static class HelpdeskPlannen
{
    public const int MinimumLengte = 12;

    public static PlanUitkomst WachtwoordInstellen(InventarisData d, Persoon p, string wachtwoord, bool wijzigenBijAanmelding, bool ontgrendelen)
    {
        if (p.Ad is not { } ad)
        {
            return PlanUitkomst.Van(Array.Empty<Wijziging>(), new[] { GeenAd(p) });
        }

        var fouten = new List<string>();
        if (wachtwoord.Length < MinimumLengte)
        {
            fouten.Add($"Het wachtwoord moet minstens {MinimumLengte} tekens hebben.");
        }

        if (wachtwoord.Trim() != wachtwoord)
        {
            fouten.Add("Het wachtwoord begint of eindigt met een spatie.");
        }

        if (fouten.Count > 0)
        {
            return PlanUitkomst.Van(Array.Empty<Wijziging>(), fouten);
        }

        var plan = new List<Wijziging>
        {
            new()
            {
                Systeem = Systeem.ActiveDirectory,
                Actie = Actie.AdWachtwoordInstellen,
                Doel = ad.DistinguishedName,
                DoelNaam = p.Naam,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["wijzigenBijAanmelding"] = wijzigenBijAanmelding ? "True" : "False" },
                Omschrijving = "Nieuw wachtwoord instellen" + (wijzigenBijAanmelding ? " (wijzigen bij de volgende aanmelding)" : string.Empty),
                Geheim = wachtwoord,
                Waarschuwing = Waarschuwing(d, p),
            },
        };

        if (ontgrendelen)
        {
            plan.Add(Ontgrendel(p, ad));
        }

        if (!ad.Enabled)
        {
            plan.Add(Inschakel(p, ad, geselecteerd: false));
        }

        return PlanUitkomst.Van(plan, Array.Empty<string>());
    }

    public static PlanUitkomst Ontgrendelen(Persoon p)
        => p.Ad is { } ad
            ? PlanUitkomst.Van(new[] { Ontgrendel(p, ad) }, Array.Empty<string>())
            : PlanUitkomst.Van(Array.Empty<Wijziging>(), new[] { GeenAd(p) });

    public static PlanUitkomst Inschakelen(Persoon p)
        => p.Ad is { } ad
            ? PlanUitkomst.Van(new[] { Inschakel(p, ad, geselecteerd: true) }, Array.Empty<string>())
            : PlanUitkomst.Van(Array.Empty<Wijziging>(), new[] { GeenAd(p) });

    private static Wijziging Ontgrendel(Persoon p, AdGebruiker ad) => new()
    {
        Systeem = Systeem.ActiveDirectory,
        Actie = Actie.AdAccountOntgrendelen,
        Doel = ad.DistinguishedName,
        DoelNaam = p.Naam,
        Omschrijving = "Account ontgrendelen",
        HuidigeWaarde = ad.LockedOut ? "vergrendeld" : "niet vergrendeld (volgens de geladen gegevens)",
        NieuweWaarde = "ontgrendeld",
    };

    private static Wijziging Inschakel(Persoon p, AdGebruiker ad, bool geselecteerd) => new()
    {
        Systeem = Systeem.ActiveDirectory,
        Actie = Actie.AdAccountInschakelen,
        Doel = ad.DistinguishedName,
        DoelNaam = p.Naam,
        Omschrijving = "AD-account inschakelen",
        HuidigeWaarde = ad.Enabled ? "al ingeschakeld" : "uitgeschakeld",
        NieuweWaarde = "ingeschakeld",
        Waarschuwing = ad.OuPad.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            ? "Dit account staat in de OU van uitgestroomde gebruikers. Is de persoon terug in dienst?"
            : null,
        Geselecteerd = geselecteerd,
    };

    private static string? Waarschuwing(InventarisData d, Persoon p)
        => d.AdGroepenVan(p).Where(Spiegelgebruiker.IsBevoorrecht).Select(g => g.Name).ToList() is { Count: > 0 } groepen
            ? $"Bevoorrecht account (lid van {string.Join(", ", groepen)}): controleer extra goed wie dit vraagt."
            : null;

    private static string GeenAd(Persoon p)
        => p.Entra is not null
            ? $"{p.Naam} heeft geen AD-account. Het wachtwoord van een cloudaccount stel je in via het Entra-portaal."
            : $"{p.Naam} heeft geen AD-account.";
}
