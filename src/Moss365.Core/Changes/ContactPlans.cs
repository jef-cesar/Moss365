using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Core.Changes;

/// <summary>A new mail contact (R21): someone outside the organisation in the address book.</summary>
public sealed class NewContactRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string External { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
}

/// <summary>What a mail contact has to become (R21); null = do not change.</summary>
public sealed class ContactDesired
{
    public string? DisplayName { get; set; }
    public string? External { get; set; }
    public bool? Hidden { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Company { get; set; }
    public string? Title { get; set; }
    public string? Phone { get; set; }

    public static ContactDesired Of(MailContact c) => new()
    {
        DisplayName = c.DisplayName, External = c.External, Hidden = c.Hidden, FirstName = c.FirstName, LastName = c.LastName,
        Company = c.Company, Title = c.Title, Phone = c.Phone,
    };
}

/// <summary>Plans for mail contacts (R21), through the fixed ContactAanmaken and ContactInstellen of Exchange.ps1.</summary>
public static class ContactPlans
{
    /// <summary>A new contact: a name, an address nobody in the tenant has, a unique alias.</summary>
    public static PlanOutcome New(InventoryData data, NewContactRequest r)
    {
        var display = r.DisplayName.Trim();
        var external = r.External.Trim();
        if (display.Length == 0)
        {
            return PlanOutcome.Error(Texts.T["contact.name-needed"]);
        }

        if (Problem(data, external, own: null) is { } problem)
        {
            return PlanOutcome.Error(problem);
        }

        if (!AttributePlans.ValidValue(nameof(UserDesired.Phone), r.Phone.Trim()))
        {
            return PlanOutcome.Error(Texts.T.Format("attribute.invalid", Texts.T["attribute.phone"]));
        }

        // Exchange wants a unique name and alias over all recipients
        var name = data.Contacts.Any(c => string.Equals(c.Name, display, StringComparison.OrdinalIgnoreCase))
                   || data.Mailboxes.Any(m => string.Equals(m.DisplayName, display, StringComparison.OrdinalIgnoreCase))
            ? Shorten($"{display} ({external})", 64)
            : Shorten(display, 64);
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = name,
            ["DisplayName"] = display,
            ["Alias"] = Alias(data, external),
        };
        foreach (var (key, value) in new[] { ("FirstName", r.FirstName), ("LastName", r.LastName), ("Company", r.Company), ("Title", r.Title), ("Phone", r.Phone) })
        {
            if (value.Trim().Length > 0)
            {
                parameters[key] = value.Trim();
            }
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeCreateContact,
                Target = external,
                TargetName = display,
                Parameters = parameters,
                Description = Texts.T.Format("contact.plan.new", display),
                NewValue = $"{display} <{external}>",
            },
        });
    }

    /// <summary>Only what differs, with the previous values (undo); a contact from AD is changed there.</summary>
    public static PlanOutcome Edit(InventoryData data, MailContact c, ContactDesired desired)
    {
        if (c.IsDirSynced)
        {
            return PlanOutcome.Error(Texts.T["contact.synced"]);
        }

        var errors = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var now = new List<string>();
        var becomes = new List<string>();
        void Set(string key, string label, string current, string? value)
        {
            if (value is null || string.Equals(current, value.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            parameters[key] = value.Trim();
            previous[key] = current;
            now.Add($"{label}: {(current.Length == 0 ? Texts.T["undo.empty"] : current)}");
            becomes.Add($"{label}: {(value.Trim().Length == 0 ? Texts.T["undo.empty"] : value.Trim())}");
        }

        if (desired.DisplayName is { } display && display.Trim().Length == 0)
        {
            errors.Add(Texts.T["contact.name-needed"]);
        }
        else
        {
            Set("DisplayName", Texts.T["edit-user.display-name"], c.DisplayName, desired.DisplayName);
        }

        if (desired.External is { } external && !string.Equals(external.Trim(), c.External, StringComparison.OrdinalIgnoreCase))
        {
            if (Problem(data, external.Trim(), c) is { } problem)
            {
                errors.Add(problem);
            }
            else
            {
                Set("ExternalEmailAddress", Texts.T["contact.external"], c.External, external);
            }
        }

        if (desired.Hidden is { } hidden && hidden != c.Hidden)
        {
            Set("HiddenFromAddressListsEnabled", Texts.T["bulk-tasks.hide-in-the-address-list"], c.Hidden ? "True" : "False", hidden ? "True" : "False");
        }

        if (desired.Phone is { } phone && !AttributePlans.ValidValue(nameof(UserDesired.Phone), phone.Trim()))
        {
            errors.Add(Texts.T.Format("attribute.invalid", Texts.T["attribute.phone"]));
        }
        else
        {
            Set("Phone", Texts.T["attribute.phone"], c.Phone, desired.Phone);
        }

        Set("FirstName", Texts.T["attribute.givenname"], c.FirstName, desired.FirstName);
        Set("LastName", Texts.T["attribute.surname"], c.LastName, desired.LastName);
        Set("Company", Texts.T["attribute.company"], c.Company, desired.Company);
        Set("Title", Texts.T["attribute.jobtitle"], c.Title, desired.Title);

        if (parameters.Count == 0)
        {
            return PlanOutcome.Error(errors.Count > 0 ? string.Join(" ", errors) : Texts.T["plan.nothing-changed"]);
        }

        return PlanOutcome.Of(new[]
        {
            new Change
            {
                TargetSystem = TargetSystem.Exchange,
                ChangeAction = ChangeAction.ExchangeUpdateContact,
                Target = c.Smtp,
                TargetName = c.DisplayName,
                Parameters = parameters,
                Previous = previous,
                Description = Texts.T.Format("contact.plan.edit", c.DisplayName),
                CurrentValue = string.Join("; ", now),
                NewValue = string.Join("; ", becomes),
            },
        }, errors);
    }

    /// <summary>Why a contact cannot have this address, or null: not an address, or another recipient has it.</summary>
    public static string? Problem(InventoryData data, string address, MailContact? own)
    {
        if (!AttributePlans.ValidValue(nameof(UserDesired.Upn), address))
        {
            return Texts.T.Format("code.mailbox-plans.is-not-an-e-mail", address);
        }

        return MailboxPlans.AddressHolder(data, address, own?.Smtp ?? string.Empty) is { } holder && !string.Equals(holder, own?.DisplayName, StringComparison.Ordinal)
            ? Texts.T.Format("mailbox.primary.taken", address, holder)
            : null;
    }

    /// <summary>A unique alias from the address (letters, digits and . - _; a number when it is taken).</summary>
    public static string Alias(InventoryData data, string external)
    {
        var local = external.Split('@')[0];
        var clean = new string(local.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_').ToArray()).Trim('.');
        var start = Shorten(clean.Length > 0 ? clean : "contact", 60);
        var taken = data.Contacts.Select(c => c.Alias).Concat(data.Mailboxes.Select(m => m.Alias)).Concat(data.ExoGroups.Select(g => g.Alias))
            .Concat(data.Persons.Select(p => p.Entra?.MailNickname ?? string.Empty)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alias = start;
        for (var i = 2; taken.Contains(alias); i++)
        {
            alias = start + i;
        }

        return alias;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
