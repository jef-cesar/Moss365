using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>A new user as the administrator fills it in.</summary>
public sealed class NewUserRequest
{
    public string Population { get; set; } = string.Empty;
    public string GivenName { get; set; } = string.Empty;
    public string Surname { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Office { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string? LogonScript { get; set; }
    public string? LicenceSku { get; set; }
    public Person? Mirror { get; set; }
    public bool DefaultAdGroups { get; set; } = true;

    /// <summary>Id of a job profile (0.22): its groups, mailboxes, licences and attributes come along.</summary>
    public string? JobProfile { get; set; }

    /// <summary>Create the account in Entra instead of AD (0.31): always for a tenant without AD.</summary>
    public bool InCloud { get; set; }

    /// <summary>The domain of the new account in the cloud when the kind of user does not say it (0.31).</summary>
    public string? Domain { get; set; }
}

/// <summary>What the app suggests for a new user: the derived names and two plans (now and after the
/// synchronisation).</summary>
public sealed record NewUserProposal(
    string Sam,
    string Upn,
    string ViewName,
    string Dn,
    string Password,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<Change> Now,
    IReadOnlyList<Change> AfterSynchronisation)
{
    public bool CanBeExecuted => Errors.Count == 0;
}

/// <summary>Desired room settings; null means "do not change".</summary>
public sealed class RoomDesired
{
    public string? AutomateProcessing { get; set; }
    public int? BookingWindowInDays { get; set; }
    public int? MaximumDurationInMinutes { get; set; }
    public bool? AllowConflicts { get; set; }
    public bool? AllowRecurringMeetings { get; set; }
    public bool? AddOrganizerToSubject { get; set; }
    public bool? DeleteSubject { get; set; }
    public string? Building { get; set; }
    public string? Floor { get; set; }
    public string? FloorLabel { get; set; }
    public int? Capacity { get; set; }
    public string? DefaultCalendarPermission { get; set; }

    public static RoomDesired FromDefault(RoomDefault s) => new()
    {
        AutomateProcessing = s.AutomateProcessing,
        BookingWindowInDays = s.BookingWindowInDays,
        MaximumDurationInMinutes = s.MaximumDurationInMinutes,
        AllowConflicts = s.AllowConflicts,
        AllowRecurringMeetings = s.AllowRecurringMeetings,
        AddOrganizerToSubject = s.AddOrganizerToSubject,
        DeleteSubject = s.DeleteSubject,
        DefaultCalendarPermission = s.CalendarPermissionDefault,
    };
}

/// <summary>Which steps on offboarding; the default follows the decisions in docs/conventies.md.</summary>
public sealed class OffboardingOptions
{
    public bool Disable { get; set; } = true;
    public bool CompleteDescription { get; set; } = true;
    public bool Move { get; set; } = true;
    public bool RevokeSessions { get; set; } = true;
    public bool AdRemoveGroups { get; set; }
    public bool RemoveCloudGroups { get; set; }
    public bool ToSharedMailbox { get; set; }
    public bool RemoveLicences { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>Builds the plans (lists of changes) for the four kinds of management actions.</summary>
public static class PlanBuilder
{
    // ---------------------------------------------------------------- spiegelgebruiker

    /// <summary>The groups with status Add become changes along the right route (AD, Graph or Exchange).</summary>
    public static IReadOnlyList<Change> Mirror(InventoryData data, IReadOnlyList<MirrorRule> rules, Person target)
    {
        var plan = new List<Change>();
        var adDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in rules.Where(r => r.Status == MirrorStatus.Add))
        {
            switch (r.Layer, r.Route)
            {
                case ("AD", WriteRoute.ActiveDirectory) when target.Ad is not null:
                    if (adDns.Add(r.GroupId))
                    {
                        plan.Add(AdAddMember(target.Ad.DistinguishedName, target.Name, r.GroupId, r.Group));
                    }

                    break;
                case ("Cloud", WriteRoute.ActiveDirectory) when target.Ad is not null:
                    // Synchronised cloud group: become a member of the AD group behind it
                    var cloud = data.CloudGroups.GetValueOrDefault(r.GroupId);
                    var adGroup = cloud is null ? null : data.FindAdGroupBySam(cloud.OnPremisesSamAccountName);
                    var alreadyMember = adGroup is not null && data.AdGroupsOf(target).Any(g => string.Equals(g.DistinguishedName, adGroup.DistinguishedName, StringComparison.OrdinalIgnoreCase));
                    if (adGroup is not null && !alreadyMember && adDns.Add(adGroup.DistinguishedName))
                    {
                        plan.Add(AdAddMember(target.Ad.DistinguishedName, target.Name, adGroup.DistinguishedName, adGroup.Name));
                    }

                    break;
                case ("Cloud", WriteRoute.Graph) when target.Entra is not null:
                    plan.Add(GraphAddMember(target.Entra.Id, target.Name, r.GroupId, r.Group));
                    break;
                case ("Cloud", WriteRoute.Exchange) when target.Entra is not null:
                    plan.Add(ExchangeAddMember(target.Entra.UserPrincipalName, target.Name, data.CloudGroups.GetValueOrDefault(r.GroupId), r.Group, r.GroupId));
                    break;
            }
        }

        return plan;
    }

    // ---------------------------------------------------------------- rooms

    /// <summary>Changes to bring a room to the desired settings; only what differs.</summary>
    public static IReadOnlyList<Change> Room(Room room, RoomDesired desired)
    {
        var plan = new List<Change>();

        var processing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var old = new List<string>();
        var newValue = new List<string>();
        void Compare(string name, string current, string? targetValue)
        {
            if (targetValue is not null && !string.Equals(current, targetValue, StringComparison.OrdinalIgnoreCase))
            {
                processing[name] = targetValue;
                old.Add($"{name}={current}");
                newValue.Add($"{name}={targetValue}");
            }
        }

        Compare("AutomateProcessing", room.AutomateProcessing, desired.AutomateProcessing);
        Compare("BookingWindowInDays", room.BookingWindowInDays?.ToString() ?? string.Empty, desired.BookingWindowInDays?.ToString());
        Compare("MaximumDurationInMinutes", room.MaximumDurationInMinutes?.ToString() ?? string.Empty, desired.MaximumDurationInMinutes?.ToString());
        Compare("AllowConflicts", Bool(room.AllowConflicts), Bool(desired.AllowConflicts));
        Compare("AllowRecurringMeetings", Bool(room.AllowRecurringMeetings), Bool(desired.AllowRecurringMeetings));
        Compare("AddOrganizerToSubject", Bool(room.AddOrganizerToSubject), Bool(desired.AddOrganizerToSubject));
        Compare("DeleteSubject", Bool(room.DeleteSubject), Bool(desired.DeleteSubject));
        if (processing.Count > 0)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeRoomCalendarProcessing,
                Target = room.Smtp,
                TargetName = room.DisplayName,
                Parameters = processing,
                Description = Texts.T.Format("code.plan-builder.change-the-calendar-processing-of", room.DisplayName),
                CurrentValue = string.Join(", ", old),
                NewValue = string.Join(", ", newValue),
            });
        }

        var place = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        old = new List<string>();
        newValue = new List<string>();
        void ComparePlace(string name, string current, string? targetValue)
        {
            if (targetValue is not null && !string.Equals(current.Trim(), targetValue.Trim(), StringComparison.Ordinal))
            {
                place[name] = targetValue.Trim();
                old.Add($"{name}={current}");
                newValue.Add($"{name}={targetValue.Trim()}");
            }
        }

        ComparePlace("Building", room.Building, desired.Building);
        ComparePlace("Floor", room.Floor, desired.Floor);
        ComparePlace("FloorLabel", room.FloorLabel, desired.FloorLabel);
        ComparePlace("Capacity", room.Capacity?.ToString() ?? string.Empty, desired.Capacity?.ToString());
        if (place.Count > 0)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeRoomPlace,
                Target = room.Smtp,
                TargetName = room.DisplayName,
                Parameters = place,
                Description = Texts.T.Format("code.plan-builder.change-the-building-floor-or", room.DisplayName),
                CurrentValue = string.Join(", ", old),
                NewValue = string.Join(", ", newValue),
            });
        }

        if (desired.DefaultCalendarPermission is { } permission && !string.Equals(room.DefaultCalendarPermission, permission, StringComparison.OrdinalIgnoreCase))
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeCalendarPermission,
                Target = room.Smtp,
                TargetName = room.DisplayName,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["User"] = "Default", ["AccessRights"] = permission },
                Description = Texts.T.Format("code.plan-builder.set-the-default-permission-on", room.DisplayName),
                CurrentValue = room.DefaultCalendarPermission.Length > 0 ? room.DefaultCalendarPermission : Texts.T["kpi.value.unknown"],
                NewValue = permission,
            });
        }

        return plan;
    }

    /// <summary>Deviations of a room from the default, as readable text.</summary>
    public static IReadOnlyList<string> RoomDeviations(Room room, RoomDefault defaultValue)
    {
        var list = new List<string>();
        void Check(string name, string current, string expected)
        {
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
            {
                list.Add($"{name}: {(current.Length == 0 ? "(leeg)" : current)} (standaard {expected})");
            }
        }

        Check("AutomateProcessing", room.AutomateProcessing, defaultValue.AutomateProcessing);
        Check(Texts.T["room-detail.booking-window"], room.BookingWindowInDays?.ToString() ?? string.Empty, defaultValue.BookingWindowInDays.ToString());
        Check(Texts.T["room-detail.maximum-duration"], room.MaximumDurationInMinutes?.ToString() ?? string.Empty, defaultValue.MaximumDurationInMinutes.ToString());
        Check(Texts.T["room-form.allow-conflicts"], Bool(room.AllowConflicts), Bool(defaultValue.AllowConflicts));
        Check(Texts.T["room-form.recurring-meetings"], Bool(room.AllowRecurringMeetings), Bool(defaultValue.AllowRecurringMeetings));
        Check(Texts.T["room-form.organiser-in-subject"], Bool(room.AddOrganizerToSubject), Bool(defaultValue.AddOrganizerToSubject));
        Check(Texts.T["room-form.clear-subject"], Bool(room.DeleteSubject), Bool(defaultValue.DeleteSubject));
        Check(Texts.T["room-form.default-calendar-permission"], room.DefaultCalendarPermission, defaultValue.CalendarPermissionDefault);
        if (string.IsNullOrWhiteSpace(room.Building))
        {
            list.Add(Texts.T["code.plan-builder.building-empty"]);
        }

        if (room.Capacity is null or 0)
        {
            list.Add(Texts.T["code.plan-builder.capacity-empty"]);
        }

        return list;
    }

    // ---------------------------------------------------------------- new user

    public static NewUserProposal NewUser(InventoryData data, OrganisationConfig config, NewUserRequest request, string? password = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var population = config.SearchPopulation(request.Population);
        if (population is null)
        {
            errors.Add(Texts.T["code.plan-builder.choose-the-kind-of-account"]);
        }

        if (string.IsNullOrWhiteSpace(request.GivenName) || string.IsNullOrWhiteSpace(request.Surname))
        {
            errors.Add(Texts.T["code.plan-builder.first-name-and-last-name"]);
        }

        // A job profile fills in what the form leaves empty (0.22)
        var profile = JobProfiles.Find(config, request.JobProfile);
        if (request.JobProfile is { Length: > 0 } && profile is null)
        {
            errors.Add(Texts.T["code.plan-builder.the-chosen-job-profile-no"]);
        }

        var departmentValue = string.IsNullOrWhiteSpace(request.Department) ? profile?.Department ?? string.Empty : request.Department;
        var officeValue = string.IsNullOrWhiteSpace(request.Office) ? profile?.Office ?? string.Empty : request.Office;
        var titleValue = string.IsNullOrWhiteSpace(request.JobTitle) ? profile?.JobTitle ?? string.Empty : request.JobTitle;
        var scriptValue = request.LogonScript ?? profile?.LogonScript;

        var department = config.SearchDepartment(departmentValue);
        if (population is not null && population.City is null && department is null)
        {
            errors.Add(Texts.T["code.plan-builder.choose-a-department-from-the"]);
        }

        if (population is not null && population.City is null && string.IsNullOrWhiteSpace(officeValue))
        {
            errors.Add(Texts.T["code.plan-builder.choose-an-office-from-the"]);
        }

        var domainDn = data.DomainDn.Length > 0 ? data.DomainDn : config.DomainDn;
        if (domainDn.Length == 0)
        {
            errors.Add(Texts.T["code.plan-builder.the-ad-domain-is-unknown"]);
        }

        if (!data.HasAd)
        {
            warnings.Add(Texts.T["code.plan-builder.no-ad-data-is-loaded"]);
        }

        // Derive the names and make them unique
        var local = NamingSettings.Local(request.GivenName, request.Surname, config.Naming.LocalPart);
        var domain = population?.UpnDomain ?? string.Empty;
        var sam = NameRules.Sam(local);
        var upn = $"{local}@{domain}";
        if (local.Length > 0 && Exists(data, sam, upn))
        {
            var found = false;
            for (var n = 2; n <= 9 && !found; n++)
            {
                var s = NameRules.SamWithNumber(local, n);
                var u = $"{local}{n}@{domain}";
                if (!Exists(data, s, u))
                {
                    warnings.Add(Texts.T.Format("code.plan-builder.already-exists-suggested-check-that", local, local, n));
                    (sam, upn, found) = (s, u, true);
                }
            }

            if (!found)
            {
                errors.Add(Texts.T.Format("code.plan-builder.no-free-name-found-for", local));
            }
        }

        if (sam != upn.Split('@')[0])
        {
            warnings.Add(Texts.T.Format("code.plan-builder.samaccountname-is-limited-to-characters", NameRules.MaxSam, sam, upn));
        }

        var view = NamingSettings.Display(request.GivenName, request.Surname, config.Naming.DisplayName);
        var ouDn = population is null ? string.Empty : config.OuDn(population.Ou, domainDn);
        var cn = view;
        if (data.AdUsers.Any(a => string.Equals(a.DistinguishedName, $"CN={cn},{ouDn}", StringComparison.OrdinalIgnoreCase)))
        {
            cn = $"{view} ({sam})";
        }

        var dn = $"CN={EscapeCn(cn)},{ouDn}";
        password ??= NameRules.InitialPassword();

        var now = new List<Change>();
        var afterSync = new List<Change>();
        if (errors.Count == 0 && population is not null)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ou"] = ouDn,
                ["cn"] = cn,
                ["sAMAccountName"] = sam,
                ["userPrincipalName"] = upn,
                ["givenName"] = request.GivenName.Trim(),
                ["sn"] = request.Surname.Trim(),
                ["displayName"] = view,
                ["mail"] = upn,
                ["company"] = config.Company,
            };
            if (department is not null)
            {
                attributes["department"] = department.Department;
            }

            if (!string.IsNullOrWhiteSpace(officeValue))
            {
                attributes["physicalDeliveryOfficeName"] = officeValue.Trim();
            }

            if (!string.IsNullOrWhiteSpace(titleValue))
            {
                attributes["title"] = titleValue.Trim();
            }

            var script = scriptValue ?? department?.LogonScript;
            if (!string.IsNullOrWhiteSpace(script))
            {
                attributes["scriptPath"] = script.Trim();
            }

            if (!string.IsNullOrWhiteSpace(population.City))
            {
                attributes["l"] = population.City;
            }

            var create = new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdCreateUser,
                Target = dn,
                TargetName = view,
                Parameters = attributes,
                Description = Texts.T.Format("code.plan-builder.create-account-in-enabled-change", sam, population.Ou),
                NewValue = upn,
                Secret = password,
            };
            now.Add(create);

            // AD groups: default groups of the population and the groups of the mirror user
            var adGroups = new Dictionary<string, AdGroup>(StringComparer.OrdinalIgnoreCase);
            if (request.DefaultAdGroups)
            {
                foreach (var name in population.DefaultAdGroups)
                {
                    var g = data.FindAdGroupByName(name);
                    if (g is null)
                    {
                        warnings.Add(Texts.T.Format("code.plan-builder.default-group-not-found-in", name));
                    }
                    else
                    {
                        adGroups[g.DistinguishedName] = g;
                    }
                }
            }

            var cloudGroups = new Dictionary<string, CloudGroup>(StringComparer.OrdinalIgnoreCase);
            if (request.Mirror is { } mirror)
            {
                foreach (var g in data.AdGroupsOf(mirror).Where(g => !MirrorUser.IsPrivileged(g)))
                {
                    adGroups[g.DistinguishedName] = g;
                }

                foreach (var g in data.CloudGroupsOf(mirror).Where(g => !g.Dynamic && !config.IsDynamicGroup(g.DisplayName)))
                {
                    if (g.FromAd)
                    {
                        if (data.FindAdGroupBySam(g.OnPremisesSamAccountName) is { } underlying)
                        {
                            adGroups[underlying.DistinguishedName] = underlying;
                        }
                    }
                    else if (!IsDivisionGroup(config, g))
                    {
                        cloudGroups[g.Id] = g;
                    }
                }
            }

            if (profile is not null)
            {
                var accessGroups = profile.Mailboxes.Select(m => data.Index.Mailbox(m.Trim())).OfType<Mailbox>()
                    .Select(m => (Mailbox: m, Group: data.Index.AccessGroupOf(m))).ToList();
                foreach (var (m, g) in accessGroups.Where(x => x.Group is null))
                {
                    warnings.Add(Texts.T.Format("code.plan-builder.has-no-access-group-yet", m.DisplayName));
                }

                var names = profile.Groups.Concat(accessGroups.Where(x => x.Group is not null).Select(x => x.Group!.Name));
                foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var g = JobProfilePlans.FindGroup(data, name);
                    if (g is null)
                    {
                        warnings.Add(Texts.T.Format("code.plan-builder.group-from-the-job-profile", name));
                    }
                    else if (g.Dynamic || config.IsDynamicGroup(g.Name) || (g.Ad is { } privileged && MirrorUser.IsPrivileged(privileged)))
                    {
                        warnings.Add(Texts.T.Format("code.plan-builder.is-dynamic-or-privileged-not", g.Name));
                    }
                    else if (g.Ad is { } a)
                    {
                        adGroups[a.DistinguishedName] = a;
                    }
                    else if (g.Cloud is { } c)
                    {
                        cloudGroups[c.Id] = c;
                    }
                }
            }

            foreach (var g in adGroups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                now.Add(AdAddMember(dn, view, g.DistinguishedName, g.Name, create.Id));
            }

            // After the synchronisation to Entra: licence and cloud groups
            var licences = new[] { request.LicenceSku }.Concat(profile?.Licences ?? new List<string>())
                .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Change? location = null;
            var country = JobProfilePlans.UsageLocation(data);
            foreach (var sku in licences)
            {
                if (population.NoLicence && sku == licences[0])
                {
                    warnings.Add(Texts.T.Format("code.plan-builder.normally-gets-no-licence", population.Name));
                }

                var licence = data.Licences.FirstOrDefault(l => string.Equals(l.SkuPartNumber, sku, StringComparison.OrdinalIgnoreCase));
                if (licence is { Free: 0 })
                {
                    warnings.Add(Texts.T.Format("code.plan-builder.there-are-no-free-licences", sku, licence.Assigned, licence.Total));
                }

                if (location is null)
                {
                    location = new Change
                    {
                        TargetSystem = TargetSystem.Graph,
                        ChangeAction = ChangeAction.GraphSetAttributes,
                        Target = upn,
                        TargetName = view,
                        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["usageLocation"] = country },
                        Description = Texts.T.Format("plan.usage-location", country),
                        NewValue = country,
                    };
                    afterSync.Add(location);
                }

                afterSync.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphAssignLicence,
                    Target = upn,
                    TargetName = view,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["sku"] = sku, ["skuId"] = licence?.SkuId ?? string.Empty },
                    Description = Texts.T.Format("code.plan-builder.assign-licence", sku),
                    NewValue = sku,
                    DependentOf = location.Id,
                });
            }

            if (department is not null && config.DivisionFor(department.Department) is { Group: { Length: > 0 } divisionGroup })
            {
                if (data.FindCloudGroupByName(divisionGroup) is { } g)
                {
                    cloudGroups[g.Id] = g;
                }
                else
                {
                    warnings.Add(Texts.T.Format("code.plan-builder.division-group-not-found-in", divisionGroup));
                }
            }

            foreach (var g in cloudGroups.Values.OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                afterSync.Add(MirrorUser.RouteFor(g) == WriteRoute.Exchange
                    ? ExchangeAddMember(upn, view, g, g.DisplayName, g.Id)
                    : GraphAddMember(upn, view, g.Id, g.DisplayName));
            }
        }

        return new NewUserProposal(sam, upn, view, dn, password, errors, warnings, now, afterSync);
    }

    // ---------------------------------------------------------------- offboarding

    public static IReadOnlyList<Change> Offboarding(InventoryData data, OrganisationConfig config, Person person, OffboardingOptions options)
    {
        var plan = new List<Change>();
        var ad = person.Ad;
        var entra = person.Entra;
        var name = person.Name;

        if (ad is not null)
        {
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdDisableAccount,
                Target = ad.DistinguishedName,
                TargetName = name,
                Description = Texts.T["code.plan-builder.disable-ad-account"],
                CurrentValue = ad.Enabled ? "ingeschakeld" : Texts.T["code.plan-builder.already-disabled"],
                NewValue = "uitgeschakeld",
                Selected = options.Disable && ad.Enabled,
            });

            var description = Texts.T.Format("code.plan-builder.left", options.Date) + (string.IsNullOrWhiteSpace(ad.Description) ? string.Empty : " - " + ad.Description.Trim());
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.ActiveDirectory,
                ChangeAction = ChangeAction.AdSetAttributes,
                Target = ad.DistinguishedName,
                TargetName = name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["description"] = description },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["description"] = ad.Description },
                Description = Texts.T["code.plan-builder.put-the-leaving-date-in"],
                CurrentValue = ad.Description,
                NewValue = description,
                Selected = options.CompleteDescription,
            });

            foreach (var g in data.AdGroupsOf(person).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdRemoveGroupMember,
                    Target = ad.DistinguishedName,
                    TargetName = name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = g.DistinguishedName, ["groep"] = g.Name },
                    Description = Texts.T.Format("code.plan-builder.remove-from-ad-group", g.Name) + (MirrorUser.IsPrivileged(g) ? (" " + Texts.T["code.plan-builder.privileged"]) : string.Empty),
                    Selected = options.AdRemoveGroups || MirrorUser.IsPrivileged(g),
                });
            }

            var targetOu = config.Offboarding?.Ou;
            if (!string.IsNullOrWhiteSpace(targetOu))
            {
                // Move as the last AD step: after that the DN of the account changes
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.ActiveDirectory,
                    ChangeAction = ChangeAction.AdMove,
                    Target = ad.DistinguishedName,
                    TargetName = name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["doelOu"] = config.OuDn(targetOu, data.DomainDn) },
                    Description = Texts.T.Format("code.plan-builder.move-to", targetOu),
                    CurrentValue = ad.OuPath,
                    NewValue = targetOu,
                    Selected = options.Move && !string.Equals(ad.OuPath, targetOu, StringComparison.OrdinalIgnoreCase),
                });
            }
        }

        if (entra is not null)
        {
            if (ad is null && !entra.OnPremisesSyncEnabled)
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphSetAttributes,
                    Target = entra.Id,
                    TargetName = name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = entra.AccountEnabled ? "true" : "false" },
                    Description = Texts.T["code.plan-builder.disable-cloud-account-block-sign"],
                    CurrentValue = entra.AccountEnabled ? "ingeschakeld" : Texts.T["code.plan-builder.already-disabled"],
                    NewValue = "uitgeschakeld",
                    Selected = options.Disable && entra.AccountEnabled,
                });
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphRevokeSessions,
                Target = entra.Id,
                TargetName = name,
                Description = Texts.T["code.plan-builder.revoke-all-sign-in-sessions"],
                Selected = options.RevokeSessions,
            });

            foreach (var g in data.CloudGroupsOf(person).Where(g => !g.Dynamic && !g.FromAd).OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var exchange = MirrorUser.RouteFor(g) == WriteRoute.Exchange;
                plan.Add(new Change
                {
                    TargetSystem = exchange ? TargetSystem.Exchange : TargetSystem.Graph,
                    ChangeAction = exchange ? ChangeAction.ExchangeRemoveGroupMember : ChangeAction.GraphRemoveGroupMember,
                    Target = exchange ? entra.UserPrincipalName : entra.Id,
                    TargetName = name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepId"] = g.Id, ["groep"] = exchange && g.Mail.Length > 0 ? g.Mail : g.DisplayName },
                    Description = Texts.T.Format("code.plan-builder.remove-from-cloud-group", g.DisplayName),
                    Selected = options.RemoveCloudGroups,
                });
            }

            var convert = new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeConvertToSharedMailbox,
                Target = entra.UserPrincipalName,
                TargetName = name,
                Description = Texts.T["code.plan-builder.convert-the-mailbox-to-a"],
                Selected = options.ToSharedMailbox,
            };
            plan.Add(convert);

            if (entra.Licences.Count > 0)
            {
                plan.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphRemoveLicences,
                    Target = entra.Id,
                    TargetName = name,
                    Description = Texts.T.Format("code.plan-builder.remove-all-licences", string.Join(", ", entra.Licences)),
                    CurrentValue = string.Join(", ", entra.Licences),
                    Warning = Texts.T["code.plan-builder.without-a-licence-the-mailbox"],
                    Selected = options.RemoveLicences,
                });
            }
        }

        return plan;
    }

    // ---------------------------------------------------------------- hulpfuncties

    private static Change AdAddMember(string userDn, string name, string groupDn, string groupValue, Guid? dependentOf = null) => new()
    {
        TargetSystem = TargetSystem.ActiveDirectory,
        ChangeAction = ChangeAction.AdAddGroupMember,
        Target = userDn,
        TargetName = name,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepDn"] = groupDn, ["groep"] = groupValue },
        Description = Texts.T.Format("code.plan-builder.make-member-of-ad-group", groupValue),
        DependentOf = dependentOf,
    };

    private static Change GraphAddMember(string user, string name, string groupId, string groupValue) => new()
    {
        TargetSystem = TargetSystem.Graph,
        ChangeAction = ChangeAction.GraphAddGroupMember,
        Target = user,
        TargetName = name,
        Parameters = new(StringComparer.OrdinalIgnoreCase) { ["groepId"] = groupId, ["groep"] = groupValue },
        Description = Texts.T.Format("code.plan-builder.make-member-of-cloud-group", groupValue),
    };

    private static Change ExchangeAddMember(string upn, string name, CloudGroup? groupValue, string groupName, string groupId) => new()
    {
        TargetSystem = TargetSystem.Exchange,
        ChangeAction = ChangeAction.ExchangeAddGroupMember,
        Target = upn,
        TargetName = name,
        Parameters = new(StringComparer.OrdinalIgnoreCase)
        {
            ["groep"] = groupValue is { Mail.Length: > 0 } ? groupValue.Mail : groupName,
            ["groepId"] = groupId,
            ["groepNaam"] = groupName,
        },
        Description = groupName.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase) ? Texts.T.Format("plan.member-exchange.shared", groupName) : Texts.T.Format("plan.member-exchange", groupName),
    };

    private static bool Exists(InventoryData data, string sam, string upn)
        => data.AdUsers.Any(a => string.Equals(a.SamAccountName, sam, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(a.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(a.EmailAddress, upn, StringComparison.OrdinalIgnoreCase))
           || data.EntraUsers.Any(e => string.Equals(e.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(e.Mail, upn, StringComparison.OrdinalIgnoreCase));

    private static bool IsDivisionGroup(OrganisationConfig config, CloudGroup g)
        => config.Divisions.Any(a => string.Equals(a.Group, g.DisplayName, StringComparison.OrdinalIgnoreCase));

    private static string Bool(bool value) => value ? "True" : "False";

    /// <summary>null stays null: a field that is not filled in means "do not change".</summary>
    private static string? Bool(bool? value) => value is null ? null : Bool(value.Value);

    /// <summary>Commas, plus signs and the like must be escaped in a CN.</summary>
    public static string EscapeCn(string cn)
    {
        var output = new System.Text.StringBuilder();
        foreach (var c in cn)
        {
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=')
            {
                output.Append('\\');
            }

            output.Append(c);
        }

        return output.ToString();
    }
}
