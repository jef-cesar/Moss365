using System.Globalization;
using System.Text;

namespace Moss365.Core.Inventaris;

/// <summary>Welke onderdelen live gelezen zijn en dus als inventaris bewaard worden.</summary>
[Flags]
public enum InventarisOnderdeel
{
    Geen = 0,
    Ad = 1,
    Entra = 2,
    Rooms = 4,
    Postvakken = 8,
}

/// <summary>
/// Schrijft (een deel van) de gegevens weg als inventarismap, in dezelfde CSV-vorm als Export-Inventaris.ps1. Zo start de
/// app de volgende keer met de laatste live gegevens. De bestanden bevatten persoonsgegevens en blijven lokaal.
/// </summary>
public static class InventarisSchrijver
{
    /// <summary>Maakt een nieuwe inventarismap onder de basis en schrijft de gevraagde onderdelen; geeft de map terug.</summary>
    public static string Bewaar(InventarisData d, string basisMap, InventarisOnderdeel onderdelen, DateTime tijd)
    {
        var map = Path.Combine(basisMap, $"Inventaris-{tijd:yyyyMMdd-HHmm}-live");
        Directory.CreateDirectory(map);

        if (onderdelen.HasFlag(InventarisOnderdeel.Ad))
        {
            Schrijf(map, InventarisLader.AdGebruikers, d.AdGebruikers.Select(u => new Dictionary<string, string?>
            {
                ["OU"] = DnHulp.Ouder(u.DistinguishedName), ["SamAccountName"] = u.SamAccountName, ["UserPrincipalName"] = u.UserPrincipalName,
                ["DisplayName"] = u.DisplayName, ["GivenName"] = u.GivenName, ["Surname"] = u.Surname, ["Title"] = u.Title,
                ["Department"] = u.Department, ["Company"] = u.Company, ["Office"] = u.Office, ["City"] = u.City,
                ["Description"] = u.Description, ["EmailAddress"] = u.EmailAddress, ["Enabled"] = B(u.Enabled),
                ["DistinguishedName"] = u.DistinguishedName, ["whenCreated"] = u.WhenCreated, ["LastLogonDate"] = u.LastLogonDate,
                ["PasswordLastSet"] = u.PasswordLastSet, ["PasswordNeverExpires"] = B(u.PasswordNeverExpires), ["LockedOut"] = B(u.LockedOut), ["ScriptPath"] = u.ScriptPath,
                ["OfficePhone"] = u.Telefoon, ["MobilePhone"] = u.Gsm, ["Manager"] = u.Manager,
                ["MemberOf"] = string.Join('|', u.MemberOf),
            }));
            Schrijf(map, InventarisLader.AdGroepen, d.AdGroepenOpDn.Values.Select(g => new Dictionary<string, string?>
            {
                ["Name"] = g.Name, ["SamAccountName"] = g.SamAccountName, ["DistinguishedName"] = g.DistinguishedName,
                ["OU"] = DnHulp.Ouder(g.DistinguishedName), ["Category"] = g.Category, ["Scope"] = g.Scope, ["Description"] = g.Description,
                ["DisplayName"] = g.DisplayName, ["Mail"] = g.Mail, ["AantalLeden"] = N(g.AantalLeden), ["ManagedBy"] = g.ManagedBy,
            }));
            Schrijf(map, InventarisLader.AdGroepsleden, d.AdGroepenOpDn.Values.Where(g => g.LedenBekend).SelectMany(g => g.Leden.Select(l => new Dictionary<string, string?>
            {
                ["Groep"] = g.Name, ["GroepDN"] = g.DistinguishedName, ["LidDN"] = l,
            })));
        }

        if (onderdelen.HasFlag(InventarisOnderdeel.Entra))
        {
            Schrijf(map, InventarisLader.EntraGebruikers, d.EntraGebruikers.Select(e => new Dictionary<string, string?>
            {
                ["Id"] = e.Id, ["UserPrincipalName"] = e.UserPrincipalName, ["DisplayName"] = e.DisplayName, ["GivenName"] = e.GivenName,
                ["Surname"] = e.Surname, ["Mail"] = e.Mail, ["MailNickname"] = e.MailNickname, ["AccountEnabled"] = B(e.AccountEnabled),
                ["UserType"] = e.UserType, ["Department"] = e.Department, ["JobTitle"] = e.JobTitle, ["OfficeLocation"] = e.OfficeLocation,
                ["BusinessPhone"] = e.Telefoon, ["MobilePhone"] = e.Gsm, ["ManagerId"] = e.ManagerId,
                ["CompanyName"] = e.CompanyName, ["City"] = e.City, ["UsageLocation"] = e.UsageLocation, ["CreatedDateTime"] = e.CreatedDateTime,
                ["OnPremisesSyncEnabled"] = B(e.OnPremisesSyncEnabled), ["OnPremisesSamAccountName"] = e.OnPremisesSamAccountName,
                ["OnPremisesDistinguishedName"] = e.OnPremisesDistinguishedName, ["Licenties"] = string.Join('|', e.Licenties),
                ["LaatsteAanmelding"] = e.LaatsteAanmelding,
            }));
            Schrijf(map, InventarisLader.EntraGroepen, d.CloudGroepen.Values.Select(g => new Dictionary<string, string?>
            {
                ["Id"] = g.Id, ["DisplayName"] = g.DisplayName, ["Description"] = g.Description, ["Mail"] = g.Mail,
                ["Type"] = g.Type switch
                {
                    CloudGroepType.Microsoft365 => "M365",
                    CloudGroepType.Beveiliging => "Beveiliging",
                    CloudGroepType.MailBeveiliging => "MailBeveiliging",
                    CloudGroepType.Distributie => "Distributie",
                    _ => string.Empty,
                },
                ["Bron"] = g.UitAd ? "AD" : "Cloud", ["Dynamisch"] = B(g.Dynamisch), ["Lidmaatschapsregel"] = g.Lidmaatschapsregel,
                ["OnPremisesSamAccountName"] = g.OnPremisesSamAccountName, ["GroepLicentie"] = B(g.GroepLicentie), ["AantalLeden"] = N(g.AantalLeden),
            }));
            var gebruikers = d.EntraGebruikers.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Schrijf(map, InventarisLader.EntraGroepsleden, d.CloudLidmaatschap.SelectMany(kv => kv.Value.Select(g => new Dictionary<string, string?>
            {
                ["GroepId"] = g, ["Groep"] = d.CloudGroepen.GetValueOrDefault(g)?.DisplayName, ["LidId"] = kv.Key,
                ["LidType"] = gebruikers.Contains(kv.Key) ? "user" : d.CloudLidNamen.GetValueOrDefault(kv.Key).Soort ?? "user",
                ["Lid"] = d.CloudLidNamen.GetValueOrDefault(kv.Key).Naam,
            })));
            Schrijf(map, InventarisLader.EntraLicenties, d.Licenties.Select(l => new Dictionary<string, string?>
            {
                ["SkuId"] = l.SkuId, ["SkuPartNumber"] = l.SkuPartNumber, ["Toegewezen"] = N(l.Toegewezen), ["Totaal"] = N(l.Totaal),
            }));
        }

        if (onderdelen.HasFlag(InventarisOnderdeel.Entra) && d.Beveiliging is { } b)
        {
            if (b.MfaBekend)
            {
                Schrijf(map, InventarisLader.EntraMfa, b.Mfa.Select(m => new Dictionary<string, string?>
                {
                    ["Id"] = m.GebruikerId, ["UserPrincipalName"] = m.Upn, ["IsMfaRegistered"] = B(m.MfaGeregistreerd),
                    ["IsMfaCapable"] = B(m.MfaMogelijk), ["Methods"] = string.Join('|', m.Methodes), ["DefaultMethod"] = m.StandaardMethode,
                    ["IsAdmin"] = B(m.IsBeheerder),
                }));
            }

            if (b.RollenBekend)
            {
                Schrijf(map, InventarisLader.EntraRollen, b.Rollen.Select(r => new Dictionary<string, string?>
                {
                    ["Rol"] = r.Rol, ["LidId"] = r.LidId, ["LidNaam"] = r.LidNaam, ["LidUpn"] = r.LidUpn, ["LidSoort"] = r.LidSoort,
                    ["RoleTemplateId"] = r.RoleTemplateId,
                }));
            }
        }

        if (onderdelen.HasFlag(InventarisOnderdeel.Entra) && d.ConditionalAccess is { Melding: null } ca)
        {
            // Beleiden zijn geneste structuren: als JSON, niet als CSV
            File.WriteAllText(Path.Combine(map, InventarisLader.EntraCa), CaOpslag.NaarJson(ca), new UTF8Encoding(false));
        }

        if (onderdelen.HasFlag(InventarisOnderdeel.Rooms))
        {
            Schrijf(map, InventarisLader.ExoRooms, d.Rooms.Select(r => new Dictionary<string, string?>
            {
                ["Smtp"] = r.Smtp, ["DisplayName"] = r.DisplayName, ["Alias"] = r.Alias, ["Gebouw"] = r.Gebouw, ["Verdieping"] = r.Verdieping,
                ["VerdiepingLabel"] = r.VerdiepingLabel, ["Capaciteit"] = N(r.Capaciteit), ["Hidden"] = B(r.Verborgen),
                ["AutomateProcessing"] = r.AutomateProcessing, ["AllBookInPolicy"] = B(r.AllBookInPolicy), ["AllRequestInPolicy"] = B(r.AllRequestInPolicy),
                ["BookInPolicy"] = r.BookInPolicy, ["ResourceDelegates"] = r.ResourceDelegates, ["BookingWindowInDays"] = N(r.BookingWindowInDays),
                ["MaximumDurationInMinutes"] = N(r.MaximumDurationInMinutes), ["AllowConflicts"] = B(r.AllowConflicts),
                ["AllowRecurringMeetings"] = B(r.AllowRecurringMeetings), ["AddOrganizerToSubject"] = B(r.AddOrganizerToSubject),
                ["DeleteSubject"] = B(r.DeleteSubject), ["AgendaRechten"] = r.AgendaRechten,
            }));
        }

        if (onderdelen.HasFlag(InventarisOnderdeel.Postvakken))
        {
            Schrijf(map, InventarisLader.ExoGroepen, d.ExoGroepen.Select(g => new Dictionary<string, string?>
            {
                ["Type"] = g.Type, ["DisplayName"] = g.DisplayName, ["Name"] = g.Name, ["Alias"] = g.Alias, ["PrimarySmtpAddress"] = g.Smtp,
                ["IsDirSynced"] = B(g.IsDirSynced), ["MemberJoinRestriction"] = g.MemberJoinRestriction, ["AantalLeden"] = N(g.AantalLeden),
                ["ManagedBy"] = string.Join('|', g.ManagedBy),
            }));
            Schrijf(map, InventarisLader.ExoGroepsleden, d.ExoGroepen.SelectMany(g => g.Leden.Select(l => new Dictionary<string, string?>
            {
                ["Groep"] = g.DisplayName, ["Type"] = g.Type, ["Lid"] = l.Smtp, ["LidNaam"] = l.Naam,
            })));
            Schrijf(map, InventarisLader.ExoPostvakken, d.Postvakken.Select(p => new Dictionary<string, string?>
            {
                ["Type"] = p.Type, ["DisplayName"] = p.DisplayName, ["Alias"] = p.Alias, ["PrimarySmtpAddress"] = p.Smtp,
                ["Hidden"] = B(p.Verborgen), ["IsDirSynced"] = B(p.IsDirSynced), ["WhenCreated"] = p.WhenCreated, ["Doorsturen"] = p.Doorsturen,
                ["DoorsturenNaar"] = p.DoorsturenNaar, ["DoorsturenEnBewaren"] = p.DoorsturenEnBewaren is null ? string.Empty : B(p.DoorsturenEnBewaren.Value),
                ["AutoAntwoord"] = p.AutoAntwoord, ["AutoAntwoordIntern"] = p.AutoAntwoordIntern, ["AutoAntwoordExtern"] = p.AutoAntwoordExtern,
            }));
            Schrijf(map, InventarisLader.ExoPostvakRechten, d.PostvakRechten.Select(r => new Dictionary<string, string?>
            {
                ["Postvak"] = r.Postvak, ["Recht"] = r.Recht, ["Gebruiker"] = r.Trustee, ["Adres"] = r.Trustee,
            }));
        }

        return map;
    }

