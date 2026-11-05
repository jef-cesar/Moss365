using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Moss365.Core.Inventaris;
using Moss365.Core.Wijzigingen;

namespace Moss365.Connections.Exchange;

/// <summary>Start een PowerShell-script in een apart proces en wacht tot het klaar is.</summary>
public interface IPowerShellStarter
{
    /// <param name="omgeving">Extra omgevingsvariabelen voor het proces (bv. het toegangstoken; nooit op de opdrachtregel).</param>
    Task<(int Code, string Fouten)> StartAsync(string script, IReadOnlyList<string> argumenten, IReadOnlyDictionary<string, string>? omgeving, CancellationToken annuleren);
}

/// <summary>Start pwsh.exe (PowerShell 7) als dat er is, anders Windows PowerShell 5.1; of de opgegeven PowerShell.</summary>
public sealed class PowerShellProces : IPowerShellStarter
{
    private readonly string? _pad;

    public PowerShellProces(string? pad = null) => _pad = pad;

    /// <summary>De PowerShells op deze pc: PowerShell 7 (als geinstalleerd) en Windows PowerShell 5.1.</summary>
    public static IReadOnlyList<(string Naam, string Pad)> Beschikbaar()
        => new[]
        {
            ("PowerShell 7", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")),
            ("Windows PowerShell 5.1", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")),
        }.Where(x => File.Exists(x.Item2)).ToList();

    public async Task<(int Code, string Fouten)> StartAsync(string script, IReadOnlyList<string> argumenten, IReadOnlyDictionary<string, string>? omgeving, CancellationToken annuleren)
    {
        var info = new ProcessStartInfo(_pad ?? ZoekPowerShell())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(argumenten))
        {
            info.ArgumentList.Add(a);
        }

        foreach (var (naam, waarde) in omgeving ?? new Dictionary<string, string>())
        {
            info.Environment[naam] = waarde;
        }

        using var proces = Process.Start(info) ?? throw new InvalidOperationException("PowerShell kon niet gestart worden.");
        var fouten = proces.StandardError.ReadToEndAsync(annuleren);
        _ = proces.StandardOutput.ReadToEndAsync(annuleren);
        using var tijdslimiet = CancellationTokenSource.CreateLinkedTokenSource(annuleren);
        tijdslimiet.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await proces.WaitForExitAsync(tijdslimiet.Token);
        }
        catch (OperationCanceledException)
        {
            proces.Kill(entireProcessTree: true);
            throw new TimeoutException("Exchange Online reageerde niet binnen 20 minuten.");
        }

        return (proces.ExitCode, await fouten);
    }

    private static string ZoekPowerShell()
    {
        var kandidaten = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        };
        return kandidaten.FirstOrDefault(File.Exists) ?? "powershell.exe";
    }
}

/// <summary>
/// Exchange Online via het script Exchange.ps1 in een apart PowerShell-proces met de module ExchangeOnlineManagement.
/// Met een tokenbron gebruikt het proces dezelfde aanmelding als Graph; anders meldt het per uitvoering zelf aan.
/// </summary>
public sealed class ExchangeVerbinding : IExchangeVerbinding, IUitvoerder
{
    public const string TokenVariabele = "MOSS365_EXO_TOKEN";

    private readonly string? _beheerUpn;
    private readonly IPowerShellStarter _starter;
    private readonly string _werkmap;
    private readonly Func<CancellationToken, Task<string?>>? _token;
    private readonly Func<string?>? _aangemeldAls;

    public ExchangeVerbinding(string? beheerUpn, IPowerShellStarter starter, string? werkmap = null,
        Func<CancellationToken, Task<string?>>? token = null, Func<string?>? aangemeldAls = null)
    {
        _beheerUpn = string.IsNullOrWhiteSpace(beheerUpn) ? null : beheerUpn.Trim();
        _starter = starter;
        _werkmap = werkmap ?? Path.Combine(Path.GetTempPath(), "Moss365");
        _token = token;
        _aangemeldAls = aangemeldAls;
    }

    public Systeem Systeem => Systeem.Exchange;

