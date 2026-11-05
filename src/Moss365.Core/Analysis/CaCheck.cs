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
        CaVerdict.InOrder => "In orde",
        CaVerdict.Info => "Info",
        CaVerdict.Attention => "Aandacht",
        _ => "Probleem",
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
            output.Add(new CaFinding("Conditional Access", CaVerdict.Info, "Niet geladen.", "Laad live (Verbindingen > Alles live laden) met een account dat de beleiden mag lezen."));
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
                ? new CaFinding("Beveiligingsstandaarden", CaVerdict.Attention, "De beveiligingsstandaarden staan aan, naast Conditional Access-beleiden.",
                    "Kies één van de twee: met Entra ID P1 schakel je de standaarden uit en regel je alles met Conditional Access.")
                : new CaFinding("Beveiligingsstandaarden", CaVerdict.Info, "De beveiligingsstandaarden van Microsoft staan aan (basis-MFA voor iedereen)."));
        }
        else if (enabled.Count == 0)
        {
            output.Add(new CaFinding("Bescherming", CaVerdict.Problem, "Geen enkel beleid staat aan en de beveiligingsstandaarden staan uit: aanmelden kan met enkel een wachtwoord.",
                "Zet minstens MFA voor alle gebruikers aan."));
        }

        // 1. MFA for everyone
        var mfaAll = enabled.Where(b => Everyone(b) && AllApps(b) && Mfa(b) && WithoutConditions(b)).ToList();
        if (mfaAll.Count > 0)
        {
            var withLocation = mfaAll.Where(b => b.LocationsOff.Count > 0).ToList();
            output.Add(withLocation.Count == mfaAll.Count
                ? new CaFinding("MFA voor iedereen", CaVerdict.Attention, "MFA geldt voor alle gebruikers, maar niet vanaf " + string.Join(", ", withLocation[0].LocationsOff.Select(l => CaReader.LocationName(l, ca))) + ".",
                    "Microsoft raadt MFA aan ook op kantoor: een gestolen wachtwoord werkt anders in het gebouw (of via een gekaapte pc) zonder tweede stap.") { Policies = withLocation }
                : new CaFinding("MFA voor iedereen", CaVerdict.InOrder, "MFA voor alle gebruikers en alle apps.") { Policies = mfaAll });
        }
        else
        {
            output.Add(new CaFinding("MFA voor iedereen", CaVerdict.Problem, "Er is geen beleid dat MFA vraagt voor alle gebruikers en alle apps.",
                "Maak (eerst als alleen rapport) een beleid: alle gebruikers, alle apps, MFA vereisen; de noodaccounts uitgezonderd."));
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
            output.Add(new CaFinding("Medewerkers zonder MFA-beleid", CaVerdict.InOrder, $"Alle {staff.Count} actieve medewerkers vallen onder een beleid dat MFA vraagt."));
        }
        else
        {
            var noRegistration = d.Security is { MfaKnown: true } bev ? outside.Count(x => bev.MfaOf(x.G.Id) is not { MfaRegistered: true }) : (int?)null;
            output.Add(new CaFinding("Medewerkers zonder MFA-beleid", critical.Count > 0 ? CaVerdict.Problem : CaVerdict.Attention,
                $"{outside.Count} van de {staff.Count} actieve medewerkers vallen onder geen enkel beleid dat MFA vraagt"
                + (noRegistration is { } n ? $" ({n} van hen hebben ook geen MFA geregistreerd)" : string.Empty)
                + (critical.Count > 0 ? $"; {critical.Count} met een kritieke beheerrol." : "."),
                "Kijk na of ze bewust uitgezonderd zijn (bv. via een groep). Zie Wat als voor de reden per persoon.")
            {
                Persons = outside.Select(x => x.Person).ToList(),
            });
        }

        if (d.Security is { MfaKnown: true } b2)
        {
            var notRegistered = staff.Where(x => Covered(x.G) && b2.MfaOf(x.G.Id) is not { MfaRegistered: true }).ToList();
            if (notRegistered.Count > 0)
            {
                output.Add(new CaFinding("MFA nog niet ingesteld", enabled.Any(b => b.UserActions.Contains(CaReader.ActionSecurityData)) ? CaVerdict.Info : CaVerdict.Attention,
                    $"{notRegistered.Count} medewerkers vallen onder MFA maar hebben nog geen methode geregistreerd: bij de volgende aanmelding moeten ze die instellen, met enkel hun wachtwoord.",
                    "Laat ze MFA instellen op kantoor, of bescherm het registreren (zie hieronder).")
                {
                    Persons = notRegistered.Select(x => x.Person).ToList(),
                });
            }
        }

        // 3. Legacy sign-in
        var legacy = enabled.Where(b => b.Blocks && Everyone(b) && AllApps(b) && b.ClientApps.Contains("exchangeActiveSync") && b.ClientApps.Contains("other")).ToList();
        output.Add(legacy.Count > 0
            ? new CaFinding("Verouderde aanmelding geblokkeerd", CaVerdict.InOrder, "IMAP, POP, SMTP en ActiveSync (zonder MFA) zijn geblokkeerd.") { Policies = legacy }
            : new CaFinding("Verouderde aanmelding geblokkeerd", CaVerdict.Problem, "Er is geen beleid dat verouderde aanmelding (IMAP, POP, SMTP, ActiveSync) blokkeert. Die kunnen geen MFA: een wachtwoord volstaat.",
                "Maak een beleid: alle gebruikers, alle apps, client-apps Exchange ActiveSync en andere, blokkeren."));

        // 4. Administrators
        var management = enabled.Where(b => Mfa(b) && (Everyone(b) || b.RolesIn.Contains(CaData.RoleGlobalAdmin, StringComparer.OrdinalIgnoreCase))
                                    && (AllApps(b) || b.AppsIn.Contains("MicrosoftAdminPortals"))).ToList();
        var phishing = management.Where(b => b.AsksPhishingResistant && b.RolesIn.Count > 0).ToList();
        if (management.Count == 0)
        {
            output.Add(new CaFinding("Beheerders", CaVerdict.Problem, "Geen beleid vraagt MFA voor de rol Global Administrator.",
                "Maak een beleid voor de beheerrollen met MFA (liefst phishingbestendig: FIDO2-sleutel of Windows Hello)."));
        }
        else
        {
            output.Add(phishing.Count > 0
                ? new CaFinding("Beheerders", CaVerdict.InOrder, "Beheerrollen moeten phishingbestendige MFA gebruiken.") { Policies = phishing }
                : new CaFinding("Beheerders", CaVerdict.Attention, "Beheerders vallen onder MFA, maar niet onder phishingbestendige MFA.",
                    "Microsoft raadt voor beheerrollen de sterkte \"Phishingbestendige MFA\" aan (FIDO2-sleutel, Windows Hello, certificaat).") { Policies = management });
        }

        // 5. Noodaccounts
        output.AddRange(CheckEmergencyAccounts(d, ca, config, users, enabled, name, now));

        // 6. Guests
        var guests = users.Where(x => x.Person.IsGuest && x.Person.Enabled).ToList();
        if (guests.Count > 0)
        {
            var guestPolicy = enabled.Where(b => Mfa(b) && (b.GuestsIn || (Everyone(b) && !b.GuestsOff))).ToList();
            output.Add(guestPolicy.Count > 0
                ? new CaFinding("Gasten", CaVerdict.InOrder, $"Gasten ({guests.Count}) moeten MFA gebruiken.") { Policies = guestPolicy }
                : new CaFinding("Gasten", CaVerdict.Attention, $"Er zijn {guests.Count} gasten, maar geen beleid vraagt hen MFA.", "Neem gasten op in het MFA-beleid voor iedereen."));
        }

        // 7. Toestelcode
        var code = enabled.Where(b => b.Blocks && b.Transfer.Contains("deviceCodeFlow")).ToList();
        output.Add(code.Count > 0
            ? new CaFinding("Aanmelden met toestelcode", CaVerdict.InOrder, "Aanmelden met een toestelcode (device code flow) is geblokkeerd.") { Policies = code }
            : new CaFinding("Aanmelden met toestelcode", CaVerdict.Attention, "Aanmelden met een toestelcode is niet geblokkeerd. Phishing gebruikt dat vaak (\"geef deze code in op microsoft.com/devicelogin\").",
                "Blokkeer de device code flow voor iedereen behalve wie het echt nodig heeft (bv. vergaderzaal-toestellen)."));

        // 8. Register MFA
        var register = enabled.Where(b => b.UserActions.Contains(CaReader.ActionSecurityData)).ToList();
        output.Add(register.Count > 0
            ? new CaFinding("MFA registreren beschermd", CaVerdict.InOrder, "Beveiligingsgegevens registreren is beschermd.") { Policies = register }
            : new CaFinding("MFA registreren beschermd", CaVerdict.Attention, "Iedereen kan zijn MFA-methode registreren met enkel een wachtwoord, van overal.",
                "Beperk het registreren tot vertrouwde locaties (kantoor) of eis een Temporary Access Pass."));

        // 9. Notable policies
        foreach (var b in ca.Policies)
        {
            var days = b.Changed is { } g ? (int)(now - g.LocalDateTime).TotalDays : (int?)null;
            if (b.Status == CaStatus.Report && days > 30)
            {
                output.Add(new CaFinding("Lang op alleen rapport", CaVerdict.Attention, $"\"{b.Name}\" staat al {days} dagen op alleen rapport.",
                    "Kijk het effect na (aanmeldingen) en zet het aan, of verwijder het.") { Policies = new[] { b } });
            }

            if (b.Status == CaStatus.Off)
            {
                output.Add(new CaFinding("Uitgeschakeld beleid", CaVerdict.Info, $"\"{b.Name}\" staat uit.") { Policies = new[] { b } });
            }

            var nobody = !Everyone(b) && !b.GuestsIn && b.GroupsIn.Count + b.RolesIn.Count == 0 && b.UsersIn.All(u => u == "None");
            if (nobody)
            {
                output.Add(new CaFinding("Beleid zonder gebruikers", CaVerdict.Info, $"\"{b.Name}\" geldt voor niemand.") { Policies = new[] { b } });
            }

            var exclusions = b.UsersOff.Count + b.GroupsOff.Count + b.RolesOff.Count;
            if (b.Status != CaStatus.Off && exclusions > 5)
            {
                output.Add(new CaFinding("Veel uitzonderingen", CaVerdict.Attention, $"\"{b.Name}\" heeft {exclusions} uitzonderingen.",
                    "Elke uitzondering is een gat. Werk liefst met één groep uitzonderingen die je regelmatig nakijkt.") { Policies = new[] { b } });
            }

            if (b.Status == CaStatus.On && b.Blocks && Everyone(b) && AllApps(b) && WithoutConditions(b) && b.LocationsOff.Count == 0 && b.PlatformsOff.Count == 0)
            {
                output.Add(new CaFinding("Iedereen buitengesloten", CaVerdict.Problem, $"\"{b.Name}\" blokkeert alle gebruikers voor alle apps"
                    + (exclusions == 0 ? ", zonder uitzonderingen." : $", met enkel {exclusions} uitzondering(en)."),
                    "Kijk dit meteen na: niemand (ook geen beheerder) kan zich zo aanmelden.") { Policies = new[] { b } });
            }

            if (b.SignInRisk.Count + b.UsersRisk.Count > 0)
            {
                output.Add(new CaFinding("Risicobeleid", CaVerdict.Info, $"\"{b.Name}\" werkt met aanmeld- of gebruikersrisico: dat vraagt Entra ID P2.") { Policies = new[] { b } });
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
            yield return new CaFinding("Noodaccounts", CaVerdict.Attention,
                "Er zijn geen noodaccounts aangeduid" + (candidates.Count > 0 ? $"; {candidates.Count} account(s) lijken er een (naam)." : "."),
                "Maak twee cloud-only accounts met de rol Global Administrator, sluit ze uit van alle beleiden en vul ze in bij Organisatie > Noodaccounts.")
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
                yield return new CaFinding("Noodaccount " + upn, CaVerdict.Problem, "Niet gevonden in Entra.", "Kijk de naam na bij Organisatie > Noodaccounts.");
                continue;
            }

            var problems = new List<string>();
            var attention = new List<string>();
            if (x.Person.Entra!.OnPremisesSyncEnabled)
            {
                problems.Add("komt uit AD (moet cloud-only zijn, anders hangt het af van de synchronisatie)");
            }

            if (!x.Person.Entra.AccountEnabled)
            {
                problems.Add("is uitgeschakeld");
            }

            if (d.Security is { RolesKnown: true } bev && !bev.RolesOf(x.G.Id).Any(r => r.Role == "Global Administrator" || r.RoleTemplateId == CaData.RoleGlobalAdmin))
            {
                attention.Add("heeft geen (permanente) rol Global Administrator");
            }

            var inValue = enabled.Where(b => CaReader.Scope(b, x.G, name, ca).Applies != CaApplies.No).ToList();
            if (inValue.Count > 0)
            {
                problems.Add("valt onder " + inValue.Count + " beleid(en) die aan staan: " + string.Join(", ", inValue.Select(b => b.Name)));
            }

            if (Statistic.LastSignIn(x.Person) is { } last && (now - last).TotalDays <= 30)
            {
                attention.Add($"meldde zich aan op {last:dd-MM-yyyy} (klopt dat? een noodaccount gebruik je enkel in nood)");
            }

            var verdict = problems.Count > 0 ? CaVerdict.Problem : attention.Count > 0 ? CaVerdict.Attention : CaVerdict.InOrder;
            var text = verdict == CaVerdict.InOrder
                ? "Cloud-only, ingeschakeld en uitgezonderd van alle beleiden."
                : string.Join("; ", problems.Concat(attention)) + ".";
            yield return new CaFinding("Noodaccount " + x.Person.Name, verdict, char.ToUpperInvariant(text[0]) + text[1..],
                inValue.Count > 0 ? "Sluit het account (of een groep met de noodaccounts) uit van elk beleid." : string.Empty)
            {
                Persons = new[] { x.Person },
                Policies = inValue,
            };
        }
    }
}
