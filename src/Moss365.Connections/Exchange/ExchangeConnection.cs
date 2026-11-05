using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Moss365.Core.Inventory;
using Moss365.Core.Changes;

namespace Moss365.Connections.Exchange;

/// <summary>Starts a PowerShell script in a separate process and waits until it is done.</summary>
public interface IPowerShellStarter
{
    /// <param name="environment">Extra environment variables for the process (e.g. the access token; never on the
    /// command line).</param>
    Task<(int Code, string Errors)> StartAsync(string script, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel);
}

/// <summary>Starts pwsh.exe (PowerShell 7) if present, otherwise Windows PowerShell 5.1; or the given
/// PowerShell.</summary>
public sealed class PowerShellProcess : IPowerShellStarter
{
    private readonly string? _path;

    public PowerShellProcess(string? path = null) => _path = path;

    /// <summary>The PowerShells on this PC: PowerShell 7 (if installed) and Windows PowerShell 5.1.</summary>
    public static IReadOnlyList<(string Name, string FilePath)> Available()
        => new[]
        {
            ("PowerShell 7", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")),
            ("Windows PowerShell 5.1", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")),
        }.Where(x => File.Exists(x.Item2)).ToList();

    public async Task<(int Code, string Errors)> StartAsync(string script, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment, CancellationToken cancel)
    {
        var info = new ProcessStartInfo(_path ?? SearchPowerShell())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
        {
            info.ArgumentList.Add(a);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell kon niet gestart worden.");
        var errors = process.StandardError.ReadToEndAsync(cancel);
        _ = process.StandardOutput.ReadToEndAsync(cancel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Exchange Online reageerde niet binnen 20 minuten.");
        }

        return (process.ExitCode, await errors);
    }

    private static string SearchPowerShell()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? "powershell.exe";
    }
}

/// <summary>
/// Exchange Online via the script Exchange.ps1 in a separate PowerShell process with the module
/// ExchangeOnlineManagement. With a token source the process uses the same sign-in as Graph; otherwise it signs in by
/// itself for every run.
/// </summary>
public sealed class ExchangeConnection : IExchangeConnection, IExecutor
{
    public const string TokenVariable = "MOSS365_EXO_TOKEN";

    private readonly string? _managementUpn;
    private readonly IPowerShellStarter _starter;
    private readonly string _workFolder;
    private readonly Func<CancellationToken, Task<string?>>? _token;
    private readonly Func<string?>? _signedInAs;

    public ExchangeConnection(string? managementUpn, IPowerShellStarter starter, string? workFolder = null,
        Func<CancellationToken, Task<string?>>? token = null, Func<string?>? signedInAs = null)
    {
        _managementUpn = string.IsNullOrWhiteSpace(managementUpn) ? null : managementUpn.Trim();
        _starter = starter;
        _workFolder = workFolder ?? Path.Combine(Path.GetTempPath(), "Moss365");
        _token = token;
        _signedInAs = signedInAs;
    }

    public TargetSystem TargetSystem => TargetSystem.Exchange;

    public sealed record Command(string Id, string Op, Dictionary<string, string> Params);

    public sealed record Result(string Id, string Status, string Message, JsonElement? Data);

    /// <summary>Converts a planned change to a command for Exchange.ps1.</summary>
    public static Command ToCommand(Change w)
    {
        var p = new Dictionary<string, string>(StringComparer.Ordinal);
        string op;
        switch (w.ChangeAction)
        {
            case ChangeAction.ExchangeAddGroupMember:
            case ChangeAction.ExchangeRemoveGroupMember:
                op = w.ChangeAction == ChangeAction.ExchangeAddGroupMember ? "LidToevoegen" : "LidVerwijderen";
                p["Identity"] = w.P("groep");
                p["Member"] = w.Target;
                break;
            case ChangeAction.ExchangeRoomCalendarProcessing:
                op = "Agendaverwerking";
                p["Identity"] = w.Target;
                foreach (var (k, v) in w.Parameters)
                {
                    p[k] = v;
                }

                break;
            case ChangeAction.ExchangeRoomPlace:
                op = "Place";
                p["Identity"] = w.Target;
                foreach (var (k, v) in w.Parameters)
                {
                    p[k] = v;
                }

                break;
            case ChangeAction.ExchangeCalendarPermission:
                op = "AgendaRecht";
                p["Identity"] = w.Target;
                p["User"] = w.P("User");
                p["AccessRights"] = w.P("AccessRights");
                break;
            case ChangeAction.ExchangeConvertToSharedMailbox:
                op = "NaarGedeeld";
                p["Identity"] = w.Target;
                break;
            case ChangeAction.ExchangeUpdateGroup:
                op = "GroepInstellen";
                p["Identity"] = w.Target;
                Take(w, p, "DisplayName", "PrimarySmtpAddress");
                break;
            case ChangeAction.ExchangeGroupManager:
                op = "Beheerder";
                p["Identity"] = w.Target;
                Take(w, p, "Toevoegen", "Verwijderen");
                break;
            case ChangeAction.ExchangeUpdateMailbox:
                op = "PostvakInstellen";
                p["Identity"] = w.Target;
                Take(w, p, "DisplayName", "HiddenFromAddressListsEnabled", "ForwardingSmtpAddress", "DeliverToMailboxAndForward");
                break;
            case ChangeAction.ExchangeAutoReply:
                op = "AutoAntwoord";
                p["Identity"] = w.Target;
                Take(w, p, "AutoReplyState", "InternalMessage", "ExternalMessage");
                break;
            case ChangeAction.ExchangeAddMailboxPermission:
            case ChangeAction.ExchangeRemoveMailboxPermission:
                op = w.ChangeAction == ChangeAction.ExchangeAddMailboxPermission ? "RechtToevoegen" : "RechtVerwijderen";
                p["Identity"] = w.Target;
                Take(w, p, "Recht", "Trustee");
                break;
            case ChangeAction.ExchangeCreateSharedMailbox:
            case ChangeAction.ExchangeCreateGroup:
                op = w.ChangeAction == ChangeAction.ExchangeCreateSharedMailbox ? "PostvakAanmaken" : "GroepAanmaken";
                p["PrimarySmtpAddress"] = w.Target;
                Take(w, p, "Name", "DisplayName", "Alias");
                if (w.ChangeAction == ChangeAction.ExchangeCreateGroup)
                {
                    Take(w, p, "Type");
                }
                break;
            default:
                throw new InvalidOperationException($"Actie {w.ChangeAction} hoort niet bij Exchange.");
        }

        return new Command(w.Id.ToString(), op, p);
    }

    /// <summary>Takes over the given parameters when they are in the change (empty values too: they mean
    /// "clear").</summary>
    private static void Take(Change w, Dictionary<string, string> p, params string[] names)
    {
        foreach (var n in names)
        {
            if (w.Parameters.TryGetValue(n, out var v))
            {
                p[n] = v;
            }
        }
    }

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
    {
        var commands = new List<Command>();
        var output = new Dictionary<Guid, ChangeResult>();
        foreach (var w in changes)
        {
            try
            {
                commands.Add(ToCommand(w));
            }
            catch (Exception ex)
            {
                output[w.Id] = ChangeResult.Error(w, ex.Message);
            }
        }

        if (commands.Count > 0)
        {
            var results = await RunScriptAsync(commands, cancel);
            foreach (var w in changes.Where(w => !output.ContainsKey(w.Id)))
            {
                var r = results.FirstOrDefault(x => x.Id == w.Id.ToString());
                output[w.Id] = r is null
                    ? ChangeResult.Error(w, "Geen resultaat van Exchange.")
                    : r.Status == "Gelukt" ? ChangeResult.Ok(w, r.Message) : ChangeResult.Error(w, r.Message);
            }
        }

        return changes.Select(w => output[w.Id]).ToList();
    }

    public async Task<IReadOnlyList<Room>> ReadRoomsAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report("Exchange: aanmelden en rooms lezen...");
        var r = (await RunScriptAsync(new[] { new Command("rooms", "LeesRooms", new()) }, cancel)).Single();
        if (r.Status != "Gelukt")
        {
            throw new InvalidOperationException(r.Message);
        }

        var rooms = r.Data is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(ToRoom).ToList() : new List<Room>();
        progress?.Report($"Exchange: {rooms.Count} rooms gelezen.");
        return rooms;
    }

    public async Task<ExchangeData> ReadMailboxesAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report("Exchange: postvakken, rechten en groepen lezen (dit kan enkele minuten duren)...");
        var r = (await RunScriptAsync(new[] { new Command("postvakken", "LeesPostvakken", new()) }, cancel)).Single();
        if (r.Status != "Gelukt" || r.Data is not { ValueKind: JsonValueKind.Object } d)
        {
            throw new InvalidOperationException(r.Status == "Gelukt" ? "Exchange gaf geen postvakken terug." : r.Message);
        }

        var data = ToExchangeData(d);
        progress?.Report($"Exchange: {data.Mailboxes.Count} postvakken, {data.Permissions.Count} rechten en {data.Groups.Count} groepen gelezen.");
        return data;
    }

