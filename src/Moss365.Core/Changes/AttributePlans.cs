using System.Globalization;
using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>The data of a user as it has to become; null = do not change.</summary>
public sealed class UserDesired
{
    public string? DisplayName { get; set; }
    public string? GivenName { get; set; }
    public string? Surname { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Office { get; set; }
    public string? City { get; set; }

    /// <summary>Only in AD.</summary>
    public string? Description { get; set; }

    /// <summary>Only in AD.</summary>
    public string? LogonScript { get; set; }

    public string? Phone { get; set; }
    public string? Mobile { get; set; }

    /// <summary>Key of the person who is the manager; "" = no manager; null = do not change.</summary>
    public string? Manager { get; set; }

    // R21: more fields of a user
    public string? Company { get; set; }
    public string? EmployeeId { get; set; }
    public string? EmployeeType { get; set; }
    public string? StreetAddress { get; set; }
    public string? PostalCode { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }

    /// <summary>Only in Entra (where the licences may be used: two letters); also for a synchronised account it is
    /// written in Entra, because Entra Connect does not carry it.</summary>
    public string? UsageLocation { get; set; }

    /// <summary>R21: the sign-in name (UPN): the person signs in with the new one afterwards (a confirmation on the page).</summary>
    public string? Upn { get; set; }

    /// <summary>R21: other phones (home and IP phone only in AD; the fax in both).</summary>
    public string? HomePhone { get; set; }
    public string? Fax { get; set; }
    public string? IpPhone { get; set; }

    /// <summary>R21: the last day the account works ("yyyy-MM-dd"), only in AD; "" = never.</summary>
    public string? AccountExpires { get; set; }

    /// <summary>R21: the primary e-mail address: in AD (mail and proxyAddresses) for an account from AD, otherwise the
    /// mailbox in Exchange Online. The old one stays as an alias.</summary>
    public string? PrimaryAddress { get; set; }

    /// <summary>The current values (AD when the account comes from AD, otherwise Entra), as the start of the
    /// form.</summary>
    public static UserDesired Of(Person p, InventoryData? d = null)
    {
        var g = p.Ad is { } a
            ? new UserDesired
            {
                DisplayName = a.DisplayName, GivenName = a.GivenName, Surname = a.Surname, JobTitle = a.Title, Department = a.Department, Office = a.Office, City = a.City,
                Description = a.Description, LogonScript = a.ScriptPath, Phone = a.Phone, Mobile = a.Mobile,
                Company = a.Company, EmployeeId = a.EmployeeId, EmployeeType = a.EmployeeType, StreetAddress = a.StreetAddress, PostalCode = a.PostalCode, State = a.State, Country = a.Country,
                HomePhone = a.HomePhone, Fax = a.Fax, IpPhone = a.IpPhone, AccountExpires = AttributePlans.ExpiryDay(a.AccountExpires),
            }
            : new UserDesired
            {
                DisplayName = p.Entra?.DisplayName, GivenName = p.Entra?.GivenName, Surname = p.Entra?.Surname, JobTitle = p.Entra?.JobTitle, Department = p.Entra?.Department,
                Office = p.Entra?.OfficeLocation, City = p.Entra?.City, Phone = p.Entra?.Phone, Mobile = p.Entra?.Mobile,
                Company = p.Entra?.CompanyName, EmployeeId = p.Entra?.EmployeeId, EmployeeType = p.Entra?.EmployeeType, StreetAddress = p.Entra?.StreetAddress,
                PostalCode = p.Entra?.PostalCode, State = p.Entra?.State, Country = p.Entra?.Country, Fax = p.Entra?.Fax,
            };
        g.UsageLocation = p.Entra?.UsageLocation;
        g.Upn = p.Ad?.UserPrincipalName is { Length: > 0 } upn ? upn : p.Entra?.UserPrincipalName;
        g.PrimaryAddress = AttributePlans.PrimaryAddressOf(p);
        g.Manager = d is null ? null : AttributePlans.ManagerOf(d, p)?.Key ?? string.Empty;
        return g;
    }
}

/// <summary>
/// Changes attributes of a user along the right route: a synchronised account in AD (Entra follows after the
/// synchronisation), a cloud account via Graph. With the previous values, so it can be undone. Not the UPN and e-mail
/// address: they affect sign-in and mail, and come separately.
/// </summary>
public static class AttributePlans
{
    // Attribute in the form -> (LDAP name in AD or null when only Entra has it, name in Graph or null when Entra does not
    // know it, readable name)
    private static readonly (string Field, string? Ad, string? Graph, string Name)[] Fields =
    {
        (nameof(UserDesired.DisplayName), "displayName", "displayName", "Weergavenaam"),
        (nameof(UserDesired.GivenName), "givenName", "givenName", "Voornaam"),
        (nameof(UserDesired.Surname), "sn", "surname", "Achternaam"),
        (nameof(UserDesired.JobTitle), "title", "jobTitle", "Functie"),
        (nameof(UserDesired.Department), "department", "department", "Dienst"),
        (nameof(UserDesired.Office), "physicalDeliveryOfficeName", "officeLocation", "Kantoor"),
        (nameof(UserDesired.City), "l", "city", "Stad"),
        (nameof(UserDesired.Description), "description", null, "Omschrijving"),
        (nameof(UserDesired.LogonScript), "scriptPath", null, "Logonscript"),
        (nameof(UserDesired.Phone), "telephoneNumber", "businessPhones", "Telefoon"),
        (nameof(UserDesired.Mobile), "mobile", "mobilePhone", "Gsm"),
        (nameof(UserDesired.Company), "company", "companyName", "Bedrijf"),
        (nameof(UserDesired.EmployeeId), "employeeID", "employeeId", "Personeelsnummer"),
        (nameof(UserDesired.EmployeeType), "employeeType", "employeeType", "Soort medewerker"),
        (nameof(UserDesired.StreetAddress), "streetAddress", "streetAddress", "Adres"),
        (nameof(UserDesired.PostalCode), "postalCode", "postalCode", "Postcode"),
        (nameof(UserDesired.State), "st", "state", "Provincie"),
        (nameof(UserDesired.Country), "co", "country", "Land"),
        (nameof(UserDesired.UsageLocation), null, "usageLocation", "Gebruikslocatie"),
        (nameof(UserDesired.Upn), "userPrincipalName", "userPrincipalName", "UPN"),
        (nameof(UserDesired.HomePhone), "homePhone", null, "Telefoon thuis"),
        (nameof(UserDesired.Fax), "facsimileTelephoneNumber", "faxNumber", "Fax"),
        (nameof(UserDesired.IpPhone), "ipPhone", null, "IP-telefoon"),
        (nameof(UserDesired.AccountExpires), "accountExpires", null, "Account vervalt"),
    };

