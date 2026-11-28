using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Moss365.Core.Changes;
using Moss365.Core.Localization;
using Moss365.Core.Setup;

namespace Moss365.Connections.Google;

/// <summary>One run of gcloud: the exit code, what it wrote and its errors.</summary>
public sealed record GcloudRun(int Exit, string Output, string Error);

/// <summary>
/// The wizard for Google (U41) through the Google Cloud CLI on this PC, as the wizard for Microsoft 365 uses the sign-in of
/// Microsoft Graph PowerShell: gcloud signs in with its own client in the browser, then reads and changes only the own
/// project. Fixed commands only, with arguments checked first (gcloud on Windows is a .cmd file); the key of the service
/// account goes through a file in a folder of its own in the temporary folder, is read back and the folder is removed
/// at once. Nothing of the output goes to the technical log.
/// </summary>
public sealed class GcloudSetup(Func<IReadOnlyList<string>, TimeSpan, CancellationToken, Task<GcloudRun>> run) : IGoogleSetupConnection
{
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(2);

    // No character that cmd.exe gives a meaning to
    private static readonly Regex SafeArgument = new(@"^[\p{L}\p{N}@._:=/\\ ()-]+$", RegexOptions.CultureInvariant);

    public TargetSystem TargetSystem => TargetSystem.Google;

    public async Task<GcloudState> ReadAsync(string projectId, CancellationToken cancel)
    {
        GcloudRun version;
        try
        {
            version = await RunAsync(cancel, Short, "version", "--format=json");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            return new GcloudState(false, null, null, null, null);
        }

        if (version.Exit != 0)
        {
            return new GcloudState(false, null, null, null, null);
        }

        var account = Lines((await RunAsync(cancel, Short, "auth", "list", "--filter=status:ACTIVE", "--format=value(account)")).Output).FirstOrDefault();
        if (account is null || !GoogleSetup.ValidProjectId(projectId))
        {
            return new GcloudState(true, account, null, null, null);
        }

        var project = await RunAsync(cancel, Short, "projects", "describe", projectId, "--format=value(projectId)");
        if (project.Exit != 0)
        {
            // Not there, or not visible for this account: creating it says which
            return new GcloudState(true, account, false, null, null, Denied(project) ? Texts.T["google-setup.project.denied"] : null);
        }

        var services = await RunAsync(cancel, Short, "services", "list", "--enabled", $"--project={projectId}", "--format=value(config.name)");
        var sa = await RunAsync(cancel, Short, "iam", "service-accounts", "describe", GoogleSetup.ServiceAccountEmail(projectId), $"--project={projectId}", "--format=value(oauth2ClientId)");
        return new GcloudState(true, account, true, services.Exit == 0 ? Lines(services.Output) : null,
            sa.Exit == 0 ? Lines(sa.Output).FirstOrDefault() : null, services.Exit == 0 ? null : Message(services));
    }

    public async Task<string?> SignInAsync(CancellationToken cancel)
    {
        var login = await RunAsync(cancel, TimeSpan.FromMinutes(5), "auth", "login", "--brief");
        if (login.Exit != 0)
        {
            throw new InvalidOperationException(Message(login));
        }

        return Lines((await RunAsync(cancel, Short, "auth", "list", "--filter=status:ACTIVE", "--format=value(account)")).Output).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
    {
        var results = new List<ChangeResult>();
        foreach (var w in changes)
        {
            try
            {
                results.Add(await ExecuteAsync(w, cancel));
            }
            catch (InvalidOperationException ex)
            {
                results.Add(ChangeResult.Error(w, ex.Message));
            }
        }

        return results;
    }

    private async Task<ChangeResult> ExecuteAsync(Change w, CancellationToken cancel)
    {
        var project = w.Target;
        if (!GoogleSetup.ValidProjectId(project))
        {
            return ChangeResult.Error(w, Texts.T["google-setup.project.invalid"]);
        }

        var account = GoogleSetup.ServiceAccountEmail(project);
        switch (w.ChangeAction)
        {
            case ChangeAction.GcloudCreateProject:
                return Result(w, await RunAsync(cancel, Short, "projects", "create", project, $"--name={GoogleSetup.ProjectName}"));

            case ChangeAction.GcloudEnableServices:
            {
                var apis = w.P("apis").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (apis.Length == 0 || apis.Any(a => !GoogleParts.Apis.Contains(a)))
                {
                    return ChangeResult.Error(w, Texts.T["google-setup.apis.unknown"]);
                }

                return Result(w, await RunAsync(cancel, TimeSpan.FromMinutes(5), new[] { "services", "enable" }.Concat(apis).Append($"--project={project}").ToArray()));
            }

            case ChangeAction.GcloudCreateServiceAccount:
                return Result(w, await RunAsync(cancel, Short, "iam", "service-accounts", "create", GoogleSetup.ServiceAccountName,
                    "--display-name=Moss365 Gmail settings", $"--project={project}"));

            case ChangeAction.GcloudCreateServiceAccountKey:
            {
                // A folder of its own, only for this moment: the key is read back and the folder removed at once
                var folder = Path.Combine(Path.GetTempPath(), "moss365-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var file = Path.Combine(folder, "key.json");
                    var made = await RunAsync(cancel, Short, "iam", "service-accounts", "keys", "create", file, $"--iam-account={account}", $"--project={project}");
                    if (made.Exit != 0 || !File.Exists(file))
                    {
                        return ChangeResult.Error(w, Texts.T.Format("google-setup.key.refused", Message(made)));
                    }

                    w.Secret = await File.ReadAllTextAsync(file, cancel);
                    return ChangeResult.Ok(w);
                }
                finally
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            default:
                throw new InvalidOperationException(Texts.T.Format("code.exchange-connection.action-does-not-belong-to", w.ChangeAction));
        }
    }

    private Task<GcloudRun> RunAsync(CancellationToken cancel, TimeSpan timeout, params string[] arguments)
    {
        if (arguments.Any(a => !SafeArgument.IsMatch(a)))
        {
            throw new InvalidOperationException(Texts.T["google-setup.unsafe"]);
        }

        return run(arguments, timeout, cancel);
    }

    private static ChangeResult Result(Change w, GcloudRun r) => r.Exit == 0
        ? ChangeResult.Ok(w)
        : r.Error.Contains("already exists", StringComparison.OrdinalIgnoreCase) || r.Error.Contains("ALREADY_EXISTS", StringComparison.Ordinal)
            ? ChangeResult.Ok(w, Texts.T["google-setup.already"])
            : ChangeResult.Error(w, Message(r));

    private static bool Denied(GcloudRun r) => r.Error.Contains("PERMISSION_DENIED", StringComparison.Ordinal) || r.Error.Contains("does not have permission", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last line gcloud wrote as an error (its own words), or the exit code.</summary>
    private static string Message(GcloudRun r)
        => Lines(r.Error).LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? Lines(r.Error).LastOrDefault() ?? $"gcloud {r.Exit}";

    private static IReadOnlyList<string> Lines(string text)
        => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    /// <summary>Runs gcloud of this PC (gcloud.cmd on Windows), without a window, at most the given time.</summary>
    public static async Task<GcloudRun> ProcessAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "gcloud.cmd" : "gcloud")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        // gcloud asks nothing on the console: a question would wait for ever
        start.Environment["CLOUDSDK_CORE_DISABLE_PROMPTS"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("gcloud");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancel.ThrowIfCancellationRequested();
            return new GcloudRun(-1, string.Empty, Texts.T["google-setup.timeout"]);
        }

        return new GcloudRun(process.ExitCode, await output, await error);
    }
}
