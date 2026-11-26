using System.Net;
using System.Text;
using System.Text.Json;
using Moss365.Connections.Graph;
using Moss365.Core.Changes;
using Xunit;

namespace Moss365.Tests;

/// <summary>R18: reading the sign-ins to keep and the directory audit log from Graph.</summary>
public class LocalLogConnectionTests
{
    private static GraphConnection Make(FakeGraph fake) => new(new GraphClient(new HttpClient(fake), _ => Task.FromResult("token")));

    private static HttpResponseMessage Json(HttpStatusCode status, object body)
        => new(status) { Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static object SignIn(string id, string userId, string time, int error = 0, string risk = "none")
        => new { id, createdDateTime = time, userId, userPrincipalName = userId + "@test.be", status = new { errorCode = error, failureReason = error == 0 ? "" : "Invalid password" }, riskLevelDuringSignIn = risk };

    [Fact]
    public async Task SignInsToKeepAreAskedWithFiltersAndOnce()
    {
        var fake = new FakeGraph()
            .Op(HttpMethod.Get, "/auditLogs/signIns?", r =>
            {
                var query = Uri.UnescapeDataString(r.RequestUri!.Query);
                return query.Contains("riskState", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.BadRequest, "{\"error\":{\"code\":\"BadRequest\",\"message\":\"Entra ID P2 nodig\"}}")
                    : query.Contains("errorCode ne 0", StringComparison.Ordinal)
                        ? Json(HttpStatusCode.OK, new { value = new[] { SignIn("f1", "u1", "2026-11-25T10:00:00Z", error: 50126) } })
                        : Json(HttpStatusCode.OK, new { value = new[] { SignIn("a1", "u-admin", "2026-11-25T11:00:00Z", risk: "high"), SignIn("f1", "u1", "2026-11-25T10:00:00Z", error: 50126) } });
            })
            .Op(HttpMethod.Get, "/auditLogs/directoryAudits?", new
            {
                value = new[]
                {
                    new
                    {
                        id = "d1", activityDateTime = "2026-11-25T12:00:00Z", activityDisplayName = "Add member to role", category = "RoleManagement",
                        loggedByService = "Core Directory", result = "success", initiatedBy = new { user = new { userPrincipalName = "beheerder@test.be" } },
                        targetResources = new[] { new { id = "u1", displayName = "An" } },
                    },
                },
            });
        var since = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        var connection = Make(fake);

        var signIns = await connection.ReadSignInsToKeepAsync(since, new[] { "u-admin", "U-ADMIN" }, 100, CancellationToken.None);

        Assert.Equal(new[] { "a1", "f1" }, signIns.Select(s => s.Id));
        Assert.Equal("high", signIns[0].Risk);
        Assert.True(signIns[0].Risky);
        var asked = fake.Requests.Select(v => Uri.UnescapeDataString(v.Url)).ToList();
        Assert.Contains(asked, u => u.Contains("createdDateTime gt 2026-11-24T00:00:00Z and status/errorCode ne 0", StringComparison.Ordinal));
        Assert.Single(asked, u => u.Contains("and (userId eq 'u-admin')", StringComparison.Ordinal));

        var audit = Assert.Single(await connection.ReadDirectoryAuditsAsync(since, 100, CancellationToken.None));
        Assert.Equal(("d1", "Add member to role", "RoleManagement", "Core Directory", "beheerder@test.be", "An", "success"),
            (audit.Id, audit.Activity, audit.Category, audit.Service, audit.By, audit.TargetName, audit.Result));

        // Without a licence or permission: a clear error that the app shows
        var denied = new FakeGraph().Op(HttpMethod.Get, "/auditLogs/", "{\"error\":{\"code\":\"Authentication_RequestFromNonPremiumTenantOrB2CTenant\",\"message\":\"premium\"}}", HttpStatusCode.Forbidden);
        await Assert.ThrowsAsync<NoPermissionError>(() => Make(denied).ReadSignInsToKeepAsync(since, Array.Empty<string>(), 100, CancellationToken.None));
        await Assert.ThrowsAsync<NoPermissionError>(() => Make(denied).ReadDirectoryAuditsAsync(since, 100, CancellationToken.None));
    }
}
