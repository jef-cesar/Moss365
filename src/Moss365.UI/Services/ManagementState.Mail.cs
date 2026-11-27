using System.Text.Json;
using System.Text.Json.Serialization;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;
using Moss365.Core.Mail;
using Moss365.Core.Permissions;
using Moss365.Core.Storage;

namespace Moss365.UI.Services;

/// <summary>Where the mail lives (G0, U31): the default of the tenant, a different system per person, the MX check.</summary>
public sealed partial class ManagementState
{
    private static readonly JsonSerializerOptions MailJson = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }.WithLegacyNames();
    private MailLocationSettings? _mail;

    /// <summary>The settings of this tenant (document <c>postlocatie</c>).</summary>
    public MailLocationSettings MailSettings
    {
        get
        {
            if (_mail is null)
            {
                try
                {
                    _mail = Database.ReadDocument(MailLocations.Document) is { } json ? JsonSerializer.Deserialize<MailLocationSettings>(json, MailJson) : null;
                }
                catch (JsonException)
                {
                    _mail = null;
                }

                _mail ??= new MailLocationSettings();
            }

            return _mail;
        }
    }

    /// <summary>The default that counts for this tenant: the one chosen, else the proposal.</summary>
    public MailSystem MailDefault => MailLocations.DefaultOf(MailSettings, Systems);

    /// <summary>Where the mail of a person lives, and why.</summary>
    public (MailSystem System, MailLocationReason Reason) MailOf(Person person) => MailLocations.Of(person, MailSettings, Systems);

    /// <summary>The MX check can run: the Windows app (DNS of this PC), not on a date in the past.</summary>
    public bool CanCheckMail => _factory?.Dns is not null && !IsHistoric;

    /// <summary>The default of the tenant (role Settings); in the change log.</summary>
    public void SetMailDefault(MailSystem system)
    {
        RefuseMailChange();
        var before = MailSettings.Default;
        SaveMail(MailSettings with { Default = system });
        LogMail(Tenant.Id, Tenant.Name, Texts.T.Format("mail.log.default", MailLocations.Name(system)),
            new Dictionary<string, string> { ["standaard"] = system.ToString() }, new Dictionary<string, string> { ["standaard"] = before.ToString() });
    }

    /// <summary>A different system for one person, or back to the default with null (role Settings); in the change log.</summary>
    public void SetMailOf(Person person, MailSystem? system)
    {
        RefuseMailChange();
        var users = new Dictionary<string, MailSystem>(MailSettings.Users, StringComparer.OrdinalIgnoreCase);
        var before = users.TryGetValue(person.Key, out var b) ? b.ToString() : string.Empty;
        if (system is { } s && s != MailSystem.Unknown)
        {
            users[person.Key] = s;
        }
        else
        {
            users.Remove(person.Key);
        }

        SaveMail(MailSettings with { Users = users });
        LogMail(person.Key, person.Name,
            system is { } chosen ? Texts.T.Format("mail.log.person", MailLocations.Name(chosen)) : Texts.T["mail.log.person-default"],
            new Dictionary<string, string> { ["postlocatie"] = system?.ToString() ?? string.Empty }, new Dictionary<string, string> { ["postlocatie"] = before });
    }

    /// <summary>Checks the MX records of the domains of this tenant; an error, or null. Keeps the result.</summary>
    public async Task<string?> CheckMailRoutesAsync(CancellationToken cancel = default)
    {
        if (_factory?.Dns is not { } dns || IsHistoric)
        {
            return Texts.T["mail.check.not-here"];
        }

        var domains = MailLocations.Domains(Data, Tenant.Domain);
        if (domains.Count == 0)
        {
            return Texts.T["mail.check.no-domains"];
        }

        var routes = await MailLocations.CheckAsync(dns, domains, cancel);
        SaveMail(MailSettings with { Routes = routes, CheckedAt = DateTimeOffset.Now });
        // Only counts: domain names stay out of the technical log
        Log.Information("MX-controle: {Domeinen} domeinen, {Fouten} zonder antwoord", routes.Count, routes.Count(r => r.Error is not null));
        Changed?.Invoke();
        return null;
    }

    private void RefuseMailChange()
    {
        if (IsHistoric || !Can(Permission.Settings))
        {
            throw new InvalidOperationException(Texts.T["mail.not-allowed"]);
        }
    }

    private void SaveMail(MailLocationSettings settings)
    {
        Database.SaveDocument(MailLocations.Document, JsonSerializer.Serialize(settings, MailJson));
        _mail = settings;
    }

    private void LogMail(string target, string name, string description, IReadOnlyDictionary<string, string> parameters, IReadOnlyDictionary<string, string> previous)
    {
        ChangeLog.Write(new LogEntry(DateTimeOffset.Now, User, Texts.T["mail.title"], "Moss365", "MailLocation", target, name, description, parameters,
            nameof(ResultStatus.Succeeded), string.Empty, Previous: previous));
        Changed?.Invoke();
    }
}
