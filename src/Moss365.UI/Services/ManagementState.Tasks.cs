using System.Globalization;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.UI.Services;

/// <summary>What is loaded live.</summary>
public enum LoadKind
{
    AdAndEntra,
    Rooms,
    Mailboxes,
    Everything,
}

/// <summary>
/// Background tasks (live loading, refreshing objects) and how current the data is. The task lives here and not in a
/// screen, so that the progress stays when you change screens; the menu shows a short status.
/// </summary>
public sealed partial class ManagementState
{
    /// <summary>How many inventories saved live the app keeps; older ones are cleaned up.</summary>
    public const int SavedLiveInventories = 10;

    private readonly object _taskLock = new();
    private readonly Dictionary<string, DateTime> _liveOn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _read = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last (or running) background task.</summary>
    public BackgroundTask? Job { get; private set; }

    public bool TaskBusy => Job?.Busy == true;

    /// <summary>Per part ("AD", "Entra", "Rooms", "Postvakken") when it was fully loaded live.</summary>
    public IReadOnlyDictionary<string, DateTime> LiveOn => _liveOn;

    public bool IsLive => _liveOn.Count > 0;

    /// <summary>Objects that were changed but not read again yet.</summary>
    public int CountChanged
    {
        get
        {
            lock (_taskLock)
            {
                return _changed.Count;
            }
        }
    }

    /// <summary>The folder in which the latest live data was saved as an inventory.</summary>
    public string? SavedInventory { get; private set; }

    public bool IsChanged(ObjectRef r)
    {
        lock (_taskLock)
        {
            return _changed.Contains(RefreshTargets.Key(r));
        }
    }

    /// <summary>Where the shown data of an object comes from, honestly: per object, per part or the
    /// inventory.</summary>
    public string OriginOf(ObjectRef r)
    {
        lock (_taskLock)
        {
            if (_read.TryGetValue(RefreshTargets.Key(r), out var t))
            {
                return $"live gelezen om {t:HH:mm}";
            }
        }

        var (share, file) = r.Kind switch
        {
            ObjectKind.Mailbox => (new[] { "Postvakken" }, InventoryLoader.ExoMailboxes),
            ObjectKind.Room => (new[] { "Rooms" }, InventoryLoader.ExoRooms),
            _ => (new[] { "AD", "Entra" }, InventoryLoader.AdUsers),
        };
        var live = share.Where(_liveOn.ContainsKey).Select(d => $"{d} live om {_liveOn[d]:HH:mm}").ToList();
        return live.Count > 0 ? string.Join(", ", live) : "uit de inventaris" + (InventoryDate(file) is { } dt ? $" van {dt:dd/MM HH:mm}" : string.Empty);
    }

