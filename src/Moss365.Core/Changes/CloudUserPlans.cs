using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>
/// A new user in the cloud (0.31), for a tenant without AD (or by choice): the account in Entra through Graph, with
/// its licences and cloud groups in the same plan. Mail groups (and so access to shared mailboxes) wait until the
/// mailbox exists: they become a follow-up task. The first password lives only in the plan, never in a log.
/// </summary>
public static class CloudUserPlans
{
    /// <summary>The domains of the members in Entra, the most used first (onmicrosoft.com last).</summary>
    public static IReadOnlyList<string> Domains(InventoryData d)
        => d.EntraUsers.Where(e => !e.IsGuest && e.UserPrincipalName.Contains('@'))
            .Select(e => e.UserPrincipalName[(e.UserPrincipalName.IndexOf('@') + 1)..].ToLowerInvariant())
            .GroupBy(x => x)
            .OrderBy(g => g.Key.EndsWith(".onmicrosoft.com", StringComparison.Ordinal))
            .ThenByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

    public static NewUserProposal NewUser(InventoryData data, OrganisationConfig config, NewUserRequest request, string? password = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var population = string.IsNullOrWhiteSpace(request.Population) ? null : config.SearchPopulation(request.Population);
        if (!string.IsNullOrWhiteSpace(request.Population) && population is null)
        {
            errors.Add(Texts.T.Format("code.cloud-user-plans.the-kind-is-not-in", request.Population));
        }

        if (string.IsNullOrWhiteSpace(request.GivenName) || string.IsNullOrWhiteSpace(request.Surname))
        {
            errors.Add(Texts.T["code.plan-builder.first-name-and-last-name"]);
        }

        var profile = JobProfiles.Find(config, request.JobProfile);
        if (request.JobProfile is { Length: > 0 } && profile is null)
        {
            errors.Add(Texts.T["code.plan-builder.the-chosen-job-profile-no"]);
        }

        var departmentValue = string.IsNullOrWhiteSpace(request.Department) ? profile?.Department ?? string.Empty : request.Department;
        var officeValue = string.IsNullOrWhiteSpace(request.Office) ? profile?.Office ?? string.Empty : request.Office;
        var titleValue = string.IsNullOrWhiteSpace(request.JobTitle) ? profile?.JobTitle ?? string.Empty : request.JobTitle;
        var department = config.SearchDepartment(departmentValue);
        if (population is { City: null } && config.Departments.Count > 0 && department is null)
        {
            errors.Add(Texts.T["code.plan-builder.choose-a-department-from-the"]);
        }

        var domain = (population?.UpnDomain is { Length: > 0 } fromPopulation ? fromPopulation : request.Domain ?? Domains(data).FirstOrDefault() ?? string.Empty).Trim().ToLowerInvariant();
        if (domain.Length == 0)
        {
            errors.Add(Texts.T["code.cloud-user-plans.choose-the-domain-of-the"]);
        }
        else if (data.EntraUsers.Count > 0 && !Domains(data).Contains(domain))
        {
            warnings.Add(Texts.T.Format("code.cloud-user-plans.no-account-uses-the-domain", domain));
        }

        if (!data.HasEntra)
        {
            warnings.Add(Texts.T["code.cloud-user-plans.no-entra-data-is-loaded"]);
        }

        var local = NamingSettings.Local(request.GivenName, request.Surname, config.Naming.LocalPart);
        var upn = $"{local}@{domain}";
        if (local.Length > 0 && Exists(data, upn))
        {
            var found = false;
            for (var n = 2; n <= 9 && !found; n++)
            {
                if (!Exists(data, $"{local}{n}@{domain}"))
                {
                    warnings.Add(Texts.T.Format("code.plan-builder.already-exists-suggested-check-that", local, local, n));
                    (upn, local, found) = ($"{local}{n}@{domain}", $"{local}{n}", true);
                }
            }

            if (!found)
            {
                errors.Add(Texts.T.Format("code.plan-builder.no-free-name-found-for", local));
            }
        }

        var view = NamingSettings.Display(request.GivenName, request.Surname, config.Naming.DisplayName);
        password ??= NameRules.InitialPassword();
        var now = new List<Change>();
        var later = new List<Change>();
        if (errors.Count > 0)
        {
            return new NewUserProposal(string.Empty, upn, view, string.Empty, password, errors, warnings, now, later);
        }

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["displayName"] = view,
            ["mailNickname"] = local,
            ["givenName"] = request.GivenName.Trim(),
            ["surname"] = request.Surname.Trim(),
            ["companyName"] = config.Company,
            ["usageLocation"] = JobProfilePlans.UsageLocation(data),
        };
        if (department is not null || departmentValue.Trim().Length > 0)
        {
            parameters["department"] = department?.Department ?? departmentValue.Trim();
        }

        if (officeValue.Trim().Length > 0)
        {
            parameters["officeLocation"] = officeValue.Trim();
        }

        if (titleValue.Trim().Length > 0)
        {
            parameters["jobTitle"] = titleValue.Trim();
        }

