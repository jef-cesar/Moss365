using Moss365.Core.Localization;
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
    /// <summary>The data folder: <c>moss365.db</c> (the list of tenants) and <c>tenants\&lt;id&gt;\tenant.db</c>.</summary>
    string Root { get; }

    /// <summary>
    /// A folder with inventory folders (the output of Export-Inventaris.ps1) that is imported into the tenant at every
    /// start; null in the Windows app, where you import a folder on request (start page).
    /// </summary>
    string? InventoryImport => null;

    /// <summary>The bundled organisatie.json (read only): the starting point of a new tenant.</summary>
    string ConfigDefaultPath { get; }

    /// <summary>Where exports (lists, cleanup rounds) go; every tenant gets its own subfolder.</summary>
    string ExportFolder => Path.Combine(Root, "export");

    /// <summary>The built-in scripts (the scripts folder next to the app).</summary>
    string BuiltInScripts => Path.Combine(AppContext.BaseDirectory, "scripts");

    /// <summary>Temporary files: the work folders of running scripts.</summary>
    string TempFolder => Path.Combine(Path.GetTempPath(), "Moss365");

    /// <summary>Shows a file in Explorer (only in the Windows app).</summary>
    void ShowFile(string path)
    {
    }
}

/// <summary>For tests: inventory folders to import, the bundled organisation, and the data folder (the folder of the
/// third path, for compatibility with older tests).</summary>
public sealed record FixedEnvironment(string InventoryBase, string ConfigDefaultPath, string ConfigUsersPath, string? Export = null, string? Scripts = null) : IAppEnvironment
{
    public string Root => Path.GetDirectoryName(ConfigUsersPath) ?? ".";

    public string? InventoryImport => InventoryBase;

    public string BuiltInScripts => Scripts ?? Path.Combine(AppContext.BaseDirectory, "scripts");

    public string ExportFolder => Export ?? Path.Combine(Root, "export");

    public string TempFolder => Path.Combine(Root, "temp");
}

