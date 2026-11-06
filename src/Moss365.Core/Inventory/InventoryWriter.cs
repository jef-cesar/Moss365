using System.Globalization;
using System.Text;

namespace Moss365.Core.Inventory;

/// <summary>Which parts were read live and are therefore saved as an inventory.</summary>
[Flags]
public enum InventoryComponent
{
    No = 0,
    Ad = 1,
    Entra = 2,
    Rooms = 4,
    Mailboxes = 8,
}

/// <summary>
/// Writes (part of) the data as an inventory folder, in the same CSV form as Export-Inventaris.ps1. So the app starts
/// the next time with the latest live data. The files contain personal data and stay local.
/// </summary>
public static class InventoryWriter
{
    /// <summary>Creates a new inventory folder under the base and writes the requested parts; returns the
    /// folder.</summary>
    public static string Save(InventoryData d, string baseFolder, InventoryComponent components, DateTime time)
    {
        var folder = Path.Combine(baseFolder, $"Inventaris-{time:yyyyMMdd-HHmm}-live");
        Directory.CreateDirectory(folder);
        Save(d, components, (file, text) => File.WriteAllText(Path.Combine(folder, file), text, new UTF8Encoding(file.EndsWith(".csv", StringComparison.Ordinal))));
        return folder;
    }

    /// <summary>Like <see cref="Save(InventoryData, string, InventoryComponent, DateTime)"/>, in the tenant database
    /// (<c>inventory/Inventaris-...-live</c>); returns the folder.</summary>
    public static string Save(InventoryData d, Storage.IFileStore store, InventoryComponent components, DateTime time)
    {
        var folder = $"{InventoryLoader.StorePrefix}/Inventaris-{time:yyyyMMdd-HHmm}-live";
        store.DeleteAll(folder + "/");
        Save(d, components, (file, text) => store.WriteText($"{folder}/{file}", text, bom: file.EndsWith(".csv", StringComparison.Ordinal)));
        return folder;
    }