        var create = new Change
        {
            TargetSystem = TargetSystem.Graph,
            ChangeAction = ChangeAction.GraphCreateUser,
            Target = upn,
            TargetName = view,
            Parameters = parameters,
            Description = Texts.T.Format("code.cloud-user-plans.create-cloud-account-in-entra", upn),
            NewValue = upn,
            Secret = password,
        };
        now.Add(create);

        // Licences right away: the usage location is set when the account is made
        var licences = new[] { request.LicenceSku }.Concat(profile?.Licences ?? new List<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (population is { NoLicence: true } && licences.Count > 0)
        {
            warnings.Add(Texts.T.Format("code.plan-builder.normally-gets-no-licence", population.Name));
        }

        foreach (var sku in licences)
        {
            var licence = data.Licences.FirstOrDefault(l => string.Equals(l.SkuPartNumber, sku, StringComparison.OrdinalIgnoreCase));
            if (licence is { Free: 0 })
            {
                warnings.Add(Texts.T.Format("code.plan-builder.there-are-no-free-licences", sku, licence.Assigned, licence.Total));
            }

            now.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphAssignLicence,
                Target = upn,
                TargetName = view,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["sku"] = sku, ["skuId"] = licence?.SkuId ?? string.Empty },
                Description = Texts.T.Format("code.plan-builder.assign-licence", sku),
                NewValue = sku,
                DependentOf = create.Id,
            });
        }

        // Groups: the job profile (with the access groups of its mailboxes), the mirror user, the division group
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        void Add(Group? g, string name)
        {
            if (g is null)
            {
                warnings.Add(Texts.T.Format("code.cloud-user-plans.group-not-found-skipped", name));
            }
            else if (g.Dynamic || config.IsDynamicGroup(g.Name) || (g.Ad is { } privileged && MirrorUser.IsPrivileged(privileged)))
            {
                warnings.Add(Texts.T.Format("code.cloud-user-plans.is-dynamic-or-privileged-not", g.Name));
            }
            else if (g.Ad is not null)
            {
                warnings.Add(Texts.T.Format("code.cloud-user-plans.is-an-ad-group-a", g.Name));
            }
            else
            {
                groups[g.Key] = g;
            }
        }

        if (profile is not null)
        {
            foreach (var name in profile.Groups)
            {
                Add(JobProfilePlans.FindGroup(data, name), name);
            }

            foreach (var smtp in profile.Mailboxes)
            {
                if (data.Index.Mailbox(smtp.Trim()) is not { } mailbox)
                {
                    warnings.Add(Texts.T.Format("code.cloud-user-plans.mailbox-not-found-skipped", smtp));
                }
                else if (data.Index.AccessGroupOf(mailbox) is { } access)
                {
                    Add(access, access.Name);
                }
                else
                {
                    warnings.Add(Texts.T.Format("code.cloud-user-plans.has-no-access-group-yet", mailbox.DisplayName));
                }
            }
        }

        if (request.Mirror is { } mirror)
        {
            foreach (var g in data.Index.GroupsOf(mirror).Where(g => !g.Dynamic && !config.Divisions.Any(a => string.Equals(a.Group, g.Name, StringComparison.OrdinalIgnoreCase))))
            {
                Add(g, g.Name);
            }
        }

        if (department is not null && config.DivisionFor(department.Department) is { Group: { Length: > 0 } divisionGroup })
        {
            Add(data.Index.Groups.FirstOrDefault(g => string.Equals(g.Name, divisionGroup, StringComparison.OrdinalIgnoreCase)), divisionGroup);
        }

        foreach (var g in groups.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (g.Route == WriteRoute.Exchange)
            {
                // A mail group needs the mailbox of the new account: Exchange makes it a few minutes after the licence
                later.Add(new Change
                {
                    TargetSystem = TargetSystem.Exchange,
                    ChangeAction = ChangeAction.ExchangeAddGroupMember,
                    Target = upn,
                    TargetName = view,
                    Parameters = GroupPlans.P(("groep", GroupPlans.ExoIdentity(g)), ("groepId", g.Cloud?.Id ?? string.Empty), ("groepNaam", g.Name)),
                    Description = Texts.T.Format(g.Name.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase) ? "plan.member-exchange.shared" : "plan.member-exchange", g.Name),
                });
            }
            else
            {
                now.Add(new Change
                {
                    TargetSystem = TargetSystem.Graph,
                    ChangeAction = ChangeAction.GraphAddGroupMember,
                    Target = upn,
                    TargetName = view,
                    Parameters = GroupPlans.P(("groepId", g.Cloud!.Id), ("groep", g.Name)),
                    Description = Texts.T.Format("code.plan-builder.make-member-of-cloud-group", g.Name),
                    DependentOf = create.Id,
                });
            }
        }

        return new NewUserProposal(string.Empty, upn, view, string.Empty, password, errors, warnings, now, later);
    }

    private static bool Exists(InventoryData d, string upn)
        => d.EntraUsers.Any(e => string.Equals(e.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Mail, upn, StringComparison.OrdinalIgnoreCase))
           || d.AdUsers.Any(a => string.Equals(a.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
}
