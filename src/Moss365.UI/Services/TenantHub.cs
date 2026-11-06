using Moss365.Core.Configuration;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>
/// The tenants of this installation (R3): the list in <c>moss365.db</c>, which one is open, and one
/// <see cref="ManagementState"/> per tenant with its own database, connections and sign-in. Nothing is shared between
/// tenants except this list. Switching reloads the screens, so that no screen keeps data of the previous tenant.
/// </summary>
public sealed class TenantHub : IDisposable
{
    private const string CurrentKey = "tenant.current";

    private readonly IAppEnvironment _environment;
    private readonly Func<TenantInfo, ManagementState> _open;
    private readonly Dictionary<string, ManagementState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public TenantHub(IAppEnvironment environment, Func<TenantInfo, ManagementState> open)
    {
        _environment = environment;
        _open = open;
        var messages = new List<string>();

        // An installation of 0.11 or earlier: its database and files become the first tenant
        try
        {
            if (TenantMigration.FromSingleTenant(environment.Root, NameOfOldOrganisation(environment.Root)) is var (tenant, m))
            {
                App = new AppDatabase(Path.Combine(environment.Root, AppDatabase.FileName));
                App.SaveTenant(tenant);
                App.SetSetting(CurrentKey, tenant.Id);
                messages.Add($"De gegevens van de vorige versie zijn nu de tenant \"{tenant.Name}\"" + (m.Count > 0 ? ": " + string.Join(", ", m) + "." : "."));
                messages.Add($"De oude bestanden staan in {Path.Combine(environment.Root, "old-0.11")}; je kan ze verwijderen als alles werkt.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            messages.Add("De gegevens van de vorige versie overzetten is mislukt: " + ex.Message + " Er werd niets verwijderd.");
        }

        App ??= new AppDatabase(Path.Combine(environment.Root, AppDatabase.FileName));
        if (App.Tenants().Count == 0)
        {
            var first = new TenantInfo { Id = TenantFolders.NewId(), Name = "Mijn organisatie" };
            App.SaveTenant(first);
            App.SetSetting(CurrentKey, first.Id);
        }

        Messages = messages;
    }

    /// <summary>The app database: the list of tenants and app settings.</summary>
    public AppDatabase App { get; }

    /// <summary>What happened at this start (migration of an older version), for the start page.</summary>
    public IReadOnlyList<string> Messages { get; }

    public IReadOnlyList<TenantInfo> Tenants => App.Tenants();

    public TenantInfo CurrentTenant
    {
        get
        {
            var all = App.Tenants();
            return all.FirstOrDefault(t => t.Id == App.Setting(CurrentKey)) ?? all[0];
        }
    }

    /// <summary>The open tenant: its state is created the first time it is needed.</summary>
    public ManagementState Current => Open(CurrentTenant);

    /// <summary>A tenant switched; the window reloads the screens.</summary>
    public event Action? Switched;

    /// <summary>The state of a tenant (opened once and kept, so that a running task or sign-in is not lost).</summary>
    public ManagementState Open(TenantInfo tenant)
    {
        tenant = App.Tenants().FirstOrDefault(t => t.Id == tenant.Id) ?? tenant;
        lock (_lock)
        {
            if (!_states.TryGetValue(tenant.Id, out var state))
            {
                state = _open(tenant);
                state.TenantChanged += t => App.SaveTenant(t);
                _states[tenant.Id] = state;
            }

            state.Tenant = tenant;
            return state;
        }
    }

    /// <summary>The tenants that are open in this session (for tests and the isolation check).</summary>
    public IReadOnlyCollection<ManagementState> Opened
    {
        get
        {
            lock (_lock)
            {
                return _states.Values.ToList();
            }
        }
    }

    public void Switch(string id)
    {
        if (App.Tenants().All(t => t.Id != id))
        {
            throw new ArgumentException("Onbekende tenant.", nameof(id));
        }

        App.SetSetting(CurrentKey, id);
        Switched?.Invoke();
    }

    /// <summary>A new, empty tenant with its own database; it starts from the bundled organisation.</summary>
    public TenantInfo Add(string name, string? colour = null)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("Geef de tenant een naam.", nameof(name));
        }

        if (App.Tenants().Any(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"Er is al een tenant \"{name}\".", nameof(name));
        }

        var used = App.Tenants().Select(t => t.Colour).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tenant = new TenantInfo
        {
            Id = TenantFolders.NewId(),
            Name = name,
            Colour = colour ?? TenantColours.All.FirstOrDefault(c => !used.Contains(c)) ?? TenantColours.All[App.Tenants().Count % TenantColours.All.Count],
        };
        App.SaveTenant(tenant);
        return tenant;
    }