    private static string B(bool b) => b ? "True" : "False";

    private static string N(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Schrijf(string map, string bestand, IEnumerable<Dictionary<string, string?>> rijen)
    {
        var lijst = rijen.ToList();
        var kolommen = lijst.SelectMany(r => r.Keys).Distinct().ToList();
        CsvSchrijver.Schrijf(Path.Combine(map, bestand), kolommen, lijst.Select(r => kolommen.Select(k => r.GetValueOrDefault(k))));
    }
}

/// <summary>CSV zoals Export-Inventaris.ps1: puntkomma als scheiding, alles tussen aanhalingstekens, UTF-8 met BOM (opent zo goed in Excel).</summary>
public static class CsvSchrijver
{
    /// <param name="voorExcel">
    /// Waarden die met = + - @ beginnen krijgen een apostrof, zodat Excel ze niet als formule uitvoert (voor exports; de
    /// inventaris leest de app zelf terug en blijft ongewijzigd).
    /// </param>
    public static string Tekst(IEnumerable<string> kolommen, IEnumerable<IEnumerable<string?>> rijen, bool voorExcel = false)
    {
        string Q(string? w)
        {
            w ??= string.Empty;
            if (voorExcel && w.Length > 0 && w[0] is '=' or '+' or '-' or '@' && !double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                w = "'" + w;
            }

            return "\"" + w.Replace("\"", "\"\"") + "\"";
        }

        var tekst = new StringBuilder();
        tekst.AppendLine(string.Join(';', kolommen.Select(Q)));
        foreach (var r in rijen)
        {
            tekst.AppendLine(string.Join(';', r.Select(Q)));
        }

        return tekst.ToString();
    }

    public static void Schrijf(string pad, IEnumerable<string> kolommen, IEnumerable<IEnumerable<string?>> rijen, bool voorExcel = false)
    {
        if (Path.GetDirectoryName(pad) is { Length: > 0 } map)
        {
            Directory.CreateDirectory(map);
        }

        File.WriteAllText(pad, Tekst(kolommen, rijen, voorExcel), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>Een veilige bestandsnaam: tekens die Windows niet toelaat worden een streepje.</summary>
    public static string Bestandsnaam(string naam)
    {
        var ongeldig = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var uit = new string(naam.Select(c => ongeldig.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return uit.Length == 0 ? "export" : uit;
    }
}
