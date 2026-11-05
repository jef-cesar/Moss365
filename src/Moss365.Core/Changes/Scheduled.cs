using Moss365.Core.Storage;
using System.Text.Encodings.Web;
using System.Text.Json;
using Moss365.Core.Inventory;

namespace Moss365.Core.Changes;

public enum ScheduledKind
{
    /// <summary>The offboarding is computed again at the moment itself from the data loaded then.</summary>
    Offboarding,

    /// <summary>A fixed plan (e.g. a new user); the initial password is created only when it runs.</summary>
    Onboarding,

    /// <summary>A script with fixed values; at the moment itself the administrator starts the run (never by
    /// itself).</summary>
    Script,

    /// <summary>End of a temporary exclusion from a Conditional Access policy (since 0.9): the person falls under it
    /// again.</summary>
    CaExclusionEnd,
}

/// <summary>How often a scheduled action comes back (scripts only).</summary>
public enum Recurrence
{
    Once,
    Daily,
    Weekly,
    Monthly,
}

/// <summary>
/// A change that is prepared for later. The app never runs anything without you: at the scheduled moment it shows the
/// action as "ready to run", you look at the plan and confirm. No secrets: an initial password is created only when it
/// runs.
/// </summary>
public sealed class ScheduledAction
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ScheduledKind Kind { get; init; }
    public required DateTimeOffset When { get; set; }
    public required string Title { get; init; }
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public string CreatedBy { get; init; } = string.Empty;
    public string? Note { get; set; }

    // Offboarding
    public string? PersonKey { get; init; }
    public OffboardingOptions? Options { get; init; }

    // Onboarding
    public List<Change> Changes { get; init; } = new();
    public List<Change> AfterSynchronisation { get; init; } = new();
    public string? Upn { get; init; }

    // Script: the file name (without path) and the values; relative values (@werkjaar.van, ...) are filled in at the
    // run
    public string? ScriptKey { get; init; }
    public bool ScriptBuiltIn { get; init; }
    public Dictionary<string, string> ScriptValues { get; init; } = new();
    public Recurrence Recurrence { get; init; } = Recurrence.Once;

    // End of an exclusion from a Conditional Access policy (the person is in PersonKey)
    public string? CaPolicyId { get; init; }

    public bool IsDue(DateTimeOffset now) => When <= now;

    /// <summary>
    /// The next moment after a run (null for a one-time action). Always in the future: a missed week is not made up
    /// for. Monthly on a day that does not exist (31 in February) becomes the last day of that month.
    /// </summary>
    public DateTimeOffset? NextTime(DateTimeOffset now)
    {
        if (Recurrence == Recurrence.Once)
        {
            return null;
        }

        var day = When.Day;
        var next = When;
        var months = 0;
        while (next <= now)
        {
            switch (Recurrence)
            {
                case Recurrence.Daily:
                    next = next.AddDays(1);
                    break;
                case Recurrence.Weekly:
                    next = next.AddDays(7);
                    break;
                default:
                    months++;
                    var baseline = When.AddMonths(months);
                    var max = DateTime.DaysInMonth(baseline.Year, baseline.Month);
                    next = new DateTimeOffset(baseline.Year, baseline.Month, Math.Min(day, max), When.Hour, When.Minute, 0, baseline.Offset);
                    break;
            }
        }

        return next;
    }
}

/// <summary>The plan of a scheduled action at the moment of running, or why that is not possible.</summary>
public sealed record ScheduledPlan(IReadOnlyList<Change> Plan, string? Password, IReadOnlyList<string> Errors);

public static class ScheduledPlans
{
    public static ScheduledAction Offboarding(Person person, OffboardingOptions options, DateTimeOffset whenValue, string byValue) => new()
    {
        Kind = ScheduledKind.Offboarding,
        When = whenValue,
        Title = $"Uitstroom {person.Name}",
        CreatedBy = byValue,
        PersonKey = person.Key,
        Options = options,
    };

    /// <summary>Schedules a new user; the initial password of the preview is not saved.</summary>
    public static ScheduledAction Onboarding(NewUserProposal proposal, DateTimeOffset whenValue, string byValue) => new()
    {
        Kind = ScheduledKind.Onboarding,
        When = whenValue,
        Title = $"Nieuwe gebruiker {proposal.ViewName}",
        CreatedBy = byValue,
        Upn = proposal.Upn,
        Changes = proposal.Now.Select(Copy).ToList(),
        AfterSynchronisation = proposal.AfterSynchronisation.Select(Copy).ToList(),
    };

    public static ScheduledAction Script(string key, bool builtIn, string name, IReadOnlyDictionary<string, string> values, DateTimeOffset whenValue, Recurrence recurrence, string byValue) => new()
    {
        Kind = ScheduledKind.Script,
        When = whenValue,
        Title = $"Script {name}",
        CreatedBy = byValue,
        ScriptKey = key,
        ScriptBuiltIn = builtIn,
        ScriptValues = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
        Recurrence = recurrence,
    };

