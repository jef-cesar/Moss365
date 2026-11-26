using Moss365.Core.Localization;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Analysis;

public enum CaVerdict
{
    InOrder,
    Info,
    Attention,
    Problem,
}

/// <summary>One point of the check, with whom it concerns and what you can do about it.</summary>
public sealed record CaFinding(string Subject, CaVerdict Verdict, string Explanation, string Advice = "")
{
    public IReadOnlyList<Person> Persons { get; init; } = Array.Empty<Person>();
    public IReadOnlyList<CaPolicy> Policies { get; init; } = Array.Empty<CaPolicy>();

    public string VerdictText => Verdict switch
    {
        CaVerdict.InOrder => Texts.T["ca-check.verdict.ok"],
        CaVerdict.Info => Texts.T["ca-check.verdict.info"],
        CaVerdict.Attention => Texts.T["ca-check.verdict.attention"],
        _ => Texts.T["ca-check.verdict.problem"],
    };
}

/// <summary>
/// Check of Conditional Access against the recommendations of Microsoft: MFA for everyone, legacy sign-in blocked,
/// administrators strongly protected, emergency accounts excluded, etc. Only reads; changes nothing.
/// </summary>
public static class CaCheck
{
    private static readonly string[] EmergencyWords = { "nood", "breakglass", "break-glass", "break.glass", "emergency", "glass" };

