using System.Text.RegularExpressions;
using Moss365.Core.Changes;
using Moss365.Core.Google;
using Moss365.Core.Localization;

namespace Moss365.Core.Setup;

/// <summary>What gcloud on this PC says about the own Google Cloud project (U41); null fields were not read.</summary>
public sealed record GcloudState(
    bool Installed,
    string? Account,
    bool? ProjectExists,
    IReadOnlyList<string>? EnabledApis,
    string? ServiceAccountClientId,
    string? Error = null);

/// <summary>
/// The Google Cloud CLI (gcloud) on this PC, for the wizard (U41): the Google counterpart of the sign-in of Microsoft
/// Graph PowerShell for Microsoft 365. It reads the project and runs the steps of the plan that Google allows by command:
/// the project, its APIs, the service account of the part Gmail settings and its key (which comes back in
/// <see cref="Change.Secret"/>, never logged).
/// </summary>
public interface IGoogleSetupConnection : IExecutor
{
    Task<GcloudState> ReadAsync(string projectId, CancellationToken cancel);

    /// <summary>Signs in to gcloud in the browser (gcloud auth login); the account that signed in.</summary>
    Task<string?> SignInAsync(CancellationToken cancel);
}

/// <summary>What the wizard knows of the Google side of a tenant: gcloud, the settings, the parts, the kept key and the
/// last check of the project.</summary>
public sealed record GoogleSetupInput(
    string ProjectId,
    GcloudState? Gcloud,
    bool DomainFilled,
    bool ClientFilled,
    IReadOnlyCollection<string> Parts,
    GmailKeyInfo? Key,
    IReadOnlyList<GoogleCheck>? Checks);

/// <summary>
/// The wizard for Google Workspace (U41), on the page Inrichten next to that of Microsoft 365: it compares what gcloud and
/// the check of the project find with what Moss365 needs, and plans only the difference that Google allows by command
/// (the project, the APIs, the service account and its key). The consent screen, the OAuth client and the domain-wide
/// delegation have no command at Google: they are explained step by step and checked afterwards.
/// </summary>
public static class GoogleSetup
{
    public const string ServiceAccountName = "moss365-gmail";

    public const string ProjectName = "Moss365";

    public const string DelegationUrl = "https://admin.google.com/ac/owl/domainwidedelegation";

    public const string InstallUrl = "https://cloud.google.com/sdk/docs/install";

    private static readonly Regex ProjectPattern = new("^[a-z][a-z0-9-]{4,28}[a-z0-9]$", RegexOptions.CultureInvariant);

    public static bool ValidProjectId(string id) => ProjectPattern.IsMatch(id);

    public static string ServiceAccountEmail(string projectId) => $"{ServiceAccountName}@{projectId}.iam.gserviceaccount.com";

    public static string ConsentUrl(string projectId) => $"https://console.cloud.google.com/apis/credentials/consent?project={Uri.EscapeDataString(projectId)}";

    public static string ClientUrl(string projectId) => $"https://console.cloud.google.com/apis/credentials/oauthclient?project={Uri.EscapeDataString(projectId)}";

    /// <summary>The APIs this tenant needs: the Admin SDK always, and the API of every part that is on.</summary>
    public static IReadOnlyList<string> NeededApis(IReadOnlyCollection<string> parts)
        => new[] { "admin.googleapis.com" }.Concat(GoogleParts.All.Where(p => GoogleParts.IsOn(parts, p.Key)).Select(p => p.Api)).Distinct().ToList();