    /// <summary>The end of a temporary exclusion from a Conditional Access policy.</summary>
    public static ScheduledAction CaExclusionEnd(CaPolicy policy, Person person, DateTimeOffset whenValue, string byValue) => new()
    {
        Kind = ScheduledKind.CaExclusionEnd,
        When = whenValue,
        Title = $"Einde uitzondering op \"{policy.Name}\" voor {person.Name}",
        CreatedBy = byValue,
        PersonKey = person.Key,
        CaPolicyId = policy.Id,
    };

    /// <summary>The plan to run now: an offboarding computed again from the current data, an onboarding with a new
    /// initial password.</summary>
    public static ScheduledPlan PlanFor(ScheduledAction a, InventoryData data, Configuration.OrganisationConfig config)
    {
        if (a.Kind == ScheduledKind.CaExclusionEnd)
        {
            var policy = data.ConditionalAccess?.Policies.FirstOrDefault(b => b.Id == a.CaPolicyId);
            var who = a.PersonKey is null ? null : data.SearchPerson(a.PersonKey);
            if (policy is null || who is null)
            {
                return new ScheduledPlan(Array.Empty<Change>(), null,
                    new[] { "Het beleid of de persoon staat niet (meer) in de geladen gegevens. Laad Conditional Access live." });
            }

            var u = Analysis.CaPlans.Exclusion(policy, who, add: false, data, config);
            return new ScheduledPlan(u.Plan, null, u.Errors);
        }

        if (a.Kind == ScheduledKind.Offboarding)
        {
            var person = a.PersonKey is null ? null : data.SearchPerson(a.PersonKey);
            if (person is null)
            {
                return new ScheduledPlan(Array.Empty<Change>(), null, new[] { "De gebruiker staat niet (meer) in de geladen gegevens. Laad live of kies opnieuw." });
            }

            return new ScheduledPlan(PlanBuilder.Offboarding(data, config, person, a.Options ?? new OffboardingOptions()), null, Array.Empty<string>());
        }

        var errors = new List<string>();
        if (a.Upn is { } upn && (data.AdUsers.Any(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase))
                                 || data.EntraUsers.Any(u => string.Equals(u.UserPrincipalName, upn, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add($"{upn} bestaat al. Is deze instroom al uitgevoerd?");
        }

        var plan = a.Changes.Select(Copy).ToList();
        string? password = null;
        foreach (var w in plan.Where(w => w.ChangeAction == ChangeAction.AdCreateUser))
        {
            password ??= NameRules.InitialPassword();
            w.Secret = password;
        }

        return new ScheduledPlan(plan, password, errors);
    }

    /// <summary>A copy with the same id (so that DependentOf stays correct), without a secret.</summary>
    private static Change Copy(Change w) => new()
    {
        Id = w.Id,
        TargetSystem = w.TargetSystem,
        ChangeAction = w.ChangeAction,
        Target = w.Target,
        TargetName = w.TargetName,
        Parameters = new Dictionary<string, string>(w.Parameters, StringComparer.OrdinalIgnoreCase),
        Description = w.Description,
        CurrentValue = w.CurrentValue,
        NewValue = w.NewValue,
        Warning = w.Warning,
        Selected = w.Selected,
        DependentOf = w.DependentOf,
        Previous = w.Previous is null ? null : new Dictionary<string, string>(w.Previous, StringComparer.OrdinalIgnoreCase),
    };
}

/// <summary>Scheduled actions in gepland.json in the data folder.</summary>
public sealed class ScheduledStorage
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new LegacyEnumConverter() },
    }.WithLegacyNames();

    private readonly Storage.ITextStorage _storage;
    private readonly object _lock = new();

    public ScheduledStorage(string path) : this(new Storage.FileText(path))
    {
    }

    /// <summary>Since 0.8 in the database (document "gepland").</summary>
    public ScheduledStorage(Storage.ITextStorage storage) => _storage = storage;

    public List<ScheduledAction> Read()
    {
        lock (_lock)
        {
            return _storage.Read() is { } json ? JsonSerializer.Deserialize<List<ScheduledAction>>(json, Options) ?? new List<ScheduledAction>() : new List<ScheduledAction>();
        }
    }

    public void Save(IEnumerable<ScheduledAction> actions)
    {
        lock (_lock)
        {
            _storage.Save(JsonSerializer.Serialize(actions.OrderBy(a => a.When).ToList(), Options));
        }
    }

    public void Add(ScheduledAction action) => Save(Read().Append(action));

    public void Remove(Guid id) => Save(Read().Where(a => a.Id != id));
}
