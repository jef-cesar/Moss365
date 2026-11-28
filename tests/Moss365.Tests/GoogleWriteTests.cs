using System.Net;
using System.Text;
using System.Text.Json;
using Moss365.Connections.Google;
using Moss365.Core.Changes;
using Moss365.Core.Setup;
using Xunit;

namespace Moss365.Tests;

/// <summary>G2: writing in Google Workspace as the steps of a plan, with a fake Google.</summary>
public sealed class GoogleWriteTests
{
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Body)> Asked { get; } = new();
        public Func<HttpRequestMessage, string?, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.OK, "{}");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Asked.Add((request.Method, Uri.UnescapeDataString(request.RequestUri!.ToString()), body));
            var (status, text) = Answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    private static Change Step(ChangeAction action, string target, params (string, string)[] parameters) => new()
    {
        TargetSystem = TargetSystem.Google, ChangeAction = action, Target = target, Description = "x",
        Parameters = parameters.ToDictionary(p => p.Item1, p => p.Item2, StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public async Task UsersAreCreatedAndChangedWithoutLosingOtherEntries()
    {
        var google = new FakeGoogle
        {
            Answer = (r, _) => r.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, """{"id":"1","externalIds":[{"type":"organization","value":"E-1"},{"type":"custom","customType":"badge","value":"B-9"}],"organizations":[{"title":"Adviseur","department":"Oud","primary":true}]}""")
                : r.Method == HttpMethod.Post ? (HttpStatusCode.OK, """{"id":"new-1"}""") : (HttpStatusCode.OK, "{}"),
        };
        var connection = new GoogleConnection(new HttpClient(google), _ => Task.FromResult("token-1"));
        var create = Step(ChangeAction.GoogleCreateUser, "an@example.com", ("givenName", "An"), ("familyName", "Peeters"), ("orgUnitPath", "/Sales"), ("employeeId", "E-1"));
        create.Secret = "Geheim-123!";
        var update = Step(ChangeAction.GoogleUpdateUser, "1", ("department", "Cultuur"), ("employeeId", "E-2"), ("suspended", "True"));
        var results = await connection.ExecuteAsync(new[] { create, update }, CancellationToken.None);
        Assert.All(results, r => Assert.Equal(ResultStatus.Succeeded, r.Status));
        Assert.Equal("new-1", results[0].Message);

        var made = JsonDocument.Parse(google.Asked.First(a => a.Method == HttpMethod.Post).Body!).RootElement;
        Assert.Equal(("an@example.com", "/Sales", true), (made.GetProperty("primaryEmail").GetString(), made.GetProperty("orgUnitPath").GetString(), made.GetProperty("changePasswordAtNextLogin").GetBoolean()));

        // The badge and the job title stay; only the employee id and the department change
        var patch = JsonDocument.Parse(google.Asked.Single(a => a.Method == HttpMethod.Patch).Body!).RootElement;
        Assert.True(patch.GetProperty("suspended").GetBoolean());
        Assert.Contains(patch.GetProperty("externalIds").EnumerateArray(), x => x.GetProperty("value").GetString() == "B-9");
        Assert.Contains(patch.GetProperty("externalIds").EnumerateArray(), x => x.GetProperty("value").GetString() == "E-2");
        Assert.DoesNotContain(patch.GetProperty("externalIds").EnumerateArray(), x => x.GetProperty("value").GetString() == "E-1");
        var organisation = patch.GetProperty("organizations")[0];
        Assert.Equal(("Adviseur", "Cultuur"), (organisation.GetProperty("title").GetString(), organisation.GetProperty("department").GetString()));

        // Never a password in the plan: refused
        var noPassword = Step(ChangeAction.GoogleSetPassword, "1");
        Assert.Equal(ResultStatus.Failed, (await connection.ExecuteAsync(new[] { noPassword }, CancellationToken.None))[0].Status);
    }

    [Fact]
    public async Task WhatIsAlreadySoIsASuccessAndTokensAreRevokedOneByOne()
    {
        var google = new FakeGoogle
        {
            Answer = (r, _) => r.RequestUri!.ToString() switch
            {
                var u when u.EndsWith("/members", StringComparison.Ordinal) => (HttpStatusCode.Conflict, """{"error":{"code":409,"message":"Member already exists."}}"""),
                var u when u.Contains("/members/", StringComparison.Ordinal) => (HttpStatusCode.NotFound, """{"error":{"code":404,"message":"Resource Not Found: memberKey"}}"""),
                var u when u.EndsWith("/tokens", StringComparison.Ordinal) => (HttpStatusCode.OK, """{"items":[{"clientId":"app-1"},{"clientId":"app-2"}]}"""),
                _ => (HttpStatusCode.OK, ""),
            },
        };
        var connection = new GoogleConnection(new HttpClient(google), _ => Task.FromResult("token-1"));
        var results = await connection.ExecuteAsync(new[]
        {
            Step(ChangeAction.GoogleAddGroupMember, "an@example.com", ("groep", "team@example.com"), ("rol", "OWNER")),
            Step(ChangeAction.GoogleRemoveGroupMember, "bo@example.com", ("groep", "team@example.com")),
            Step(ChangeAction.GoogleRevokeTokens, "1"),
            Step(ChangeAction.GoogleSignOut, "1"),
        }, CancellationToken.None);
        Assert.All(results, r => Assert.Equal(ResultStatus.Succeeded, r.Status));
        Assert.Equal("Was al lid.", results[0].Message);
        Assert.Equal("Was al geen lid.", results[1].Message);
        Assert.Equal(2, google.Asked.Count(a => a.Method == HttpMethod.Delete && a.Url.Contains("/tokens/", StringComparison.Ordinal)));
        Assert.Contains(google.Asked, a => a.Method == HttpMethod.Post && a.Url.EndsWith("/users/1/signOut", StringComparison.Ordinal));
        Assert.Contains("\"role\":\"OWNER\"", google.Asked[0].Body, StringComparison.Ordinal);

        // Gmail without a service account: refused with what to do
        var gmail = await connection.ExecuteAsync(new[] { Step(ChangeAction.GmailSetSignature, "an@example.com", ("handtekening", "x")) }, CancellationToken.None);
        Assert.Contains("serviceaccount", gmail[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEngineRefusesAPartThatIsOffAndEveryStepHasItsLabel()
    {
        var licence = Step(ChangeAction.GoogleAssignLicence, "an@example.com", ("productId", "Google-Apps"), ("skuId", "1010020025"));
        Assert.NotNull(GoogleParts.Refusal(licence, Array.Empty<string>()));
        Assert.Null(GoogleParts.Refusal(licence, new[] { GoogleParts.Licences }));
        Assert.NotNull(GoogleParts.Refusal(Step(ChangeAction.GmailSetForwarding, "an@example.com"), new[] { GoogleParts.Licences }));
        Assert.Null(GoogleParts.Refusal(Step(ChangeAction.GoogleUpdateUser, "1"), Array.Empty<string>()));

        Assert.Equal(Platform.GoogleWorkspace, Platforms.Of(ChangeAction.GoogleUpdateUser));
        Assert.Equal(Platform.Gmail, Platforms.Of(ChangeAction.GmailSetVacation));
        Assert.Equal("Google", Step(ChangeAction.GoogleSignOut, "1").SystemText);
    }

    [Fact]
    public void GoogleChangesCanBeUndoneWhereTheyCan()
    {
        LogEntry Log(Change c) => new(DateTimeOffset.Now, "beheerder", "test", c.TargetSystem.ToString(), c.ChangeAction.ToString(), c.Target, c.TargetName,
            c.Description, c.Parameters, "Succeeded", string.Empty, Previous: c.Previous);
        var remove = Step(ChangeAction.GoogleRemoveGroupMember, "bo@example.com", ("groep", "team@example.com"), ("rol", "MANAGER"));
        var back = Assert.Single(Undo.Plan(new[] { Log(remove) }).Plan);
        Assert.Equal((ChangeAction.GoogleAddGroupMember, TargetSystem.Google, "MANAGER"), (back.ChangeAction, back.TargetSystem, back.P("rol")));

        var update = Step(ChangeAction.GoogleUpdateUser, "1", ("suspended", "True"));
        update = new Change { TargetSystem = update.TargetSystem, ChangeAction = update.ChangeAction, Target = "1", Description = "x", Parameters = update.Parameters, Previous = new() { ["suspended"] = "False" } };
        Assert.Equal("False", Assert.Single(Undo.Plan(new[] { Log(update) }).Plan).P("suspended"));

        var filter = new Change
        {
            TargetSystem = TargetSystem.Google, ChangeAction = ChangeAction.GmailRemoveFilter, Target = "an@example.com", Description = "x",
            Parameters = new() { ["id"] = "f1" }, Previous = new() { ["json"] = """{"criteria":{"from":"x@example.net"},"action":{"addLabelIds":["TRASH"]}}""" },
        };
        var again = Assert.Single(Undo.Plan(new[] { Log(filter) }).Plan);
        Assert.Equal(ChangeAction.GmailAddFilter, again.ChangeAction);
        Assert.NotNull(Undo.WhyNot(Log(Step(ChangeAction.GoogleSetPassword, "1"))));
        Assert.NotNull(Undo.WhyNot(Log(Step(ChangeAction.GoogleCreateUser, "an@example.com"))));
    }
}