    public static SetupAssessment Assess(GoogleSetupInput i)
    {
        var items = new List<SetupItem>();
        var plan = new List<Change>();
        string Step(string key) => Texts.T["google-setup.step." + key];
        void Item(string step, string name, SetupStatus status, string explanation) => items.Add(new SetupItem(Step(step), Texts.T[name], status, explanation));

        // 1. gcloud on this PC
        var g = i.Gcloud;
        if (g is null)
        {
            Item("cli", "google-setup.cli", SetupStatus.Unknown, Texts.T["google-setup.not-checked"]);
        }
        else if (!g.Installed)
        {
            Item("cli", "google-setup.cli", SetupStatus.Missing, Texts.T.Format("google-setup.cli.missing", InstallUrl));
        }
        else if (g.Account is null)
        {
            Item("cli", "google-setup.cli", SetupStatus.Missing, Texts.T["google-setup.cli.sign-in"]);
        }
        else
        {
            Item("cli", "google-setup.cli", SetupStatus.InOrder, Texts.T.Format("google-setup.cli.ok", g.Account));
        }

        // 2. The project and its APIs: what gcloud can do becomes a plan
        var project = i.ProjectId.Trim();
        var canRun = g is { Installed: true, Account: not null };
        Change? created = null;
        if (!ValidProjectId(project))
        {
            Item("project", "google-setup.project", SetupStatus.Missing, Texts.T["google-setup.project.invalid"]);
        }
        else if (g?.ProjectExists is null)
        {
            Item("project", "google-setup.project", SetupStatus.Unknown, g?.Error ?? Texts.T["google-setup.not-checked"]);
        }
        else if (g.ProjectExists == true)
        {
            Item("project", "google-setup.project", SetupStatus.InOrder, project);
        }
        else
        {
            Item("project", "google-setup.project", SetupStatus.Missing, Texts.T.Format("google-setup.project.missing", project));
            if (canRun)
            {
                created = Step0(ChangeAction.GcloudCreateProject, project, Texts.T.Format("google-setup.plan.project", project), new() { ["naam"] = ProjectName });
                plan.Add(created);
            }
        }

        var needed = NeededApis(i.Parts);
        if (ValidProjectId(project) && (g?.EnabledApis is not null || created is not null))
        {
            var missing = needed.Where(a => created is not null || !g!.EnabledApis!.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
            Item("project", "google-setup.apis", missing.Count == 0 ? SetupStatus.InOrder : SetupStatus.Missing,
                missing.Count == 0 ? string.Join(", ", needed) : Texts.T.Format("google-setup.apis.missing", string.Join(", ", missing)));
            if (missing.Count > 0 && canRun)
            {
                plan.Add(Step0(ChangeAction.GcloudEnableServices, project, Texts.T.Format("google-setup.plan.apis", string.Join(", ", missing)),
                    new() { ["apis"] = string.Join(' ', missing) }, created?.Id));
            }
        }
        else if (ValidProjectId(project))
        {
            Item("project", "google-setup.apis", SetupStatus.Unknown, Texts.T["google-setup.not-checked"]);
        }

        // 3. The consent screen and the OAuth client: by hand in the console, then filled in and checked here
        var signIn = i.Checks?.FirstOrDefault(c => c.Name == Texts.T["google.check.sign-in"]);
        var link = ValidProjectId(project) ? project : string.Empty;
        Item("client", "google-setup.consent", signIn?.Ok == true ? SetupStatus.InOrder : i.ClientFilled ? SetupStatus.Unknown : SetupStatus.Missing,
            signIn?.Ok == true ? Texts.T["google-setup.consent.ok"] : Texts.T.Format("google-setup.consent.todo", link.Length > 0 ? ConsentUrl(link) : "console.cloud.google.com"));
        Item("client", "google-setup.client", !i.ClientFilled || !i.DomainFilled ? SetupStatus.Missing : signIn?.Ok == true ? SetupStatus.InOrder : SetupStatus.Unknown,
            !i.ClientFilled || !i.DomainFilled
                ? Texts.T.Format("google-setup.client.todo", link.Length > 0 ? ClientUrl(link) : "console.cloud.google.com")
                : signIn?.Ok == true ? Texts.T["google-setup.client.ok"] : Texts.T["google-setup.check-now"]);

        // 4. Signing in, the admin role and every API of the project, from the check of the project
        if (i.Checks is null || i.Checks.Count == 0)
        {
            Item("sign-in", "google-setup.sign-in", SetupStatus.Unknown, i.ClientFilled ? Texts.T["google-setup.check-now"] : Texts.T["google-setup.client-first"]);
        }
        else
        {
            foreach (var c in i.Checks.Where(c => c.Name != Texts.T["gmail.check"]))
            {
                items.Add(new SetupItem(Step("sign-in"), c.Name, c.Ok ? SetupStatus.InOrder : SetupStatus.Attention, c.Message ?? string.Empty));
            }
        }

        // 5. The part Gmail settings: the service account and its key by gcloud, the delegation by hand
        if (GoogleParts.IsOn(i.Parts, GoogleParts.GmailSettings) && ValidProjectId(project))
        {
            var account = ServiceAccountEmail(project);
            Change? madeAccount = null;
            if (g?.ServiceAccountClientId is { Length: > 0 })
            {
                Item("gmail", "google-setup.service-account", SetupStatus.InOrder, account);
            }
            else if (g is null || g.ProjectExists is null)
            {
                Item("gmail", "google-setup.service-account", SetupStatus.Unknown, Texts.T["google-setup.not-checked"]);
            }
            else
            {
                Item("gmail", "google-setup.service-account", SetupStatus.Missing, Texts.T.Format("google-setup.service-account.missing", account));
                if (canRun)
                {
                    madeAccount = Step0(ChangeAction.GcloudCreateServiceAccount, project, Texts.T.Format("google-setup.plan.service-account", account), new(), created?.Id);
                    plan.Add(madeAccount);
                }
            }

            var keyOk = i.Key is { } key && string.Equals(key.ClientEmail, account, StringComparison.OrdinalIgnoreCase);
            Item("gmail", "google-setup.key", keyOk ? SetupStatus.InOrder : i.Key is not null ? SetupStatus.Attention : SetupStatus.Missing,
                keyOk ? Texts.T.Format("google-setup.key.ok", i.Key!.KeyId) : i.Key is { } other ? Texts.T.Format("google-setup.key.other", other.ClientEmail) : Texts.T["google-setup.key.missing"]);
            if (!keyOk && canRun && (g?.ServiceAccountClientId is { Length: > 0 } || madeAccount is not null))
            {
                plan.Add(Step0(ChangeAction.GcloudCreateServiceAccountKey, project, Texts.T.Format("google-setup.plan.key", account), new(),
                    madeAccount?.Id, Texts.T["google-setup.plan.key-warning"]));
            }

            var delegation = i.Checks?.FirstOrDefault(c => c.Name == Texts.T["gmail.check"]);
            var clientId = keyOk ? i.Key!.ClientId : g?.ServiceAccountClientId ?? string.Empty;
            Item("gmail", "google-setup.delegation", delegation?.Ok == true ? SetupStatus.InOrder : delegation is null ? SetupStatus.Unknown : SetupStatus.Missing,
                delegation?.Ok == true
                    ? Texts.T["google-setup.delegation.ok"]
                    : Texts.T.Format("google-setup.delegation.todo", clientId.Length > 0 ? clientId : "?", string.Join(",", GoogleParts.GmailScopes), DelegationUrl));
        }

        return new SetupAssessment(items, plan, null);
    }

    private static Change Step0(ChangeAction action, string project, string description, Dictionary<string, string> parameters, Guid? after = null, string? warning = null) => new()
    {
        TargetSystem = TargetSystem.Google,
        ChangeAction = action,
        Target = project,
        TargetName = project,
        Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase),
        Description = description,
        DependentOf = after,
        Warning = warning,
    };
}
