using Moss365.Core.Changes;
using Moss365.Core.Permissions;
using Moss365.Core.Setup;

namespace Moss365.UI.Services;

/// <summary>
/// Setting up a tenant (R5): the wizard finds the tenant ID, signs in with Microsoft Graph PowerShell, compares the
/// tenant with what Moss365 needs and fixes the difference as a plan (preview, confirmation, change log).
/// </summary>
public sealed partial class ManagementState
{
    /// <summary>What the wizard last read of this tenant, or null.</summary>
    public TenantSetupReading? SetupReading { get; private set; }

    public SetupChoices SetupChoices { get; set; } = new();

    public SetupAssessment? SetupAssessment => SetupReading is { } r ? TenantSetup.Assess(r, SetupChoices) : null;

    /// <summary>The wizard can work: the Windows app, with a tenant ID.</summary>
    public bool SetupAvailable => _factory?.Setup(Settings.TenantId) is not null;

    public string? SetupSignedInAs => _factory?.Setup(Settings.TenantId)?.SignedInAs;

    /// <summary>Finds the tenant ID of a domain and keeps it (settings and the tenant list). Returns an error, or null.</summary>
    public async Task<string?> FindTenantAsync(string domainOrId, CancellationToken cancel = default)
    {
        if (_factory is null)
        {
            return "Niet beschikbaar in deze omgeving.";
        }

        var clean = TenantLookup.Clean(domainOrId);
        var result = await _factory.FindTenantAsync(domainOrId, cancel);
        if (result.TenantId is not { } id)
        {
            return result.Error ?? "Geen tenant gevonden.";
        }

        if (!string.Equals(Settings.TenantId.Trim(), id, StringComparison.OrdinalIgnoreCase))
        {
            // Another tenant: what was read and the app registration of the old one no longer count
            Settings.ClientId = string.Empty;
            SetupReading = null;
        }

        Settings.TenantId = id;
        SaveSettings();
        Tenant = Tenant with { TenantId = id, Domain = clean is not null && !TenantLookup.IsTenantId(clean) ? clean : Tenant.Domain };
        TenantChanged?.Invoke(Tenant);
        Log.Information("Tenant ID opgezocht voor de wizard");
        return null;
    }

    /// <summary>The tenant info changed (the hub saves it in the list of tenants).</summary>
    public event Action<Core.Storage.TenantInfo>? TenantChanged;

    /// <summary>Signs in (the link appears in the app) and reads what Moss365 needs. Returns an error, or null.</summary>
    public async Task<string?> ReadSetupAsync(IProgress<string>? progress = null, CancellationToken cancel = default)
    {
        if (_factory?.Setup(Settings.TenantId) is not { } setup)
        {
            return "Zoek eerst de tenant op (stap 1).";
        }

        try
        {
            SetupReading = await setup.ReadAsync(progress, cancel);
            Access("Tenant ingericht: nagekeken", Settings.TenantId);
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning("Wizard: lezen mislukt ({Soort})", ex.GetType().Name);
            return "Nakijken mislukt: " + ex.Message;
        }
    }

    /// <summary>
    /// Runs the plan of the wizard with the sign-in of the wizard, then reads again and fills in the client ID of the
    /// app registration.
    /// </summary>
    public async Task<IReadOnlyList<ChangeResult>> ExecuteSetupAsync(IReadOnlyList<Change> plan, string context, IProgress<string>? progress, CancellationToken cancel = default)
    {
        if (_factory?.Setup(Settings.TenantId) is not { } setup)
        {
            throw new InvalidOperationException("Zoek eerst de tenant op (stap 1).");
        }

        if (IsHistoric)
        {
            throw new InvalidOperationException("Je bekijkt een stand uit het verleden: ga eerst terug naar nu.");
        }

        var roles = Roles;
        var results = await new PlanExecutor(new IExecutor[] { setup }, ChangeLog, () => setup.SignedInAs ?? User, w => PermissionTable.Refusal(roles, w))
            .ExecuteAsync(plan, context, progress, cancel);
        Log.Information("Wizard: plan uitgevoerd: {Gelukt} gelukt, {Mislukt} mislukt", results.Count(r => r.Status == ResultStatus.Succeeded), results.Count(r => r.Status == ResultStatus.Failed));

        progress?.Report("Opnieuw nakijken...");
        await ReadSetupAsync(progress, cancel);
        TakeOverClientId();
        Changed?.Invoke();
        return results;
    }

    /// <summary>The client ID of the app registration that was found goes into the connection settings.</summary>
    public bool TakeOverClientId()
    {
        if (SetupReading?.App?.AppId is not { Length: > 0 } appId || string.Equals(Settings.ClientId.Trim(), appId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Settings.ClientId = appId;
        SaveSettings();
        return true;
    }

    /// <summary>The Exchange admin account: the account that signed in to the wizard.</summary>
    public bool TakeOverManagementUpn()
    {
        if (SetupReading?.SignedIn.Upn is not { Length: > 0 } upn || Settings.ManagementUpn.Trim().Length > 0)
        {
            return false;
        }

        Settings.ManagementUpn = upn;
        SaveSettings();
        return true;
    }

    /// <summary>The domain of this PC, or null.</summary>
    public LocalDomain? LocalDomain => _factory?.FindLocalDomain();

    /// <summary>Takes the domain of this PC over: its domain controller and the AD domain of the organisation.</summary>
    public void TakeOverLocalDomain(LocalDomain domain)
    {
        if (domain.Controller is { } dc)
        {
            Settings.AdServer = dc;
            SaveSettings();
        }

        if (string.IsNullOrWhiteSpace(Config.AdDomain))
        {
            Config.AdDomain = domain.Domain;
            SaveConfig();
        }
    }

    /// <summary>Suggestions for the organisation from what is loaded.</summary>
    public IReadOnlyList<Suggestion> Suggestions() => OrganisationSuggestions.For(Data, Config);

    public void TakeOver(IEnumerable<Suggestion> chosen)
    {
        var list = chosen.ToList();
        OrganisationSuggestions.Apply(Config, list);
        SaveConfig();
        Log.Information("Wizard: {Aantal} voorstellen overgenomen", list.Count);
    }

    /// <summary>
    /// Installs a missing component of this PC (only from official sources, after confirmation in the screen) and
    /// writes it to the change log. Returns "Gelukt" or the error.
    /// </summary>
    public async Task<string> InstallAsync(CheckRule rule, CancellationToken cancel = default)
    {
        if (Check is not { } check || rule.Module is null || rule.PowerShell is null)
        {
            return "Mislukt: installeren kan hier niet.";
        }

        string outcome;
        try
        {
            outcome = await check.InstallAsync(rule.PowerShell, rule.Module, cancel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = "Mislukt: " + ex.Message;
        }

        var ok = outcome == "Gelukt";
        ChangeLog.Write(new LogEntry(DateTimeOffset.Now, User, "Controle van deze pc", "Deze pc", "InstallComponent", rule.Module, rule.Section,
            $"{rule.Section} installeren ({rule.Group})", new Dictionary<string, string> { ["bron"] = rule.Solution ?? string.Empty },
            ok ? nameof(ResultStatus.Succeeded) : nameof(ResultStatus.Failed), ok ? string.Empty : outcome));
        Log.Information("Onderdeel {Onderdeel} installeren: {Resultaat}", rule.Module, ok ? "gelukt" : "mislukt");
        return outcome;
    }
}
