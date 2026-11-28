using Moss365.Core.Localization;
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

    /// <summary>Google Workspace only (G1).</summary>
    Google,
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
    /// <summary>A copy: background steps change the list while a screen reads it.</summary>
    public IReadOnlyDictionary<string, DateTime> LiveOn
    {
        get
        {
            lock (_taskLock)
            {
                return new Dictionary<string, DateTime>(_liveOn, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public bool IsLive
    {
        get
        {
            lock (_taskLock)
            {
                return _liveOn.Count > 0;
            }
        }
    }

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
                return Texts.T.Format("code.management-state-tasks.read-live-at", t);
            }
        }

        var (share, file) = r.Kind switch
        {
            ObjectKind.Mailbox => (new[] { "Postvakken" }, InventoryLoader.ExoMailboxes),
            ObjectKind.Room => (new[] { "Rooms" }, InventoryLoader.ExoRooms),
            _ => (new[] { "AD", "Entra" }, InventoryLoader.AdUsers),
        };
        var liveOn = LiveOn;
        var live = share.Where(liveOn.ContainsKey).Select(d => $"{d} live om {liveOn[d]:HH:mm}").ToList();
        return live.Count > 0 ? string.Join(", ", live) : Texts.T["code.connections.from-the-inventory"] + (InventoryDate(file) is { } dt ? (" " + Texts.T.Format("code.management-state-tasks.of", dt)) : string.Empty);
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
            Data = change(_data);
            return Data;
        }
    }

    /// <summary>The check of the own Google project (G1): the sign-in and every API, with what to do; one line when
    /// Google is not set up.</summary>
    public async Task<IReadOnlyList<GoogleCheck>> CheckGoogleAsync(CancellationToken cancel = default)
    {
        if (IsHistoric || !HasGoogle || _factory?.Google(EffectiveSettings) is not { } google)
        {
            return new[] { new GoogleCheck(Texts.T["google.check.settings"], false, Texts.T["google.fill-in"]) };
        }

        try
        {
            return await google.CheckAsync(cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new[] { new GoogleCheck(Texts.T["google.check.sign-in"], false, ex.Message) };
        }
    }

    /// <summary>Starts live loading in the background; false when a task is already running or connecting is not
    /// possible.</summary>
    public bool StartLoading(LoadKind kind)
    {
        if (_factory is null || IsHistoric)
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
            // Without a window (R18) nobody can type the AD password or sign in: those parts wait for the app
            if (EffectiveSettings.AdFilled && !(_readingWithoutWindow && AdNeedsPassword))
            {
                steps.Add(new(Texts.T["code.management-state-tasks.read-ad"], "AD", async (p, ct) =>
                {
                    var ad = _factory.Ad(EffectiveSettings, AdPassword) ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.ad-is-not-available-here"]);
                    var read = await ad.ReadAsync(AdBaseForQuick(), p, ct);
                    UpdateData(d => LiveLoader.Merge(d, read, null));
                    RememberSnapshot(read);
                    Live("AD", ObjectKind.User, ObjectKind.Group);
                    WasRead(InventoryComponent.Ad);
                }));
            }

            if (EffectiveSettings.GraphFilled && !(_readingWithoutWindow && !EffectiveSettings.RememberSignIn))
            {
                steps.Add(new(Texts.T["code.management-state-tasks.read-entra"], "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.entra-is-not-set-up"]);
                    var read = await graph.ReadAsync(GraphBaseForQuick(), p, ct);
                    UpdateData(d => LiveLoader.Merge(d, null, read));
                    RememberDelta(read);
                    Live("Entra", ObjectKind.User, ObjectKind.Group);
                    WasRead(InventoryComponent.Entra);
                }));

                steps.Add(new(Texts.T["code.management-state-tasks.read-security-mfa-and-admin"], "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.entra-is-not-set-up"]);
                    if (await graph.ReadSecurityAsync(p, ct) is not { } security)
                    {
                        p.Report(Texts.T["code.management-state-tasks.not-available-in-this-environment"]);
                        return;
                    }

                    UpdateData(d => LiveLoader.WithSecurity(d, security));
                    var problems = new[] { security.MfaMessage, security.RolesMessage }.Where(m => m is not null).ToList();
                    if (problems.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(" ", problems));
                    }
                }));

                steps.Add(new(Texts.T["code.management-state-tasks.read-conditional-access"], "Entra", async (p, ct) =>
                {
                    var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.entra-is-not-set-up"]);
                    if (await graph.ReadConditionalAccessAsync(p, ct) is not { } ca)
                    {
                        p.Report(Texts.T["code.management-state-tasks.not-available-in-this-environment"]);
                        return;
                    }

                    UpdateData(d => LiveLoader.WithConditionalAccess(d, ca));
                    if (ca.Message is not null)
                    {
                        throw new InvalidOperationException(ca.Message);
                    }

                    SaveCaBackup(ca, p);
                }));

                // R18: after the security data (the administrators), in the same lane
                if (Config.Retention.KeepLocalLogs)
                {
                    steps.Add(new(Texts.T["local-logs.step"], "Entra", async (p, ct) =>
                    {
                        var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.entra-is-not-set-up"]);
                        await CollectLocalLogsAsync(graph, p, ct);
                    }));
                }
            }

            if (steps.Count == 0)
            {
                Error = Texts.T["code.management-state-tasks.fill-in-the-ad-and"];
                Changed?.Invoke();
                return false;
            }
        }

        if (kind == LoadKind.Everything && HasMicrosoft365)
        {
            // Rooms and mailboxes in one Exchange session: sign in once and load the module once
            steps.Add(new(Texts.T["code.management-state-tasks.read-rooms-mailboxes-and-groups"], "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(EffectiveSettings) ?? throw new InvalidOperationException(Texts.T["code.management-state-helpdesk.exchange-online-is-not-available"]);
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

        if (kind == LoadKind.Rooms && HasMicrosoft365)
        {
            steps.Add(new(Texts.T["code.management-state-tasks.read-rooms-exchange"], "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(EffectiveSettings) ?? throw new InvalidOperationException(Texts.T["code.management-state-helpdesk.exchange-online-is-not-available"]);
                var rooms = await exchange.ReadRoomsAsync(p, ct);
                UpdateData(d => LiveLoader.WithRooms(d, rooms));
                Live("Rooms", ObjectKind.Room);
                WasRead(InventoryComponent.Rooms);
            }));
        }

        if (kind == LoadKind.Mailboxes && HasMicrosoft365)
        {
            steps.Add(new(Texts.T["code.management-state-tasks.read-mailboxes-and-groups-exchange"], "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(EffectiveSettings) ?? throw new InvalidOperationException(Texts.T["code.management-state-helpdesk.exchange-online-is-not-available"]);
                var data = await exchange.ReadMailboxesAsync(p, ct);
                UpdateData(d => LiveLoader.WithExchange(d, data));
                Live("Postvakken", ObjectKind.Mailbox, ObjectKind.Group);
                WasRead(InventoryComponent.Mailboxes);
            }));
        }

        // G1: Google Workspace in its own lane; never without a window (signing in needs the browser)
        if (kind is LoadKind.Everything or LoadKind.Google && HasGoogle && EffectiveSettings.GoogleFilled && !_readingWithoutWindow)
        {
            steps.Add(new(Texts.T["google.read.step"], "Google", async (p, ct) =>
            {
                var google = _factory.Google(EffectiveSettings) ?? throw new InvalidOperationException(Texts.T["google.not-available"]);
                var data = await google.ReadAsync(p, ct);
                UpdateData(d => LiveLoader.WithGoogle(d, data));
                Live("Google", ObjectKind.User, ObjectKind.Group);
                WasRead(InventoryComponent.Google);
            }));

            // The logs of Google kept locally (as R18), after the users (the administrators), in the same lane
            if (Config.Retention.KeepLocalLogs)
            {
                steps.Add(new(Texts.T["local-logs.google.step"], "Google", async (p, ct) =>
                {
                    var google = _factory.Google(EffectiveSettings) ?? throw new InvalidOperationException(Texts.T["google.not-available"]);
                    await CollectGoogleLogsAsync(google, p, ct);
                }));
            }
        }

        if (kind == LoadKind.Google && steps.Count == 0)
        {
            Error = Texts.T["google.fill-in"];
            Changed?.Invoke();
            return false;
        }

        // Only when all lanes are done
        steps.Add(new(Texts.T["code.management-state-tasks.save-as-inventory"], null, (p, _) =>
        {
            if (components == InventoryComponent.No)
            {
                p.Report(Texts.T["code.management-state-tasks.nothing-read-live-nothing-saved"]);
                return Task.CompletedTask;
            }

            SaveAsInventory(components, p);
            return Task.CompletedTask;
        }));

        var name = kind switch
        {
            LoadKind.AdAndEntra => Texts.T["code.management-state-tasks.load-live-ad-and-entra"],
            LoadKind.Rooms => Texts.T["code.management-state-tasks.load-rooms"],
            LoadKind.Mailboxes => Texts.T["code.management-state-tasks.load-mailboxes"],
            _ => Texts.T["set-up-tenant.load-everything-live"],
        };
        return Start(name, steps);
    }

    /// <summary>Read a few objects again (Refresh button).</summary>
    public bool StartRefresh(IEnumerable<ObjectRef> objects)
    {
        var list = objects.ToList();
        var targets = RefreshTargets.For(Data, list);
        return StartRefresh(targets, list.Count == 1 ? Texts.T.Format("code.management-state-tasks.refresh", list[0].Name) : Texts.T.Format("code.management-state-tasks.refresh-2", targets.Count));
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
                var ad = _factory.Ad(EffectiveSettings, AdPassword) ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.ad-is-not-set-up"]);
                foreach (var sam in targets.AdUsers)
                {
                    p.Report(Texts.T.Format("code.management-state-tasks.user", sam));
                    var g = await ad.ReadUserAsync(sam, ct);
                    UpdateData(d => ObjectRefresher.WithAdUser(d, sam, g));
                }

                foreach (var sam in targets.AdGroups)
                {
                    p.Report(Texts.T.Format("code.management-state-tasks.group", sam));
                    var g = await ad.ReadGroupAsync(sam, ct);
                    UpdateData(d => ObjectRefresher.WithAdGroup(d, sam, g));
                }
            }));
        }

        if (targets.EntraUsers.Count + targets.CloudGroups.Count > 0)
        {
            steps.Add(new($"Entra ({targets.EntraUsers.Count + targets.CloudGroups.Count})", "Entra", async (p, ct) =>
            {
                var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.entra-is-not-set-up-2"]);
                foreach (var id in targets.EntraUsers)
                {
                    p.Report(Texts.T.Format("code.management-state-tasks.user", id));
                    var g = await graph.ReadUserAsync(id, ct);
                    UpdateData(d => ObjectRefresher.WithEntraUser(d, id, g));
                }

                foreach (var id in targets.CloudGroups)
                {
                    p.Report(Texts.T.Format("code.management-state-tasks.group", id));
                    var g = await graph.ReadGroupAsync(id, ct);
                    UpdateData(d => ObjectRefresher.WithCloudGroup(d, id, g));
                }
            }));
        }

        if (targets.ConditionalAccess)
        {
            steps.Add(new("Conditional Access", "Entra", async (p, ct) =>
            {
                var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.management-state-tasks.entra-is-not-set-up-2"]);
                if (await graph.ReadConditionalAccessAsync(p, ct) is { Message: null } ca)
                {
                    UpdateData(d => LiveLoader.WithConditionalAccess(d, ca));
                    SaveCaBackup(ca, p);
                }
            }));
        }

        if (targets.ExchangeAddresses.Count > 0 && HasMicrosoft365)
        {
            steps.Add(new($"Exchange ({targets.ExchangeAddresses.Count})", "Exchange", async (p, ct) =>
            {
                var exchange = _factory.Exchange(EffectiveSettings) ?? throw new InvalidOperationException(Texts.T["code.management-state-helpdesk.exchange-online-is-not-available"]);
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
            task.Write((Texts.T["code.management-state-tasks.finishing-failed"] + " ") + ex.Message);
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
            task.Write(Texts.T.Format("code.management-state-tasks.done", step.Name));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            step.Status = StepStatus.Cancelled;
            task.Cancelled = true;
            task.Write(Texts.T.Format("code.management-state-tasks.cancelled", step.Name));
        }
        catch (Exception ex)
        {
            step.Status = StepStatus.Error;
            step.Message = ex.Message;
            task.Write(Texts.T.Format("code.management-state-tasks.failed", step.Name, ex.Message));
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
        var folder = InventoryWriter.Save(Data, Files, components, DateTime.Now);
        SavedInventory = folder;
        SaveQuickSnapshot(folder, components);
        SaveLiveSnapshot(components, folder);
        p.Report(Texts.T.Format("tasks.saved-in", folder[(folder.LastIndexOf('/') + 1)..]));
        if ((components & (InventoryComponent.Ad | InventoryComponent.Entra)) != 0)
        {
            SaveKpiSnapshot();
            SaveOverviewCounts();
        }

        // Keep only the latest live inventories (those of Export-Inventaris.ps1 stay)
        foreach (var old in InventoryLoader.SearchFolders(Files)
                     .Where(m => m.EndsWith("-live", StringComparison.OrdinalIgnoreCase))
                     .Skip(SavedLiveInventories))
        {
            Files.DeleteAll(old + "/");
        }

        Folders = InventoryLoader.SearchFolders(Files);
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

        StartRefresh(targets, Texts.T["code.management-state-tasks.refresh-after-a-change"]);
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