    /// <summary>The last day an account works, from accountExpires as read (local time; midnight is the end of the day
    /// before, as Active Directory Users and Computers sets it); empty = never.</summary>
    public static string ExpiryDay(string accountExpires)
        => DateTime.TryParse(accountExpires, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? (t.TimeOfDay == TimeSpan.Zero ? t.AddDays(-1) : t).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>accountExpires for the last day an account works: the start of the next day in local time, as a file
    /// time; "0" = never.</summary>
    public static string ExpiryValue(string day)
        => DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d.AddDays(1), DateTimeKind.Local).ToFileTime().ToString(CultureInfo.InvariantCulture)
            : "0";

    /// <summary>accountExpires as it was read (local time), as a file time to put back on undo; "0" = never.</summary>
    private static string ExpiryValueOf(string accountExpires)
        => DateTime.TryParse(accountExpires, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Local).ToFileTime().ToString(CultureInfo.InvariantCulture)
            : "0";

    /// <summary>The fields that change how the person signs in (R21): a confirmation on the page, a warning on the plan.
    /// The logon name in AD (sAMAccountName) is not among them: it is the key of a person in the app (snapshots,
    /// history, documents), so it is not changed here (a limitation).</summary>
    public static IReadOnlyList<string> SignInFields { get; } = new[] { nameof(UserDesired.Upn) };

