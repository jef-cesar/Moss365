using System.Net.Sockets;
using System.Text.Json;
using Moss365.Core.Wijzigingen;
using Moss365.Connections.Exchange;

namespace Moss365.Connections.Controle;

/// <summary>Controleert deze pc: PowerShell, de modules voor de app en de scripts, en of AD en Microsoft 365 bereikbaar zijn.</summary>
public sealed class SysteemControle : ISysteemControle
{
    /// <summary>Vanaf deze versie kan Connect-ExchangeOnline het token van de app gebruiken (-AccessToken).</summary>
    public static readonly Version MinimumExchange = new(3, 1);

    private readonly Func<string, IPowerShellStarter> _starter;
    private readonly Func<IReadOnlyList<(string Naam, string Pad)>> _powerShells;
    private readonly Func<string, int, CancellationToken, Task<bool>> _bereikbaar;
    private readonly string _werkmap;

    public SysteemControle(
        Func<string, IPowerShellStarter>? starter = null,
        Func<IReadOnlyList<(string Naam, string Pad)>>? powerShells = null,
        Func<string, int, CancellationToken, Task<bool>>? bereikbaar = null,
        string? werkmap = null)
    {
        _starter = starter ?? (pad => new PowerShellProces(pad));
        _powerShells = powerShells ?? PowerShellProces.Beschikbaar;
        _bereikbaar = bereikbaar ?? Bereikbaar;
        _werkmap = werkmap ?? Path.Combine(Path.GetTempPath(), "Moss365");
    }

    public sealed record ModuleVersie(string Naam, string Versie);

    public async Task<IReadOnlyList<ControleRegel>> ControleerAsync(VerbindingInstellingen instellingen, IProgress<string>? voortgang, CancellationToken annuleren)
    {
        var regels = new List<ControleRegel>();
        var shells = _powerShells();
        if (shells.Count == 0)
        {
            regels.Add(new("PowerShell", "PowerShell", ControleStatus.Fout, "niet gevonden", "Zonder PowerShell werkt Exchange niet."));
        }

        // De app gebruikt de eerste (PowerShell 7 als die er is) voor Exchange
        for (var i = 0; i < shells.Count; i++)
        {
            var (naam, pad) = shells[i];
            voortgang?.Report($"{naam}: modules nakijken...");
            try
            {
                var (versie, modules, _) = await VoerScriptUitAsync(pad, null, annuleren);
                regels.Add(new(naam, "Versie", ControleStatus.Goed, versie, i == 0 ? "De app gebruikt deze PowerShell voor Exchange." : "Voor de scripts als je ze hier draait."));
                regels.AddRange(Beoordeel(naam, pad, appGebruiktDeze: i == 0, modules));
            }
            catch (Exception ex)
            {
                regels.Add(new(naam, "Starten", ControleStatus.Fout, "mislukt", ex.Message));
            }
        }

        voortgang?.Report("Netwerk nakijken...");
        if (instellingen.AdIngevuld)
        {
            var server = instellingen.AdServer.Trim();
            var ldaps = await _bereikbaar(server, 636, annuleren);
            var ldap = ldaps || await _bereikbaar(server, 389, annuleren);
            regels.Add(new("Netwerk", $"Domeincontroller {server}", ldap ? ControleStatus.Goed : ControleStatus.Fout,
                ldaps ? "bereikbaar (poort 636)" : ldap ? "bereikbaar (poort 389)" : "niet bereikbaar",
                ldap ? "AD via LDAP, ondertekend en versleuteld." : "Op het gemeentenetwerk of via VPN? Klopt de naam of het IP-adres?"));
        }
        else
        {
            regels.Add(new("Instellingen", "Domeincontroller", ControleStatus.Waarschuwing, "niet ingevuld", "Vul hem in op het scherm Verbindingen."));
        }

        foreach (var (host, wat) in new[] { ("login.microsoftonline.com", "aanmelden"), ("graph.microsoft.com", "Entra"), ("outlook.office365.com", "Exchange Online") })
        {
            var ok = await _bereikbaar(host, 443, annuleren);
            regels.Add(new("Netwerk", host, ok ? ControleStatus.Goed : ControleStatus.Fout, ok ? "bereikbaar" : "niet bereikbaar",
                ok ? $"Nodig voor {wat}." : $"Nodig voor {wat}; controleer de internetverbinding of de proxy."));
        }

        regels.Add(instellingen.GraphIngevuld
            ? new("Instellingen", "App-registratie", ControleStatus.Goed, "Tenant ID en Client ID ingevuld", "Draai Registreer-App.ps1 opnieuw na een nieuwe versie van de app.")
            : new("Instellingen", "App-registratie", ControleStatus.Waarschuwing, "niet ingevuld", "Draai Registreer-App.ps1 en vul Tenant ID en Client ID in op het scherm Verbindingen."));
        return regels;
    }

    public async Task<string> InstalleerAsync(string powerShell, string module, CancellationToken annuleren)
    {
        if (!_powerShells().Any(s => string.Equals(s.Pad, powerShell, StringComparison.OrdinalIgnoreCase)))
        {
            return "Mislukt: onbekende PowerShell.";
        }

        var (_, _, installatie) = await VoerScriptUitAsync(powerShell, module, annuleren);
        return installatie ?? "Mislukt: geen antwoord.";
    }

