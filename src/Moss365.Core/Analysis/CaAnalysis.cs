using Moss365.Core.Localization;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

/// <summary>Does a policy apply? "Maybe" when the app cannot say for sure with the data it read.</summary>
public enum CaApplies
{
    Yes,
    No,
    Maybe,
}

/// <summary>An answer with the reason in plain language.</summary>
public sealed record CaAnswer(CaApplies Applies, string Reason)
{
    public static CaAnswer Yes(string reason) => new(CaApplies.Yes, reason);
    public static CaAnswer No(string reason) => new(CaApplies.No, reason);
    public static CaAnswer Maybe(string reason) => new(CaApplies.Maybe, reason);
}

/// <summary>What Conditional Access knows about a user: groups (also via nested groups) and admin roles.</summary>
public sealed record CaUser(string Id, string Name, bool IsGuest, IReadOnlySet<string> Groups, IReadOnlySet<string> Roles, bool GroupsFull)
{
    /// <summary>Null when the person has no Entra account (Conditional Access applies only in Entra).</summary>
    public static CaUser? Of(Person p, InventoryData d)
    {
        if (p.Entra is not { } e)
        {
            return null;
        }

        var ca = d.ConditionalAccess;
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (d.CloudMembership.TryGetValue(e.Id, out var direct))
        {
            groups.UnionWith(direct);
        }

        if (ca?.GroupMembers is { } members)
        {
            groups.UnionWith(members.Where(g => g.Value.Contains(e.Id, StringComparer.OrdinalIgnoreCase)).Select(g => g.Key));
        }

        var roleIdByName = ca?.Roles.GroupBy(r => r.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase)
                          ?? new Dictionary<string, string>();
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in d.Security?.RolesOf(e.Id) ?? Enumerable.Empty<RoleMember>())
        {
            if (r.RoleTemplateId.Length > 0)
            {
                roles.Add(r.RoleTemplateId);
            }
            else if (roleIdByName.TryGetValue(r.Role, out var id))
            {
                roles.Add(id);
            }
            else if (r.Role == "Global Administrator")
            {
                roles.Add(CaData.RoleGlobalAdmin);
            }
        }

        return new CaUser(e.Id, p.Name, e.IsGuest, groups, roles, ca?.GroupMembers is not null);
    }
}

/// <summary>A policy in plain language: who, which apps, under which conditions and with which consequence.</summary>
public sealed record CaExplanation(IReadOnlyList<string> Who, IReadOnlyList<string> Apps, IReadOnlyList<string> Conditions, string Consequence, IReadOnlyList<string> Session)
{
    public string Short => $"{string.Join("; ", Who)} - {string.Join("; ", Apps)}: {Consequence}";
}