    public async Task<ExchangeReading> ReadAllAsync(IProgress<string>? progress, CancellationToken cancel)
    {
        progress?.Report("Exchange: aanmelden, rooms, postvakken, rechten en groepen lezen (dit kan enkele minuten duren)...");
        var results = await RunScriptAsync(new[] { new Command("rooms", "LeesRooms", new()), new Command("postvakken", "LeesPostvakken", new()) }, cancel);
        var r = results.FirstOrDefault(x => x.Id == "rooms");
        var p = results.FirstOrDefault(x => x.Id == "postvakken");

        IReadOnlyList<Room>? rooms = r is { Status: "Gelukt" }
            ? r.Data is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray().Select(ToRoom).ToList() : new List<Room>()
            : null;
        ExchangeData? mailboxes = p is { Status: "Gelukt", Data: { ValueKind: JsonValueKind.Object } d } ? ToExchangeData(d) : null;
        var reading = new ExchangeReading(
            rooms, rooms is null ? r?.Message ?? "Geen resultaat van Exchange." : null,
            mailboxes, mailboxes is null ? (p is { Status: "Gelukt" } ? "Exchange gaf geen postvakken terug." : p?.Message ?? "Geen resultaat van Exchange.") : null);
        progress?.Report($"Exchange: {rooms?.Count ?? 0} rooms, {mailboxes?.Mailboxes.Count ?? 0} postvakken en {mailboxes?.Groups.Count ?? 0} groepen gelezen.");
        return reading;
    }

