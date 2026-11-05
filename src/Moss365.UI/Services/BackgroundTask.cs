namespace Moss365.UI.Services;

public enum StepStatus
{
    Wait,
    Busy,
    Done,
    Error,
    Cancelled,
}

/// <summary>One step of a background task, e.g. "AD lezen".</summary>
public sealed class TaskStep
{
    public required string Name { get; init; }
    public StepStatus Status { get; internal set; }

    /// <summary>Last progress message or the error.</summary>
    public string? Message { get; internal set; }

    public DateTime? Start { get; internal set; }
    public DateTime? End { get; internal set; }

    public TimeSpan? Duration => Start is { } s ? (End ?? DateTime.Now) - s : null;
}

/// <summary>
/// A task that runs in the background (live loading, refreshing objects). It lives in <see cref="ManagementState"/>, so
/// the progress is kept when you change screens.
/// </summary>
public sealed class BackgroundTask
{
    private readonly List<string> _log = new();

    public BackgroundTask(string name, IEnumerable<string> steps)
    {
        Name = name;
        Steps = steps.Select(s => new TaskStep { Name = s }).ToList();
    }

    public string Name { get; }
    public IReadOnlyList<TaskStep> Steps { get; }
    public DateTime Start { get; } = DateTime.Now;
    public DateTime? End { get; internal set; }
    public bool Busy => End is null;
    public bool Cancelled { get; internal set; }
    public bool WithErrors => Steps.Any(s => s.Status == StepStatus.Error);

    internal CancellationTokenSource Cancel { get; } = new();

    public TaskStep? Current => Steps.FirstOrDefault(s => s.Status == StepStatus.Busy);

    /// <summary>0..1: finished steps, a running step counts as half.</summary>
    public double Progress => Steps.Count == 0 ? 1
        : Steps.Sum(s => s.Status is StepStatus.Done or StepStatus.Error or StepStatus.Cancelled ? 1.0 : s.Status == StepStatus.Busy ? 0.5 : 0) / Steps.Count;

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToList();
            }
        }
    }

    internal void Write(string text)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {text}");
        }
    }

    /// <summary>Short text for the status at the bottom of the menu.</summary>
    public string Summary => Busy
        ? Steps.Count(s => s.Status == StepStatus.Busy) > 1
            ? $"{Steps.Count(s => s.Status == StepStatus.Busy)} bezig: {string.Join(", ", Steps.Where(s => s.Status == StepStatus.Busy).Select(s => s.Name))}"
            : $"{Current?.Name ?? Name}: {Current?.Message ?? "bezig..."}"
        : Cancelled ? $"{Name}: geannuleerd"
        : WithErrors ? $"{Name}: mislukt om {End:HH:mm}"
        : $"{Name}: klaar om {End:HH:mm}";
}
