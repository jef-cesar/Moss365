using Moss365.Core.Changes;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;

namespace Moss365.Core.Helpdesk;

/// <summary>A device in Entra (for BitLocker and LAPS).</summary>
public sealed record DeviceInfo(string Id, string DeviceId, string Name, string OperatingSystem, string TrustType, DateTimeOffset? LastSignIn);

/// <summary>A BitLocker recovery key; Key is only filled in when it was asked for (and that is in the access log).</summary>
public sealed record BitLockerKey(string Id, string DeviceId, string? Key, DateTimeOffset? Created, string VolumeType);

/// <summary>The local administrator password of a device (Windows LAPS or the legacy LAPS in AD).</summary>
public sealed record LapsPassword(string Account, string? Password, DateTimeOffset? Backup, string Source);

/// <summary>What AD keeps for a computer: BitLocker keys and the LAPS password.</summary>
public sealed record AdComputerSecrets(string Name, IReadOnlyList<BitLockerKey> BitLocker, LapsPassword? Laps, string? Note);

/// <summary>One message of a message trace.</summary>
public sealed record MessageTraceRow(DateTimeOffset Received, string Sender, string Recipient, string Subject, string Status, long Size, string Id);

/// <summary>An account whose password expires soon.</summary>
public sealed record ExpiringPassword(Person Person, DateTime Expires, int DaysLeft);

/// <summary>A group of a person and the groups in between ("via"), empty for a direct membership.</summary>
public sealed record GroupPath(Group Group, IReadOnlyList<string> Via);

/// <summary>Out of office and forwarding for someone else (illness, leave), with end dates.</summary>
public sealed class AbsenceRequest
{
    public bool AutoReply { get; set; } = true;
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string Internal { get; set; } = string.Empty;
    public string External { get; set; } = string.Empty;
    public string? ForwardTo { get; set; }
    public bool KeepCopy { get; set; } = true;
    public DateOnly? ForwardUntil { get; set; }
}