    /// <summary>Renames a tenant or changes its colour.</summary>
    public void Update(TenantInfo tenant)
    {
        if (App.Tenants().Any(t => t.Id != tenant.Id && string.Equals(t.Name, tenant.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new ArgumentException($"Er is al een tenant \"{tenant.Name}\".", nameof(tenant));
        }

        tenant = tenant with { Name = tenant.Name.Trim() };
        App.SaveTenant(tenant);
        lock (_lock)
        {
            if (_states.TryGetValue(tenant.Id, out var state))
            {
                state.Tenant = tenant;
            }
        }
    }

    /// <summary>
    /// Takes a tenant off the list (not the open one). Its folder moves to <c>tenants\removed\</c>: nothing is deleted.
    /// </summary>
    public string? Remove(string id)
    {
        if (id == CurrentTenant.Id)
        {
            throw new InvalidOperationException("Open eerst een andere tenant.");
        }

        lock (_lock)
        {
            if (_states.Remove(id, out var state))
            {
                state.Dispose();
            }
        }

        App.RemoveTenant(id);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return TenantFolders.Remove(_environment.Root, id);
    }

    /// <summary>A backup of a tenant: its whole database in one file in the folder; returns the file.</summary>
    public string Backup(TenantInfo tenant, string folder)
    {
        var path = Path.Combine(folder, $"Moss365-{CsvSafe(tenant.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        Open(tenant).Database.BackupTo(path);
        Open(tenant).Log.Information("Back-up van de tenant gemaakt");
        return path;
    }

    /// <summary>
    /// Restores a backup as a new tenant (never over an existing one, so that nothing is lost by mistake). The database
    /// is checked first; an older version is migrated as usual.
    /// </summary>
    public TenantInfo Restore(string backupFile, string name)
    {
        if (!File.Exists(backupFile))
        {
            throw new FileNotFoundException("Back-up niet gevonden.", backupFile);
        }

        var tenant = Add(name);
        var path = TenantFolders.DatabasePath(_environment.Root, tenant.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(backupFile, path);
        try
        {
            if (new Database(path).IntegrityCheck() is var check && check != "ok")
            {
                throw new InvalidDataException("De back-up is beschadigd: " + check);
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidDataException)
        {
            App.RemoveTenant(tenant.Id);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TenantFolders.Remove(_environment.Root, tenant.Id);
            throw new InvalidDataException(ex is InvalidDataException ? ex.Message : "Dit is geen back-up van Moss365: " + ex.Message, ex);
        }

        return tenant;
    }

    private static string CsvSafe(string name)
        => new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray()).Trim('-') is { Length: > 0 } n ? n : "tenant";

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _states.Values)
            {
                s.Dispose();
            }

            _states.Clear();
        }
    }

    /// <summary>The company name in the organisation of the older version, as the name of the first tenant.</summary>
    private static string NameOfOldOrganisation(string root)
    {
        try
        {
            var path = Path.Combine(root, "organisatie.json");
            if (File.Exists(path) && ConfigStorage.Load(null, path).Config.Company is { Length: > 0 } company)
            {
                return company;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
        }

        return "Mijn organisatie";
    }
}