    /// <summary>Date of the inventory folder a file was loaded from (the name contains yyyyMMdd-HHmm).</summary>
    public DateTime? InventoryDate(string file)
    {
        var folder = Data.Sources.FirstOrDefault(b => b.File == file)?.Folder;
        var name = folder is null ? null : Path.GetFileName(folder);
        return name is { Length: >= 24 } && DateTime.TryParseExact(name.Substring(11, 13), "yyyyMMdd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;
    }

    // ------------------------------------------------------------------ starting

    /// <summary>
    /// A step of a background task. Steps with the same <see cref="Lane"/> run one after the other; different lanes run
    /// at the same time (AD, Entra and Exchange). Steps without a lane run only when all lanes are done.
    /// </summary>
    private sealed record TaskWork(string Name, string? Lane, Func<IProgress<string>, CancellationToken, Task> Work);

    private readonly object _dataLock = new();

    /// <summary>Updates the data with what a step read; one at a time, so that lanes do not overwrite each other's
    /// result.</summary>
    private InventoryData UpdateData(Func<InventoryData, InventoryData> change)
    {
        lock (_dataLock)
        {
            Data = change(Data);
            return Data;
        }
    }

    /// <summary>Starts live loading in the background; false when a task is already running or connecting is not
    /// possible.</summary>
    public bool StartLoading(LoadKind kind)
    {
        if (_factory is null)
        {
            return false;
        }

        var steps = new List<TaskWork>();
        var components = InventoryComponent.No;
        void WasRead(InventoryComponent o)
        {
            lock (_taskLock)
            {
                components |= o;
            }
        }

        if (kind is LoadKind.AdAndEntra or LoadKind.Everything)
        {
            if (Settings.AdFilled)
            {
                steps.Add(new("AD lezen", "AD", async (p, ct) =>
                {
                    var ad = _factory.Ad(Settings, AdPassword) ?? throw new InvalidOperationException("AD is hier niet beschikbaar.");
                    var read = await ad.ReadAsync(AdBaseForQuick(), p, ct);
                    UpdateData(d => LiveLoader.Merge(d, read, null));
                    RememberSnapshot(read);
                    Live("AD", ObjectKind.User, ObjectKind.Group);
                    WasRead(InventoryComponent.Ad);
                }));
            }

            if (Settings.GraphFilled)
            {
                steps.Add(new("Entra lezen", "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld.");
                    var read = await graph.ReadAsync(GraphBaseForQuick(), p, ct);
                    UpdateData(d => LiveLoader.Merge(d, null, read));
                    RememberDelta(read);
                    Live("Entra", ObjectKind.User, ObjectKind.Group);
                    WasRead(InventoryComponent.Entra);
                }));

                steps.Add(new("Beveiliging lezen (MFA en beheerrollen)", "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld.");
                    if (await graph.ReadSecurityAsync(p, ct) is not { } security)
                    {
                        p.Report("Niet beschikbaar in deze omgeving.");
                        return;
                    }

                    UpdateData(d => LiveLoader.WithSecurity(d, security));
                    var problems = new[] { security.MfaMessage, security.RolesMessage }.Where(m => m is not null).ToList();
                    if (problems.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(" ", problems));
                    }
                }));

                steps.Add(new("Conditional Access lezen", "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld.");
                    if (await graph.ReadConditionalAccessAsync(p, ct) is not { } ca)
                    {
                        p.Report("Niet beschikbaar in deze omgeving.");
                        return;
                    }

                    UpdateData(d => LiveLoader.WithConditionalAccess(d, ca));
                    if (ca.Message is not null)
                    {
                        throw new InvalidOperationException(ca.Message);
                    }

                    SaveCaBackup(ca, p);
                }));
            }

            if (steps.Count == 0)
            {
                Error = "Vul eerst de AD- en/of Entra-gegevens in (scherm Verbindingen).";
                Changed?.Invoke();
                return false;
            }
        }

        if (kind == LoadKind.Everything)
        {
            // Rooms and mailboxes in one Exchange session: sign in once and load the module once
            steps.Add(new("Rooms, postvakken en groepen lezen (Exchange)", "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(Settings) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                var reading = await exchange.ReadAllAsync(p, ct);
                if (reading.Rooms is { } rooms)
                {
                    UpdateData(d => LiveLoader.WithRooms(d, rooms));
                    Live("Rooms", ObjectKind.Room);
                    WasRead(InventoryComponent.Rooms);
                }

                if (reading.Mailboxes is { } mailboxes)
                {
                    UpdateData(d => LiveLoader.WithExchange(d, mailboxes));
                    Live("Postvakken", ObjectKind.Mailbox, ObjectKind.Group);
                    WasRead(InventoryComponent.Mailboxes);
                }

                var errors = new[] { reading.RoomsError is null ? null : "Rooms: " + reading.RoomsError, reading.MailboxesError is null ? null : "Postvakken: " + reading.MailboxesError }
                    .Where(f => f is not null).ToList();
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(string.Join(" ", errors));
                }
            }));
        }

        if (kind == LoadKind.Rooms)
        {
            steps.Add(new("Rooms lezen (Exchange)", "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(Settings) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                var rooms = await exchange.ReadRoomsAsync(p, ct);
                UpdateData(d => LiveLoader.WithRooms(d, rooms));
                Live("Rooms", ObjectKind.Room);
                WasRead(InventoryComponent.Rooms);
            }));
        }

        if (kind == LoadKind.Mailboxes)
        {
            steps.Add(new("Postvakken en groepen lezen (Exchange)", "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(Settings) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                var data = await exchange.ReadMailboxesAsync(p, ct);
                UpdateData(d => LiveLoader.WithExchange(d, data));
                Live("Postvakken", ObjectKind.Mailbox, ObjectKind.Group);
                WasRead(InventoryComponent.Mailboxes);
            }));
        }

        // Only when all lanes are done
        steps.Add(new("Bewaren als inventaris", null, (p, _) =>
        {
            if (components == InventoryComponent.No)
            {
                p.Report("Niets live gelezen; niets bewaard.");
                return Task.CompletedTask;
            }

            SaveAsInventory(components, p);
            return Task.CompletedTask;
        }));

        var name = kind switch
        {
            LoadKind.AdAndEntra => "Live laden (AD en Entra)",
            LoadKind.Rooms => "Rooms laden",
            LoadKind.Mailboxes => "Postvakken laden",
            _ => "Alles live laden",
        };
        return Start(name, steps);
    }

    /// <summary>Read a few objects again (Refresh button).</summary>
    public bool StartRefresh(IEnumerable<ObjectRef> objects)
    {
        var list = objects.ToList();
        var targets = RefreshTargets.For(Data, list);
        return StartRefresh(targets, list.Count == 1 ? $"Vernieuwen: {list[0].Name}" : $"Vernieuwen ({targets.Count})");
    }

    private bool StartRefresh(RefreshTargets targets, string name)
    {
        if (_factory is null || targets.Empty)
        {
            return false;
        }

        var steps = new List<TaskWork>();
        if (targets.AdUsers.Count + targets.AdGroups.Count > 0)
        {
            steps.Add(new($"AD ({targets.AdUsers.Count + targets.AdGroups.Count})", "AD", async (p, ct) =>
            {
                var ad = _factory.Ad(Settings, AdPassword) ?? throw new InvalidOperationException("AD is niet ingesteld (scherm Verbindingen).");
                foreach (var sam in targets.AdUsers)
                {
                    p.Report($"gebruiker {sam}");
                    var g = await ad.ReadUserAsync(sam, ct);
                    UpdateData(d => ObjectRefresher.WithAdUser(d, sam, g));
                }

                foreach (var sam in targets.AdGroups)
                {
                    p.Report($"groep {sam}");
                    var g = await ad.ReadGroupAsync(sam, ct);
                    UpdateData(d => ObjectRefresher.WithAdGroup(d, sam, g));
                }
            }));
        }

        if (targets.EntraUsers.Count + targets.CloudGroups.Count > 0)
        {
            steps.Add(new($"Entra ({targets.EntraUsers.Count + targets.CloudGroups.Count})", "Entra", async (p, ct) =>
            {
                var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld (scherm Verbindingen).");
                foreach (var id in targets.EntraUsers)
                {
                    p.Report($"gebruiker {id}");
                    var g = await graph.ReadUserAsync(id, ct);
                    UpdateData(d => ObjectRefresher.WithEntraUser(d, id, g));
                }

                foreach (var id in targets.CloudGroups)
                {
                    p.Report($"groep {id}");
                    var g = await graph.ReadGroupAsync(id, ct);
                    UpdateData(d => ObjectRefresher.WithCloudGroup(d, id, g));
                }
            }));
        }

        if (targets.ConditionalAccess)
        {
            steps.Add(new("Conditional Access", "Entra", async (p, ct) =>
            {
                var graph = Graph() ?? throw new InvalidOperationException("Entra is niet ingesteld (scherm Verbindingen).");
                if (await graph.ReadConditionalAccessAsync(p, ct) is { Message: null } ca)
                {
                    UpdateData(d => LiveLoader.WithConditionalAccess(d, ca));
                    SaveCaBackup(ca, p);
                }
            }));
        }

        if (targets.ExchangeAddresses.Count > 0)
        {
            steps.Add(new($"Exchange ({targets.ExchangeAddresses.Count})", "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(Settings) ?? throw new InvalidOperationException("Exchange Online is hier niet beschikbaar.");
                p.Report(string.Join(", ", targets.ExchangeAddresses));
                var objects = await exchange.ReadObjectsAsync(targets.ExchangeAddresses.ToList(), ct);
                UpdateData(d => ObjectRefresher.WithExchange(d, objects));
            }));
        }

        return Start(name, steps, done: task =>
        {
            // Only when everything succeeded are the objects current again
            if (task.Steps.All(s => s.Status == StepStatus.Done))
            {
                lock (_taskLock)
                {
                    foreach (var key in targets.Objects)
                    {
                        _read[key] = DateTime.Now;
                        _changed.Remove(key);
                    }
                }
            }
        });
    }

    public void CancelTask()
    {
        try
        {
            Job?.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Just finished
        }
    }

    // ------------------------------------------------------------------ running

    private bool Start(string name, List<TaskWork> steps, Action<BackgroundTask>? done = null)
    {
        BackgroundTask task;
        lock (_taskLock)
        {
            if (TaskBusy)
            {
                return false;
            }

            task = new BackgroundTask(name, steps.Select(s => s.Name));
            Job = task;
        }

        Error = null;
        Changed?.Invoke();
        _ = Task.Run(() => ExecuteAsync(task, steps, done));
        return true;
    }

    private async Task ExecuteAsync(BackgroundTask task, List<TaskWork> steps, Action<BackgroundTask>? done)
    {
        var ct = task.Cancel.Token;

        // Each lane one after the other, the lanes at the same time; an error in one lane lets the others continue
        var lanes = steps.Select((s, i) => (s, i)).Where(x => x.s.Lane is not null).GroupBy(x => x.s.Lane!)
            .Select(g => Task.Run(async () =>
            {
                foreach (var (_, i) in g)
                {
                    await RunStepAsync(task, i, steps[i], ct);
                }
            }))
            .ToList();
        await Task.WhenAll(lanes);

        foreach (var (s, i) in steps.Select((s, i) => (s, i)).Where(x => x.s.Lane is null))
        {
            await RunStepAsync(task, i, s, ct);
        }

        task.Cancelled |= ct.IsCancellationRequested;
        LoadedOn = DateTime.Now;
        try
        {
            done?.Invoke(task);
        }
        catch (Exception ex)
        {
            task.Write("Afronden mislukt: " + ex.Message);
        }

        task.End = DateTime.Now;
        Log.Information("{Taak} klaar in {Seconden:0} s: {Stappen}", LogName(task.Name), (task.End.Value - task.Start).TotalSeconds,
            string.Join(", ", task.Steps.Select(s => $"{s.Name} {s.Status}")));
        task.Cancel.Dispose();
        Changed?.Invoke();
    }

    private async Task RunStepAsync(BackgroundTask task, int index, TaskWork work, CancellationToken ct)
    {
        var step = task.Steps[index];
        if (ct.IsCancellationRequested)
        {
            step.Status = StepStatus.Cancelled;
            return;
        }

        step.Status = StepStatus.Busy;
        step.Start = DateTime.Now;
        task.Write($"{step.Name}...");
        Changed?.Invoke();
        var reporter = new Reporter(t =>
        {
            step.Message = t;
            task.Write($"{step.Name}: {t}");
            Changed?.Invoke();
        });
        try
        {
            await work.Work(reporter, ct);
            step.Status = StepStatus.Done;
            step.Message = null;
            task.Write($"{step.Name}: klaar.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            step.Status = StepStatus.Cancelled;
            task.Cancelled = true;
            task.Write($"{step.Name}: geannuleerd.");
        }
        catch (Exception ex)
        {
            step.Status = StepStatus.Error;
            step.Message = ex.Message;
            task.Write($"{step.Name}: MISLUKT - {ex.Message}");
            Log.Warning("{Taak} - {Stap} mislukt: {Fout}", LogName(task.Name), step.Name, ex.Message);
        }

        step.End = DateTime.Now;
        Changed?.Invoke();
    }

    /// <summary>Synchronous progress: Progress&lt;T&gt; would send messages via the thread pool and so in random
    /// order.</summary>
    private sealed class Reporter(Action<string> doValue) : IProgress<string>
    {
        public void Report(string value) => doValue(value);
    }

    private void Live(string component, params ObjectKind[] kinds)
    {
        lock (_taskLock)
        {
            _liveOn[component] = DateTime.Now;

            // What was read again in full is no longer "changed, not refreshed yet"
            var prefixes = kinds.Select(s => s.ToString().ToLowerInvariant() + ":").ToList();
            _changed.RemoveWhere(k => prefixes.Any(v => k.StartsWith(v, StringComparison.Ordinal)));
        }
    }

    private void SaveAsInventory(InventoryComponent components, IProgress<string> p)
    {
        var folder = InventoryWriter.Save(Data, _environment.InventoryBase, components, DateTime.Now);
        SavedInventory = folder;
        SaveQuickSnapshot(folder, components);
        p.Report($"Bewaard in {Path.GetFileName(folder)}.");
        if ((components & (InventoryComponent.Ad | InventoryComponent.Entra)) != 0)
        {
            SaveKpiSnapshot();
        }

        // Keep only the latest live inventories (those of Export-Inventaris.ps1 stay)
        foreach (var old in InventoryLoader.SearchFolders(_environment.InventoryBase)
                     .Where(m => m.EndsWith("-live", StringComparison.OrdinalIgnoreCase))
                     .Skip(SavedLiveInventories))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception)
            {
                // No problem; again the next time
            }
        }

        Folders = InventoryLoader.SearchFolders(_environment.InventoryBase);
    }

    /// <summary>After a plan was run: mark the objects involved and read them again in the background.</summary>
    private void AfterExecute(IReadOnlyList<Change> plan, IReadOnlyList<ChangeResult> results)
    {
        var targets = RefreshTargets.AfterPlan(Data, plan, results);
        lock (_taskLock)
        {
            foreach (var key in targets.Objects)
            {
                _changed.Add(key);
                _read.Remove(key);
            }
        }

        StartRefresh(targets, "Vernieuwen na wijziging");
    }

    /// <summary>After loading an inventory from disk again, nothing is live any more.</summary>
    private void ClearLiveStatus()
    {
        lock (_taskLock)
        {
            _liveOn.Clear();
            _read.Clear();
        }
    }

    /// <summary>The name of a task for the technical log, without the name of an object ("Vernieuwen: An
    /// Peeters").</summary>
    private static string LogName(string task) => task.Contains(": ", StringComparison.Ordinal) ? task[..task.IndexOf(": ", StringComparison.Ordinal)] : task;
}