    public static IReadOnlyList<CaFinding> Check(InventoryData d, OrganisationConfig config, DateTime now)
    {
        var output = new List<CaFinding>();
        if (d.ConditionalAccess is not { } ca)
        {
            output.Add(new CaFinding("Conditional Access", CaVerdict.Info, Texts.T["code.ca-check.not-loaded"], Texts.T["code.ca-check.load-live-connections-load-everything"]));
            return output;
        }

        if (ca.Message is not null)
        {
            output.Add(new CaFinding("Conditional Access", CaVerdict.Problem, ca.Message));
            return output;
        }

        var name = CaReader.Names(d);
        var enabled = ca.Policies.Where(b => b.Status == CaStatus.On).ToList();
        var users = d.Persons
            .Select(p => (Person: p, G: CaUser.Of(p, d)))
            .Where(x => x.G is not null)
            .Select(x => (x.Person, G: x.G!))
            .ToList();
        var staff = users.Where(x => !x.Person.IsGuest && x.Person.Enabled && !d.Index.IsMailboxAccount(x.Person)).ToList();

        bool Everyone(CaPolicy b) => b.UsersIn.Contains("All");
        bool AllApps(CaPolicy b) => b.AppsIn.Contains("All");
        bool WithoutConditions(CaPolicy b) => (b.ClientApps.Count == 0 || b.ClientApps.Contains("all")) && b.PlatformsIn.Count == 0
                                              && b.LocationsIn.Count == 0 && b.SignInRisk.Count + b.UsersRisk.Count == 0 && !b.DeviceFilter
                                              && b.Transfer.Count == 0;
        bool Mfa(CaPolicy b) => b.AsksMfa && !b.Blocks;

        // Beveiligingsstandaarden
        if (ca.SecurityDefaults == true)
        {
            output.Add(enabled.Count > 0
                ? new CaFinding(Texts.T["ca-overview.security-defaults"], CaVerdict.Attention, Texts.T["code.ca-check.the-security-defaults-are-on"],
                    Texts.T["code.ca-check.choose-one-of-the-two"])
                : new CaFinding(Texts.T["ca-overview.security-defaults"], CaVerdict.Info, Texts.T["code.ca-check.the-security-defaults-of-microsoft"]));
        }
        else if (enabled.Count == 0)
        {
            output.Add(new CaFinding(Texts.T["code.ca-check.protection"], CaVerdict.Problem, Texts.T["code.ca-check.no-policy-is-on-and"],
                Texts.T["code.ca-check.turn-on-at-least-mfa"]));
        }

        // 1. MFA for everyone
        var mfaAll = enabled.Where(b => Everyone(b) && AllApps(b) && Mfa(b) && WithoutConditions(b)).ToList();
        if (mfaAll.Count > 0)
        {
            var withLocation = mfaAll.Where(b => b.LocationsOff.Count > 0).ToList();
            output.Add(withLocation.Count == mfaAll.Count
                ? new CaFinding(Texts.T["code.ca-check.mfa-for-everyone"], CaVerdict.Attention, Texts.T.Format("ca-check.mfa.notfrom", string.Join(", ", withLocation[0].LocationsOff.Select(l => CaReader.LocationName(l, ca)))),
                    Texts.T["code.ca-check.microsoft-recommends-mfa-at-the"]) { Policies = withLocation }
                : new CaFinding(Texts.T["code.ca-check.mfa-for-everyone"], CaVerdict.InOrder, Texts.T["code.ca-check.mfa-for-all-users-and"]) { Policies = mfaAll });
        }
        else
        {
            output.Add(new CaFinding(Texts.T["code.ca-check.mfa-for-everyone"], CaVerdict.Problem, Texts.T["code.ca-check.there-is-no-policy-that"],
                Texts.T["code.ca-check.make-a-policy-first-as"]));
        }

        // 2. Who falls outside MFA
        bool Covered(CaUser g) => enabled.Any(b => Mfa(b)
                                                  && (AllApps(b) || b.AppsIn.Contains("Office365"))
                                                  && (b.ClientApps.Count == 0 || b.ClientApps.Contains("all") || b.ClientApps.Contains("browser"))
                                                  && CaReader.Scope(b, g, name, ca).Applies == CaApplies.Yes);
        var emergencyUpns = config.EmergencyAccounts.Select(u => u.Trim()).Where(u => u.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outside = staff.Where(x => !emergencyUpns.Contains(x.Person.Upn) && !emergencyUpns.Contains(x.Person.Entra!.UserPrincipalName) && !Covered(x.G)).ToList();
        var critical = outside.Where(x => d.Security?.RolesOf(x.G.Id).Any(r => SecurityData.IsCriticalRole(r.Role)) == true).ToList();
        if (outside.Count == 0)
        {
            output.Add(new CaFinding(Texts.T["code.ca-check.employees-without-an-mfa-policy"], CaVerdict.InOrder, Texts.T.Format("code.ca-check.all-active-employees-come-under", staff.Count)));
        }
        else
        {
            var noRegistration = d.Security is { MfaKnown: true } bev ? outside.Count(x => bev.MfaOf(x.G.Id) is not { MfaRegistered: true }) : (int?)null;
            output.Add(new CaFinding(Texts.T["code.ca-check.employees-without-an-mfa-policy"], critical.Count > 0 ? CaVerdict.Problem : CaVerdict.Attention,
                Texts.T.Format("code.ca-check.of-the-active-employees-come", outside.Count, staff.Count)
                + (noRegistration is { } n ? (" " + Texts.T.Format("code.ca-check.of-them-have-no-mfa", n)) : string.Empty)
                + (critical.Count > 0 ? Texts.T.Format("code.ca-check.with-a-critical-admin-role", critical.Count) : "."),
                Texts.T["code.ca-check.check-whether-they-are-excluded"])
            {
                Persons = outside.Select(x => x.Person).ToList(),
            });
        }

        if (d.Security is { MfaKnown: true } b2)
        {
            var notRegistered = staff.Where(x => Covered(x.G) && b2.MfaOf(x.G.Id) is not { MfaRegistered: true }).ToList();
            if (notRegistered.Count > 0)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.mfa-not-set-up-yet"], enabled.Any(b => b.UserActions.Contains(CaReader.ActionSecurityData)) ? CaVerdict.Info : CaVerdict.Attention,
                    Texts.T.Format("code.ca-check.employees-come-under-mfa-but", notRegistered.Count),
                    Texts.T["code.ca-check.have-them-set-up-mfa"])
                {
                    Persons = notRegistered.Select(x => x.Person).ToList(),
                });
            }
        }

        // 3. Legacy sign-in
        var legacy = enabled.Where(b => b.Blocks && Everyone(b) && AllApps(b) && b.ClientApps.Contains("exchangeActiveSync") && b.ClientApps.Contains("other")).ToList();
        output.Add(legacy.Count > 0
            ? new CaFinding(Texts.T["code.ca-check.legacy-sign-in-blocked"], CaVerdict.InOrder, Texts.T["code.ca-check.imap-pop-smtp-and-activesync"]) { Policies = legacy }
            : new CaFinding(Texts.T["code.ca-check.legacy-sign-in-blocked"], CaVerdict.Problem, Texts.T["code.ca-check.there-is-no-policy-that-2"],
                Texts.T["code.ca-check.make-a-policy-all-users"]));

        // 4. Administrators
        var management = enabled.Where(b => Mfa(b) && (Everyone(b) || b.RolesIn.Contains(CaData.RoleGlobalAdmin, StringComparer.OrdinalIgnoreCase))
                                    && (AllApps(b) || b.AppsIn.Contains("MicrosoftAdminPortals"))).ToList();
        var phishing = management.Where(b => b.AsksPhishingResistant && b.RolesIn.Count > 0).ToList();
        if (management.Count == 0)
        {
            output.Add(new CaFinding(Texts.T["code.ca-check.administrators"], CaVerdict.Problem, Texts.T["code.ca-check.no-policy-asks-mfa-for"],
                Texts.T["code.ca-check.make-a-policy-for-the"]));
        }
        else
        {
            output.Add(phishing.Count > 0
                ? new CaFinding(Texts.T["code.ca-check.administrators"], CaVerdict.InOrder, Texts.T["code.ca-check.admin-roles-should-use-phishing"]) { Policies = phishing }
                : new CaFinding(Texts.T["code.ca-check.administrators"], CaVerdict.Attention, Texts.T["code.ca-check.administrators-come-under-mfa-but"],
                    Texts.T["code.ca-check.for-admin-roles-microsoft-recommends"]) { Policies = management });
        }

        // 5. Noodaccounts
        output.AddRange(CheckEmergencyAccounts(d, ca, config, users, enabled, name, now));

        // 6. Guests
        var guests = users.Where(x => x.Person.IsGuest && x.Person.Enabled).ToList();
        if (guests.Count > 0)
        {
            var guestPolicy = enabled.Where(b => Mfa(b) && (b.GuestsIn || (Everyone(b) && !b.GuestsOff))).ToList();
            output.Add(guestPolicy.Count > 0
                ? new CaFinding(Texts.T["collaboration.guests"], CaVerdict.InOrder, Texts.T.Format("code.ca-check.guests-should-use-mfa", guests.Count)) { Policies = guestPolicy }
                : new CaFinding(Texts.T["collaboration.guests"], CaVerdict.Attention, Texts.T.Format("code.ca-check.there-are-guests-but-no", guests.Count), Texts.T["code.ca-check.include-guests-in-the-mfa"]));
        }

        // 7. Toestelcode
        var code = enabled.Where(b => b.Blocks && b.Transfer.Contains("deviceCodeFlow")).ToList();
        output.Add(code.Count > 0
            ? new CaFinding(Texts.T["code.ca-check.sign-in-with-device-code"], CaVerdict.InOrder, Texts.T["code.ca-check.signing-in-with-a-device"]) { Policies = code }
            : new CaFinding(Texts.T["code.ca-check.sign-in-with-device-code"], CaVerdict.Attention, Texts.T["code.ca-check.signing-in-with-a-device-2"],
                Texts.T["code.ca-check.block-the-device-code-flow"]));

        // 8. Register MFA
        var register = enabled.Where(b => b.UserActions.Contains(CaReader.ActionSecurityData)).ToList();
        output.Add(register.Count > 0
            ? new CaFinding(Texts.T["code.ca-check.mfa-registration-protected"], CaVerdict.InOrder, Texts.T["code.ca-check.registering-security-info-is-protected"]) { Policies = register }
            : new CaFinding(Texts.T["code.ca-check.mfa-registration-protected"], CaVerdict.Attention, Texts.T["code.ca-check.everyone-can-register-their-mfa"],
                Texts.T["code.ca-check.limit-registration-to-trusted-locations"]));

        // 9. Notable policies
        foreach (var b in ca.Policies)
        {
            var days = b.Changed is { } g ? (int)(now - g.LocalDateTime).TotalDays : (int?)null;
            if (b.Status == CaStatus.Report && days > 30)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.long-in-report-only"], CaVerdict.Attention, Texts.T.Format("code.ca-check.has-been-in-report-only", b.Name, days),
                    Texts.T["code.ca-check.check-the-effect-sign-ins"]) { Policies = new[] { b } });
            }

            if (b.Status == CaStatus.Off)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.disabled-policy"], CaVerdict.Info, Texts.T.Format("code.ca-check.is-off", b.Name)) { Policies = new[] { b } });
            }

            var nobody = !Everyone(b) && !b.GuestsIn && b.GroupsIn.Count + b.RolesIn.Count == 0 && b.UsersIn.All(u => u == "None");
            if (nobody)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.policy-without-users"], CaVerdict.Info, Texts.T.Format("code.ca-check.applies-to-nobody", b.Name)) { Policies = new[] { b } });
            }

            var exclusions = b.UsersOff.Count + b.GroupsOff.Count + b.RolesOff.Count;
            if (b.Status != CaStatus.Off && exclusions > 5)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.many-exceptions"], CaVerdict.Attention, Texts.T.Format("ca-check.exclusions", b.Name, exclusions),
                    Texts.T["code.ca-check.every-exception-is-a-hole"]) { Policies = new[] { b } });
            }

            if (b.Status == CaStatus.On && b.Blocks && Everyone(b) && AllApps(b) && WithoutConditions(b) && b.LocationsOff.Count == 0 && b.PlatformsOff.Count == 0)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.everyone-locked-out"], CaVerdict.Problem, Texts.T.Format("code.ca-check.blocks-all-users-for-all", b.Name)
                    + (exclusions == 0 ? Texts.T["ca-check.no-exclusions"] : Texts.T.Format("code.ca-check.with-only-exception-s", exclusions)),
                    Texts.T["code.ca-check.check-this-at-once-nobody"]) { Policies = new[] { b } });
            }

            if (b.SignInRisk.Count + b.UsersRisk.Count > 0)
            {
                output.Add(new CaFinding(Texts.T["code.ca-check.risk-policy"], CaVerdict.Info, Texts.T.Format("code.ca-check.works-with-sign-in-or", b.Name)) { Policies = new[] { b } });
            }
        }

        return output;
    }

    private static IEnumerable<CaFinding> CheckEmergencyAccounts(
        InventoryData d, CaData ca, OrganisationConfig config, IReadOnlyList<(Person Person, CaUser G)> users,
        IReadOnlyList<CaPolicy> enabled, Func<string, string> name, DateTime now)
    {
        var upns = config.EmergencyAccounts.Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
        if (upns.Count == 0)
        {
            var candidates = users.Where(x => EmergencyWords.Any(w => x.Person.Upn.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                                        || x.Person.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.Person).ToList();
            yield return new CaFinding(Texts.T["menu.organisatie.noodaccounts"], CaVerdict.Attention,
                Texts.T["code.ca-check.no-emergency-accounts-are-marked"] + (candidates.Count > 0 ? Texts.T.Format("code.ca-check.account-s-look-like-one", candidates.Count) : "."),
                Texts.T["code.ca-check.make-two-cloud-only-accounts"])
            {
                Persons = candidates,
            };
            yield break;
        }

        foreach (var upn in upns)
        {
            var x = users.FirstOrDefault(g => string.Equals(g.Person.Entra!.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase));
            if (x.Person is null)
            {
                yield return new CaFinding(Texts.T.Format("ca-check.emergency", upn), CaVerdict.Problem, Texts.T["code.ca-check.not-found-in-entra"], Texts.T["code.ca-check.check-the-name-under-organisation"]);
                continue;
            }

            var problems = new List<string>();
            var attention = new List<string>();
            if (x.Person.Entra!.OnPremisesSyncEnabled)
            {
                problems.Add(Texts.T["code.ca-check.comes-from-ad-must-be"]);
            }

            if (!x.Person.Entra.AccountEnabled)
            {
                problems.Add(Texts.T["code.ca-check.is-disabled"]);
            }

            if (d.Security is { RolesKnown: true } bev && !bev.RolesOf(x.G.Id).Any(r => r.Role == "Global Administrator" || r.RoleTemplateId == CaData.RoleGlobalAdmin))
            {
                attention.Add(Texts.T["code.ca-check.has-no-permanent-role-global"]);
            }

            var inValue = enabled.Where(b => CaReader.Scope(b, x.G, name, ca).Applies != CaApplies.No).ToList();
            if (inValue.Count > 0)
            {
                problems.Add(Texts.T.Format("ca-check.emergency.under", inValue.Count, string.Join(", ", inValue.Select(b => b.Name))));
            }

            if (Statistic.LastSignIn(x.Person) is { } last && (now - last).TotalDays <= 30)
            {
                attention.Add(Texts.T.Format("code.ca-check.signed-in-on-right-you", last));
            }

            var verdict = problems.Count > 0 ? CaVerdict.Problem : attention.Count > 0 ? CaVerdict.Attention : CaVerdict.InOrder;
            var text = verdict == CaVerdict.InOrder
                ? Texts.T["code.ca-check.cloud-only-enabled-and-excluded"]
                : string.Join("; ", problems.Concat(attention)) + ".";
            yield return new CaFinding(Texts.T.Format("ca-check.emergency", x.Person.Name), verdict, char.ToUpperInvariant(text[0]) + text[1..],
                inValue.Count > 0 ? Texts.T["code.ca-check.exclude-the-account-or-a"] : string.Empty)
            {
                Persons = new[] { x.Person },
                Policies = inValue,
            };
        }
    }
}
