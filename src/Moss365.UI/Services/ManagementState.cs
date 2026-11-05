using System.Text.Json;
using Moss365.Core.Analysis;
using Moss365.Core.Configuration;
using Moss365.Core.Inventory;
using Moss365.Core.Storage;
using Moss365.Core.Permissions;
using Moss365.Core.Changes;

namespace Moss365.UI.Services;

/// <summary>Where the app finds its files. The Windows app fills this in; tests use temporary folders.</summary>
public interface IAppEnvironment
{
    /// <summary>Base folder with the inventory folders (Inventaris-yyyyMMdd-HHmm).</summary>
    string InventoryBase { get; }

    /// <summary>The bundled organisatie.json (read only).</summary>
    string ConfigDefaultPath { get; }

    /// <summary>Where the app saves changes to the organisation.</summary>
    string ConfigUsersPath { get; }

    /// <summary>Folder for the own files of the app: connections, change log, follow-up.</summary>
    string DataFolder => Path.GetDirectoryName(ConfigUsersPath) ?? ".";

    /// <summary>Where exports (lists, cleanup rounds) go.</summary>
    string ExportFolder => Path.Combine(DataFolder, "export");

    /// <summary>The built-in scripts (the scripts folder next to the app).</summary>
    string BuiltInScripts => Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>Own scripts, last values and runs.</summary>
    string ScriptsData => Path.Combine(DataFolder, "scripts");

    /// <summary>Shows a file in Explorer (only in the Windows app).</summary>
    void ShowFile(string path)
    {
    }
}

public sealed record FixedEnvironment(string InventoryBase, string ConfigDefaultPath, string ConfigUsersPath, string? Export = null, string? Scripts = null) : IAppEnvironment
{
    public string BuiltInScripts => Scripts ?? Path.Combine(AppContext.BaseDirectory, "scripts");

    public string ExportFolder => Export ?? Path.Combine(Path.GetDirectoryName(ConfigUsersPath) ?? ".", "export");
}

