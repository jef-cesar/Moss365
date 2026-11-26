using Moss365.Core.Localization;
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
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _serverTime;
    private readonly Func<string, CancellationToken, Task<string>> _installComponent;

    /// <summary>Components outside PowerShell that the app installs itself (after confirmation).</summary>
    public const string PowerShell7 = "PowerShell7";
    public const string Rsat = "RSAT";

    public SystemCheck(
        Func<string, IPowerShellStarter>? starter = null,
        Func<IReadOnlyList<(string Name, string FilePath)>>? powerShells = null,
        Func<string, int, CancellationToken, Task<bool>>? reachable = null,
        string? workFolder = null,
        Func<CancellationToken, Task<DateTimeOffset?>>? serverTime = null,
        Func<string, CancellationToken, Task<string>>? installComponent = null)
    {
        _starter = starter ?? (path => new PowerShellProcess(path));
        _powerShells = powerShells ?? PowerShellProcess.Available;
        _reachable = reachable ?? Reachable;
        _workFolder = workFolder ?? Path.Combine(Path.GetTempPath(), "Moss365");
        _serverTime = serverTime ?? ServerTimeAsync;
        _installComponent = installComponent ?? ComponentInstaller.InstallAsync;
    }

    public sealed record ModuleVersion(string Name, string Version);

    public async Task<IReadOnlyList<CheckRule>> CheckAsync(ConnectionSettings settings, IProgress<string>? progress, CancellationToken cancel)
    {
        var rules = new List<CheckRule>();
        var shells = _powerShells();
        if (shells.Count == 0)
        {
            rules.Add(new("PowerShell", "PowerShell", CheckStatus.Error, Texts.T["index.not-found"], Texts.T["code.system-check.without-powershell-exchange-does-not"]));
        }

        // The app uses the first one (PowerShell 7 if present) for Exchange
        for (var i = 0; i < shells.Count; i++)
        {
            var (name, path) = shells[i];
            progress?.Report(Texts.T.Format("code.system-check.checking-modules", name));
            try
            {
                var (version, modules, _) = await RunScriptAsync(path, null, cancel);
                rules.Add(new(name, Texts.T["update-panel.version"], CheckStatus.Good, version, i == 0 ? Texts.T["code.system-check.the-app-uses-this-powershell"] : Texts.T["code.system-check.for-the-scripts-if-you"]));
                rules.AddRange(Assess(name, path, appUsedThis: i == 0, modules));
            }
            catch (Exception ex)
            {
                rules.Add(new(name, Texts.T["word.start"], CheckStatus.Error, "mislukt", ex.Message));
            }
        }

        if (shells.Count > 0 && !shells.Any(s => s.Name == "PowerShell 7"))
        {
            rules.Add(new(Texts.T["code.system-check.this-pc"], "PowerShell 7", CheckStatus.Warning, Texts.T["code.system-check.not-installed"],
                Texts.T["code.system-check.recommended-for-exchange-and-the"],
                "winget install --id Microsoft.PowerShell --source winget", PowerShell7, "winget"));
        }

        progress?.Report(Texts.T["code.system-check.checking-the-clock"]);
        rules.Add(Clock(DateTimeOffset.UtcNow, await _serverTime(cancel)));

        progress?.Report(Texts.T["code.system-check.checking-the-network"]);
        if (settings.AdFilled)
        {
            var server = settings.AdServer.Trim();
            var ldaps = await _reachable(server, 636, cancel);
            var ldap = ldaps || await _reachable(server, 389, cancel);
            rules.Add(new(Texts.T["code.system-check.network"], Texts.T.Format("code.system-check.domain-controller", server), ldap ? CheckStatus.Good : CheckStatus.Error,
                ldaps ? Texts.T["code.system-check.reachable-port-636"] : ldap ? Texts.T["code.system-check.reachable-port-389"] : Texts.T["code.system-check.not-reachable"],
                ldap ? Texts.T["code.system-check.ad-through-ldap-signed-and"] : Texts.T["code.system-check.on-the-company-network-or"]));
        }
        else
        {
            rules.Add(new(Texts.T["compare.settings"], Texts.T["connections.domain-controller"], CheckStatus.Warning, Texts.T["code.system-check.not-filled-in"], Texts.T["code.system-check.fill-it-in-on-the"]));
        }

        foreach (var (host, what) in new[] { ("login.microsoftonline.com", "aanmelden"), ("graph.microsoft.com", "Entra"), ("outlook.office365.com", "Exchange Online") })
        {
            var ok = await _reachable(host, 443, cancel);
            rules.Add(new(Texts.T["code.system-check.network"], host, ok ? CheckStatus.Good : CheckStatus.Error, ok ? "bereikbaar" : Texts.T["code.system-check.not-reachable"],
                ok ? Texts.T.Format("code.system-check.needed-for", what) : Texts.T.Format("code.system-check.needed-for-check-the-internet", what)));
        }

        rules.Add(settings.GraphFilled
            ? new(Texts.T["compare.settings"], Texts.T["setup.step.app"], CheckStatus.Good, Texts.T["code.system-check.tenant-id-and-client-id"], Texts.T["code.system-check.after-a-new-version-check"])
            : new(Texts.T["compare.settings"], Texts.T["setup.step.app"], CheckStatus.Warning, Texts.T["code.system-check.not-filled-in"], Texts.T["code.system-check.set-up-the-tenant-settings"]));
        return rules;
    }

    public async Task<string> InstallAsync(string powerShell, string module, CancellationToken cancel)
    {
        if (module is PowerShell7 or Rsat)
        {
            return await _installComponent(module, cancel);
        }

        if (!_powerShells().Any(s => string.Equals(s.FilePath, powerShell, StringComparison.OrdinalIgnoreCase)))
        {
            return Texts.T["code.system-check.failed-unknown-powershell"];
        }

        var (_, _, installation) = await RunScriptAsync(powerShell, module, cancel);
        return installation ?? Texts.T["code.system-check.failed-no-answer"];
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
            yield return new(name, Exo, appUsedThis ? CheckStatus.Error : CheckStatus.Warning, Texts.T["code.system-check.not-installed"],
                appUsedThis ? Texts.T["code.system-check.needed-for-rooms-shared-mailboxes"] : Texts.T["code.system-check.needed-if-you-run-export"],
                $"Install-Module {Exo} -Scope CurrentUser", Exo, path);
        }
        else if (exo < MinimumExchange)
        {
            yield return new(name, Exo, appUsedThis ? CheckStatus.Error : CheckStatus.Warning, exo.ToString(),
                Texts.T.Format("code.system-check.too-old-from-on-exchange", MinimumExchange), $"Install-Module {Exo} -Scope CurrentUser -Force", Exo, path);
        }
        else
        {
            yield return new(name, Exo, CheckStatus.Good, exo.ToString(), "Exchange Online.");
        }

        foreach (var g in new[] { "Microsoft.Graph.Authentication", "Microsoft.Graph.Applications", "Microsoft.Graph.Users", "Microsoft.Graph.Identity.SignIns" })
        {
            var v = V(g);
            yield return v is null
                ? new(name, g, CheckStatus.Warning, Texts.T["code.system-check.not-installed"], Texts.T["code.system-check.needed-for-register-app-ps1"],
                    $"Install-Module {g} -Scope CurrentUser", g, path)
                : new(name, g, CheckStatus.Good, v.ToString(), Texts.T["code.system-check.for-the-scripts"]);
        }

        var ad = V("ActiveDirectory");
        yield return ad is null
            ? new(name, "ActiveDirectory (RSAT)", CheckStatus.Warning, Texts.T["code.system-check.not-installed"],
                Texts.T["code.system-check.needed-for-the-ad-part"],
                "Add-WindowsCapability -Online -Name 'Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0'  (als administrator)", Rsat, path)
            : new(name, "ActiveDirectory (RSAT)", CheckStatus.Good, ad.ToString(), Texts.T["code.system-check.for-the-ad-part-of"]);
    }

    private async Task<(string Version, IReadOnlyList<ModuleVersion> Modules, string? Installation)> RunScriptAsync(string path, string? install, CancellationToken cancel)
    {
        Directory.CreateDirectory(_workFolder);
        var script = Path.Combine(_workFolder, "Check.ps1");
        await using (var source = typeof(SystemCheck).Assembly.GetManifestResourceStream("Check.ps1")
                                ?? throw new InvalidOperationException(Texts.T["code.system-check.check-ps1-is-missing-in"]))
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
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errors) ? Texts.T.Format("code.system-check.powershell-stopped-with-code", code) : errors.Trim());
            }

            using var doc = JsonDocument.Parse((await File.ReadAllTextAsync(output, cancel)).TrimStart('﻿'));
            var root = doc.RootElement;
            string S(JsonElement e, string n) => e.TryGetProperty(n, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;
            var modules = root.TryGetProperty("Modules", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => new ModuleVersion(S(x, "Naam"), S(x, Texts.T["update-panel.version"]))).ToList()
                : new List<ModuleVersion>();
            var installation = S(root, Texts.T["code.system-check.installation"]);
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

    /// <summary>Kerberos and the sign-in fail when the clock of the PC is more than five minutes off.</summary>
    internal static CheckRule Clock(DateTimeOffset now, DateTimeOffset? server)
    {
        if (server is not { } s)
        {
            return new(Texts.T["code.system-check.this-pc"], "Klok", CheckStatus.Warning, Texts.T["code.system-check.cannot-be-compared"], Texts.T["code.system-check.microsoft-not-reachable-to-check"]);
        }

        var off = Math.Abs((now - s).TotalSeconds);
        var text = off < 60 ? "juist" : Texts.T.Format("code.system-check.min-difference", Math.Round(off / 60));
        return off switch
        {
            > 300 => new(Texts.T["code.system-check.this-pc"], "Klok", CheckStatus.Error, text, Texts.T["code.system-check.more-than-5-minutes-difference"]),
            > 60 => new(Texts.T["code.system-check.this-pc"], "Klok", CheckStatus.Warning, text, Texts.T["code.system-check.the-clock-is-a-bit"]),
            _ => new(Texts.T["code.system-check.this-pc"], "Klok", CheckStatus.Good, text, Texts.T["code.system-check.in-sync-with-microsoft"]),
        };
    }

    private static readonly HttpClient TimeClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static async Task<DateTimeOffset?> ServerTimeAsync(CancellationToken cancel)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://login.microsoftonline.com/");
            using var answer = await TimeClient.SendAsync(request, cancel);
            return answer.Headers.Date;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return null;
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