    public sealed record Opdracht(string Id, string Op, Dictionary<string, string> Params);

    public sealed record Resultaat(string Id, string Status, string Melding, JsonElement? Data);

    /// <summary>Zet een geplande wijziging om naar een opdracht voor Exchange.ps1.</summary>
    public static Opdracht NaarOpdracht(Wijziging w)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        string op;
        switch (w.Actie)
        {
            case Actie.ExchangeGroepLidToevoegen:
            case Actie.ExchangeGroepLidVerwijderen:
                op = w.Actie == Actie.ExchangeGroepLidToevoegen ? "LidToevoegen" : "LidVerwijderen";
                p["Identity"] = w.P("groep");
                p["Member"] = w.Doel;
                break;
            case Actie.ExchangeRoomAgendaverwerking:
                op = "Agendaverwerking";
                p["Identity"] = w.Doel;
                foreach (var (k, v) in w.Parameters)
                {
                    p[k] = v;
                }

                break;
            case Actie.ExchangeRoomPlace:
                op = "Place";
                p["Identity"] = w.Doel;
                foreach (var (k, v) in w.Parameters)
                {
                    p[k] = v;
                }

                break;
            case Actie.ExchangeAgendaRecht:
                op = "AgendaRecht";
                p["Identity"] = w.Doel;
                p["User"] = w.P("User");
                p["AccessRights"] = w.P("AccessRights");
                break;
            case Actie.ExchangeNaarGedeeldPostvak:
                op = "NaarGedeeld";
                p["Identity"] = w.Doel;
                break;
            case Actie.ExchangeGroepInstellen:
                op = "GroepInstellen";
                p["Identity"] = w.Doel;
                Neem(w, p, "DisplayName", "PrimarySmtpAddress");
                break;
            case Actie.ExchangeGroepBeheerder:
                op = "Beheerder";
                p["Identity"] = w.Doel;
                Neem(w, p, "Toevoegen", "Verwijderen");
                break;
            case Actie.ExchangePostvakInstellen:
                op = "PostvakInstellen";
                p["Identity"] = w.Doel;
                Neem(w, p, "DisplayName", "HiddenFromAddressListsEnabled", "ForwardingSmtpAddress", "DeliverToMailboxAndForward");
                break;
            case Actie.ExchangeAutoAntwoord:
                op = "AutoAntwoord";
                p["Identity"] = w.Doel;
                Neem(w, p, "AutoReplyState", "InternalMessage", "ExternalMessage");
                break;
            case Actie.ExchangePostvakRechtToevoegen:
            case Actie.ExchangePostvakRechtVerwijderen:
                op = w.Actie == Actie.ExchangePostvakRechtToevoegen ? "RechtToevoegen" : "RechtVerwijderen";
                p["Identity"] = w.Doel;
                Neem(w, p, "Recht", "Trustee");
                break;
            case Actie.ExchangeGedeeldPostvakAanmaken:
            case Actie.ExchangeGroepAanmaken:
                op = w.Actie == Actie.ExchangeGedeeldPostvakAanmaken ? "PostvakAanmaken" : "GroepAanmaken";
                p["PrimarySmtpAddress"] = w.Doel;
                Neem(w, p, "Name", "DisplayName", "Alias");
                if (w.Actie == Actie.ExchangeGroepAanmaken)
                {
                    Neem(w, p, "Type");
                }
                break;
            default:
                throw new InvalidOperationException($"Actie {w.Actie} hoort niet bij Exchange.");
        }

