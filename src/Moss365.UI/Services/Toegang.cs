using Moss365.Core.Rechten;

namespace Moss365.UI.Services;

/// <summary>Welk recht een pagina vraagt (eerste plaats van de drie: pagina's, knoppen, de motor).</summary>
public static class Toegang
{
    /// <summary>Het recht voor een adres (zonder zoekterm), of null als iedereen er mag komen.</summary>
    public static Recht? VoorPad(string pad)
    {
        var p = Openstaand.Normaliseer(pad).ToLowerInvariant();
        var eerste = p.Split('/')[0];
        return eerste switch
        {
            // Verbinden en de controle van deze pc: iedereen (aanmelden gebeurt per pc)
            "" or "verbindingen" or "controle" => null,
            "groepen" when p == "groepen/nieuw" => Recht.Beheren,
            "statistieken" or "gebruikers" or "gebruiker" or "gasten" or "groepen" or "groep" or "postvak" or "room" or "rooms" or "vergelijk" => Recht.Bekijken,
            "postvakken" => p == "postvakken/nieuw" ? Recht.Beheren : Recht.Bekijken,
            "beveiliging" or "conditional-access" => Recht.Beveiliging,
            "spiegel" => Recht.SpiegelVergelijken,
            "nieuw" or "uitstroom" or "gepland" or "opvolging" => Recht.Acties,
            "opkuis" => Recht.Beheren,
            "scripts" => Recht.Scripts,
            "logboek" => Recht.LogboekEigen,
            "organisatie" => Recht.Instellingen,
            _ => Recht.Bekijken,
        };
    }
}
