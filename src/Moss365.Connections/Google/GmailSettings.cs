using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Google;
using Moss365.Core.Localization;

namespace Moss365.Connections.Google;

/// <summary>
/// The Gmail settings of someone else (G2, part Gmail settings, U34): read and changed through the Gmail API with a
/// token of the service account for that user. Forwarding to a new address first adds it as a forwarding address;
/// outside the domain Google asks the receiver to confirm it before forwarding starts.
/// </summary>
public sealed partial class GoogleConnection
{
    private const string Gmail = "https://gmail.googleapis.com/gmail/v1/users";

    public async Task<GmailSettings?> ReadGmailAsync(string user, CancellationToken cancel)
    {
        if (GmailToken is not { } tokens)
        {
            return null;
        }

        Task<string> Token(CancellationToken c) => tokens(user, c);
        var settings = $"{Gmail}/{Key(user)}/settings";
        var forwarding = await SendAsync(HttpMethod.Get, $"{settings}/autoForwarding", null, cancel, Token);
        var vacation = await SendAsync(HttpMethod.Get, $"{settings}/vacation", null, cancel, Token);
        var delegates = await SendAsync(HttpMethod.Get, $"{settings}/delegates", null, cancel, Token);
        var filters = await SendAsync(HttpMethod.Get, $"{settings}/filters", null, cancel, Token);
        var addresses = await SendAsync(HttpMethod.Get, $"{settings}/forwardingAddresses", null, cancel, Token);
        var sendAs = await SendAsync(HttpMethod.Get, $"{settings}/sendAs/{Key(user)}", null, cancel, Token);
        static long? Ms(JsonElement e, string name) => long.TryParse(S(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        return new GmailSettings(
            new GmailForwarding(B(forwarding, "enabled"), S(forwarding, "emailAddress"), S(forwarding, "disposition")),
            new GmailVacation(B(vacation, "enableAutoReply"), S(vacation, "responseSubject"),
                S(vacation, "responseBodyPlainText") is { Length: > 0 } plain ? plain : S(vacation, "responseBodyHtml"), B(vacation, "restrictToDomain"),
                Ms(vacation, "startTime"), Ms(vacation, "endTime")),
            Items(delegates, "delegates").Select(d => S(d, "delegateEmail")).Where(d => d.Length > 0).ToList(),
            Items(filters, "filter").Select(f => GmailPlans.Filter(S(f, "id"), f.TryGetProperty("criteria", out var c) ? c : default, f.TryGetProperty("action", out var a) ? a : default)).ToList(),
            S(sendAs, "signature"),
            Items(addresses, "forwardingAddresses").Where(a => S(a, "verificationStatus") == "accepted").Select(a => S(a, "forwardingEmail")).ToList());
    }

    private async partial Task<ChangeResult> GmailWriteAsync(Change w, CancellationToken cancel)
    {
        var tokens = GmailToken!;
        Task<string> Token(CancellationToken c) => tokens(w.Target, c);
        Task<JsonElement> Send(HttpMethod method, string path, object? body) => SendAsync(method, $"{Gmail}/{Key(w.Target)}/settings/{path}", body, cancel, Token);
        switch (w.ChangeAction)
        {
            case ChangeAction.GmailSetForwarding:
            {
                var to = w.P("adres");
                if (to.Length == 0)
                {
                    await Send(HttpMethod.Put, "autoForwarding", new JsonObject { ["enabled"] = false });
                    return ChangeResult.Ok(w);
                }

                // The address must be a forwarding address first; outside the domain the receiver confirms it
                try
                {
                    await Send(HttpMethod.Post, "forwardingAddresses", new JsonObject { ["forwardingEmail"] = to });
                }
                catch (GoogleError ex) when (ex.Status == HttpStatusCode.Conflict)
                {
                }

                var address = await Send(HttpMethod.Get, $"forwardingAddresses/{Key(to)}", null);
                if (S(address, "verificationStatus") != "accepted")
                {
                    return ChangeResult.Error(w, Texts.T.Format("gmail.forwarding.pending", to));
                }

                await Send(HttpMethod.Put, "autoForwarding", new JsonObject
                {
                    ["enabled"] = true, ["emailAddress"] = to, ["disposition"] = w.P("disposition") is { Length: > 0 } d ? d : "leaveInInbox",
                });
                return ChangeResult.Ok(w);
            }

            case ChangeAction.GmailSetVacation:
            {
                var body = new JsonObject
                {
                    ["enableAutoReply"] = w.P("aan") == "True",
                    ["responseSubject"] = w.P("onderwerp"),
                    ["responseBodyPlainText"] = w.P("tekst"),
                    ["restrictToDomain"] = w.P("alleenIntern") == "True",
                };
                foreach (var (parameter, field) in new[] { ("start", "startTime"), ("einde", "endTime") })
                {
                    if (long.TryParse(w.P(parameter), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                    {
                        body[field] = ms.ToString(CultureInfo.InvariantCulture);
                    }
                }

                await Send(HttpMethod.Put, "vacation", body);
                return ChangeResult.Ok(w);
            }

            case ChangeAction.GmailAddDelegate:
                return await AlreadyAsync(w, () => Send(HttpMethod.Post, "delegates", new JsonObject { ["delegateEmail"] = w.P("gemachtigde") }),
                    HttpStatusCode.Conflict, "gmail.delegate.already-result", cancel);

            case ChangeAction.GmailRemoveDelegate:
                return await AlreadyAsync(w, () => Send(HttpMethod.Delete, $"delegates/{Key(w.P("gemachtigde"))}", null),
                    HttpStatusCode.NotFound, "gmail.delegate.already-gone", cancel);

            case ChangeAction.GmailSetSignature:
                await Send(HttpMethod.Patch, $"sendAs/{Key(w.Target)}", new JsonObject { ["signature"] = w.P("handtekening") });
                return ChangeResult.Ok(w);

            case ChangeAction.GmailAddFilter:
            {
                var made = await Send(HttpMethod.Post, "filters", JsonNode.Parse(w.P("json")));
                return ChangeResult.Ok(w, S(made, "id"));
            }

            case ChangeAction.GmailRemoveFilter:
                return await AlreadyAsync(w, () => Send(HttpMethod.Delete, $"filters/{Key(w.P("id"))}", null),
                    HttpStatusCode.NotFound, "gmail.filter.already-gone", cancel);

            default:
                throw new InvalidOperationException(Texts.T.Format("code.exchange-connection.action-does-not-belong-to", w.ChangeAction));
        }
    }
}
