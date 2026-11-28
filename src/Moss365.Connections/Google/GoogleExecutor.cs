using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Moss365.Core.Changes;
using Moss365.Core.Inventory;
using Moss365.Core.Localization;

namespace Moss365.Connections.Google;

/// <summary>
/// Writing in Google Workspace (G2): every change of a plan through the Directory, Groups Settings and Licensing API,
/// and with the part Gmail settings through the Gmail API with the service account. Never deletes a user. What was
/// already so (a member that is a member already) is a success with that message, as for AD and Microsoft 365.
/// </summary>
public sealed partial class GoogleConnection
{
    /// <summary>The token of the service account for a user (part Gmail settings), or null without that part.</summary>
    internal Func<string, CancellationToken, Task<string>>? GmailToken { get; init; }

    public TargetSystem TargetSystem => TargetSystem.Google;

    public async Task<IReadOnlyList<ChangeResult>> ExecuteAsync(IReadOnlyList<Change> changes, CancellationToken cancel)
    {
        var results = new List<ChangeResult>();
        foreach (var w in changes)
        {
            try
            {
                results.Add(await ExecuteAsync(w, cancel));
            }
            catch (GoogleError ex)
            {
                results.Add(ChangeResult.Error(w, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                results.Add(ChangeResult.Error(w, ex.Message));
            }
        }

        return results;
    }

    private static string Key(string id) => Uri.EscapeDataString(id);

    private async Task<ChangeResult> ExecuteAsync(Change w, CancellationToken cancel)
    {
        var user = $"{Directory}/users/{Key(w.Target)}";
        switch (w.ChangeAction)
        {
            case ChangeAction.GoogleCreateUser:
            {
                if (string.IsNullOrEmpty(w.Secret))
                {
                    return ChangeResult.Error(w, Texts.T["code.graph-connection.no-initial-password-in-the"]);
                }

                var body = new JsonObject
                {
                    ["primaryEmail"] = w.Target,
                    ["name"] = new JsonObject { ["givenName"] = w.P("givenName"), ["familyName"] = w.P("familyName") },
                    ["password"] = w.Secret,
                    ["changePasswordAtNextLogin"] = true,
                    ["orgUnitPath"] = w.P("orgUnitPath") is { Length: > 0 } ou ? ou : "/",
                };
                if (w.P("employeeId") is { Length: > 0 } employee)
                {
                    body["externalIds"] = new JsonArray(new JsonObject { ["type"] = "organization", ["value"] = employee });
                }

                if (w.P("department") is { Length: > 0 } department)
                {
                    body["organizations"] = new JsonArray(new JsonObject { ["department"] = department, ["primary"] = true });
                }

                var made = await SendAsync(HttpMethod.Post, $"{Directory}/users", body, cancel);
                return ChangeResult.Ok(w, S(made, "id"));
            }

            case ChangeAction.GoogleUpdateUser:
                await SendAsync(HttpMethod.Patch, user, await UserChangesAsync(w, cancel), cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GoogleSetPassword:
                if (string.IsNullOrEmpty(w.Secret))
                {
                    return ChangeResult.Error(w, Texts.T["google.password.missing"]);
                }

                await SendAsync(HttpMethod.Patch, user, new JsonObject { ["password"] = w.Secret, ["changePasswordAtNextLogin"] = w.P("changeAtNextLogin") != "False" }, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GoogleSignOut:
                await SendAsync(HttpMethod.Post, $"{user}/signOut", null, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GoogleRevokeTokens:
            {
                var tokens = Items(await GetAsync($"{user}/tokens", cancel), "items").Select(t => S(t, "clientId")).Where(c => c.Length > 0).ToList();
                foreach (var client in tokens)
                {
                    await SendAsync(HttpMethod.Delete, $"{user}/tokens/{Key(client)}", null, cancel);
                }

                return ChangeResult.Ok(w, Texts.T.Format("google.tokens.revoked", tokens.Count));
            }

            case ChangeAction.GoogleAddAlias:
                return await AlreadyAsync(w, () => SendAsync(HttpMethod.Post, $"{user}/aliases", new JsonObject { ["alias"] = w.P("alias") }, cancel),
                    HttpStatusCode.Conflict, "google.alias.already", cancel);

            case ChangeAction.GoogleRemoveAlias:
                return await AlreadyAsync(w, () => SendAsync(HttpMethod.Delete, $"{user}/aliases/{Key(w.P("alias"))}", null, cancel),
                    HttpStatusCode.NotFound, "google.alias.already-gone", cancel);

            case ChangeAction.GoogleAddGroupMember:
                return await AlreadyAsync(w, () => SendAsync(HttpMethod.Post, $"{Directory}/groups/{Key(w.P("groep"))}/members",
                    new JsonObject { ["email"] = w.Target, ["role"] = w.P("rol") is { Length: > 0 } role ? role : "MEMBER" }, cancel), HttpStatusCode.Conflict, "result.already-member", cancel);

            case ChangeAction.GoogleRemoveGroupMember:
                return await AlreadyAsync(w, () => SendAsync(HttpMethod.Delete, $"{Directory}/groups/{Key(w.P("groep"))}/members/{Key(w.Target)}", null, cancel),
                    HttpStatusCode.NotFound, "google.member.already-gone", cancel);

            case ChangeAction.GoogleSetGroupMemberRole:
                await SendAsync(HttpMethod.Patch, $"{Directory}/groups/{Key(w.P("groep"))}/members/{Key(w.Target)}", new JsonObject { ["role"] = w.P("rol") }, cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GoogleUpdateGroup:
                await SendAsync(HttpMethod.Patch, $"{Directory}/groups/{Key(w.Target)}", Fields(w, "name", "description"), cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GoogleUpdateGroupSettings:
                await SendAsync(HttpMethod.Patch, $"{GroupsSettings}/{Key(w.Target)}?alt=json", Fields(w, "whoCanJoin", "whoCanPostMessage", "whoCanViewMembership", "allowExternalMembers"), cancel);
                return ChangeResult.Ok(w);

            case ChangeAction.GoogleUpdateResource:
            {
                var body = Fields(w, "resourceName", "floorName", "resourceDescription");
                if (w.Parameters.TryGetValue("capacity", out var capacity))
                {
                    body["capacity"] = int.TryParse(capacity, out var n) ? n : null;
                }

                await SendAsync(HttpMethod.Patch, $"{Directory}/customer/my_customer/resources/calendars/{Key(w.Target)}", body, cancel);
                return ChangeResult.Ok(w);
            }

            case ChangeAction.GoogleAssignLicence:
                return await AlreadyAsync(w, () => SendAsync(HttpMethod.Post, $"{Licensing}/product/{Key(w.P("productId"))}/sku/{Key(w.P("skuId"))}/user",
                    new JsonObject { ["userId"] = w.Target }, cancel), HttpStatusCode.Conflict, "google.licence.already", cancel);

            case ChangeAction.GoogleRemoveLicence:
                return await AlreadyAsync(w, () => SendAsync(HttpMethod.Delete, $"{Licensing}/product/{Key(w.P("productId"))}/sku/{Key(w.P("skuId"))}/user/{Key(w.Target)}", null, cancel),
                    HttpStatusCode.NotFound, "google.licence.already-gone", cancel);

            default:
                if (w.ChangeAction.ToString().StartsWith("Gmail", StringComparison.Ordinal))
                {
                    return await GmailAsync(w, cancel);
                }

                throw new InvalidOperationException(Texts.T.Format("code.exchange-connection.action-does-not-belong-to", w.ChangeAction));
        }
    }

    /// <summary>A change that finds the state it wanted already there is a success with that message.</summary>
    private static async Task<ChangeResult> AlreadyAsync(Change w, Func<Task<JsonElement>> send, HttpStatusCode already, string message, CancellationToken cancel)
    {
        try
        {
            await send();
            return ChangeResult.Ok(w);
        }
        catch (GoogleError ex) when (ex.Status == already)
        {
            cancel.ThrowIfCancellationRequested();
            return ChangeResult.Ok(w, Texts.T[message]);
        }
    }

    /// <summary>The given parameters as a JSON body ("" = null; True/False as booleans, except where the API wants text).</summary>
    private static JsonObject Fields(Change w, params string[] names)
    {
        var body = new JsonObject();
        foreach (var n in names)
        {
            if (w.Parameters.TryGetValue(n, out var v))
            {
                // The Groups Settings API takes "true"/"false" as text
                body[n] = n == "allowExternalMembers" ? v.ToLowerInvariant() : v.Length == 0 ? null : v;
            }
        }

        return body;
    }

    /// <summary>
    /// The body of a user change. Names, the organisational unit and suspended are plain fields; the employee id and the
    /// department live in lists (externalIds, organizations), so the user is read first and only that entry changes:
    /// the other entries (a job title, a cost centre) stay.
    /// </summary>
    private async Task<JsonObject> UserChangesAsync(Change w, CancellationToken cancel)
    {
        var body = new JsonObject();
        if (w.Parameters.ContainsKey("givenName") || w.Parameters.ContainsKey("familyName"))
        {
            var name = new JsonObject();
            if (w.Parameters.TryGetValue("givenName", out var given)) name["givenName"] = given;
            if (w.Parameters.TryGetValue("familyName", out var family)) name["familyName"] = family;
            body["name"] = name;
        }

        if (w.Parameters.TryGetValue("orgUnitPath", out var ou)) body["orgUnitPath"] = ou.Length > 0 ? ou : "/";
        if (w.Parameters.TryGetValue("suspended", out var suspended)) body["suspended"] = string.Equals(suspended, "True", StringComparison.OrdinalIgnoreCase);

        if (w.Parameters.ContainsKey("employeeId") || w.Parameters.ContainsKey("department"))
        {
            var current = await GetAsync($"{Directory}/users/{Key(w.Target)}?projection=full", cancel);
            if (w.Parameters.TryGetValue("employeeId", out var employee))
            {
                var ids = Items(current, "externalIds").Where(x => S(x, "type") != "organization").Select(x => (JsonNode?)JsonNode.Parse(x.GetRawText())).ToList();
                if (employee.Length > 0) ids.Add(new JsonObject { ["type"] = "organization", ["value"] = employee });
                body["externalIds"] = new JsonArray(ids.ToArray());
            }

            if (w.Parameters.TryGetValue("department", out var department))
            {
                var organisations = Items(current, "organizations").Select(x => JsonNode.Parse(x.GetRawText())!.AsObject()).ToList();
                var primary = organisations.FirstOrDefault(o => o["primary"]?.GetValue<bool>() == true) ?? organisations.FirstOrDefault();
                if (primary is null)
                {
                    organisations.Add(primary = new JsonObject { ["primary"] = true });
                }

                primary["department"] = department;
                body["organizations"] = new JsonArray(organisations.Select(o => (JsonNode?)o).ToArray());
            }
        }

        return body;
    }

    /// <summary>The direct members of a group with their role (G2), read when the page of the group asks.</summary>
    public async Task<IReadOnlyList<GoogleMember>?> ReadMembersAsync(string group, CancellationToken cancel)
        => (await AllAsync($"{Directory}/groups/{Key(group)}/members?maxResults=200", "members", cancel))
            .Select(m => new GoogleMember(S(m, "email"), S(m, "role") is { Length: > 0 } role ? role : "MEMBER", S(m, "type"), S(m, "status")))
            .Where(m => m.Email.Length > 0)
            .OrderBy(m => m.Role switch { "OWNER" => 0, "MANAGER" => 1, _ => 2 }).ThenBy(m => m.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private Task<ChangeResult> GmailAsync(Change w, CancellationToken cancel)
        => GmailToken is null
            ? Task.FromResult(ChangeResult.Error(w, Texts.T["gmail.no-service-account"]))
            : GmailWriteAsync(w, cancel);

    private partial Task<ChangeResult> GmailWriteAsync(Change w, CancellationToken cancel);
}
