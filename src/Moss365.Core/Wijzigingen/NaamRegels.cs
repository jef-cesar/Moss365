using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Moss365.Core.Wijzigingen;

/// <summary>Naamgeving voor nieuwe accounts volgens de conventie voornaam.achternaam (kleine letters, zonder accenten).</summary>
public static class NaamRegels
{
    /// <summary>De maximale lengte van sAMAccountName voor gebruikers.</summary>
    public const int MaxSam = 20;

    /// <summary>"Van den Broeck" naar "vandenbroeck", "D'Hondt" naar "dhondt", "Beliën" naar "belien".</summary>
    public static string Normaliseer(string? naam)
    {
        if (string.IsNullOrWhiteSpace(naam))
        {
            return string.Empty;
        }

        var zonderAccenten = new StringBuilder();
        foreach (var c in naam.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                zonderAccenten.Append(c);
            }
        }

        var uit = new StringBuilder();
        foreach (var c in zonderAccenten.ToString().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                uit.Append(c);
            }
        }

        return uit.ToString().Trim('-');
    }

    /// <summary>Het deel voor de @: voornaam.achternaam.</summary>
    public static string LokaalDeel(string voornaam, string achternaam)
    {
        var v = Normaliseer(voornaam);
        var a = Normaliseer(achternaam);
        return v.Length == 0 ? a : a.Length == 0 ? v : $"{v}.{a}";
    }

    /// <summary>sAMAccountName: het lokale deel, ingekort tot 20 tekens.</summary>
    public static string Sam(string lokaalDeel)
        => lokaalDeel.Length <= MaxSam ? lokaalDeel : lokaalDeel[..MaxSam].TrimEnd('.', '-');

    /// <summary>sAMAccountName met een volgnummer, zo nodig ingekort zodat het nummer binnen 20 tekens past.</summary>
    public static string SamMetNummer(string lokaalDeel, int nummer)
    {
        var suffix = nummer.ToString(CultureInfo.InvariantCulture);
        var basis = lokaalDeel.Length + suffix.Length > MaxSam ? lokaalDeel[..(MaxSam - suffix.Length)].TrimEnd('.', '-') : lokaalDeel;
        return basis + suffix;
    }

    public static string WeergaveNaam(string voornaam, string achternaam)
        => string.Join(' ', new[] { voornaam.Trim(), achternaam.Trim() }.Where(s => s.Length > 0));

    /// <summary>
    /// Een startwachtwoord van 16 tekens met hoofdletters, kleine letters, cijfers en tekens; zonder verwarrende tekens
    /// (0/O, 1/l/I). De gebruiker moet het bij de eerste aanmelding wijzigen.
    /// </summary>
    public static string Startwachtwoord()
    {
        const string groot = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string klein = "abcdefghijkmnopqrstuvwxyz";
        const string cijfers = "23456789";
        const string tekens = "!#$%*+-=?@";
        var alles = groot + klein + cijfers + tekens;

        var tekensLijst = new List<char>
        {
            groot[RandomNumberGenerator.GetInt32(groot.Length)],
            klein[RandomNumberGenerator.GetInt32(klein.Length)],
            cijfers[RandomNumberGenerator.GetInt32(cijfers.Length)],
            tekens[RandomNumberGenerator.GetInt32(tekens.Length)],
        };
        while (tekensLijst.Count < 16)
        {
            tekensLijst.Add(alles[RandomNumberGenerator.GetInt32(alles.Length)]);
        }

        // Fisher-Yates met een cryptografische generator
        for (var i = tekensLijst.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (tekensLijst[i], tekensLijst[j]) = (tekensLijst[j], tekensLijst[i]);
        }

        return new string(tekensLijst.ToArray());
    }
}