/// <summary>
/// The helpdesk tools of R10 (0.33): a Temporary Access Pass, out of office and forwarding with an end date, the
/// reminder for a password that expires, passwords that expire soon, and the groups of a person including the groups
/// in between.
/// </summary>
public static class HelpdeskTools
{
    /// <summary>A Temporary Access Pass (one-time or for a while); the pass itself is only shown once after running.</summary>
    public static PlanOutcome TemporaryAccessPass(Person p, int minutes, bool once)
    {
        if (p.Entra is not { } e)
        {
            return PlanOutcome.Error($"{p.Name} heeft geen Entra-account.");
        }

        if (p.IsGuest)
        {
            return PlanOutcome.Error("Een gast meldt zich aan bij de eigen organisatie; een Temporary Access Pass kan niet.");
        }

        if (minutes is < 60 or > 43200)
        {
            return PlanOutcome.Error("Een Temporary Access Pass is tussen 1 uur en 30 dagen geldig.");
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphCreateTemporaryAccessPass,
                Target = e.Id,
                TargetName = p.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["minuten"] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), ["eenmalig"] = once ? "True" : "False" },
                Description = $"Temporary Access Pass maken ({(once ? "eenmalig" : "meermaals")}, {Duration(minutes)} geldig)",
                NewValue = once ? "eenmalig" : "meermaals",
                Warning = "De pass verschijnt één keer na het uitvoeren; de app bewaart hem nergens. Geef hem rechtstreeks aan de gebruiker.",
            },
        });
    }

    private static string Duration(int minutes) => minutes % 1440 == 0 ? $"{minutes / 1440} dag(en)" : $"{minutes / 60} uur";

    /// <summary>Out of office (now or between two moments) and forwarding for a user; the end of the forwarding is
    /// prepared as a scheduled plan (never run by itself).</summary>
    public static (PlanOutcome Outcome, IReadOnlyList<Change> EndOfForwarding) Absence(Person p, AbsenceRequest a)
    {
        var address = p.Entra?.Mail is { Length: > 0 } m ? m : p.Upn;
        if (p.Entra is null || address.Length == 0)
        {
            return (PlanOutcome.Error($"{p.Name} heeft geen postvak in Exchange Online."), Array.Empty<Change>());
        }

        var errors = new List<string>();
        var plan = new List<Change>();
        if (a.AutoReply)
        {
            if (a.Start is { } s && a.End is { } t && t <= s)
            {
                errors.Add("Het einde van het automatisch antwoord ligt voor het begin.");
            }

            if (a.Internal.Trim().Length == 0)
            {
                errors.Add("Schrijf het bericht voor collega's.");
            }

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AutoReplyState"] = a.End is null ? "Enabled" : "Scheduled",
                ["InternalMessage"] = a.Internal.Trim(),
                ["ExternalMessage"] = a.External.Trim().Length > 0 ? a.External.Trim() : a.Internal.Trim(),
            };
            if (a.End is { } end)
            {
                parameters["StartTime"] = (a.Start ?? DateTimeOffset.Now).ToString("o");
                parameters["EndTime"] = end.ToString("o");
            }

            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeAutoReply,
                Target = address,
                TargetName = p.Name,
                Parameters = parameters,
                Description = a.End is { } until ? $"Automatisch antwoord tot {until.LocalDateTime:dd/MM/yyyy HH:mm}" : "Automatisch antwoord aanzetten",
                NewValue = a.End is null ? "aan" : "gepland",
            });
        }

        var endOfForwarding = new List<Change>();
        if (!string.IsNullOrWhiteSpace(a.ForwardTo))
        {
            var to = a.ForwardTo.Trim();
            if (!System.Net.Mail.MailAddress.TryCreate(to, out _))
            {
                errors.Add($"'{to}' is geen geldig e-mailadres.");
            }

            var domain = address.Contains('@') ? address[(address.IndexOf('@') + 1)..] : string.Empty;
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeUpdateMailbox,
                Target = address,
                TargetName = p.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["ForwardingSmtpAddress"] = to, ["DeliverToMailboxAndForward"] = a.KeepCopy ? "True" : "False" },
                Previous = new(StringComparer.OrdinalIgnoreCase) { ["ForwardingSmtpAddress"] = string.Empty, ["DeliverToMailboxAndForward"] = "False" },
                Description = $"Mail doorsturen naar {to}" + (a.KeepCopy ? " (een kopie blijft in het postvak)" : string.Empty)
                              + (a.ForwardUntil is { } u ? $", tot {u:dd/MM/yyyy}" : string.Empty),
                NewValue = to,
                Warning = domain.Length > 0 && !to.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase) ? "Doorsturen naar een ander domein: controleer of dat mag." : null,
            });

            if (a.ForwardUntil is { } until)
            {
                endOfForwarding.Add(new Change
                {
                    TargetSystem = TargetSystem.Exchange,
                    ChangeAction = ChangeAction.ExchangeUpdateMailbox,
                    Target = address,
                    TargetName = p.Name,
                    Parameters = new(StringComparer.OrdinalIgnoreCase) { ["ForwardingSmtpAddress"] = string.Empty, ["DeliverToMailboxAndForward"] = "False" },
                    Previous = new(StringComparer.OrdinalIgnoreCase) { ["ForwardingSmtpAddress"] = to, ["DeliverToMailboxAndForward"] = a.KeepCopy ? "True" : "False" },
                    Description = $"Doorsturen naar {to} stoppen (einde {until:dd/MM/yyyy})",
                    NewValue = "niet doorsturen",
                });
            }
        }

        if (plan.Count == 0 && errors.Count == 0)
        {
            errors.Add("Kies een automatisch antwoord, doorsturen of allebei.");
        }

        return (errors.Count > 0 ? new PlanOutcome(Array.Empty<Change>(), errors) : PlanOutcome.Of(plan), endOfForwarding);
    }

    /// <summary>Turns the automatic reply of a user off.</summary>
    public static PlanOutcome AutoReplyOff(Person p)
    {
        var address = p.Entra?.Mail is { Length: > 0 } m ? m : p.Upn;
        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeAutoReply,
                Target = address,
                TargetName = p.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["AutoReplyState"] = "Disabled" },
                Description = "Automatisch antwoord uitzetten",
                NewValue = "uit",
            },
        });
    }

    /// <summary>
    /// Accounts in AD whose password expires within the coming days (enabled, not "never expires"). maxAgeDays is the
    /// password age of the domain (Organisatie); 0 = passwords do not expire.
    /// </summary>
    public static IReadOnlyList<ExpiringPassword> ExpiringPasswords(InventoryData d, int maxAgeDays, int withinDays, DateTime now)
    {
        if (maxAgeDays <= 0)
        {
            return Array.Empty<ExpiringPassword>();
        }

        var output = new List<ExpiringPassword>();
        foreach (var p in d.Persons.Where(p => p.Ad is { Enabled: true, PasswordNeverExpires: false }))
        {
            if (!DateTime.TryParse(p.Ad!.PasswordLastSet, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var set))
            {
                continue;
            }

            var expires = set.AddDays(maxAgeDays);
            var left = (int)Math.Floor((expires - now).TotalDays);
            if (left <= withinDays)
            {
                output.Add(new ExpiringPassword(p, expires, left));
            }
        }

        return output.OrderBy(x => x.Expires).ToList();
    }

    /// <summary>The usual password age: the most common age at which passwords were changed, rounded to a known value
    /// (30, 42, 60, 90, 180 or 365 days); null when the data does not tell.</summary>
    public static int? SuggestedMaxAge(InventoryData d)
    {
        var known = new[] { 30, 42, 60, 90, 180, 365 };
        var oldest = d.AdUsers.Where(a => a.Enabled && !a.PasswordNeverExpires)
            .Select(a => DateTime.TryParse(a.PasswordLastSet, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? (DateTime?)t : null)
            .OfType<DateTime>().ToList();
        if (oldest.Count < 5)
        {
            return null;
        }

        var age = (DateTime.Now - oldest.Min()).TotalDays;
        return known.FirstOrDefault(k => k >= age * 0.95, 0) is var k && k > 0 ? k : null;
    }

    /// <summary>A reminder e-mail (from the signed-in administrator) for a password that expires; to the user and/or the
    /// manager.</summary>
    public static PlanOutcome PasswordReminders(InventoryData d, IEnumerable<ExpiringPassword> list, string template, bool toManager)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var x in list)
        {
            var to = new List<string>();
            var address = x.Person.Entra?.Mail is { Length: > 0 } m ? m : x.Person.Ad?.EmailAddress ?? string.Empty;
            if (address.Length > 0)
            {
                to.Add(address);
            }

            if (toManager && AttributePlans.ManagerOf(d, x.Person) is { } manager && (manager.Entra?.Mail ?? manager.Ad?.EmailAddress) is { Length: > 0 } ma)
            {
                to.Add(ma);
            }

            if (to.Count == 0)
            {
                errors.Add($"{x.Person.Name} heeft geen e-mailadres.");
                continue;
            }

            var body = template.Replace("{naam}", x.Person.Name, StringComparison.OrdinalIgnoreCase)
                .Replace("{datum}", x.Expires.ToString("dd/MM/yyyy"), StringComparison.OrdinalIgnoreCase)
                .Replace("{dagen}", Math.Max(0, x.DaysLeft).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphSendMail,
                Target = string.Join(';', to),
                TargetName = x.Person.Name,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["onderwerp"] = "Je wachtwoord verloopt binnenkort", ["tekst"] = body },
                Description = $"Herinnering mailen: wachtwoord verloopt op {x.Expires:dd/MM/yyyy}",
                NewValue = string.Join(", ", to),
            });
        }

        return PlanOutcome.Of(plan, errors);
    }

    public const string DefaultReminder = "Beste {naam},\n\nJe wachtwoord verloopt op {datum} (over {dagen} dagen). Wijzig het voor die datum: "
                                          + "druk op Ctrl+Alt+Del op je pc en kies Wachtwoord wijzigen, of via https://mysignins.microsoft.com.\n\nDe IT-dienst";

    /// <summary>Every group of a person, also through other groups (nested), with the groups in between.</summary>
    public static IReadOnlyList<GroupPath> GroupsWithPaths(InventoryData d, Person p)
    {
        var index = d.Index;
        var output = new Dictionary<string, GroupPath>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Group Group, List<string> Via)>();
        foreach (var g in index.GroupsOf(p))
        {
            queue.Enqueue((g, new List<string>()));
        }

        while (queue.Count > 0)
        {
            var (g, via) = queue.Dequeue();
            if (output.ContainsKey(g.Key) || via.Count > 10)
            {
                continue;
            }

            output[g.Key] = new GroupPath(g, via);
            foreach (var parent in ParentsOf(d, g))
            {
                if (!output.ContainsKey(parent.Key))
                {
                    queue.Enqueue((parent, via.Append(g.Name).ToList()));
                }
            }
        }

        return output.Values.OrderBy(x => x.Via.Count).ThenBy(x => x.Group.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The groups a group is a direct member of (AD, Entra and Exchange).</summary>
    private static IEnumerable<Group> ParentsOf(InventoryData d, Group g)
        => d.Index.MemberOf(g).Where(r => r.Object.Kind == ObjectKind.Group).Select(r => d.Index.Group(r.Object.Key)).OfType<Group>();
}