    /// <summary>Welke modules goed, te oud of afwezig zijn; met een oplossing en (waar dat kan) installeren.</summary>
    internal static IEnumerable<ControleRegel> Beoordeel(string naam, string pad, bool appGebruiktDeze, IReadOnlyList<ModuleVersie> modules)
    {
        Version? V(string m) => Version.TryParse(modules.FirstOrDefault(x => x.Naam == m)?.Versie, out var v) ? v : null;

        var exo = V("ExchangeOnlineManagement");
        const string Exo = "ExchangeOnlineManagement";
        if (exo is null)
        {
            yield return new(naam, Exo, appGebruiktDeze ? ControleStatus.Fout : ControleStatus.Waarschuwing, "niet geïnstalleerd",
                appGebruiktDeze ? "Nodig voor rooms, gedeelde postvakken en mailgroepen." : "Nodig als je Export-Inventaris.ps1 in deze PowerShell draait.",
                $"Install-Module {Exo} -Scope CurrentUser", Exo, pad);
        }
        else if (exo < MinimumExchange)
        {
            yield return new(naam, Exo, appGebruiktDeze ? ControleStatus.Fout : ControleStatus.Waarschuwing, exo.ToString(),
                $"Te oud: vanaf {MinimumExchange} gebruikt Exchange dezelfde aanmelding als de app.", $"Install-Module {Exo} -Scope CurrentUser -Force", Exo, pad);
        }
        else
        {
            yield return new(naam, Exo, ControleStatus.Goed, exo.ToString(), "Exchange Online.");
        }

        foreach (var g in new[] { "Microsoft.Graph.Authentication", "Microsoft.Graph.Applications", "Microsoft.Graph.Users", "Microsoft.Graph.Identity.SignIns" })
        {
            var v = V(g);
            yield return v is null
                ? new(naam, g, ControleStatus.Waarschuwing, "niet geïnstalleerd", "Nodig voor Registreer-App.ps1 en Export-Inventaris.ps1 (niet voor de app zelf).",
                    $"Install-Module {g} -Scope CurrentUser", g, pad)
                : new(naam, g, ControleStatus.Goed, v.ToString(), "Voor de scripts.");
        }

        var ad = V("ActiveDirectory");
        yield return ad is null
            ? new(naam, "ActiveDirectory (RSAT)", ControleStatus.Waarschuwing, "niet geïnstalleerd",
                "Nodig voor het AD-deel van Export-Inventaris.ps1; de app zelf gebruikt LDAP. Installeren vraagt beheerdersrechten.",
                "Add-WindowsCapability -Online -Name 'Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0'  (als administrator)")
            : new(naam, "ActiveDirectory (RSAT)", ControleStatus.Goed, ad.ToString(), "Voor het AD-deel van Export-Inventaris.ps1.");
    }

    private async Task<(string Versie, IReadOnlyList<ModuleVersie> Modules, string? Installatie)> VoerScriptUitAsync(string pad, string? installeer, CancellationToken annuleren)
    {
        Directory.CreateDirectory(_werkmap);
        var script = Path.Combine(_werkmap, "Controle.ps1");
        await using (var bron = typeof(SysteemControle).Assembly.GetManifestResourceStream("Controle.ps1")
                                ?? throw new InvalidOperationException("Controle.ps1 ontbreekt in de app."))
        await using (var doel = File.Create(script))
        {
            await bron.CopyToAsync(doel, annuleren);
        }

        var uitvoer = Path.Combine(_werkmap, $"controle-{Guid.NewGuid():N}.json");
        try
        {
            var argumenten = new List<string> { "-Uitvoer", uitvoer };
            if (installeer is not null)
            {
                argumenten.AddRange(new[] { "-Installeer", installeer });
            }

            var (code, fouten) = await _starter(pad).StartAsync(script, argumenten, null, annuleren);
            if (!File.Exists(uitvoer))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(fouten) ? $"PowerShell stopte met code {code}." : fouten.Trim());
            }

            using var doc = JsonDocument.Parse((await File.ReadAllTextAsync(uitvoer, annuleren)).TrimStart('﻿'));
            var root = doc.RootElement;
            string S(JsonElement e, string n) => e.TryGetProperty(n, out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() ?? string.Empty : string.Empty;
            var modules = root.TryGetProperty("Modules", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => new ModuleVersie(S(x, "Naam"), S(x, "Versie"))).ToList()
                : new List<ModuleVersie>();
            var installatie = S(root, "Installatie");
            return ($"{S(root, "PowerShell")} ({S(root, "Editie")})", modules, installatie.Length > 0 ? installatie : null);
        }
        finally
        {
            if (File.Exists(uitvoer))
            {
                File.Delete(uitvoer);
            }
        }
    }

    private static async Task<bool> Bereikbaar(string host, int poort, CancellationToken annuleren)
    {
        using var client = new TcpClient();
        using var tijd = CancellationTokenSource.CreateLinkedTokenSource(annuleren);
        tijd.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, poort, tijd.Token);
            return true;
        }
        catch (Exception) when (!annuleren.IsCancellationRequested)
        {
            return false;
        }
    }
}