/// <summary>Explanation and scope of Conditional Access policies.</summary>
public static class CaReader
{
    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All"] = "alle cloud-apps",
        ["None"] = "geen enkele app",
        ["Office365"] = "Office 365 (Exchange, SharePoint, OneDrive, Teams ...)",
        ["MicrosoftAdminPortals"] = "de Microsoft-beheerportalen",
        ["00000002-0000-0ff1-ce00-000000000000"] = "Exchange Online",
        ["00000003-0000-0ff1-ce00-000000000000"] = "SharePoint Online",
        ["cc15fd57-2c6c-4117-a88c-83b1d56b4bbe"] = "Microsoft Teams",
        ["797f4846-ba00-4fd7-ba43-dac1f8f63013"] = "Azure-beheer",
        ["00000003-0000-0000-c000-000000000000"] = "Microsoft Graph",
        ["0000000a-0000-0000-c000-000000000000"] = "Microsoft Intune",
        ["d4ebce55-015a-49b5-a083-c84d1797ae8c"] = "Intune-inschrijving",
    };

    /// <summary>The apps that belong to Office 365 (a policy for one of them also applies in the Office 365
    /// scenario).</summary>
    private static readonly HashSet<string> OfficeApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Office365", "00000002-0000-0ff1-ce00-000000000000", "00000003-0000-0ff1-ce00-000000000000", "cc15fd57-2c6c-4117-a88c-83b1d56b4bbe",
    };

    public const string ActionSecurityData = "urn:user:registersecurityinfo";

    public static string AppName(string id) => AppNames.TryGetValue(id, out var n) ? Texts.T.Text("ca-app." + id, n) : Texts.T.Format("code.ca-analysis.app", id);

    /// <summary>The apps the app knows by name (for the editor).</summary>
    public static IReadOnlyCollection<string> KnownApps => AppNames.Keys;

    public static string ActionName(string action) => action.ToLowerInvariant() switch
    {
        ActionSecurityData => "beveiligingsgegevens registreren (MFA instellen)",
        "urn:user:registerdevice" => Texts.T["code.ca-analysis.registering-or-joining-a-device"],
        _ => action,
    };

    public static string ClientName(string c) => c switch
    {
        "all" => Texts.T["ca-analysis.all-kinds"],
        "browser" => Texts.T["ca-overview.the-browser"],
        "mobileAppsAndDesktopClients" => Texts.T["code.ca-analysis.apps-and-desktop-programs"],
        "exchangeActiveSync" => Texts.T["ca-analysis.exchange-activesync-legacy"],
        "other" => Texts.T["ca-analysis.other-legacy-programs-imap-pop-smtp"],
        _ => c,
    };

    public static string PlatformName(string p) => p.ToLowerInvariant() switch
    {
        "all" => Texts.T["ca-analysis.all-platforms"],
        "windows" => "Windows",
        "macos" => "macOS",
        "ios" => "iOS",
        "android" => "Android",
        "linux" => "Linux",
        "windowsphone" => "Windows Phone",
        _ => p,
    };

    public static string CheckName(string c) => c switch
    {
        "block" => Texts.T["ca-analysis.block"],
        "mfa" => "MFA",
        "compliantDevice" => Texts.T["code.ca-analysis.a-compliant-device-intune"],
        "domainJoinedDevice" => Texts.T["code.ca-analysis.a-device-that-is-hybrid"],
        "approvedApplication" => Texts.T["code.ca-analysis.an-approved-app"],
        "compliantApplication" => Texts.T["code.ca-analysis.an-app-with-an-app"],
        "passwordChange" => Texts.T["code.ca-analysis.changing-the-password"],
        _ => c,
    };

    public static string RiskName(string r) => r switch
    {
        "low" => Texts.T["ca-analysis.low"],
        "medium" => Texts.T["ca-analysis.medium"],
        "high" => Texts.T["ca-analysis.high"],
        "none" => Texts.T["security-card.none"],
        _ => r,
    };

    public static string StatusName(CaStatus s) => s switch
    {
        CaStatus.On => Texts.T["ca-editor.state.on"],
        CaStatus.Report => Texts.T["word.reportonly"],
        _ => Texts.T["word.off"],
    };

    public static string LocationName(string id, CaData ca) => id switch
    {
        "All" => Texts.T["ca-analysis.every-location"],
        "AllTrusted" => Texts.T["ca-analysis.all-trusted-locations"],
        _ => ca.Location(id) is { } l ? l.Name : Texts.T.Format("code.ca-analysis.location", id),
    };

    // ------------------------------------------------------------------ explanation

    public static CaExplanation Explanation(CaPolicy b, CaData ca, Func<string, string> name)
    {
        string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(name));
        string Roles(IEnumerable<string> ids) => string.Join(", ", ids.Select(ca.RoleName));

        var who = new List<string>();
        var realIn = b.UsersIn.Where(u => u is not ("All" or "None" or "GuestsOrExternalUsers")).ToList();
        if (b.UsersIn.Contains("All"))
        {
            who.Add(Texts.T["word.allusers"]);
        }
        else if (b.UsersIn.Contains("None") && b.GroupsIn.Count + b.RolesIn.Count == 0 && !b.GuestsIn)
        {
            who.Add(Texts.T["code.ca-analysis.nobody-the-policy-applies-to"]);
        }
        else
        {
            if (realIn.Count > 0) who.Add(Texts.T.Format("code.ca-analysis.users", Names(realIn)));
            if (b.GroupsIn.Count > 0) who.Add(Texts.T.Format("code.ca-analysis.members-of", Names(b.GroupsIn)));
            if (b.RolesIn.Count > 0) who.Add(Texts.T.Format("code.ca-analysis.with-the-role", Roles(b.RolesIn)));
            if (b.GuestsIn) who.Add(Texts.T["ca-policy-editor.guests-and-external-users"]);
            if (who.Count == 0) who.Add(Texts.T["word.nobody"]);
        }

        var realOff = b.UsersOff.Where(u => u != "GuestsOrExternalUsers").ToList();
        if (realOff.Count > 0) who.Add(Texts.T.Format("code.ca-analysis.except", Names(realOff)));
        if (b.GroupsOff.Count > 0) who.Add(Texts.T.Format("code.ca-analysis.except-members-of", Names(b.GroupsOff)));
        if (b.RolesOff.Count > 0) who.Add(Texts.T.Format("code.ca-analysis.except-who-has-the-role", Roles(b.RolesOff)));
        if (b.GuestsOff) who.Add(Texts.T["ca-analysis.except-guests"]);

        var apps = new List<string>();
        if (b.UserActions.Count > 0)
        {
            apps.Add(Texts.T.Format("code.ca-analysis.when", string.Join((" " + Texts.T["keys.or"] + " "), b.UserActions.Select(ActionName))));
        }
        else if (b.AppsIn.Count > 0)
        {
            apps.Add(Texts.T.Format("code.ca-analysis.for", string.Join(", ", b.AppsIn.Select(AppName))));
        }

        if (b.AppsOff.Count > 0) apps.Add(Texts.T.Format("code.ca-analysis.except-2", string.Join(", ", b.AppsOff.Select(AppName))));
        if (apps.Count == 0) apps.Add(Texts.T["ca-analysis.no-apps"]);

        var conditions = new List<string>();
        if (b.ClientApps.Count > 0 && !b.ClientApps.Contains("all"))
        {
            conditions.Add(Texts.T.Format("code.ca-analysis.only-through", string.Join(", ", b.ClientApps.Select(ClientName))));
        }

        if (b.PlatformsIn.Count > 0) conditions.Add(Texts.T.Format("ca-analysis.on", string.Join(", ", b.PlatformsIn.Select(PlatformName))));
        if (b.PlatformsOff.Count > 0) conditions.Add(Texts.T.Format("code.ca-analysis.not-on", string.Join(", ", b.PlatformsOff.Select(PlatformName))));
        if (b.LocationsIn.Count > 0 && !(b.LocationsIn.Count == 1 && b.LocationsIn[0] == "All"))
        {
            conditions.Add(Texts.T.Format("code.ca-analysis.from", string.Join(", ", b.LocationsIn.Select(l => LocationName(l, ca)))));
        }

        if (b.LocationsOff.Count > 0) conditions.Add(Texts.T.Format("code.ca-analysis.not-from", string.Join(", ", b.LocationsOff.Select(l => LocationName(l, ca)))));
        if (b.SignInRisk.Count > 0) conditions.Add(Texts.T.Format("code.ca-analysis.at-a-sign-in-with", string.Join(", ", b.SignInRisk.Select(RiskName))));
        if (b.UsersRisk.Count > 0) conditions.Add(Texts.T.Format("code.ca-analysis.for-a-user-with-risk", string.Join(", ", b.UsersRisk.Select(RiskName))));
        if (b.DeviceFilter) conditions.Add(Texts.T["code.ca-analysis.with-a-filter-on-devices"]);
        if (b.Transfer.Contains("deviceCodeFlow")) conditions.Add(Texts.T["code.ca-analysis.when-signing-in-with-a"]);
        if (b.Transfer.Contains("authenticationTransfer")) conditions.Add(Texts.T["code.ca-analysis.when-passing-a-sign-in"]);
        if (conditions.Count == 0) conditions.Add(Texts.T["code.ca-analysis.always-no-extra-conditions"]);

        string consequence;
        if (b.Blocks)
        {
            consequence = Texts.T["ca-policy-editor.block-access"];
        }
        else
        {
            var requirements = b.Checks.Where(c => !(c == "mfa" && b.StrengthId is not null)).Select(CheckName).ToList();
            if (b.StrengthId is not null) requirements.Insert(0, Texts.T.Format("code.ca-analysis.mfa-with-strength", (b.StrengthName ?? ca.Strengths.FirstOrDefault(s => s.Id == b.StrengthId)?.Name ?? b.StrengthId)));
            if (b.TermsOfUse) requirements.Add(Texts.T["code.ca-analysis.accepting-the-terms-of-use"]);
            consequence = requirements.Count switch
            {
                0 => Texts.T["code.ca-analysis.allow-access-no-requirement"],
                1 => Texts.T.Format("code.ca-analysis.access-provided", requirements[0]),
                _ => Texts.T.Format("code.ca-analysis.access-provided", string.Join(b.Operator == "AND" ? (" " + Texts.T["code.ca-analysis.and"] + " ") : (" " + Texts.T["keys.or"] + " "), requirements)),
            };
        }

        var session = new List<string>();
        if (b.SignInFrequency is { } f) session.Add(Texts.T.Format("code.ca-analysis.sign-in-again", f));
        if (b.PersistentBrowser is { } br) session.Add(Texts.T.Format("code.ca-analysis.browser", br));

        return new CaExplanation(who, apps, conditions, consequence, session);
    }

    // ------------------------------------------------------------------ scope (who)

    /// <summary>Does this user fall under the policy (only the "who" part)? Exclusions win.</summary>
    public static CaAnswer Scope(CaPolicy b, CaUser g, Func<string, string> name, CaData ca)
    {
        if (b.UsersOff.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
        {
            return CaAnswer.No("rechtstreeks uitgezonderd");
        }

        if (b.GroupsOff.FirstOrDefault(g.Groups.Contains) is { } groupOff)
        {
            return CaAnswer.No(Texts.T.Format("code.ca-analysis.excluded-through-the-group", name(groupOff)));
        }

        if (b.RolesOff.FirstOrDefault(g.Roles.Contains) is { } roleOff)
        {
            return CaAnswer.No(Texts.T.Format("code.ca-analysis.excluded-through-the-role", ca.RoleName(roleOff)));
        }

        if (b.GuestsOff && g.IsGuest)
        {
            return CaAnswer.No(Texts.T["code.ca-analysis.guests-are-excluded"]);
        }

        CaAnswer? inValue = null;
        if (b.UsersIn.Contains("All"))
        {
            inValue = CaAnswer.Yes("alle gebruikers");
        }
        else if (b.UsersIn.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
        {
            inValue = CaAnswer.Yes("rechtstreeks opgenomen");
        }
        else if (b.GuestsIn && g.IsGuest)
        {
            inValue = CaAnswer.Yes(Texts.T["code.ca-analysis.guests-and-external-users"]);
        }
        else if (b.GroupsIn.FirstOrDefault(g.Groups.Contains) is { } groupIn)
        {
            inValue = CaAnswer.Yes(Texts.T.Format("code.ca-analysis.member-of", name(groupIn)));
        }
        else if (b.RolesIn.FirstOrDefault(g.Roles.Contains) is { } roleIn)
        {
            inValue = CaAnswer.Yes(Texts.T.Format("code.ca-analysis.has-the-role", ca.RoleName(roleIn)));
        }

        // Without the full group members (also nested groups) a group in the policy is not certain
        if (!g.GroupsFull)
        {
            if (inValue is not null && b.GroupsOff.Count > 0)
            {
                return CaAnswer.Maybe(Texts.T.Format("code.ca-analysis.unless-member-of-an-excluded", inValue.Reason));
            }

            if (inValue is null && b.GroupsIn.Count > 0)
            {
                return CaAnswer.Maybe(Texts.T["code.ca-analysis.maybe-member-of-an-included"]);
            }
        }

        return inValue ?? CaAnswer.No(Texts.T["code.ca-analysis.not-included"]);
    }

    /// <summary>All persons with an Entra account who fall under the policy (certain or maybe).</summary>
    public static IReadOnlyList<(Person Person, CaAnswer Answer)> WhoFallsUnder(CaPolicy b, InventoryData d)
    {
        if (d.ConditionalAccess is not { } ca)
        {
            return Array.Empty<(Person, CaAnswer)>();
        }

        var name = Names(d);
        return d.Persons
            .Select(p => (Person: p, G: CaUser.Of(p, d)))
            .Where(x => x.G is not null)
            .Select(x => (x.Person, Answer: Scope(b, x.G!, name, ca)))
            .Where(x => x.Answer.Applies != CaApplies.No)
            .OrderBy(x => x.Person.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Names for ids of users and groups, via the index.</summary>
    public static Func<string, string> Names(InventoryData d) => id => d.Index.Find(id)?.Name ?? id;

    // ------------------------------------------------------------------ what if

    /// <summary>Whether a policy covers an app in the scenario.</summary>
    internal static CaAnswer CoversApp(CaPolicy b, CaApp app)
    {
        if (b.UserActions.Count > 0 && b.AppsIn.Count == 0)
        {
            return CaAnswer.No(Texts.T.Format("code.ca-analysis.only-applies-when", string.Join(", ", b.UserActions.Select(ActionName))));
        }

        bool Belongs(string id) => id == "All"
                                 || (app == CaApp.Office365 && OfficeApps.Contains(id))
                                 || (app == CaApp.AdminPortals && id == "MicrosoftAdminPortals");

        if (b.AppsOff.FirstOrDefault(Belongs) is { } output)
        {
            return CaAnswer.No(Texts.T.Format("code.ca-analysis.excluded", AppName(output)));
        }

        if (b.AppsIn.FirstOrDefault(Belongs) is { } inValue)
        {
            return CaAnswer.Yes(AppName(inValue));
        }

        var specific = b.AppsIn.Where(a => a is not "None" && !AppNames.ContainsKey(a)).ToList();
        return app == CaApp.Other && specific.Count > 0
            ? CaAnswer.Maybe(Texts.T["code.ca-analysis.applies-to-specific-apps"])
            : CaAnswer.No("andere apps");
    }

    private static bool? LocationMatches(string id, CaPlace place, CaData ca)
    {
        if (id == "All")
        {
            return true;
        }

        if (id == "AllTrusted")
        {
            return place == CaPlace.Office;
        }

        if (ca.Location(id) is not { } l)
        {
            return null;
        }

        if (l.IsIp)
        {
            // The office: the trusted IP ranges. The app cannot place another IP location.
            return place == CaPlace.Office ? (l.Trusted ? true : null) : false;
        }

        if (place is CaPlace.Office or CaPlace.Belgium)
        {
            return l.ContainsBelgium;
        }

        return l.Values.Any(w => !w.Equals("BE", StringComparison.OrdinalIgnoreCase)) || l.UnknownCountries ? null : false;
    }

    private static bool? AOf(IEnumerable<string> ids, CaPlace place, CaData ca)
    {
        var outcomes = ids.Select(i => LocationMatches(i, place, ca)).ToList();
        return outcomes.Contains(true) ? true : outcomes.Contains(null) ? null : false;
    }

    /// <summary>Does the policy apply to this user in this scenario? Combines who, app and conditions.</summary>
    public static CaAnswer Applies(CaPolicy b, CaUser g, CaScenario s, CaData ca, Func<string, string> name)
    {
        var who = Scope(b, g, name, ca);
        if (who.Applies == CaApplies.No)
        {
            return who;
        }

        var reasons = new List<string> { who.Reason };
        var certain = who.Applies == CaApplies.Yes;

        var app = CoversApp(b, s.App);
        if (app.Applies == CaApplies.No)
        {
            return app;
        }

        certain &= app.Applies == CaApplies.Yes;
        reasons.Add(app.Reason);

        var client = s.Client switch
        {
            CaClient.Browser => "browser",
            CaClient.Desktop => "mobileAppsAndDesktopClients",
            _ => null,
        };
        if (b.ClientApps.Count > 0 && !b.ClientApps.Contains("all"))
        {
            var matches = client is null
                ? b.ClientApps.Any(c => c is "exchangeActiveSync" or "other")
                : b.ClientApps.Contains(client);
            if (!matches)
            {
                return CaAnswer.No(Texts.T.Format("code.ca-analysis.only-through-2", string.Join(", ", b.ClientApps.Select(ClientName))));
            }
        }

        if (b.PlatformsOff.Any(p => p.Equals(s.Platform, StringComparison.OrdinalIgnoreCase) || p == "all"))
        {
            return CaAnswer.No(Texts.T.Format("code.ca-analysis.not-on-2", PlatformName(s.Platform)));
        }

        if (b.PlatformsIn.Count > 0 && !b.PlatformsIn.Any(p => p.Equals(s.Platform, StringComparison.OrdinalIgnoreCase) || p == "all"))
        {
            return CaAnswer.No(Texts.T.Format("code.ca-analysis.only-on", string.Join(", ", b.PlatformsIn.Select(PlatformName))));
        }

        if (b.LocationsOff.Count > 0)
        {
            switch (AOf(b.LocationsOff, s.Place, ca))
            {
                case true:
                    return CaAnswer.No(Texts.T.Format("code.ca-analysis.not-from-2", string.Join(", ", b.LocationsOff.Select(l => LocationName(l, ca)))));
                case null:
                    certain = false;
                    reasons.Add(Texts.T["code.ca-analysis.depends-on-the-location"]);
                    break;
            }
        }

        if (b.LocationsIn.Count > 0)
        {
            switch (AOf(b.LocationsIn, s.Place, ca))
            {
                case false:
                    return CaAnswer.No(Texts.T.Format("code.ca-analysis.only-from", string.Join(", ", b.LocationsIn.Select(l => LocationName(l, ca)))));
                case null:
                    certain = false;
                    reasons.Add(Texts.T["code.ca-analysis.depends-on-the-location"]);
                    break;
            }
        }

        if (b.SignInRisk.Count > 0 || b.UsersRisk.Count > 0)
        {
            return CaAnswer.No(Texts.T["code.ca-analysis.only-applies-at-a-risky"]);
        }

        if (b.Transfer.Count > 0)
        {
            return CaAnswer.No(Texts.T["code.ca-analysis.only-applies-when-signing-in"]);
        }

        if (b.DeviceFilter)
        {
            certain = false;
            reasons.Add(Texts.T["code.ca-analysis.depends-on-the-device-filter"]);
        }

        return new CaAnswer(certain ? CaApplies.Yes : CaApplies.Maybe, string.Join("; ", reasons.Where(r => r.Length > 0)));
    }

    /// <summary>All policies (on and report-only) for a user in a scenario, with the summary.</summary>
    public static CaWhatIfOutcome WhatIf(CaUser g, CaScenario s, CaData ca, Func<string, string> name)
    {
        var rules = ca.Policies
            .Where(b => b.Status != CaStatus.Off)
            .Select(b => new CaWhatIfRow(b, Applies(b, g, s, ca, name), Explanation(b, ca, name).Consequence))
            .ToList();

        var enabled = rules.Where(r => r.Policy.Status == CaStatus.On).ToList();
        var certain = enabled.Where(r => r.Answer.Applies == CaApplies.Yes).ToList();
        var blocked = certain.Any(r => r.Policy.Blocks);
        var requirements = new List<string>();
        foreach (var r in certain.Where(r => !r.Policy.Blocks))
        {
            var b = r.Policy;
            var options = b.Checks.Where(c => !(c == "mfa" && b.StrengthId is not null)).ToList();
            if (b.StrengthId is not null) options.Insert(0, "mfa");

            var device = options.Where(c => c is "compliantDevice" or "domainJoinedDevice").ToList();
            if (s.ManagedDevice && device.Count > 0)
            {
                // A managed device is enough for "one of", and is already in order for "all"
                if (b.Operator == "OR")
                {
                    continue;
                }

                options = options.Except(device).ToList();
            }

            var texts = options.Select(c => c == "mfa" && b.AsksPhishingResistant ? "phishingbestendige MFA" : CheckName(c)).ToList();
            if (b.TermsOfUse)
            {
                texts.Add(Texts.T["code.ca-analysis.accepting-the-terms-of-use"]);
            }

            var requirement = string.Join(b.Operator == "AND" ? (" " + Texts.T["code.ca-analysis.and"] + " ") : (" " + Texts.T["keys.or"] + " "), texts);
            if (requirement.Length > 0 && !requirements.Contains(requirement))
            {
                requirements.Add(requirement);
            }
        }

        string summary;
        if (blocked)
        {
            summary = Texts.T.Format("code.ca-analysis.blocked-by", string.Join(", ", certain.Where(r => r.Policy.Blocks).Select(r => r.Policy.Name)));
        }
        else if (requirements.Count > 0)
        {
            summary = Texts.T.Format("code.ca-analysis.access-provided", string.Join((" " + Texts.T["code.ca-analysis.and"] + " "), requirements));
        }
        else if (certain.Count > 0)
        {
            summary = "Toegang zonder extra eisen";
        }
        else
        {
            summary = Texts.T["code.ca-analysis.no-policy-surely-applies-signing"];
        }

        if (enabled.Any(r => r.Answer.Applies == CaApplies.Maybe))
        {
            summary += (" " + Texts.T["code.ca-analysis.and-maybe-more-see-the"]);
        }

        return new CaWhatIfOutcome(rules, blocked, requirements, summary);
    }
}

public enum CaApp
{
    Office365,
    AdminPortals,
    Other,
}

public enum CaPlace
{
    /// <summary>At the office: from a trusted location.</summary>
    Office,
    Belgium,
    Abroad,
}

public enum CaClient
{
    Browser,
    Desktop,

    /// <summary>Legacy programs (IMAP, POP, SMTP, ActiveSync): cannot do MFA.</summary>
    Outdated,
}

/// <summary>A sign-in to replay: which app, from where, on which platform, with which program.</summary>
public sealed record CaScenario(CaApp App = CaApp.Office365, CaPlace Place = CaPlace.Belgium, string Platform = "windows", CaClient Client = CaClient.Browser, bool ManagedDevice = false);

public sealed record CaWhatIfRow(CaPolicy Policy, CaAnswer Answer, string Consequence);

public sealed record CaWhatIfOutcome(IReadOnlyList<CaWhatIfRow> Rules, bool Blocked, IReadOnlyList<string> Requirements, string Summary)
{
    public IEnumerable<CaWhatIfRow> Enforced => Rules.Where(r => r.Policy.Status == CaStatus.On);
    public IEnumerable<CaWhatIfRow> OnlyReport => Rules.Where(r => r.Policy.Status == CaStatus.Report);
}

/// <summary>How often a policy played a part in the sign-ins (appliedConditionalAccessPolicies).</summary>
public sealed record CaEffectRow(string PolicyId, string Name, int Applied, int Failed, int NotApplicable, int ReportSucceeded, int ReportFailed, int ReportInterrupted)
{
    public int Total => Applied + Failed + NotApplicable + ReportSucceeded + ReportFailed + ReportInterrupted;

    /// <summary>Report-only: how many sign-ins the policy would have blocked or interrupted.</summary>
    public int WouldBlock => ReportFailed + ReportInterrupted;
}

public static class CaEffect
{
    public static IReadOnlyList<CaEffectRow> Compute(IEnumerable<SignInEvent> signIns, CaData ca)
    {
        var counters = ca.Policies.ToDictionary(b => b.Id, _ => new int[6], StringComparer.OrdinalIgnoreCase);
        foreach (var t in signIns.SelectMany(a => a.Ca ?? Array.Empty<CaApplication>()))
        {
            if (!counters.TryGetValue(t.PolicyId, out var n))
            {
                continue;
            }

            var i = t.Result switch
            {
                "success" => 0,
                "failure" => 1,
                "notApplied" or "notEnabled" or "reportOnlyNotApplied" => 2,
                "reportOnlySuccess" => 3,
                "reportOnlyFailure" => 4,
                "reportOnlyInterrupted" => 5,
                _ => -1,
            };
            if (i >= 0)
            {
                n[i]++;
            }
        }

        return ca.Policies.Select(b =>
        {
            var n = counters[b.Id];
            return new CaEffectRow(b.Id, b.Name, n[0], n[1], n[2], n[3], n[4], n[5]);
        }).ToList();
    }
}