/// <summary>The shared state of the app: data, organisation, connections, change log and follow-up.</summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true }.WithLegacyNames();

    private readonly IAppEnvironment _environment;
    private readonly IConnectionFactory? _factory;
    private IGraphConnection? _graph;
    private string? _graphKey;

    /// <param name="databaseKey">The key of the tenant database when it is encrypted (R6), or null.</param>
    public ManagementState(IAppEnvironment environment, IConnectionFactory? factory = null, SignInStatus? signIn = null, TenantInfo? tenant = null, byte[]? databaseKey = null)
    {
        _environment = environment;
        _factory = factory;
        SignInEvent = signIn ?? new SignInStatus();

        // Since 0.12 one database per tenant (tenants\<id>\tenant.db) with everything of that tenant in it. Without a
        // tenant (tests): the database directly in the data folder.
        Tenant = tenant ?? new TenantInfo { Id = "default", Name = "Tenant" };
        TenantFolder = tenant is null ? environment.Root : TenantFolders.Folder(environment.Root, tenant.Id);
        Database = new Database(Path.Combine(TenantFolder, TenantFolders.DatabaseName), databaseKey);
        Files = new DatabaseFileStore(Database);
        Migrated = Database.BackupBeforeMigration is { } copy ? new[] { Texts.T.Format("code.management-state.copy-before-the-migration-of", Path.GetFileName(copy)) } : Array.Empty<string>();
        StartLog();
        Log.Information("Moss365 {Versie} gestart; databank versie {Schema}", AppVersion, Database.Version);
        foreach (var m in Migrated)
        {
            Log.Information("Databank: {Melding}", m);
        }

        ChangeLog = new DatabaseChangeLog(Database);
        FollowUp = new FollowUpStorage(new DatabaseText(Database, Documents.FollowUp));
        Scheduled = new ScheduledStorage(new DatabaseText(Database, Documents.Scheduled));
        View = new ViewStorage(new DatabaseText(Database, Documents.View));
        _settingsStorage = new DatabaseText(Database, Documents.Connection);
        _configStorage = new DatabaseText(Database, Documents.Organisation);
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

    /// <summary>The update channel of this installation (set by the hub from the app settings).</summary>
    public UpdateChannel UpdateChannel { get; set; } = UpdateSettings.DefaultChannel(AppVersion);

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
            Update = await updates.CheckAsync(UpdateChannel, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Update = new UpdateInfo(null, ReleasesLink, ex.Message);
        }

        Changed?.Invoke();
    }

    public event Action? Changed;

    public IAppEnvironment AppEnvironment => _environment;
    private InventoryData _data = new();
    private (InventoryData Of, InventoryData Without)? _withoutAd;

    /// <summary>The data shown; for a tenant without AD everything of AD is left out (0.21.1), whatever was read or
    /// imported, also right after the systems of the tenant change. What was read stays in <see cref="_data"/>.</summary>
    public InventoryData Data
    {
        get
        {
            if (HasAd)
            {
                return _data;
            }

            var data = _data;
            if (_withoutAd is not { } w || !ReferenceEquals(w.Of, data))
            {
                w = (data, data.WithoutAd());
                _withoutAd = w;
            }

            return w.Without;
        }
        private set => _data = value;
    }
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
            errors.Add(Texts.T.Format("code.management-state.the-department-is-times-in", doubleValue.First().Department.Trim(), doubleValue.Count()));
        }

        if (config.Departments.Any(d => string.IsNullOrWhiteSpace(d.Department)))
        {
            errors.Add(Texts.T["code.management-state.a-department-has-no-name"]);
        }

        foreach (var doubleValue in config.Divisions.GroupBy(a => OrganisationConfig.Normalise(a.Name)).Where(g => g.Count() > 1))
        {
            errors.Add(Texts.T.Format("code.management-state.the-division-is-times-in", doubleValue.First().Name, doubleValue.Count()));
        }

        return errors;
    }

    private IEnumerable<OpenItem> FixedPending()
    {
        if (ConfigModifications > 0)
        {
            yield return new OpenItem("organisatie", OpenKind.Settings,
                Texts.T.Format(ConfigModifications == 1 ? "state.not-saved.one" : "state.not-saved", ConfigModifications))
            {
                Count = ConfigModifications,
                Screen = "Organisatie",
                Saving = ConfigErrors(Config).Count == 0 ? () => { SaveConfig(); return Task.CompletedTask; } : null,
                Discard = () => { LoadConfig(); return Task.CompletedTask; },
            };
        }

        if (SettingsChanged)
        {
            yield return new OpenItem("verbindingen", OpenKind.Settings, Texts.T["code.management-state.not-saved-the-connection-settings"])
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

    /// <summary>This tenant has on-premises AD (chosen per tenant, Settings > Tenants).</summary>
    public bool HasAd => Tenant.Systems.HasFlag(TenantSystems.ActiveDirectory);

    /// <summary>This tenant has Microsoft 365: Entra ID and Exchange Online.</summary>
    public bool HasMicrosoft365 => Tenant.Systems.HasFlag(TenantSystems.Microsoft365);

    /// <summary>The connection settings as the connections use them: without AD for a tenant that has no AD, without
    /// Microsoft 365 for one that has no Microsoft 365, so that nothing reads or writes there.</summary>
    public ConnectionSettings EffectiveSettings => (HasAd, HasMicrosoft365) switch
    {
        (true, true) => Settings,
        _ => new ConnectionSettings
        {
            AdServer = HasAd ? Settings.AdServer : string.Empty,
            AdUser = HasAd ? Settings.AdUser : string.Empty,
            TenantId = HasMicrosoft365 ? Settings.TenantId : string.Empty,
            ClientId = HasMicrosoft365 ? Settings.ClientId : string.Empty,
            ManagementUpn = HasMicrosoft365 ? Settings.ManagementUpn : string.Empty,
            NetlogonPath = HasAd ? Settings.NetlogonPath : string.Empty,
            RememberSignIn = Settings.RememberSignIn,
            TokenCache = Settings.TokenCache,
            SearchUpdates = Settings.SearchUpdates,
            QuickLoad = Settings.QuickLoad,
            RefreshAuto = Settings.RefreshAuto,
        },
    };

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
    public Role Roles => PermissionTable.Determine(Graph()?.AppRoles, Config.RolesModeEffective);

    public bool Can(Permission permission)
        => PermissionTable.Can(Roles, permission) && !(IsHistoric && permission is Permission.Helpdesk or Permission.GroupMembers or Permission.InviteGuests
            or Permission.Actions or Permission.Manage or Permission.Privileged or Permission.Scripts);

    /// <summary>No roles in the sign-in: text for the bottom of the menu (transition or read only), otherwise
    /// null.</summary>
    public string? ProfileMessage => Config.RolesModeEffective switch
    {
        RolesMode.Off => null,
        _ when Graph()?.AppRoles is { Count: > 0 } r && PermissionTable.FromClaims(r) != Role.No => null,
        RolesMode.Required => Graph()?.AppRoles is null ? Texts.T["code.management-state.not-signed-in-yet-read"] : Texts.T["code.management-state.no-role-in-moss365-read"],
        _ => Texts.T["code.management-state.roles-not-required-yet-everyone"],
    };

    /// <summary>
    /// Why this mode cannot be chosen now, or null: requiring roles while you have no role with Instellingen yourself
    /// would lock you out of this setting.
    /// </summary>
    public string? RolesModeRefusal(RolesMode mode)
        => mode == RolesMode.Required && !PermissionTable.Can(PermissionTable.Determine(Graph()?.AppRoles, RolesMode.Required), Permission.Settings)
            ? Texts.T["code.management-state.requiring-roles-is-only-possible"]
            : null;

    /// <summary>The tenant has its own organisation (saved in its database), not (only) the bundled one.</summary>
    public bool ConfigIsModified => ConfigSource == OwnConfigName;

    /// <summary>How the own organisation is named in messages.</summary>
    public const string OwnConfigName = "de databank van deze tenant";

    /// <summary>Where the organisation comes from, in the language of the interface.</summary>
    public string? ConfigSourceText => ConfigIsModified ? Texts.T["state.ownconfig"] : ConfigSource;

    public int CountFollowUpTasks => FollowUp.Read().Count;

    private readonly ITextStorage _settingsStorage;
    private readonly ITextStorage _configStorage;

    /// <summary>The tenant this state belongs to: everything below is of this tenant only.</summary>
    public TenantInfo Tenant { get; internal set; }

    /// <summary>The folder of the tenant (with tenant.db and the automatic copies).</summary>
    public string TenantFolder { get; }

    /// <summary>
    /// The database of the tenant: change log, documents (scheduled, follow-up, view, connection, organisation), access
    /// log, technical log, KPIs and the file store.
    /// </summary>
    public Database Database { get; }

    /// <summary>Files of the tenant in its database: inventory, Conditional Access backups, scripts and runs.</summary>
    public IFileStore Files { get; }

    /// <summary>Where the exports of this tenant go (a subfolder per tenant, so that exports never mix).</summary>
    public string ExportFolder => Path.Combine(_environment.ExportFolder, InventoryWriterSafeName(Tenant.Name));

    private static string InventoryWriterSafeName(string name) => CsvWriter.FileName(name).TrimEnd('.').Trim() is { Length: > 0 } n ? n : "tenant";

    /// <summary>What was migrated from separate files to the database at this start.</summary>
    public IReadOnlyList<string> Migrated { get; }

    // ------------------------------------------------------------------ inventory and organisation

    public void ChooseFolder(string? folder)
    {
        PreferenceFolder = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();
        LoadInventory();
    }

    /// <summary>Reads the inventory folders of Export-Inventaris.ps1 in a folder (or the folder itself) into this tenant.</summary>
    public string ImportInventory(string folder)
    {
        folder = folder.Trim().Trim('"');
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            return Texts.T["code.management-state.that-folder-does-not-exist"];
        }

        try
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var count = name.StartsWith("Inventaris-", StringComparison.OrdinalIgnoreCase)
                ? InventoryLoader.ImportFolders(Files, Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!, int.MaxValue)
                : InventoryLoader.ImportFolders(Files, folder, int.MaxValue);
            Log.Information("Inventaris ingelezen: {Aantal} map(pen)", count);
            LoadInventory();
            return count == 0 ? Texts.T["code.management-state.no-new-inventory-folders-found"] : Texts.T.Format("code.management-state.inventory-folder-s-read-into", count, Tenant.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Texts.T["code.management-state.reading-failed"] + " ") + ex.Message;
        }
    }

    public void LoadInventory()
    {
        try
        {
            // Tests: inventory folders on disk are read into the tenant first (the Windows app imports on request)
            if (_environment.InventoryImport is { } import)
            {
                InventoryLoader.ImportFolders(Files, import, int.MaxValue);
            }

            SnapshotNewFolders();

            Folders = InventoryLoader.SearchFolders(Files);
            var order = PreferenceFolder is null
                ? Folders
                : new[] { PreferenceFolder }.Concat(Folders.Where(m => !string.Equals(m, PreferenceFolder, StringComparison.OrdinalIgnoreCase))).ToList();
            Data = InventoryLoader.Load(Files, order);
            LoadedOn = DateTime.Now;
            Error = null;
            ClearLiveStatus();
            RestoreQuickSnapshot();
        }
        catch (Exception ex)
        {
            Error = (Texts.T["code.management-state.loading-the-inventory-failed"] + " ") + ex.Message;
        }

        Changed?.Invoke();
    }

    /// <summary>Why the organisation could not be loaded (fully); stays until it is solved.</summary>
    public string? ConfigWarning { get; private set; }

    public void LoadConfig()
    {
        try
        {
            (Config, ConfigSource, ConfigWarning) = ConfigStorage.LoadSafe(_environment.ConfigDefaultPath, _configStorage, OwnConfigName);
        }
        catch (Exception ex)
        {
            Config = new OrganisationConfig();
            ConfigSource = null;
            ConfigWarning = (Texts.T["code.management-state.loading-the-organisation-failed"] + " ") + ex.Message;
        }

        ConfigModifications = 0;
        Changed?.Invoke();
    }

    public void SaveConfig()
    {
        ConfigStorage.Save(Config, _configStorage);
        ConfigSource = OwnConfigName;
        ConfigModifications = 0;
        Changed?.Invoke();
    }

    /// <summary>Deletes the own changes and loads the bundled organisatie.json again.</summary>
    public void RestoreDefaultConfig()
    {
        Database.DeleteDocument(Documents.Organisation);

        LoadConfig();
    }

    // ------------------------------------------------------------------ connections

    private ConnectionSettings ReadSettings()
    {
        ConnectionSettings read;
        try
        {
            read = _settingsStorage.Read() is { } json
                ? JsonSerializer.Deserialize<ConnectionSettings>(json, JsonOptions) ?? new ConnectionSettings()
                : new ConnectionSettings();
        }
        catch (Exception)
        {
            read = new ConnectionSettings();
        }

        // Where the encrypted sign-in is kept, when "stay signed in" is on (0.33); never saved with the settings
        read.TokenCache = new DocumentTokenStore(Database);
        return read;
    }

    /// <summary>Stay signed in on or off; off removes the kept sign-in from the database.</summary>
    public void RememberSignIn(bool on)
    {
        Settings.RememberSignIn = on;
        if (!on)
        {
            Settings.TokenCache?.Clear();
        }

        SaveSettings();
    }

    public void SaveSettings()
    {
        _settingsSaved = JsonSerializer.Serialize(Settings, JsonOptions);
        _settingsStorage.Save(_settingsSaved);
        Changed?.Invoke();
    }

    private IGraphConnection? Graph()
    {
        if (_factory is null || !EffectiveSettings.GraphFilled)
        {
            return null;
        }

        var key = $"{EffectiveSettings.TenantId}|{EffectiveSettings.ClientId}|{EffectiveSettings.ManagementUpn}";
        if (_graph is null || _graphKey != key)
        {
            _graph = _factory.Graph(EffectiveSettings);
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
            return (null, Texts.T["code.manage-ca.graph-is-not-set-up"]);
        }

        Access("Agenda bekeken", $"room {room.Smtp}, {start:dd/MM/yyyy} tot {until.AddDays(-1):dd/MM/yyyy}");
        try
        {
            return (await graph.ReadCalendarAsync(room.Smtp, start, until, cancel), null);
        }
        catch (NoPermissionError ex)
        {
            return (null, Texts.T.Format("state.calendar.denied", room.Smtp, ex.Message));
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
            throw new InvalidOperationException(Texts.T["code.security-card.fill-in-the-entra-details"]);
        }

        Access("Aanmeldingen bekeken", $"effect Conditional Access, alle gebruikers, {days} dagen");
        var signIns = await graph.ReadSignInsAsync(days, CaEffectMax, cancel);
        CaEffect = Core.Analysis.CaEffect.Compute(signIns, ca);
        CaEffectExplanation = Texts.T.Format("state.caeffect", signIns.Count, days, signIns.Count >= CaEffectMax ? Texts.T.Format("state.caeffect.more", CaEffectMax) : string.Empty, DateTime.Now);
        Changed?.Invoke();
    }

    /// <summary>The systems in the plan for which no connection is configured yet.</summary>
    public IReadOnlyList<TargetSystem> MissingConnections(IEnumerable<Change> plan)
    {
        var needed = plan.Where(w => w.Selected).Select(w => w.TargetSystem).Distinct();
        return needed.Where(s => s switch
        {
            TargetSystem.ActiveDirectory => _factory is null || !EffectiveSettings.AdFilled,
            TargetSystem.Graph => _factory is null || !EffectiveSettings.GraphFilled,
            _ => _factory is null || !HasMicrosoft365,
        }).ToList();
    }

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> plan, string context, IProgress<string>? progress, CancellationToken cancel = default)
    {
        if (_factory is null)
        {
            throw new InvalidOperationException(Texts.T["code.management-state.running-changes-is-not-available"]);
        }

        if (IsHistoric)
        {
            throw new InvalidOperationException(Texts.T["code.management-state-library.you-are-looking-at-a"]);
        }

        var executors = new List<IExecutor>();
        if (_factory.Ad(EffectiveSettings, AdPassword) is IExecutor ad)
        {
            executors.Add(ad);
        }

        if (Graph() is IExecutor graph)
        {
            executors.Add(graph);
        }

        if (HasMicrosoft365 && _factory.Exchange(EffectiveSettings) is IExecutor exchange)
        {
            executors.Add(exchange);
        }

        // Sign in first, so that the profile is known; after that the engine checks every change against that profile
        if (Config.RolesModeEffective == RolesMode.Required && Graph() is { AppRoles: null } g)
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

        // A destructive action on a device is only for one device per plan (R17); refused as a whole otherwise
        if (Core.Devices.DeviceSafety.PlanRefusal(plan) is { } tooMany)
        {
            throw new InvalidOperationException(tooMany);
        }

        var roles = Roles;
        var data = Data;
        var config = Config;
        var user = User;
        // The engine refuses what the profile does not allow, every change to Conditional Access that does not
        // exclude the emergency accounts or locks yourself out (also on undo or a scheduled plan), and wiping a
        // personal device
        var results = await new PlanExecutor(executors, ChangeLog, () => User,
                w => PermissionTable.Refusal(roles, w, data) ?? CaSafety.Refusal(w, data, config, user) ?? Core.Devices.DeviceSafety.Refusal(w))
            .ExecuteAsync(plan, context, progress, cancel);
        // No context: it often contains a name ("Uitstroom ..."); only counts and kinds of actions
        Log.Information("Plan uitgevoerd: {Gelukt} gelukt, {Mislukt} mislukt, {Overgeslagen} overgeslagen; acties {Acties}",
            results.Count(r => r.Status == ResultStatus.Succeeded), results.Count(r => r.Status == ResultStatus.Failed),
            results.Count(r => r.Status == ResultStatus.Skipped), string.Join(", ", plan.Where(w => w.Selected).Select(w => w.ChangeAction).Distinct()));
        AfterExecute(plan, results);
        ForgetKpiCounts();
        Changed?.Invoke();
        return results;
    }

    /// <summary>Checks a follow-up task: when the user exists in Entra, the remaining steps are run.</summary>
    public async Task<string> CompleteFollowUpAsync(Guid taskId, IProgress<string>? progress, CancellationToken cancel = default)
    {
        var tasks = FollowUp.Read();
        var task = tasks.FirstOrDefault(t => t.Id == taskId) ?? throw new InvalidOperationException(Texts.T["code.management-state.task-not-found"]);
        var graph = Graph() ?? throw new InvalidOperationException(Texts.T["code.security-card.fill-in-the-entra-details"]);

        task.LastChecked = DateTimeOffset.Now;
        var id = await graph.FindUserIdAsync(task.Upn, cancel);
        if (id is null)
        {
            task.LastMessage = Texts.T["code.management-state.not-in-entra-yet-entra"];
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
                ? Texts.T["code.management-state.everything-done"]
                : (Texts.T.Format("code.management-state.succeeded-still-open", succeeded.Count, task.Changes.Count) + " ") + string.Join("; ", results.Where(r => r.Status != ResultStatus.Succeeded).Select(r => r.Message).Distinct());
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
