using Moss365.Core.Changes;
using Moss365.Core.Google;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.UI.Services;

/// <summary>G2: what the screens of Google Workspace ask of the state.</summary>
public sealed partial class ManagementState
{
    /// <summary>Where the Google users get their fields (the source of truth, U33).</summary>
    public GoogleSource GoogleUserSource => GooglePlans.Source(Systems, Data, SourceSettings);

    /// <summary>The platform the Google groups follow, or null when Google keeps them itself.</summary>
    public Platform? GoogleGroupsFollow => GoogleGroupPlans.FollowsFrom(Systems, Data, SourceSettings);

    /// <summary>The domains of Google Workspace of this tenant, the domain of the settings first.</summary>
    public IReadOnlyList<string> GoogleDomains => GooglePlans.Domains(Data, EffectiveSettings.GoogleDomain);

    /// <summary>The parts of Google that are on (licences, group settings, alerts, Gmail settings).</summary>
    public IReadOnlyList<string> GoogleParts => EffectiveSettings.GoogleParts;

    /// <summary>The service account of the part Gmail settings (U34), or null; never its key.</summary>
    public GmailKeyInfo? GmailKey => HasGoogle ? _factory?.GmailKey(EffectiveSettings) : null;

    /// <summary>Keeps the key of the service account (the JSON file of Google), encrypted; returns why not, or null. The
    /// access log notes that it was set, never the key.</summary>
    public string? SaveGmailKey(string json)
    {
        if (IsHistoric || !HasGoogle || _factory is null)
        {
            return Texts.T["google.fill-in"];
        }

        var error = _factory.SaveGmailKey(EffectiveSettings, json);
        if (error is null)
        {
            Access("Gmail-serviceaccount ingesteld", GmailKey?.ClientEmail ?? string.Empty);
            Changed?.Invoke();
        }

        return error;
    }

    public void RemoveGmailKey()
    {
        if (_factory?.SaveGmailKey(EffectiveSettings, null) is null)
        {
            Access("Gmail-serviceaccount verwijderd", string.Empty);
            Changed?.Invoke();
        }
    }

    /// <summary>The Gmail settings of a user, read live through the service account (the access log notes who read
    /// whose settings); null with a note when that is not possible now.</summary>
    public async Task<(GmailSettings? Settings, string? Note)> ReadGmailAsync(GoogleUser user, CancellationToken cancel = default)
    {
        if (GmailPlans.PartOff(GoogleParts) is { } off)
        {
            return (null, off);
        }

        if (IsHistoric || !HasGoogle || _factory?.Google(EffectiveSettings) is not { } google)
        {
            return (null, Texts.T["google.fill-in"]);
        }

        try
        {
            var settings = await google.ReadGmailAsync(user.PrimaryEmail, cancel);
            if (settings is not null)
            {
                Access("Gmail-instellingen bekeken", user.PrimaryEmail);
            }

            return (settings, settings is null ? Texts.T["gmail.no-service-account"] : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The direct members of a Google group, read live when the page of the group asks; null with a note when
    /// that is not possible now.</summary>
    public async Task<(IReadOnlyList<GoogleMember>? Members, string? Note)> ReadGoogleMembersAsync(GoogleGroup group, CancellationToken cancel = default)
    {
        if (IsHistoric || !HasGoogle || _factory?.Google(EffectiveSettings) is not { } google)
        {
            return (null, Texts.T["google.fill-in"]);
        }

        try
        {
            var members = await google.ReadMembersAsync(group.Email, cancel);
            return (members, members is null ? Texts.T["google.not-available"] : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }
}
