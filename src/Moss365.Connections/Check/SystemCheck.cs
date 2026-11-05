using System.Net.Sockets;
using System.Text.Json;
using Moss365.Core.Changes;
using Moss365.Connections.Exchange;

namespace Moss365.Connections.Check;

/// <summary>Checks this PC: PowerShell, the modules for the app and the scripts, and whether AD and Microsoft 365 can
/// be reached.</summary>
public sealed class SystemCheck : ISystemCheck
{
    /// <summary>From this version on Connect-ExchangeOnline can use the token of the app (-AccessToken).</summary>
    public static readonly Version MinimumExchange = new(3, 1);

    private readonly Func<string, IPowerShellStarter> _starter;
    private readonly Func<IReadOnlyList<(string Name, string FilePath)>> _powerShells;
    private readonly Func<string, int, CancellationToken, Task<bool>> _reachable;
    private readonly string _workFolder;

    public SystemCheck(
        Func<string, IPowerShellStarter>? starter = null,
        Func<IReadOnlyList<(string Name, string FilePath)>>? powerShells = null,
        Func<string, int, CancellationToken, Task<bool>>? reachable = null,
        string? workFolder = null)
    {
        _starter = starter ?? (path => new PowerShellProcess(path));
        _powerShells = powerShells ?? PowerShellProcess.Available;
        _reachable = reachable ?? Reachable;
        _workFolder = workFolder ?? Path.Combine(Path.GetTempPath(), "Moss365");
    }

    public sealed record ModuleVersion(string Name, string Version);

    public async Task<IReadOnlyList<CheckRule>> CheckAsync(ConnectionSettings settings, IProgress<string>? progress, CancellationToken cancel)
    {
        var rules = new List<CheckRule>();
        var shells = _powerShells();
        if (shells.Count == 0)
        {
            rules.Add(new("PowerShell", "PowerShell", CheckStatus.Error, "niet gevonden", "Zonder PowerShell werkt Exchange niet."));
        }

        // The app uses the first one (PowerShell 7 if present) for Exchange
        for (var i = 0; i < shells.Count; i++)
        {
            var (name, path) = shells[i];
            progress?.Report($"{name}: modules nakijken...");
            try
            {
                var (version, modules, _) = await RunScriptAsync(path, null, cancel);
                rules.Add(new(name, "Versie", CheckStatus.Good, version, i == 0 ? "De app gebruikt deze PowerShell voor Exchange." : "Voor de scripts als je ze hier draait."));
                rules.AddRange(Assess(name, path, appUsedThis: i == 0, modules));
            }
            catch (Exception ex)
            {
                rules.Add(new(name, "Starten", CheckStatus.Error, "mislukt", ex.Message));
            }
        }

        progress?.Report("Netwerk nakijken...");
        if (settings.AdFilled)
        {
            var server = settings.AdServer.Trim();
            var ldaps = await _reachable(server, 636, cancel);
            var ldap = ldaps || await _reachable(server, 389, cancel);
            rules.Add(new("Netwerk", $"Domeincontroller {server}", ldap ? CheckStatus.Good : CheckStatus.Error,
                ldaps ? "bereikbaar (poort 636)" : ldap ? "bereikbaar (poort 389)" : "niet bereikbaar",
                ldap ? "AD via LDAP, ondertekend en versleuteld." : "Op het gemeentenetwerk of via VPN? Klopt de naam of het IP-adres?"));
        }
        else
        {
            rules.Add(new("Instellingen", "Domeincontroller", CheckStatus.Warning, "niet ingevuld", "Vul hem in op het scherm Verbindingen."));
        }

        foreach (var (host, what) in new[] { ("login.microsoftonline.com", "aanmelden"), ("graph.microsoft.com", "Entra"), ("outlook.office365.com", "Exchange Online") })
        {
            var ok = await _reachable(host, 443, cancel);
            rules.Add(new("Netwerk", host, ok ? CheckStatus.Good : CheckStatus.Error, ok ? "bereikbaar" : "niet bereikbaar",
                ok ? $"Nodig voor {what}." : $"Nodig voor {what}; controleer de internetverbinding of de proxy."));
        }

        rules.Add(settings.GraphFilled
            ? new("Instellingen", "App-registratie", CheckStatus.Good, "Tenant ID en Client ID ingevuld", "Draai Register-App.ps1 opnieuw na een nieuwe versie van de app.")
            : new("Instellingen", "App-registratie", CheckStatus.Warning, "niet ingevuld", "Draai Register-App.ps1 en vul Tenant ID en Client ID in op het scherm Verbindingen."));
        return rules;
    }