        return new Opdracht(w.Id.ToString(), op, p);
    }

    /// <summary>Neemt de opgegeven parameters over als ze in de wijziging staan (ook lege waarden: die betekenen "wissen").</summary>
    private static void Neem(Wijziging w, Dictionary<string, string> p, params string[] namen)
    {
        foreach (var n in namen)
        {
            if (w.Parameters.TryGetValue(n, out var v))
            {
                p[n] = v;
            }
        }
    }

    public async Task<IReadOnlyList<WijzigingResultaat>> VoerUitAsync(IReadOnlyList<Wijziging> wijzigingen, CancellationToken annuleren)
    {
        var opdrachten = new List<Opdracht>();
        var uit = new Dictionary<Guid, WijzigingResultaat>();
        foreach (var w in wijzigingen)
        {
            try
            {
                opdrachten.Add(NaarOpdracht(w));
            }
            catch (Exception ex)
            {
                uit[w.Id] = WijzigingResultaat.Fout(w, ex.Message);
            }
        }

        if (opdrachten.Count > 0)
        {
            var resultaten = await VoerScriptUitAsync(opdrachten, annuleren);
            foreach (var w in wijzigingen.Where(w => !uit.ContainsKey(w.Id)))
            {
                var r = resultaten.FirstOrDefault(x => x.Id == w.Id.ToString());
                uit[w.Id] = r is null
                    ? WijzigingResultaat.Fout(w, "Geen resultaat van Exchange.")
                    : r.Status == "Gelukt" ? WijzigingResultaat.Ok(w, r.Melding) : WijzigingResultaat.Fout(w, r.Melding);
            }
        }

        return wijzigingen.Select(w => uit[w.Id]).ToList();
    }

    public async Task<IReadOnlyList<Room>> LeesRoomsAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Exchange: aanmelden en rooms lezen...");
        var r = (await VoerScriptUitAsync(new[] { new Opdracht("rooms", "LeesRooms", new()) }, annuleren)).Single();
        if (r.Status != "Gelukt")
        {
            throw new InvalidOperationException(r.Melding);
        }

        var rooms = r.Data is { ValueKind: JsonValueKind.Array } lijst ? lijst.EnumerateArray().Select(NaarRoom).ToList() : new List<Room>();
        voortgang?.Report($"Exchange: {rooms.Count} rooms gelezen.");
        return rooms;
    }

    public async Task<ExchangeGegevens> LeesPostvakkenAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Exchange: postvakken, rechten en groepen lezen (dit kan enkele minuten duren)...");
        var r = (await VoerScriptUitAsync(new[] { new Opdracht("postvakken", "LeesPostvakken", new()) }, annuleren)).Single();
        if (r.Status != "Gelukt" || r.Data is not { ValueKind: JsonValueKind.Object } d)
        {
            throw new InvalidOperationException(r.Status == "Gelukt" ? "Exchange gaf geen postvakken terug." : r.Melding);
        }

        var gegevens = NaarExchangeGegevens(d);
        voortgang?.Report($"Exchange: {gegevens.Postvakken.Count} postvakken, {gegevens.Rechten.Count} rechten en {gegevens.Groepen.Count} groepen gelezen.");
        return gegevens;
    }

    public async Task<ExchangeLezing> LeesAllesAsync(IProgress<string>? voortgang, CancellationToken annuleren)
    {
        voortgang?.Report("Exchange: aanmelden, rooms, postvakken, rechten en groepen lezen (dit kan enkele minuten duren)...");
        var resultaten = await VoerScriptUitAsync(new[] { new Opdracht("rooms", "LeesRooms", new()), new Opdracht("postvakken", "LeesPostvakken", new()) }, annuleren);
        var r = resultaten.FirstOrDefault(x => x.Id == "rooms");
        var p = resultaten.FirstOrDefault(x => x.Id == "postvakken");

        IReadOnlyList<Room>? rooms = r is { Status: "Gelukt" }
            ? r.Data is { ValueKind: JsonValueKind.Array } lijst ? lijst.EnumerateArray().Select(NaarRoom).ToList() : new List<Room>()
            : null;
        ExchangeGegevens? postvakken = p is { Status: "Gelukt", Data: { ValueKind: JsonValueKind.Object } d } ? NaarExchangeGegevens(d) : null;
        var lezing = new ExchangeLezing(
            rooms, rooms is null ? r?.Melding ?? "Geen resultaat van Exchange." : null,
            postvakken, postvakken is null ? (p is { Status: "Gelukt" } ? "Exchange gaf geen postvakken terug." : p?.Melding ?? "Geen resultaat van Exchange.") : null);
        voortgang?.Report($"Exchange: {rooms?.Count ?? 0} rooms, {postvakken?.Postvakken.Count ?? 0} postvakken en {postvakken?.Groepen.Count ?? 0} groepen gelezen.");
        return lezing;
    }

    public async Task<ExchangeObjecten> LeesObjectenAsync(IReadOnlyList<string> adressen, CancellationToken annuleren)
    {
        var lijst = adressen.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (lijst.Count == 0)
        {
            return new ExchangeObjecten(Array.Empty<Room>(), new ExchangeGegevens(Array.Empty<ExoGroep>(), Array.Empty<Postvak>(), Array.Empty<PostvakRecht>()), Array.Empty<string>());
        }

        var r = (await VoerScriptUitAsync(new[] { new Opdracht("objecten", "LeesObjecten", new() { ["Adressen"] = string.Join('|', lijst) }) }, annuleren)).Single();
        if (r.Status != "Gelukt" || r.Data is not { ValueKind: JsonValueKind.Object } d)
        {
            throw new InvalidOperationException(r.Status == "Gelukt" ? "Exchange gaf geen gegevens terug." : r.Melding);
        }

        var rooms = d.TryGetProperty("Rooms", out var rl) && rl.ValueKind == JsonValueKind.Array ? rl.EnumerateArray().Select(NaarRoom).ToList() : new List<Room>();
        var weg = d.TryGetProperty("NietGevonden", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new ExchangeObjecten(rooms, NaarExchangeGegevens(d), weg);
    }

    internal static ExchangeGegevens NaarExchangeGegevens(JsonElement d)
    {
        IEnumerable<JsonElement> Lijst(JsonElement e, string naam)
            => e.TryGetProperty(naam, out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray() : Enumerable.Empty<JsonElement>();

        var postvakken = Lijst(d, "Postvakken").Select(e => new Postvak
        {
            Type = S(e, "Type"),
            DisplayName = S(e, "DisplayName"),
            Alias = S(e, "Alias"),
            Smtp = S(e, "Smtp"),
            Verborgen = B(e, "Hidden"),
            IsDirSynced = B(e, "IsDirSynced"),
            WhenCreated = S(e, "WhenCreated"),
            DoorsturenNaar = S(e, "DoorsturenNaar"),
            Doorsturen = S(e, "DoorsturenNaar"),
            DoorsturenEnBewaren = S(e, "DoorsturenEnBewaren").Length > 0 ? B(e, "DoorsturenEnBewaren") : null,
            AutoAntwoord = S(e, "AutoAntwoord"),
            AutoAntwoordIntern = S(e, "AutoAntwoordIntern"),
            AutoAntwoordExtern = S(e, "AutoAntwoordExtern"),
        }).Where(p => p.Smtp.Length > 0).ToList();
        var rechten = Lijst(d, "Rechten")
            .SelectMany(e => S(e, "Recht").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(recht => new PostvakRecht(S(e, "Postvak"), recht, S(e, "Trustee"))))
            .Where(r => r.Postvak.Length > 0 && r.Trustee.Length > 0)
            .ToList();
        var groepen = Lijst(d, "Groepen").Select(e =>
        {
            var leden = Lijst(e, "Leden").Select(l => new ExoLid(S(l, "Smtp"), S(l, "Naam"))).ToList();
            return new ExoGroep
            {
                Type = S(e, "Type"),
                DisplayName = S(e, "DisplayName"),
                Name = S(e, "Name"),
                Alias = S(e, "Alias"),
                Smtp = S(e, "Smtp"),
                IsDirSynced = B(e, "IsDirSynced"),
                MemberJoinRestriction = S(e, "MemberJoinRestriction"),
                ManagedBy = S(e, "ManagedBy").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Leden = leden,
                AantalLeden = leden.Count,
            };
        }).ToList();
        return new ExchangeGegevens(groepen, postvakken, rechten);
    }

    internal async Task<IReadOnlyList<Resultaat>> VoerScriptUitAsync(IReadOnlyList<Opdracht> opdrachten, CancellationToken annuleren)
    {
        Directory.CreateDirectory(_werkmap);
        var script = Path.Combine(_werkmap, "Exchange.ps1");
        await using (var bron = typeof(ExchangeVerbinding).Assembly.GetManifestResourceStream("Exchange.ps1")
                                ?? throw new InvalidOperationException("Exchange.ps1 ontbreekt in de app."))
        await using (var doel = File.Create(script))
        {
            await bron.CopyToAsync(doel, annuleren);
        }

        var id = Guid.NewGuid().ToString("N");
        var invoer = Path.Combine(_werkmap, $"opdrachten-{id}.json");
        var uitvoer = Path.Combine(_werkmap, $"resultaten-{id}.json");
        await File.WriteAllTextAsync(invoer, JsonSerializer.Serialize(opdrachten.Select(o => new { id = o.Id, op = o.Op, @params = o.Params })), new UTF8Encoding(false), annuleren);
        try
        {
            var argumenten = new List<string> { "-Opdrachten", invoer, "-Uitvoer", uitvoer };
            var omgeving = new Dictionary<string, string>();
            var token = _token is null ? null : await _token(annuleren);
            var upn = token is null ? _beheerUpn : _beheerUpn ?? _aangemeldAls?.Invoke();
            if (token is not null)
            {
                omgeving[TokenVariabele] = token;
            }

            if (upn is not null)
            {
                argumenten.AddRange(new[] { "-Upn", upn });
            }

            var (code, fouten) = await _starter.StartAsync(script, argumenten, omgeving, annuleren);
            if (!File.Exists(uitvoer))
            {
                var reden = string.IsNullOrWhiteSpace(fouten) ? $"PowerShell stopte met code {code}." : fouten.Trim();
                throw new InvalidOperationException("Exchange-script gaf geen resultaat: " + reden);
            }

            return LeesResultaten(await File.ReadAllTextAsync(uitvoer, annuleren));
        }
        finally
        {
            File.Delete(invoer);
            if (File.Exists(uitvoer))
            {
                File.Delete(uitvoer);
            }
        }
    }

    internal static IReadOnlyList<Resultaat> LeesResultaten(string json)
    {
        using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
        var lijst = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        return lijst.Select(e => new Resultaat(
            S(e, "id"), S(e, "status"), S(e, "melding"),
            e.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null)).ToList();
    }

    internal static Room NaarRoom(JsonElement e) => new()
    {
        Smtp = S(e, "Smtp"),
        DisplayName = S(e, "DisplayName"),
        Alias = S(e, "Alias"),
        Gebouw = S(e, "Gebouw"),
        Verdieping = S(e, "Verdieping"),
        VerdiepingLabel = S(e, "VerdiepingLabel"),
        Capaciteit = I(e, "Capaciteit"),
        Verborgen = B(e, "Hidden"),
        AutomateProcessing = S(e, "AutomateProcessing"),
        AllBookInPolicy = B(e, "AllBookInPolicy"),
        AllRequestInPolicy = B(e, "AllRequestInPolicy"),
        BookInPolicy = S(e, "BookInPolicy"),
        ResourceDelegates = S(e, "ResourceDelegates"),
        BookingWindowInDays = I(e, "BookingWindowInDays"),
        MaximumDurationInMinutes = I(e, "MaximumDurationInMinutes"),
        AllowConflicts = B(e, "AllowConflicts"),
        AllowRecurringMeetings = B(e, "AllowRecurringMeetings"),
        AddOrganizerToSubject = B(e, "AddOrganizerToSubject"),
        DeleteSubject = B(e, "DeleteSubject"),
        AgendaRechten = S(e, "AgendaRechten"),
    };

    private static string S(JsonElement e, string naam)
        => e.TryGetProperty(naam, out var w) ? w.ValueKind switch
        {
            JsonValueKind.String => w.GetString() ?? string.Empty,
            JsonValueKind.Number => w.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => string.Empty,
        } : string.Empty;

    private static bool B(JsonElement e, string naam) => string.Equals(S(e, naam), "True", StringComparison.OrdinalIgnoreCase);

    private static int? I(JsonElement e, string naam) => int.TryParse(S(e, naam), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