    private static void Save(InventoryData d, InventoryComponent components, Action<string, string> writeFile)
    {
        void Write(string file, IEnumerable<Dictionary<string, string?>> rows)
        {
            var list = rows.ToList();
            var columns = list.SelectMany(r => r.Keys).Distinct().ToList();
            writeFile(file, CsvWriter.Text(columns, list.Select(r => columns.Select(k => r.GetValueOrDefault(k)))));
        }

        if (components.HasFlag(InventoryComponent.Ad))
        {
            Write(InventoryLoader.AdUsers, d.AdUsers.Select(u => new Dictionary<string, string?>
            {
                ["OU"] = DnHelp.Parent(u.DistinguishedName), ["SamAccountName"] = u.SamAccountName, ["UserPrincipalName"] = u.UserPrincipalName,
                ["DisplayName"] = u.DisplayName, ["GivenName"] = u.GivenName, ["Surname"] = u.Surname, ["Title"] = u.Title,
                ["Department"] = u.Department, ["Company"] = u.Company, ["Office"] = u.Office, ["City"] = u.City,
                ["Description"] = u.Description, ["EmailAddress"] = u.EmailAddress, ["Enabled"] = B(u.Enabled),
                ["DistinguishedName"] = u.DistinguishedName, ["whenCreated"] = u.WhenCreated, ["LastLogonDate"] = u.LastLogonDate,
                ["PasswordLastSet"] = u.PasswordLastSet, ["PasswordNeverExpires"] = B(u.PasswordNeverExpires), ["LockedOut"] = B(u.LockedOut), ["ScriptPath"] = u.ScriptPath,
                ["OfficePhone"] = u.Phone, ["MobilePhone"] = u.Mobile, ["Manager"] = u.Manager,
                ["MemberOf"] = string.Join('|', u.MemberOr),
            }));
            Write(InventoryLoader.AdGroups, d.AdGroupsByDn.Values.Select(g => new Dictionary<string, string?>
            {
                ["Name"] = g.Name, ["SamAccountName"] = g.SamAccountName, ["DistinguishedName"] = g.DistinguishedName,
                ["OU"] = DnHelp.Parent(g.DistinguishedName), ["Category"] = g.Category, ["Scope"] = g.Scope, ["Description"] = g.Description,
                ["DisplayName"] = g.DisplayName, ["Mail"] = g.Mail, ["AantalLeden"] = N(g.CountMembers), ["ManagedBy"] = g.ManagedBy,
            }));
            Write(InventoryLoader.AdGroupMembers, d.AdGroupsByDn.Values.Where(g => g.MembersKnown).SelectMany(g => g.Members.Select(l => new Dictionary<string, string?>
            {
                ["Groep"] = g.Name, ["GroepDN"] = g.DistinguishedName, ["LidDN"] = l,
            })));
        }

        if (components.HasFlag(InventoryComponent.Entra))
        {
            Write(InventoryLoader.EntraUsers, d.EntraUsers.Select(e => new Dictionary<string, string?>
            {
                ["Id"] = e.Id, ["UserPrincipalName"] = e.UserPrincipalName, ["DisplayName"] = e.DisplayName, ["GivenName"] = e.GivenName,
                ["Surname"] = e.Surname, ["Mail"] = e.Mail, ["MailNickname"] = e.MailNickname, ["AccountEnabled"] = B(e.AccountEnabled),
                ["UserType"] = e.UserType, ["Department"] = e.Department, ["JobTitle"] = e.JobTitle, ["OfficeLocation"] = e.OfficeLocation,
                ["BusinessPhone"] = e.Phone, ["MobilePhone"] = e.Mobile, ["ManagerId"] = e.ManagerId,
                ["CompanyName"] = e.CompanyName, ["City"] = e.City, ["UsageLocation"] = e.UsageLocation, ["CreatedDateTime"] = e.CreatedDateTime,
                ["OnPremisesSyncEnabled"] = B(e.OnPremisesSyncEnabled), ["OnPremisesSamAccountName"] = e.OnPremisesSamAccountName,
                ["OnPremisesDistinguishedName"] = e.OnPremisesDistinguishedName, ["Licenties"] = string.Join('|', e.Licences),
                ["LaatsteAanmelding"] = e.LastSignIn,
            }));
            Write(InventoryLoader.EntraGroups, d.CloudGroups.Values.Select(g => new Dictionary<string, string?>
            {
                ["Id"] = g.Id, ["DisplayName"] = g.DisplayName, ["Description"] = g.Description, ["Mail"] = g.Mail,
                ["Type"] = g.Type switch
                {
                    CloudGroupType.Microsoft365 => "M365",
                    CloudGroupType.Security => "Beveiliging",
                    CloudGroupType.MailSecurity => "MailBeveiliging",
                    CloudGroupType.Distribution => "Distributie",
                    _ => string.Empty,
                },
                ["Bron"] = g.FromAd ? "AD" : "Cloud", ["Dynamisch"] = B(g.Dynamic), ["Lidmaatschapsregel"] = g.MembershipRule,
                ["OnPremisesSamAccountName"] = g.OnPremisesSamAccountName, ["GroepLicentie"] = B(g.GroupLicence), ["AantalLeden"] = N(g.CountMembers),
            }));
            var users = d.EntraUsers.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Write(InventoryLoader.EntraGroupMembers, d.CloudMembership.SelectMany(kv => kv.Value.Select(g => new Dictionary<string, string?>
            {
                ["GroepId"] = g, ["Groep"] = d.CloudGroups.GetValueOrDefault(g)?.DisplayName, ["LidId"] = kv.Key,
                ["LidType"] = users.Contains(kv.Key) ? "user" : d.CloudMemberNames.GetValueOrDefault(kv.Key).Kind ?? "user",
                ["Lid"] = d.CloudMemberNames.GetValueOrDefault(kv.Key).Name,
            })));
            Write(InventoryLoader.EntraLicences, d.Licences.Select(l => new Dictionary<string, string?>
            {
                ["SkuId"] = l.SkuId, ["SkuPartNumber"] = l.SkuPartNumber, ["Toegewezen"] = N(l.Assigned), ["Totaal"] = N(l.Total),
            }));
        }

        if (components.HasFlag(InventoryComponent.Entra) && d.Security is { } b)
        {
            if (b.MfaKnown)
            {
                Write(InventoryLoader.EntraMfa, b.Mfa.Select(m => new Dictionary<string, string?>
                {
                    ["Id"] = m.UserId, ["UserPrincipalName"] = m.Upn, ["IsMfaRegistered"] = B(m.MfaRegistered),
                    ["IsMfaCapable"] = B(m.MfaPossible), ["Methods"] = string.Join('|', m.Methods), ["DefaultMethod"] = m.DefaultMethod,
                    ["IsAdmin"] = B(m.IsAdministrator),
                }));
            }

            if (b.RolesKnown)
            {
                Write(InventoryLoader.EntraRoles, b.Roles.Select(r => new Dictionary<string, string?>
                {
                    ["Rol"] = r.Role, ["LidId"] = r.MemberId, ["LidNaam"] = r.MemberName, ["LidUpn"] = r.MemberUpn, ["LidSoort"] = r.MemberKind,
                    ["RoleTemplateId"] = r.RoleTemplateId,
                }));
            }
        }

        if (components.HasFlag(InventoryComponent.Entra) && d.ConditionalAccess is { Message: null } ca)
        {
            // Policies are nested structures: as JSON, not as CSV
            writeFile(InventoryLoader.EntraCa, CaStorage.ToJson(ca));
        }

        if (components.HasFlag(InventoryComponent.Rooms))
        {
            Write(InventoryLoader.ExoRooms, d.Rooms.Select(r => new Dictionary<string, string?>
            {
                ["Smtp"] = r.Smtp, ["DisplayName"] = r.DisplayName, ["Alias"] = r.Alias, ["Gebouw"] = r.Building, ["Verdieping"] = r.Floor,
                ["VerdiepingLabel"] = r.FloorLabel, ["Capaciteit"] = N(r.Capacity), ["Hidden"] = B(r.Hidden),
                ["AutomateProcessing"] = r.AutomateProcessing, ["AllBookInPolicy"] = B(r.AllBookInPolicy), ["AllRequestInPolicy"] = B(r.AllRequestInPolicy),
                ["BookInPolicy"] = r.BookInPolicy, ["ResourceDelegates"] = r.ResourceDelegates, ["BookingWindowInDays"] = N(r.BookingWindowInDays),
                ["MaximumDurationInMinutes"] = N(r.MaximumDurationInMinutes), ["AllowConflicts"] = B(r.AllowConflicts),
                ["AllowRecurringMeetings"] = B(r.AllowRecurringMeetings), ["AddOrganizerToSubject"] = B(r.AddOrganizerToSubject),
                ["DeleteSubject"] = B(r.DeleteSubject), ["AgendaRechten"] = r.CalendarPermissions,
            }));
        }

        if (components.HasFlag(InventoryComponent.Mailboxes))
        {
            Write(InventoryLoader.ExoGroups, d.ExoGroups.Select(g => new Dictionary<string, string?>
            {
                ["Type"] = g.Type, ["DisplayName"] = g.DisplayName, ["Name"] = g.Name, ["Alias"] = g.Alias, ["PrimarySmtpAddress"] = g.Smtp,
                ["IsDirSynced"] = B(g.IsDirSynced), ["MemberJoinRestriction"] = g.MemberJoinRestriction, ["AantalLeden"] = N(g.CountMembers),
                ["ManagedBy"] = string.Join('|', g.ManagedBy),
            }));
            Write(InventoryLoader.ExoGroupMembers, d.ExoGroups.SelectMany(g => g.Members.Select(l => new Dictionary<string, string?>
            {
                ["Groep"] = g.DisplayName, ["Type"] = g.Type, ["Lid"] = l.Smtp, ["LidNaam"] = l.Name,
            })));
            Write(InventoryLoader.ExoMailboxes, d.Mailboxes.Select(p => new Dictionary<string, string?>
            {
                ["Type"] = p.Type, ["DisplayName"] = p.DisplayName, ["Alias"] = p.Alias, ["PrimarySmtpAddress"] = p.Smtp,
                ["Hidden"] = B(p.Hidden), ["IsDirSynced"] = B(p.IsDirSynced), ["WhenCreated"] = p.WhenCreated, ["Doorsturen"] = p.Forwarding,
                ["DoorsturenNaar"] = p.ForwardingTo, ["DoorsturenEnBewaren"] = p.SaveForwardingAnd is null ? string.Empty : B(p.SaveForwardingAnd.Value),
                ["AutoAntwoord"] = p.AutoReply, ["AutoAntwoordIntern"] = p.AutoReplyInternal, ["AutoAntwoordExtern"] = p.AutoReplyExternal,
            }));
            Write(InventoryLoader.ExoMailboxPermissions, d.MailboxPermissions.Select(r => new Dictionary<string, string?>
            {
                ["Postvak"] = r.Mailbox, ["Recht"] = r.Permission, ["Gebruiker"] = r.Trustee, ["Adres"] = r.Trustee,
            }));
        }

    }

