using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Moss365.Core.Changes;

/// <summary>Naming for new accounts according to the convention givenname.surname (lower case, without
/// accents).</summary>
public static class NameRules
{
    /// <summary>The maximum length of sAMAccountName for users.</summary>
    public const int MaxSam = 20;

    /// <summary>"Van den Broeck" to "vandenbroeck", "D'Hondt" to "dhondt", "Beliën" to "belien".</summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var withoutAccents = new StringBuilder();
        foreach (var c in name.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                withoutAccents.Append(c);
            }
        }

        var output = new StringBuilder();
        foreach (var c in withoutAccents.ToString().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                output.Append(c);
            }
        }

        return output.ToString().Trim('-');
    }

    /// <summary>The part before the @: givenname.surname.</summary>
    public static string LocalPart(string givenName, string surname)
    {
        var v = Normalise(givenName);
        var a = Normalise(surname);
        return v.Length == 0 ? a : a.Length == 0 ? v : $"{v}.{a}";
    }

    /// <summary>sAMAccountName: the local part, shortened to 20 characters.</summary>
    public static string Sam(string localPart)
        => localPart.Length <= MaxSam ? localPart : localPart[..MaxSam].TrimEnd('.', '-');

    /// <summary>sAMAccountName with a sequence number, shortened if needed so that the number fits within 20
    /// characters.</summary>
    public static string SamWithNumber(string localPart, int number)
    {
        var suffix = number.ToString(CultureInfo.InvariantCulture);
        var baseline = localPart.Length + suffix.Length > MaxSam ? localPart[..(MaxSam - suffix.Length)].TrimEnd('.', '-') : localPart;
        return baseline + suffix;
    }

    public static string ViewName(string givenName, string surname)
        => string.Join(' ', new[] { givenName.Trim(), surname.Trim() }.Where(s => s.Length > 0));

    /// <summary>
    /// An initial password of 16 characters with upper case, lower case, digits and symbols; without confusing
    /// characters (0/O, 1/l/I). The user has to change it at the first sign-in.
    /// </summary>
    public static string InitialPassword()
    {
        const string large = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string small = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string characters = "!#$%*+-=?@";
        var all = large + small + digits + characters;

        var charactersList = new List<char>
        {
            large[RandomNumberGenerator.GetInt32(large.Length)],
            small[RandomNumberGenerator.GetInt32(small.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            characters[RandomNumberGenerator.GetInt32(characters.Length)],
        };
        while (charactersList.Count < 16)
        {
            charactersList.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        }

        // Fisher-Yates with a cryptographic generator
        for (var i = charactersList.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (charactersList[i], charactersList[j]) = (charactersList[j], charactersList[i]);
        }

        return new string(charactersList.ToArray());
    }
}