    public async Task<string> InstallAsync(string powerShell, string module, CancellationToken cancel)
    {
        if (!_powerShells().Any(s => string.Equals(s.FilePath, powerShell, StringComparison.OrdinalIgnoreCase)))
        {
            return "Mislukt: onbekende PowerShell.";
        }

        var (_, _, installation) = await RunScriptAsync(powerShell, module, cancel);
        return installation ?? "Mislukt: geen antwoord.";
    }

    /// <summary>Which modules are fine, too old or missing; with a solution and (where possible)
    /// installation.</summary>
    internal static IEnumerable<CheckRule> Assess(string name, string path, bool appUsedThis, IReadOnlyList<ModuleVersion> modules)
    {
        Version? V(string m) => Version.TryParse(modules.FirstOrDefault(x => x.Name == m)?.Version, out var v) ? v : null;

        var exo = V("ExchangeOnlineManagement");
        const string Exo = "ExchangeOnlineManagement";
        if (exo is null)
        {
            yield return new(name, Exo, appUsedThis ? CheckStatus.Error : CheckStatus.Warning, "niet geïnstalleerd",
                appUsedThis ? "Nodig voor rooms, gedeelde postvakken en mailgroepen." : "Nodig als je Export-Inventaris.ps1 in deze PowerShell draait.",
                $"Install-Module {Exo} -Scope CurrentUser", Exo, path);
        }
        else if (exo < MinimumExchange)
        {
            yield return new(name, Exo, appUsedThis ? CheckStatus.Error : CheckStatus.Warning, exo.ToString(),
                $"Te oud: vanaf {MinimumExchange} gebruikt Exchange dezelfde aanmelding als de app.", $"Install-Module {Exo} -Scope CurrentUser -Force", Exo, path);
        }
        else
        {
            yield return new(name, Exo, CheckStatus.Good, exo.ToString(), "Exchange Online.");
        }

        foreach (var g in new[] { "Microsoft.Graph.Authentication", "Microsoft.Graph.Applications", "Microsoft.Graph.Users", "Microsoft.Graph.Identity.SignIns" })
        {
            var v = V(g);
            yield return v is null
                ? new(name, g, CheckStatus.Warning, "niet geïnstalleerd", "Nodig voor Register-App.ps1 en Export-Inventaris.ps1 (niet voor de app zelf).",
                    $"Install-Module {g} -Scope CurrentUser", g, path)
                : new(name, g, CheckStatus.Good, v.ToString(), "Voor de scripts.");
        }

        var ad = V("ActiveDirectory");
        yield return ad is null
            ? new(name, "ActiveDirectory (RSAT)", CheckStatus.Warning, "niet geïnstalleerd",
                "Nodig voor het AD-deel van Export-Inventaris.ps1; de app zelf gebruikt LDAP. Installeren vraagt beheerdersrechten.",
                "Add-WindowsCapability -Online -Name 'Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0'  (als administrator)")
            : new(name, "ActiveDirectory (RSAT)", CheckStatus.Good, ad.ToString(), "Voor het AD-deel van Export-Inventaris.ps1.");
    }

    private async Task<(string Version, IReadOnlyList<ModuleVersion> Modules, string? Installation)> RunScriptAsync(string path, string? install, CancellationToken cancel)
    {
        Directory.CreateDirectory(_workFolder);
        var script = Path.Combine(_workFolder, "Check.ps1");
        await using (var source = typeof(SystemCheck).Assembly.GetManifestResourceStream("Check.ps1")
                                ?? throw new InvalidOperationException("Check.ps1 ontbreekt in de app."))
        await using (var target = File.Create(script))
        {
            await source.CopyToAsync(target, cancel);
        }

        var output = Path.Combine(_workFolder, $"controle-{Guid.NewGuid():N}.json");
        try
        {
            var arguments = new List<string> { "-Uitvoer", output };
            if (install is not null)
            {
                arguments.AddRange(new[] { "-Installeer", install });
            }

            var (code, errors) = await _starter(path).StartAsync(script, arguments, null, cancel);
            if (!File.Exists(output))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? $"PowerShell stopte met code {code}." : errors.Trim());
            }

            using var doc = JsonDocument.Parse((await File.ReadAllTextAsync(output, cancel)).TrimStart('﻿'));
            var root = doc.RootElement;
            string S(JsonElement e, string n) => e.TryGetProperty(n, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;
            var modules = root.TryGetProperty("Modules", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => new ModuleVersion(S(x, "Naam"), S(x, "Versie"))).ToList()
                : new List<ModuleVersion>();
            var installation = S(root, "Installatie");
            return ($"{S(root, "PowerShell")} ({S(root, "Editie")})", modules, installation.Length > 0 ? installation : null);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static async Task<bool> Reachable(string host, int port, CancellationToken cancel)
    {
        using var client = new TcpClient();
        using var time = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        time.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, port, time.Token);
            return true;
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            return false;
        }
    }
}