    private static string B(bool b) => b ? "True" : "False";

    private static string N(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

}

/// <summary>CSV like Export-Inventaris.ps1: semicolon as separator, everything between quotes, UTF-8 with BOM (so it
/// opens well in Excel).</summary>
public static class CsvWriter
{
    /// <param name="forExcel">
    /// Values that start with = + - @ get an apostrophe, so that Excel does not run them as a formula (for exports; the
    /// app reads the inventory back itself and it stays unchanged).
    /// </param>
    public static string Text(IEnumerable<string> columns, IEnumerable<IEnumerable<string?>> rows, bool forExcel = false)
    {
        string Q(string? w)
        {
            w ??= string.Empty;
            if (forExcel && w.Length > 0 && w[0] is '=' or '+' or '-' or '@' && !double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                w = "'" + w;
            }

            return "\"" + w.Replace("\"", "\"\"") + "\"";
        }

        var text = new StringBuilder();
        text.AppendLine(string.Join(';', columns.Select(Q)));
        foreach (var r in rows)
        {
            text.AppendLine(string.Join(';', r.Select(Q)));
        }

        return text.ToString();
    }

    public static void Write(string path, IEnumerable<string> columns, IEnumerable<IEnumerable<string?>> rows, bool forExcel = false)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, Text(columns, rows, forExcel), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>A safe file name: characters Windows does not allow become a dash.</summary>
    public static string FileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).ToHashSet();
        var output = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return output.Length == 0 ? "export" : output;
    }
}