/// <summary>The shared state of the app: data, organisation, connections, change log and follow-up.</summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true }.WithLegacyNames();

    private readonly IAppEnvironment _environment;
    private readonly IConnectionFactory? _factory;
    private IGraphConnection? _graph;
    private string? _graphKey;

    public ManagementState(IAppEnvironment environment, IConnectionFactory? factory = null, SignInStatus? signIn = null)
    {
        _environment = environment;
        _factory = factory;
        SignInEvent = signIn ?? new SignInStatus();

        // Since 0.8 one database (SQLite); the separate files from before are migrated once (and stay as .bak)
        Database = new Database(Path.Combine(environment.DataFolder, Database.FileName));
        try
        {
            Migrated = Migrate.OfFiles(Database, environment.DataFolder);
        }
        catch (Exception ex)
        {
            Migrated = new[] { "Overzetten naar de databank mislukt: " + ex.Message };
        }

        StartLog();
        Log.Information("Moss365 {Versie} gestart; databank versie {Schema}", AppVersion, Database.Version);
        foreach (var m in Migrated)
        {
            Log.Information("Databank: {Melding}", m);
        }

        ChangeLog = new DatabaseChangeLog(Database);
        FollowUp = new FollowUpStorage(new DatabaseText(Database, "opvolging"));
        Scheduled = new ScheduledStorage(new DatabaseText(Database, "gepland"));
        View = new ViewStorage(new DatabaseText(Database, "weergave"));
        _settingsStorage = new DatabaseText(Database, "verbinding");
        Settings = ReadSettings();
        _settingsSaved = JsonSerializer.Serialize(Settings, JsonOptions);
        LoadConfig();
        LoadInventory();
        CleanupScriptRuns();
        StartAutoRefresh();
        if (Settings.SearchUpdates && _factory?.Updates is not null)
        {
            _ = SearchUpdateAsync();
        }
    }

    /// <summary>The version of this app, e.g. "0.5.0".</summary>
    public static string AppVersion
    {
        get
        {
            var v = typeof(ManagementState).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }

    /// <summary>Result of the last search for a new version (null = not searched yet).</summary>
    public UpdateInfo? Update { get; private set; }

    public bool UpdateAvailable => Update?.IsNewer(AppVersion) == true;

    /// <summary>Where all releases are, also when searching fails.</summary>
    public string ReleasesLink => _factory?.Updates?.ReleasesLink ?? "https://github.com/jef-cesar/Moss365/releases";

    public async Task SearchUpdateAsync()
    {
        if (_factory?.Updates is not { } updates)
        {
            return;
        }

        try
        {
            Update = await updates.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Update = new UpdateInfo(null, ReleasesLink, ex.Message);
        }

        Changed?.Invoke();
    }

    public event Action? Changed;

    public IAppEnvironment AppEnvironment => _environment;
    public InventoryData Data { get; private set; } = new();
    public OrganisationConfig Config { get; private set; } = new();
    public string? ConfigSource { get; private set; }
    public IReadOnlyList<string> Folders { get; private set; } = Array.Empty<string>();

    /// <summary>Folder that wins; null = automatically the newest per file.</summary>
    public string? PreferenceFolder { get; private set; }

    public string? Error { get; private set; }
    public DateTime? LoadedOn { get; private set; }

    public IChangeLog ChangeLog { get; }
    public FollowUpStorage FollowUp { get; }

    /// <summary>Offboarding and onboarding prepared for a later moment.</summary>
    public ScheduledStorage Scheduled { get; }

    /// <summary>Scheduled actions whose moment has passed (waiting for your confirmation).</summary>
    public int CountDue
    {
        get
        {
            try
            {
                return Scheduled.Read().Count(a => a.IsDue(DateTimeOffset.Now));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>Personal view choices: columns of lists, collapsed menu groups.</summary>
    public ViewStorage View { get; }

    private readonly Dictionary<string, TableSession> _tableSessions = new();

    /// <summary>Filters, page and selection of a list, while the app is open.</summary>
    public TableSession TableSession(string id)
    {
        lock (_tableSessions)
        {
            return _tableSessions.TryGetValue(id, out var t) ? t : _tableSessions[id] = new TableSession();
        }
    }

    /// <summary>Search terms, filters and half-filled forms per page, while the app is open (never secrets).</summary>
    public PageState PageState { get; } = new();

    /// <summary>What is still open (not saved, plan ready, form filled in): tint, bar and dots in the menu.</summary>
    public Pending Pending => _pending ??= new Pending(PageState, FixedPending);
    private Pending? _pending;

    /// <summary>Organisation changed but not saved yet (number of changes); 0 after saving or loading again.</summary>
    public int ConfigModifications { get; private set; }

    /// <summary>The Organisation screen changed the configuration (in memory, not saved yet).</summary>
    public void ConfigModified()
    {
        ConfigModifications++;
        Pending.Reload();
    }

    /// <summary>Errors in the organisation that block saving.</summary>
    public static List<string> ConfigErrors(OrganisationConfig config)
    {
        var errors = new List<string>();
        foreach (var doubleValue in config.Departments.GroupBy(d => OrganisationConfig.Normalise(d.Department)).Where(g => g.Count() > 1))
        {
            errors.Add($"De dienst \"{doubleValue.First().Department.Trim()}\" staat {doubleValue.Count()} keer in de lijst.");
        }

        if (config.Departments.Any(d => string.IsNullOrWhiteSpace(d.Department)))
        {
            errors.Add("Een dienst heeft geen naam.");
        }

        foreach (var doubleValue in config.Divisions.GroupBy(a => OrganisationConfig.Normalise(a.Name)).Where(g => g.Count() > 1))
        {
            errors.Add($"De afdeling \"{doubleValue.First().Name}\" staat {doubleValue.Count()} keer in de lijst.");
        }

        return errors;
    }

    private IEnumerable<OpenItem> FixedPending()
    {
        if (ConfigModifications > 0)
        {
            yield return new OpenItem("organisatie", OpenKind.Settings,
                $"Niet bewaard: {ConfigModifications} {(ConfigModifications == 1 ? "wijziging" : "wijzigingen")}")
            {
                Count = ConfigModifications,
                Screen = "Organisatie",
                Saving = ConfigErrors(Config).Count == 0 ? () => { SaveConfig(); return Task.CompletedTask; } : null,
                Discard = () => { LoadConfig(); return Task.CompletedTask; },
            };
        }

        if (SettingsChanged)
        {
            yield return new OpenItem("verbindingen", OpenKind.Settings, "Niet bewaard: de verbindingsinstellingen zijn aangepast")
            {
                Count = 1,
                Screen = "Verbindingen",
                Saving = () => { SaveSettings(); return Task.CompletedTask; },
                Discard = () => { DiscardSettings(); return Task.CompletedTask; },
            };
        }
    }

    /// <summary>The connection settings differ from what is saved (the AD password never counts).</summary>
    public bool SettingsChanged => JsonSerializer.Serialize(Settings, JsonOptions) != _settingsSaved;
    private string? _settingsSaved;

    /// <summary>Back to the saved connection settings.</summary>
    public void DiscardSettings()
    {
        Settings = ReadSettings();
        _settingsSaved = JsonSerializer.Serialize(Settings, JsonOptions);
        Changed?.Invoke();
    }

    /// <summary>The visited pages, for Back and Forward.</summary>
    public History History { get; } = new();

    public ConnectionSettings Settings { get; private set; }

    /// <summary>The AD password of this session; never saved.</summary>
    public string? AdPassword { get; set; }

    public bool CanConnect => _factory is not null;

    /// <summary>Check of this PC; null outside the Windows app.</summary>
    public ISystemCheck? Check => _factory?.Check;

    /// <summary>Sign-in at Microsoft in progress: the link the app shows to copy.</summary>
    public SignInStatus SignInEvent { get; }

    /// <summary>Why Exchange could not use the same sign-in as Graph (then Exchange signs in separately).</summary>
    public string? ExchangeMessage => _factory?.ExchangeMessage;

    /// <summary>Who runs changes: the Graph sign-in if there is one, otherwise the Windows user.</summary>
    public string User => Graph()?.SignedInAs ?? Environment.UserName;

    /// <summary>The profiles of whoever is signed in (app roles from Entra), or the transition rule while there are
    /// none.</summary>
    public Role Roles => PermissionTable.Determine(Graph()?.AppRoles, Config.ProfilesRequired);

    public bool Can(Permission permission) => PermissionTable.Can(Roles, permission);

    /// <summary>No roles in the sign-in: text for the bottom of the menu (transition or read only), otherwise
    /// null.</summary>
    public string? ProfileMessage => Graph()?.AppRoles is { Count: > 0 } r && PermissionTable.FromClaims(r) != Role.No
        ? null
        : Config.ProfilesRequired
            ? Graph()?.AppRoles is null ? "Nog niet aangemeld: enkel lezen tot je aanmeldt." : "Geen profiel in Moss365: enkel lezen."
            : "Profielen nog niet ingesteld: iedereen mag alles.";

    public bool ConfigIsModified => ConfigSource is not null
                                      && string.Equals(Path.GetFullPath(ConfigSource), Path.GetFullPath(_environment.ConfigUsersPath), StringComparison.OrdinalIgnoreCase);

    public int CountFollowUpTasks => FollowUp.Read().Count;

    private readonly ITextStorage _settingsStorage;

    /// <summary>The database of the app (change log, scheduled, follow-up, view, connection, access log).</summary>
    public Database Database { get; }

    /// <summary>What was migrated from separate files to the database at this start.</summary>
    public IReadOnlyList<string> Migrated { get; }

    // ------------------------------------------------------------------ inventory and organisation

    public void ChooseFolder(string? folder)
    {
        PreferenceFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        LoadInventory();
    }

    public void LoadInventory()
    {
        try
        {
            Folders = InventoryLoader.SearchFolders(_environment.InventoryBase);
            var order = PreferenceFolder is null
                ? Folders
                : new[] { PreferenceFolder }.Concat(Folders.Where(m => !string.Equals(m, PreferenceFolder, StringComparison.OrdinalIgnoreCase))).ToList();
            Data = InventoryLoader.Load(order);
            LoadedOn = DateTime.Now;
            Error = null;
            ClearLiveStatus();
            RestoreQuickSnapshot();
        }
        catch (Exception ex)
        {
            Error = "Inventaris laden mislukt: " + ex.Message;
        }

        Changed?.Invoke();
    }

    /// <summary>Why the organisation could not be loaded (fully); stays until it is solved.</summary>
    public string? ConfigWarning { get; private set; }

    public void LoadConfig()
    {
        try
        {
            (Config, ConfigSource, ConfigWarning) = ConfigStorage.LoadSafe(_environment.ConfigDefaultPath, _environment.ConfigUsersPath);
        }
        catch (Exception ex)
        {
            Config = new OrganisationConfig();
            ConfigSource = null;
            ConfigWarning = "Organisatie laden mislukt: " + ex.Message;
        }

        ConfigModifications = 0;
        Changed?.Invoke();
    }

    public void SaveConfig()
    {
        ConfigStorage.Save(Config, _environment.ConfigUsersPath);
        ConfigSource = _environment.ConfigUsersPath;
        ConfigModifications = 0;
        Changed?.Invoke();
    }

    /// <summary>Deletes the own changes and loads the bundled organisatie.json again.</summary>
    public void RestoreDefaultConfig()
    {
        if (File.Exists(_environment.ConfigUsersPath))
        {
            File.Delete(_environment.ConfigUsersPath);
        }

        LoadConfig();
    }

    // ------------------------------------------------------------------ connections

    private ConnectionSettings ReadSettings()
    {
        try
        {
            return _settingsStorage.Read() is { } json
                ? JsonSerializer.Deserialize<ConnectionSettings>(json, JsonOptions) ?? new ConnectionSettings()
                : new ConnectionSettings();
        }
        catch (Exception)
        {
            return new ConnectionSettings();
        }
    }

    public void SaveSettings()
    {
        Directory.CreateDirectory(_environment.DataFolder);
        _settingsSaved = JsonSerializer.Serialize(Settings, JsonOptions);
        _settingsStorage.Save(_settingsSaved);
        Changed?.Invoke();
    }

    private IGraphConnection? Graph()
    {
        if (_factory is null || !Settings.GraphFilled)
        {
            return null;
        }

        var key = $"{Settings.TenantId}|{Settings.ClientId}|{Settings.ManagementUpn}";
        if (_graph is null || _graphKey != key)
        {
            _graph = _factory.Graph(Settings);
            _graphKey = key;
        }

        return _graph;
    }

    /// <summary>Owners of a cloud group, live from Graph; null when Graph is not configured.</summary>
    public async Task<IReadOnlyList<GraphOwner>?> ReadOwnersAsync(string groupId, CancellationToken cancel = default)
        => Graph() is { } graph ? await graph.ReadOwnersAsync(groupId, cancel) : null;

    /// <summary>Sign-ins live from Entra (one user, or everyone with id null); null when Graph is not
    /// configured.</summary>
    public async Task<IReadOnlyList<SignInEvent>?> ReadSignInsAsync(string? userId, int days, bool onlyFailed, CancellationToken cancel = default)
    {
        if (Graph() is not { } graph)
        {
            return null;
        }

        // Sign-ins are personal data (IP addresses, places): whoever views them goes to the access log
        Access("Aanmeldingen bekeken", $"{(userId is null ? "alle gebruikers" : "gebruiker " + userId)}, {days} dagen{(onlyFailed ? ", enkel mislukt" : string.Empty)}");
        return await graph.ReadSignInsAsync(userId, days, onlyFailed, cancel);
    }

    /// <summary>
    /// The calendar of a room, live from Graph. Returns the appointments, or a message that says why it fails (no
    /// Graph, no read permission on the calendar). Whoever views a calendar goes to the access log (organisers are
    /// people).
    /// </summary>
    public async Task<(IReadOnlyList<Appointment>? Appointments, string? Message)> ReadCalendarAsync(Room room, DateTime start, DateTime until, CancellationToken cancel = default)
    {
        if (Graph() is not { } graph)
        {
            return (null, "Graph is niet ingesteld (Verbindingen).");
        }

        Access("Agenda bekeken", $"room {room.Smtp}, {start:dd/MM/yyyy} tot {until.AddDays(-1):dd/MM/yyyy}");
        try
        {
            return (await graph.ReadCalendarAsync(room.Smtp, start, until, cancel), null);
        }
        catch (NoPermissionError ex)
        {
            return (null, "Je account mag de agenda van deze room niet lezen. Geef jezelf minstens het recht Reviewer (of LimitedDetails) "
                + "op de agenda: bij Wijzigen > Agendarechten op deze pagina, of in Exchange met Add-MailboxFolderPermission "
                + $"\"{room.Smtp}:\\Calendar\". Daarna opnieuw laden. ({ex.Message})");
        }
    }

    /// <summary>The effect of the Conditional Access policies in the latest sign-ins (shared by the screens).</summary>
    public IReadOnlyList<Core.Analysis.CaEffectRow>? CaEffect { get; private set; }

    /// <summary>Explanation of the effect: period and number of sign-ins read.</summary>
    public string? CaEffectExplanation { get; private set; }

    public const int CaEffectMax = 5000;

    /// <summary>Reads the sign-ins (up to <see cref="CaEffectMax"/>) and counts per policy how it turned out.</summary>
    public async Task LoadCaEffectAsync(int days, CancellationToken cancel = default)
    {
        if (Data.ConditionalAccess is not { } ca)
        {
            return;
        }

        if (Graph() is not { } graph)
        {
            throw new InvalidOperationException("Vul eerst de Entra-gegevens in (scherm Verbindingen).");
        }

        Access("Aanmeldingen bekeken", $"effect Conditional Access, alle gebruikers, {days} dagen");
        var signIns = await graph.ReadSignInsAsync(days, CaEffectMax, cancel);
        CaEffect = Core.Analysis.CaEffect.Compute(signIns, ca);
        CaEffectExplanation = $"{signIns.Count} aanmeldingen van de laatste {days} dagen"
                         + (signIns.Count >= CaEffectMax ? $" (de nieuwste {CaEffectMax}; er zijn er meer)" : string.Empty)
                         + $", gelezen om {DateTime.Now:HH:mm}.";
        Changed?.Invoke();
    }

    /// <summary>The systems in the plan for which no connection is configured yet.</summary>
    public IReadOnlyList<TargetSystem> MissingConnections(IEnumerable<Change> plan)
    {
        var needed = plan.Where(w => w.Selected).Select(w => w.TargetSystem).Distinct();
        return needed.Where(s => s switch
        {
            TargetSystem.ActiveDirectory => _factory is null || !Settings.AdFilled,
            TargetSystem.Graph => _factory is null || !Settings.GraphFilled,
            _ => _factory is null,
        }).ToList();
    }

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> plan, string context, IProgress<string>? progress, CancellationToken cancel = default)
    {
        if (_factory is null)
        {
            throw new InvalidOperationException("Wijzigingen uitvoeren is niet beschikbaar in deze omgeving.");
        }

        var executors = new List<IExecutor>();
        if (_factory.Ad(Settings, AdPassword) is IExecutor ad)
        {
            executors.Add(ad);
        }

        if (Graph() is IExecutor graph)
        {
            executors.Add(graph);
        }

        if (_factory.Exchange(Settings) is IExecutor exchange)
        {
            executors.Add(exchange);
        }

        // Sign in first, so that the profile is known; after that the engine checks every change against that profile
        if (Config.ProfilesRequired && Graph() is { AppRoles: null } g)
        {
            try
            {
                await g.SignInAsync(cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not signed in: the engine then refuses everything that needs more than reading
            }
        }

        var roles = Roles;
        var data = Data;
        var config = Config;
        var user = User;
        // The engine refuses what the profile does not allow, and every change to Conditional Access that does not
        // exclude the emergency accounts or locks yourself out (also on undo or a scheduled plan)
        var results = await new PlanExecutor(executors, ChangeLog, () => User,
                w => PermissionTable.Refusal(roles, w, data) ?? CaSafety.Refusal(w, data, config, user))
            .ExecuteAsync(plan, context, progress, cancel);
        // No context: it often contains a name ("Uitstroom ..."); only counts and kinds of actions
        Log.Information("Plan uitgevoerd: {Gelukt} gelukt, {Mislukt} mislukt, {Overgeslagen} overgeslagen; acties {Acties}",
            results.Count(r => r.Status == ResultStatus.Succeeded), results.Count(r => r.Status == ResultStatus.Failed),
            results.Count(r => r.Status == ResultStatus.Skipped), string.Join(", ", plan.Where(w => w.Selected).Select(w => w.ChangeAction).Distinct()));
        AfterExecute(plan, results);
        Changed?.Invoke();
        return results;
    }

    /// <summary>Checks a follow-up task: when the user exists in Entra, the remaining steps are run.</summary>
    public async Task<string> CompleteFollowUpAsync(Guid taskId, IProgress<string>? progress, CancellationToken cancel = default)
    {
        var tasks = FollowUp.Read();
        var task = tasks.FirstOrDefault(t => t.Id == taskId) ?? throw new InvalidOperationException("Taak niet gevonden.");
        var graph = Graph() ?? throw new InvalidOperationException("Vul eerst de Entra-gegevens in (scherm Verbindingen).");

        task.LastChecked = DateTimeOffset.Now;
        var id = await graph.FindUserIdAsync(task.Upn, cancel);
        if (id is null)
        {
            task.LastMessage = "Nog niet in Entra. Entra Connect synchroniseert standaard om de 30 minuten.";
        }
        else
        {
            foreach (var w in task.Changes)
            {
                w.Selected = true;
            }

            var results = await ExecuteAsync(task.Changes, "Opvolging " + task.Description, progress, cancel);
            var succeeded = results.Where(r => r.Status == ResultStatus.Succeeded).Select(r => r.ChangeId).ToHashSet();
            task.Changes.RemoveAll(w => succeeded.Contains(w.Id));
            task.LastMessage = task.Changes.Count == 0
                ? "Alles afgewerkt."
                : $"{succeeded.Count} gelukt, {task.Changes.Count} nog open: " + string.Join("; ", results.Where(r => r.Status != ResultStatus.Succeeded).Select(r => r.Message).Distinct());
        }

        var message = task.LastMessage;
        FollowUp.Save(tasks.Where(t => t.Changes.Count > 0));
        Changed?.Invoke();
        return message;
    }

    public void Schedule(ScheduledAction action)
    {
        Scheduled.Add(action);
        Changed?.Invoke();
    }

    public void RemoveScheduled(Guid id)
    {
        Scheduled.Remove(id);
        Changed?.Invoke();
    }

    public void AddFollowUp(FollowUpTask task)
    {
        FollowUp.Add(task);
        Changed?.Invoke();
    }

    public void RemoveFollowUp(Guid taskId)
    {
        FollowUp.Save(FollowUp.Read().Where(t => t.Id != taskId));
        Changed?.Invoke();
    }
}
