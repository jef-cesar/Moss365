using Moss365.Core.Localization;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

public enum MirrorStatus
{
    Add,
    AlreadyMember,
    OnlyAtTarget,
    Automatic,
    DoNotCopy,
}

public enum WriteRoute
{
    No,
    ActiveDirectory,
    Graph,
    Exchange,
}

public sealed record MirrorRule(string Layer, string GroupId, string Group, string Type, MirrorStatus Status, WriteRoute Route, string Remark)
{
    public string StatusText => Status switch
    {
        MirrorStatus.Add => Texts.T["tenants.add"],
        MirrorStatus.AlreadyMember => Texts.T["code.mirror-user.already-a-member"],
        MirrorStatus.OnlyAtTarget => Texts.T["code.mirror-user.only-at-the-target"],
        MirrorStatus.Automatic => Texts.T["code.mirror-user.automatic"],
        _ => Texts.T["code.mirror-user.do-not-copy"],
    };

    public string RouteText => Route switch
    {
        WriteRoute.ActiveDirectory => "AD",
        WriteRoute.Graph => "Graph",
        WriteRoute.Exchange => "Exchange",
        _ => "-",
    };
}

/// <summary>
/// Compares the groups of a target user with those of a reference user (the "mirror"), in AD and in the cloud, and
/// decides per group what has to happen and by which route that could be done.
/// </summary>
public static class MirrorUser
{
    private static readonly HashSet<string> PrivilegedGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "Domain Admins", "Enterprise Admins", "Schema Admins", "Administrators", "Account Operators", "Server Operators",
        "Print Operators", "Backup Operators", "Group Policy Creator Owners", "DnsAdmins", "Key Admins", "Enterprise Key Admins",
    };

    public static IReadOnlyList<MirrorRule> Compute(InventoryData data, OrganisationConfig config, Person reference, Person target)
    {
        var rules = new List<MirrorRule>();
        rules.AddRange(ComputeAd(data, reference, target));
        rules.AddRange(ComputeCloud(data, config, reference, target));
        return rules
            .OrderBy(r => r.Layer)
            .ThenBy(r => r.Status)
            .ThenBy(r => r.Group, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool IsPrivileged(AdGroup groupValue)
        => PrivilegedGroups.Contains(groupValue.Name)
           || groupValue.OuPath.Equals("Builtin", StringComparison.OrdinalIgnoreCase)
           || groupValue.Name.EndsWith(" Admins", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<MirrorRule> ComputeAd(InventoryData data, Person reference, Person target)
    {
        const string layer = "AD";
        var ofRef = data.AdGroupsOf(reference).ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);
        var ofTarget = data.AdGroupsOf(target).ToDictionary(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase);

        foreach (var g in ofRef.Values)
        {
            var type = AdType(g);
            if (IsPrivileged(g))
            {
                yield return new MirrorRule(layer, g.DistinguishedName, g.Name, type, MirrorStatus.DoNotCopy, WriteRoute.No, Texts.T["code.mirror-user.privileged-group-never-through-a"]);
            }
            else if (ofTarget.ContainsKey(g.DistinguishedName))
            {
                yield return new MirrorRule(layer, g.DistinguishedName, g.Name, type, MirrorStatus.AlreadyMember, WriteRoute.No, string.Empty);
            }
            else if (target.Ad is null)
            {
                yield return new MirrorRule(layer, g.DistinguishedName, g.Name, type, MirrorStatus.DoNotCopy, WriteRoute.No, Texts.T["code.mirror-user.the-target-user-has-no"]);
            }
            else
            {
                yield return new MirrorRule(layer, g.DistinguishedName, g.Name, type, MirrorStatus.Add, WriteRoute.ActiveDirectory, AdRemark(g));
            }
        }

        foreach (var g in ofTarget.Values.Where(g => !ofRef.ContainsKey(g.DistinguishedName)))
        {
            yield return new MirrorRule(layer, g.DistinguishedName, g.Name, AdType(g), MirrorStatus.OnlyAtTarget, WriteRoute.No,
                IsPrivileged(g) ? Texts.T["code.mirror-user.privileged-group"] : string.Empty);
        }
    }

    private static IEnumerable<MirrorRule> ComputeCloud(InventoryData data, OrganisationConfig config, Person reference, Person target)
    {
        const string layer = "Cloud";
        var ofRef = data.CloudGroupsOf(reference).ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var ofTarget = data.CloudGroupsOf(target).ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var g in ofRef.Values)
        {
            if (g.Dynamic || config.IsDynamicGroup(g.DisplayName))
            {
                var rule = g.MembershipRule.Length > 0 ? g.MembershipRule : config.DynamicRules.FirstOrDefault(r => r.Group == g.DisplayName)?.Rule ?? string.Empty;
                var status = ofTarget.ContainsKey(g.Id) ? MirrorStatus.AlreadyMember : MirrorStatus.Automatic;
                yield return new MirrorRule(layer, g.Id, g.DisplayName, g.TypeText, status, WriteRoute.No,
                    Texts.T.Format("code.mirror-user.dynamic-group-follows-from-the", rule));
            }
            else if (ofTarget.ContainsKey(g.Id))
            {
                yield return new MirrorRule(layer, g.Id, g.DisplayName, g.TypeText, MirrorStatus.AlreadyMember, WriteRoute.No, string.Empty);
            }
            else if (target.Entra is null)
            {
                yield return new MirrorRule(layer, g.Id, g.DisplayName, g.TypeText, MirrorStatus.DoNotCopy, WriteRoute.No,
                    Texts.T["code.mirror-user.the-target-user-does-not"]);
            }
            else if (g.FromAd)
            {
                yield return new MirrorRule(layer, g.Id, g.DisplayName, g.TypeText, MirrorStatus.Add, WriteRoute.ActiveDirectory,
                    Texts.T["code.mirror-user.synchronised-group-change-the-membership"]);
            }
            else
            {
                yield return new MirrorRule(layer, g.Id, g.DisplayName, g.TypeText, MirrorStatus.Add, RouteFor(g), CloudRemark(g, config));
            }
        }

        foreach (var g in ofTarget.Values.Where(g => !ofRef.ContainsKey(g.Id)))
        {
            yield return new MirrorRule(layer, g.Id, g.DisplayName, g.TypeText, MirrorStatus.OnlyAtTarget, WriteRoute.No,
                g.Dynamic ? Texts.T["code.mirror-user.dynamic-group"] : string.Empty);
        }
    }

    /// <summary>Graph cannot change members of mail-enabled security groups and distribution lists; that needs
    /// Exchange.</summary>
    public static WriteRoute RouteFor(CloudGroup groupValue) => groupValue.Type switch
    {
        CloudGroupType.MailSecurity or CloudGroupType.Distribution => WriteRoute.Exchange,
        _ => WriteRoute.Graph,
    };

    private static string AdType(AdGroup g)
        => string.Join(", ", new[] { g.Category, g.Scope }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>A remark on an AD group; organisation-specific remarks become configurable (R9).</summary>
    private static string AdRemark(AdGroup g) => string.Empty;

    private static string CloudRemark(CloudGroup g, OrganisationConfig config)
    {
        var remarks = new List<string>();
        if (g.DisplayName.StartsWith("SG-SM-", StringComparison.OrdinalIgnoreCase))
        {
            remarks.Add(Texts.T["code.mirror-user.access-to-a-shared-mailbox"]);
        }

        if (config.Divisions.Any(a => string.Equals(a.Group, g.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            remarks.Add(Texts.T["code.mirror-user.division-group-should-follow-from"]);
        }

        if (g.GroupLicence)
        {
            remarks.Add(Texts.T["code.mirror-user.assigns-a-licence"]);
        }

        return string.Join("; ", remarks);
    }
}