    /// <summary>The fields a user has on the screens (R21), in the order of the form: the name of the field in
    /// <see cref="UserDesired"/>, and where it lives (AD, Entra or both).</summary>
    public static IReadOnlyList<(string Field, bool InAd, bool InEntra)> UserFields => Fields.Select(f => (f.Field, f.Ad is not null, f.Graph is not null)).ToList();

    /// <summary>The manager of a person (AD: manager DN; cloud: manager id), or null.</summary>
    public static Person? ManagerOf(InventoryData d, Person p)
        => ViaAd(p)
            ? p.Ad!.Manager is { Length: > 0 } dn ? d.Persons.FirstOrDefault(x => string.Equals(x.Ad?.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase)) : null
            : p.Entra?.ManagerId is { Length: > 0 } id ? d.Persons.FirstOrDefault(x => string.Equals(x.Entra?.Id, id, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>Is this account changed in AD (synchronised or AD only), or via Graph (cloud)?</summary>
    public static bool ViaAd(Person p) => p.Ad is not null && (p.Entra is null || p.Entra.OnPremisesSyncEnabled);

    /// <summary>The primary address of a person now (R21): for an account from AD the "SMTP:" entry of proxyAddresses,
    /// or mail; otherwise mail in Entra.</summary>
    public static string PrimaryAddressOf(Person p)
        => ViaAd(p)
            ? p.Ad!.ProxyAddresses.FirstOrDefault(a => a.StartsWith("SMTP:", StringComparison.Ordinal)) is { } primary ? primary[5..] : p.Ad.EmailAddress
            : p.Entra?.Mail ?? string.Empty;

    /// <summary>Has the person an address that can change: an account from AD, or a cloud account with mail.</summary>
    public static bool HasPrimaryAddress(Person p) => ViaAd(p) || p.Entra?.Mail is { Length: > 0 };

    /// <summary>proxyAddresses with a new primary address (R21): "SMTP:" + the new one first, every other mail address as an
    /// alias ("smtp:", the old primary too; the new one only once), the other kinds (x500:, sip:, ...) as they are.</summary>
    public static IReadOnlyList<string> WithPrimary(IReadOnlyList<string> proxies, string address)
    {
        var list = new List<string> { "SMTP:" + address };
        foreach (var a in proxies)
        {
            if (!a.StartsWith("smtp:", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(a);
            }
            else if (!string.Equals(a[5..], address, StringComparison.OrdinalIgnoreCase))
            {
                list.Add("smtp:" + a[5..]);
            }
        }

        return list;
    }

    public static PlanOutcome User(InventoryData data, OrganisationConfig config, Person p, UserDesired desired)
    {
        if (p.Ad is null && p.Entra is null)
        {
            return PlanOutcome.Error(Texts.T["code.attribute-plans.this-account-is-not-in"]);
        }

        if (p.IsGuest)
        {
            return PlanOutcome.Error(Texts.T["code.attribute-plans.you-manage-guest-users-in"]);
        }

        var current = UserDesired.Of(p, data);
        var viaAd = ViaAd(p);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Fields that only Entra has, for an account that otherwise changes in AD (R21: the usage location)
        var cloudParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cloudPrevious = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cloudNow = new List<string>();
        var cloudBecomes = new List<string>();
        var now = new List<string>();
        var becomes = new List<string>();
        var warnings = new List<string>();

        // The department as spelled in the organisation (spaces and case)
        if (desired.Department is { } d && config.SearchDepartment(d) is { } department)
        {
            desired.Department = department.Department;
        }
        else if (!string.IsNullOrWhiteSpace(desired.Department) && config.Departments.Count > 0)
        {
            warnings.Add(Texts.T.Format("code.attribute-plans.the-department-is-not-in", desired.Department.Trim()));
        }

        foreach (var (field, adName, graphName, dutch) in Fields)
        {
            var name = Texts.T.Text("attribute." + field.ToLowerInvariant(), dutch);
            var old = Value(current, field) ?? string.Empty;
            var newValue = Value(desired, field);
            if (newValue is null || string.Equals(old, newValue.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            var key = viaAd ? adName : graphName;
            if (key is null && viaAd && graphName is not null && p.Entra is not null)
            {
                // Entra Connect does not carry it: written in Entra, also for a synchronised account
                if (!ValidValue(field, newValue.Trim()))
                {
                    warnings.Add(Texts.T.Format("attribute.invalid", name));
                    continue;
                }

                cloudParameters[graphName] = newValue.Trim();
                cloudPrevious[graphName] = old;
                cloudNow.Add($"{name}: {(old.Length == 0 ? Texts.T["undo.empty"] : old)}");
                cloudBecomes.Add($"{name}: {(newValue.Trim().Length == 0 ? Texts.T["undo.empty"] : newValue.Trim())}");
                continue;
            }

            if (key is null)
            {
                warnings.Add(Texts.T.Format(viaAd ? "attribute.only-in-entra" : "code.attribute-plans.exists-only-in-ad-this", name));
                continue;
            }

            if (!ValidValue(field, newValue.Trim()))
            {
                warnings.Add(Texts.T.Format("attribute.invalid", name));
                continue;
            }

            if (Taken(data, p, field, newValue) is { } other)
            {
                warnings.Add(Texts.T.Format("attribute.taken", name, other));
                continue;
            }

            if (SignInFields.Contains(field))
            {
                warnings.Add(Texts.T.Format("attribute.sign-in-changes", name, newValue.Trim()));
            }

            if (field == nameof(UserDesired.AccountExpires))
            {
                // A date in AD is a file time: the start of the day after the last day (R21)
                var day = newValue.Trim();
                parameters[key] = ExpiryValue(day);
                previous[key] = ExpiryValueOf(p.Ad?.AccountExpires ?? string.Empty);
                now.Add($"{name}: {(old.Length == 0 ? Texts.T["edit-user.never"] : old)}");
                becomes.Add($"{name}: {(day.Length == 0 ? Texts.T["edit-user.never"] : day)}");
                if (ExpiresBeforeToday(day))
                {
                    warnings.Add(Texts.T.Format("attribute.expires-past", day));
                }

                continue;
            }

            parameters[key] = newValue.Trim();
            previous[key] = old;
            now.Add($"{name}: {(old.Length == 0 ? Texts.T["undo.empty"] : old)}");
            becomes.Add($"{name}: {(newValue.Trim().Length == 0 ? Texts.T["undo.empty"] : newValue.Trim())}");
        }

        // R21: the primary address, at the source (AD: mail and proxyAddresses; cloud: the mailbox in Exchange Online)
        Change? addressStep = null;
        if (desired.PrimaryAddress is { } wantedAddress && wantedAddress.Trim() is var address
            && !string.Equals(address, current.PrimaryAddress ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            var name = Texts.T["attribute.primaryaddress"];
            var old = current.PrimaryAddress ?? string.Empty;
            if (!HasPrimaryAddress(p))
            {
                warnings.Add(Texts.T["edit-user.no-mailbox"]);
            }
            else if (!ValidValue(nameof(UserDesired.PrimaryAddress), address))
            {
                warnings.Add(Texts.T.Format("attribute.invalid", name));
            }
            else if (Taken(data, p, nameof(UserDesired.PrimaryAddress), address) is { } holder)
            {
                warnings.Add(Texts.T.Format("mailbox.primary.taken", address, holder));
            }
            else
            {
                var kept = old.Length > 0 && (!viaAd || p.Ad!.ProxyAddresses.Count > 0);
                var nowText = $"{name}: {(old.Length == 0 ? Texts.T["undo.empty"] : old)}";
                var becomesText = $"{name}: {address}";
                var aliasWarning = kept ? Texts.T.Format("mailbox.primary.alias", old) : null;
                if (viaAd)
                {
                    var adParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mail"] = address };
                    var adPrevious = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mail"] = p.Ad!.EmailAddress };
                    if (p.Ad.ProxyAddresses.Count > 0)
                    {
                        adParameters["proxyAddresses"] = string.Join('|', WithPrimary(p.Ad.ProxyAddresses, address));
                        adPrevious["proxyAddresses"] = string.Join('|', p.Ad.ProxyAddresses);
                    }

                    addressStep = new Change
                    {
                        TargetSystem = TargetSystem.ActiveDirectory,
                        ChangeAction = ChangeAction.AdSetProxyAddresses,
                        Target = p.Ad.DistinguishedName,
                        TargetName = p.Name,
                        Parameters = adParameters,
                        Previous = adPrevious,
                        Description = Texts.T[p.Entra is not null ? "plan.primary.ad.sync" : "plan.primary.ad"],
                        CurrentValue = nowText,
                        NewValue = becomesText,
                        Warning = aliasWarning,
                    };
                }
                else
                {
                    addressStep = new Change
                    {
                        TargetSystem = TargetSystem.Exchange,
                        ChangeAction = ChangeAction.ExchangeUpdateMailbox,
                        Target = old,
                        TargetName = p.Name,
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["WindowsEmailAddress"] = address },
                        Previous = new(StringComparer.OrdinalIgnoreCase) { ["WindowsEmailAddress"] = old },
                        Description = Texts.T["plan.primary.exchange"],
                        CurrentValue = nowText,
                        NewValue = becomesText,
                        Warning = aliasWarning,
                    };
                }
            }
        }

        // Manager: in AD an attribute (DN), in the cloud a separate command
        Change? setManager = null;
        if (desired.Manager is { } newKey && !string.Equals(newKey, current.Manager ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            var oldManager = ManagerOf(data, p);
            var newManager = newKey.Length == 0 ? null : data.SearchPerson(newKey);
            if (newKey.Length > 0 && newManager is null)
            {
                warnings.Add(Texts.T["code.attribute-plans.the-chosen-manager-is-not"]);
            }
            else if (viaAd)
            {
                if (newManager is not null && newManager.Ad is null)
                {
                    warnings.Add(Texts.T.Format("code.attribute-plans.has-no-ad-account-and", newManager.Name));
                }
                else
                {
                    parameters["manager"] = newManager?.Ad!.DistinguishedName ?? string.Empty;
                    previous["manager"] = p.Ad!.Manager;
                    now.Add($"Manager: {oldManager?.Name ?? Texts.T["plan.none"]}");
                    becomes.Add($"Manager: {newManager?.Name ?? Texts.T["plan.none"]}");
                }
            }
            else if (newManager is not null && newManager.Entra is null)
            {
                warnings.Add(Texts.T.Format("code.attribute-plans.does-not-exist-in-entra", newManager.Name));
            }
            else
            {
                setManager = new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetManager,
                    Target = p.Entra!.Id,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = newManager?.Entra!.Id ?? string.Empty },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["managerId"] = p.Entra.ManagerId },
                    Description = Texts.T["code.attribute-plans.set-the-manager-in-entra"],
                    CurrentValue = oldManager?.Name ?? Texts.T["plan.none"],
                    NewValue = newManager?.Name ?? Texts.T["plan.none"],
                };
            }
        }

        if (parameters.Count == 0 && setManager is null && cloudParameters.Count == 0 && addressStep is null)
        {
            return new PlanOutcome(Array.Empty<Change>(), new[] { warnings.Count > 0 ? string.Join(" ", warnings) : Localization.Texts.T["plan.nothing-changed"] });
        }

        var plan = new List<Change>();
        if (setManager is not null)
        {
            plan.Add(setManager);
        }

        if (addressStep is not null)
        {
            plan.Add(addressStep);
        }

        if (cloudParameters.Count > 0)
        {
            plan.Add(new()
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphSetAttributes,
                Target = p.Entra!.Id,
                TargetName = p.Name,
                Parameters = cloudParameters,
                Previous = cloudPrevious,
                Description = Texts.T.Format("attribute.entra-only", cloudParameters.Count),
                CurrentValue = string.Join("; ", cloudNow),
                NewValue = string.Join("; ", cloudBecomes),
            });
        }

        if (parameters.Count > 0)
        {
            plan.Insert(0, new()
            {
                TargetSystem = viaAd ? TargetSystem.ActiveDirectory : TargetSystem.Graph,
                ChangeAction = viaAd ? ChangeAction.AdSetAttributes : ChangeAction.GraphSetAttributes,
                Target = viaAd ? p.Ad!.DistinguishedName : p.Entra!.Id,
                TargetName = p.Name,
                Parameters = parameters,
                Previous = previous,
                Description = viaAd
                    ? Texts.T.Format(p.Entra is not null ? "plan.attributes.ad.sync" : "plan.attributes.ad", parameters.Count)
                    : Texts.T.Format("code.attribute-plans.change-details-in-entra", parameters.Count),
                CurrentValue = string.Join("; ", now),
                NewValue = string.Join("; ", becomes),
                Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            });
        }

        // Another department can be another division: switch the division group too (dynamic groups follow by
        // themselves)
        var errors = new List<string>();
        if (parameters.ContainsKey("department"))
        {
            var oldDivision = config.DivisionFor(current.Department);
            var newDivision = config.DivisionFor(desired.Department);
            if (!string.Equals(oldDivision?.Group, newDivision?.Group, StringComparison.OrdinalIgnoreCase))
            {
                var r = data.Index.Ref(p);
                foreach (var (division, added) in new[] { (oldDivision, false), (newDivision, true) })
                {
                    if (division?.Group is not { Length: > 0 } name)
                    {
                        continue;
                    }

                    if (config.IsDynamicGroup(name) || data.Index.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } groupValue)
                    {
                        errors.Add(Texts.T.Format("code.attribute-plans.follows-from-the-department-by", name));
                        continue;
                    }

                    if (groupValue.Dynamic)
                    {
                        errors.Add(Texts.T.Format("code.attribute-plans.is-dynamic-and-follows-from", name));
                        continue;
                    }

                    var u = added ? GroupPlans.AddMember(data, groupValue, r) : GroupPlans.RemoveMember(data, groupValue, r);
                    plan.AddRange(u.Plan);
                    errors.AddRange(u.Errors);
                }
            }
        }

        return new PlanOutcome(plan, errors);
    }

    /// <summary>
    /// Fixes a difference between AD and Entra in AD (for a synchronised account Entra then follows by itself): the
    /// value without superfluous spaces, and a department as it is spelled in the organisation.
    /// </summary>
    public static PlanOutcome Fix(InventoryData data, OrganisationConfig config, Difference v)
    {
        if (v.Kind != DifferenceKind.AttributeDiffers || v.Person.Ad is null)
        {
            return PlanOutcome.Error(Texts.T["code.attribute-plans.only-an-attribute-of-an"]);
        }

        // "Dienst (Department)" -> "Dienst"
        var shortValue = v.Attribute.Split(" (")[0].Trim();
        var field = Fields.FirstOrDefault(x => string.Equals(x.Name, shortValue, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Ad, v.Attribute, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Graph, v.Attribute, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Field, v.Attribute, StringComparison.OrdinalIgnoreCase));
        if (field.Field is null)
        {
            return PlanOutcome.Error(Texts.T.Format("code.attribute-plans.the-app-cannot-correct", v.Attribute));
        }

        var value = string.Join(' ', v.AdValue.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (field.Field == nameof(UserDesired.Department) && config.SearchDepartment(value) is { } department)
        {
            value = department.Department;
        }

        var desired = new UserDesired();
        typeof(UserDesired).GetProperty(field.Field)!.SetValue(desired, value);
        var output = User(data, config, v.Person, desired);
        return output.Plan.Count == 0
            ? PlanOutcome.Error(Texts.T.Format("code.attribute-plans.ad-already-has-the-difference", value))
            : output;
    }

    private static string? Value(UserDesired g, string field) => (string?)typeof(UserDesired).GetProperty(field)!.GetValue(g);

    /// <summary>A value a field can take (R21, checked while typing too): the usage location is two letters (ISO 3166),
    /// a phone number has only digits, spaces and + ( ) - . /, an employee id at most 16 characters (the limit of AD).</summary>
    public static bool ValidValue(string field, string value)
        => value.Length == 0 ? !Required.Contains(field) : field switch
        {
            nameof(UserDesired.UsageLocation) => value.Length == 2 && value.All(char.IsAsciiLetterUpper),
            nameof(UserDesired.AccountExpires) => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year is >= 2000 and < 9999,
            nameof(UserDesired.IpPhone) => value.Length <= 64,
            nameof(UserDesired.Phone) or nameof(UserDesired.Mobile) or nameof(UserDesired.HomePhone) or nameof(UserDesired.Fax) => value.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '(' or ')' or '-' or '.' or '/'),
            nameof(UserDesired.EmployeeId) => value.Length <= 16,
            nameof(UserDesired.PostalCode) => value.Length <= 40,
            nameof(UserDesired.Upn) or nameof(UserDesired.PrimaryAddress) => value.Length <= 113 && value.Count(c => c == '@') == 1 && value.IndexOf('@') > 0 && value.IndexOf('@') < value.Length - 1
                                       && !value.Any(c => char.IsWhiteSpace(c) || c is '"' or '\\' or '(' or ')' or ',' or ':' or ';' or '<' or '>' or '[' or ']'),
            _ => value.Length <= 256,
        };

    /// <summary>Fields that cannot be emptied: an account without a sign-in name or a name does not work.</summary>
    private static readonly string[] Required = { nameof(UserDesired.Upn), nameof(UserDesired.DisplayName), nameof(UserDesired.PrimaryAddress) };

    /// <summary>The account stops working at once: the last day is before today (R21).</summary>
    public static bool ExpiresBeforeToday(string day)
        => DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < DateTime.Today;

    /// <summary>Another account has this sign-in name already (R21): the plan would fail. The name of who has it, or
    /// null.</summary>
    public static string? Taken(InventoryData data, Person person, string field, string value)
    {
        var v = value.Trim();
        if (v.Length == 0)
        {
            return null;
        }

        return field switch
        {
            nameof(UserDesired.Upn) => data.Persons.FirstOrDefault(o => o.Key != person.Key && (string.Equals(o.Ad?.UserPrincipalName, v, StringComparison.OrdinalIgnoreCase)
                                                                                          || string.Equals(o.Entra?.UserPrincipalName, v, StringComparison.OrdinalIgnoreCase)))?.Name,
            nameof(UserDesired.PrimaryAddress) => MailboxPlans.AddressHolder(data, v, PrimaryAddressOf(person), person.Key),
            _ => null,
        };
    }

    /// <summary>The domains the UPNs of the tenant use now: a new UPN in another domain gets a warning.</summary>
    public static IReadOnlyCollection<string> UpnDomains(InventoryData data)
        => data.Persons.SelectMany(p => new[] { p.Ad?.UserPrincipalName, p.Entra?.IsGuest == true ? null : p.Entra?.UserPrincipalName })
            .Where(u => u is not null && u.Contains('@')).Select(u => u![(u!.IndexOf('@') + 1)..].ToLowerInvariant())
            .Where(d => !d.Contains("#ext#", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Another person has the same employee id (R21): a warning while typing, before the plan.</summary>
    public static Person? SameEmployeeId(InventoryData data, Person person, string employeeId)
        => employeeId.Trim().Length == 0 ? null
            : data.Persons.FirstOrDefault(o => o.Key != person.Key && (string.Equals(o.Ad?.EmployeeId, employeeId.Trim(), StringComparison.OrdinalIgnoreCase)
                                                                       || string.Equals(o.Entra?.EmployeeId, employeeId.Trim(), StringComparison.OrdinalIgnoreCase)));
}