    public async Task<ExchangeObjects> ReadObjectsAsync(IReadOnlyList<string> addresses, CancellationToken cancel)
    {
        var list = addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return new ExchangeObjects(Array.Empty<Room>(), new ExchangeData(Array.Empty<ExoGroup>(), Array.Empty<Mailbox>(), Array.Empty<MailboxPermission>()), Array.Empty<string>());
        }

        var r = (await RunScriptAsync(new[] { new Command("objecten", "LeesObjecten", new() { ["Adressen"] = string.Join('|', list) }) }, cancel)).Single();
        if (r.Status != "Gelukt" || r.Data is not { ValueKind: JsonValueKind.Object } d)
        {
            throw new InvalidOperationException(r.Status == "Gelukt" ? "Exchange gaf geen gegevens terug." : r.Message);
        }

        var rooms = d.TryGetProperty("Rooms", out var rl) && rl.ValueKind == JsonValueKind.Array ? rl.EnumerateArray().Select(ToRoom).ToList() : new List<Room>();
        var route = d.TryGetProperty("NietGevonden", out var w) && w.ValueKind == JsonValueKind.Array
            ? w.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : new List<string>();
        return new ExchangeObjects(rooms, ToExchangeData(d), route);
    }

    internal static ExchangeData ToExchangeData(JsonElement d)
    {
        IEnumerable<JsonElement> Items(JsonElement e, string name)
            => e.TryGetProperty(name, out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray() : Enumerable.Empty<JsonElement>();

        var mailboxes = Items(d, "Postvakken").Select(e => new Mailbox
        {
            Type = S(e, "Type"),
            DisplayName = S(e, "DisplayName"),
            Alias = S(e, "Alias"),
            Smtp = S(e, "Smtp"),
            Hidden = B(e, "Hidden"),
            IsDirSynced = B(e, "IsDirSynced"),
            WhenCreated = S(e, "WhenCreated"),
            ForwardingTo = S(e, "DoorsturenNaar"),
            Forwarding = S(e, "DoorsturenNaar"),
            SaveForwardingAnd = S(e, "DoorsturenEnBewaren").Length > 0 ? B(e, "DoorsturenEnBewaren") : null,
            AutoReply = S(e, "AutoAntwoord"),
            AutoReplyInternal = S(e, "AutoAntwoordIntern"),
            AutoReplyExternal = S(e, "AutoAntwoordExtern"),
        }).Where(p => p.Smtp.Length > 0).ToList();
        var permissions = Items(d, "Rechten")
            .SelectMany(e => S(e, "Recht").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(permission => new MailboxPermission(S(e, "Postvak"), permission, S(e, "Trustee"))))
            .Where(r => r.Mailbox.Length > 0 && r.Trustee.Length > 0)
            .ToList();
        var groups = Items(d, "Groepen").Select(e =>
        {
            var members = Items(e, "Leden").Select(l => new ExoMember(S(l, "Smtp"), S(l, "Naam"))).ToList();
            return new ExoGroup
            {
                Type = S(e, "Type"),
                DisplayName = S(e, "DisplayName"),
                Name = S(e, "Name"),
                Alias = S(e, "Alias"),
                Smtp = S(e, "Smtp"),
                IsDirSynced = B(e, "IsDirSynced"),
                MemberJoinRestriction = S(e, "MemberJoinRestriction"),
                ManagedBy = S(e, "ManagedBy").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Members = members,
                CountMembers = members.Count,
            };
        }).ToList();
        return new ExchangeData(groups, mailboxes, permissions);
    }

    internal async Task<IReadOnlyList<Result>> RunScriptAsync(IReadOnlyList<Command> commands, CancellationToken cancel)
    {
        Directory.CreateDirectory(_workFolder);
        var script = Path.Combine(_workFolder, "Exchange.ps1");
        await using (var source = typeof(ExchangeConnection).Assembly.GetManifestResourceStream("Exchange.ps1")
                                ?? throw new InvalidOperationException("Exchange.ps1 ontbreekt in de app."))
        await using (var target = File.Create(script))
        {
            await source.CopyToAsync(target, cancel);
        }

        var id = Guid.NewGuid().ToString("N");
        var input = Path.Combine(_workFolder, $"opdrachten-{id}.json");
        var output = Path.Combine(_workFolder, $"resultaten-{id}.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(commands.Select(o => new { id = o.Id, op = o.Op, @params = o.Params })), new UTF8Encoding(false), cancel);
        try
        {
            var arguments = new List<string> { "-Opdrachten", input, "-Uitvoer", output };
            var environment = new Dictionary<string, string>();
            var token = _token is null ? null : await _token(cancel);
            var upn = token is null ? _managementUpn : _managementUpn ?? _signedInAs?.Invoke();
            if (token is not null)
            {
                environment[TokenVariable] = token;
            }

            if (upn is not null)
            {
                arguments.AddRange(new[] { "-Upn", upn });
            }

            var (code, errors) = await _starter.StartAsync(script, arguments, environment, cancel);
            if (!File.Exists(output))
            {
                var reason = string.IsNullOrWhiteSpace(errors) ? $"PowerShell stopte met code {code}." : errors.Trim();
                throw new InvalidOperationException("Exchange-script gaf geen resultaat: " + reason);
            }

            return ReadResults(await File.ReadAllTextAsync(output, cancel));
        }
        finally
        {
            File.Delete(input);
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    internal static IReadOnlyList<Result> ReadResults(string json)
    {
        using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        return list.Select(e => new Result(
            S(e, "id"), S(e, "status"), S(e, "melding"),
            e.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null)).ToList();
    }

    internal static Room ToRoom(JsonElement e) => new()
    {
        Smtp = S(e, "Smtp"),
        DisplayName = S(e, "DisplayName"),
        Alias = S(e, "Alias"),
        Building = S(e, "Gebouw"),
        Floor = S(e, "Verdieping"),
        FloorLabel = S(e, "VerdiepingLabel"),
        Capacity = I(e, "Capaciteit"),
        Hidden = B(e, "Hidden"),
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
        CalendarPermissions = S(e, "AgendaRechten"),
    };

    private static string S(JsonElement e, string name)
        => e.TryGetProperty(name, out var w) ? w.ValueKind switch
        {
            JsonValueKind.String => w.GetString() ?? string.Empty,
            JsonValueKind.Number => w.GetRawText(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => string.Empty,
        } : string.Empty;

    private static bool B(JsonElement e, string name) => string.Equals(S(e, name), "True", StringComparison.OrdinalIgnoreCase);

    private static int? I(JsonElement e, string name) => int.TryParse(S(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
