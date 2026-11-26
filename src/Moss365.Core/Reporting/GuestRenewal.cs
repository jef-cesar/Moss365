using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.Core.Localization;

namespace Moss365.Core.Reporting;

/// <summary>Who vouches for a guest and when it was last confirmed that the guest is still needed.</summary>
public sealed record GuestReview(string GuestId, string Sponsor = "", DateTimeOffset? Confirmed = null, string ConfirmedBy = "", string Note = "");

/// <summary>A guest whose yearly confirmation is due.</summary>
public sealed record GuestDue(Person Guest, string Sponsor, DateTimeOffset? Since, string Reason);

/// <summary>
/// Guests with renewal (R14, 0.37): every guest has a sponsor who confirms each year that the guest is still needed.
/// Asking the sponsors is a plan of mails; a guest that is no longer needed is disabled (a plan, never deleted here).
/// The reviews are stored in the tenant database (document "gastenverlenging").
/// </summary>
public static class GuestRenewal
{
    public const string Document = "gastenverlenging";

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }.WithLegacyNames();

    public static IReadOnlyList<GuestReview> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<GuestReview>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<GuestReview>>(json, Json) ?? new List<GuestReview>();
        }
        catch (JsonException)
        {
            return Array.Empty<GuestReview>();
        }
    }

    public static string Write(IEnumerable<GuestReview> reviews) => JsonSerializer.Serialize(reviews.OrderBy(r => r.GuestId, StringComparer.Ordinal).ToList(), Json);

    public static DateTimeOffset? Created(Person guest)
        => DateTimeOffset.TryParse(guest.Entra?.CreatedDateTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>Guests whose last confirmation (or invitation) is older than the period, or without a sponsor.</summary>
    public static IReadOnlyList<GuestDue> Due(InventoryData d, IReadOnlyList<GuestReview> reviews, DateTimeOffset now, int months = 12)
    {
        var byId = reviews.GroupBy(r => r.GuestId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var output = new List<GuestDue>();
        foreach (var guest in d.Persons.Where(p => p.IsGuest && p.Enabled && p.Entra is not null))
        {
            var review = byId.GetValueOrDefault(guest.Entra!.Id);
            var since = review?.Confirmed ?? Created(guest);
            var sponsor = review?.Sponsor ?? string.Empty;
            if (since is null || since < now.AddMonths(-months))
            {
                output.Add(new GuestDue(guest, sponsor, since, review?.Confirmed is null
                    ? since is null ? Texts.T["code.guest-renewal.never-confirmed-invitation-unknown"] : Texts.T.Format("code.guest-renewal.never-confirmed-invited-on", since.Value.LocalDateTime)
                    : Texts.T.Format("code.guest-renewal.last-confirmed-on", since.GetValueOrDefault().LocalDateTime)));
            }
            else if (sponsor.Length == 0)
            {
                output.Add(new GuestDue(guest, sponsor, since, Texts.T["code.guest-renewal.no-sponsor"]));
            }
        }

        return output.OrderBy(g => g.Since ?? DateTimeOffset.MinValue).ToList();
    }

    public static IReadOnlyList<GuestReview> SetSponsor(IReadOnlyList<GuestReview> reviews, string guestId, string sponsor)
    {
        var current = reviews.FirstOrDefault(r => r.GuestId.Equals(guestId, StringComparison.OrdinalIgnoreCase)) ?? new GuestReview(guestId);
        return reviews.Where(r => !r.GuestId.Equals(guestId, StringComparison.OrdinalIgnoreCase)).Append(current with { Sponsor = sponsor.Trim() }).ToList();
    }

    public static IReadOnlyList<GuestReview> Confirm(IReadOnlyList<GuestReview> reviews, string guestId, string by, DateTimeOffset now, string note = "")
    {
        var current = reviews.FirstOrDefault(r => r.GuestId.Equals(guestId, StringComparison.OrdinalIgnoreCase)) ?? new GuestReview(guestId);
        return reviews.Where(r => !r.GuestId.Equals(guestId, StringComparison.OrdinalIgnoreCase))
            .Append(current with { Confirmed = now, ConfirmedBy = by, Note = note.Length > 0 ? note : current.Note }).ToList();
    }

    /// <summary>The default question in the language of the interface; {gasten} stays (the list per sponsor).</summary>
    public static string DefaultQuestion => Texts.T.Format("guest-renewal.question", "{gasten}");

    /// <summary>One mail per sponsor with the guests to confirm; guests without a sponsor are an error.</summary>
    public static PlanOutcome AskSponsors(IEnumerable<GuestDue> due, string template)
    {
        var plan = new List<Change>();
        var errors = new List<string>();
        foreach (var group in due.GroupBy(g => g.Sponsor, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Key.Length == 0 || !group.Key.Contains('@'))
            {
                errors.AddRange(group.Select(g => Texts.T.Format("code.guest-renewal.has-no-sponsor-with-an", g.Guest.Name)));
                continue;
            }

            var list = string.Join("\n", group.Select(g => $"- {g.Guest.Name} ({g.Guest.Upn})"));
            plan.Add(new Change
            {
                TargetSystem = TargetSystem.Graph,
                ChangeAction = ChangeAction.GraphSendMail,
                Target = group.Key,
                TargetName = group.Key,
                Parameters = new(StringComparer.OrdinalIgnoreCase) { ["onderwerp"] = Texts.T["guest-renewal.subject"], ["tekst"] = template.Replace("{gasten}", list, StringComparison.OrdinalIgnoreCase) },
                Description = Texts.T.Format("code.guest-renewal.ask-the-sponsor-to-confirm", group.Count()),
                NewValue = group.Key,
            });
        }

        return PlanOutcome.Of(plan, errors);
    }

    /// <summary>Disable guests that are no longer needed (sign-in blocked; can be undone).</summary>
    public static PlanOutcome Disable(IEnumerable<Person> guests)
        => PlanOutcome.Of(guests.Where(g => g.IsGuest && g.Entra is { AccountEnabled: true }).Select(g => new Change
        {
            TargetSystem = TargetSystem.Graph,
            ChangeAction = ChangeAction.GraphSetAttributes,
            Target = g.Entra!.Id,
            TargetName = g.Name,
            Parameters = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "false" },
            Previous = new(StringComparer.OrdinalIgnoreCase) { ["accountEnabled"] = "true" },
            Description = Texts.T["code.guest-renewal.disable-guest-no-longer-confirmed"],
            CurrentValue = "ingeschakeld",
            NewValue = "uitgeschakeld",
        }));
}
