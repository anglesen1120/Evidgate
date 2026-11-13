using System.Net;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Testing;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests;

/// <summary>
/// The ordering rules of the install routes: what a denied caller sees before anything is parsed, that routing's
/// trailing-slash form resolves the same org, and that concurrent starts cannot exceed the live-state cap.
/// </summary>
public sealed partial class GitHubInstallationTests
{
    /// <summary>
    /// A malformed state is refused by the binding permission first: the state envelope is parsed inside the gated
    /// command, so a caller the catalogue denies sees the same denial as on any other route, never a 400 that would
    /// tell it which states are well-formed. A permitted caller then gets the 400 for the same value.
    /// </summary>
    [Fact]
    public async Task Callback_MalformedState_IsRefusedByThePermissionStage()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var viewer = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);

        // The state names the org, so the tenant resolves and the only thing that can refuse is the permission.
        var malformed = $"{org:D}.not a nonce";

        await using var denied = Host(org, viewer, new FakeGitHubOAuth { UserId = ProbeUserId }, new FakeGitHubAppInstallations(), productionEvaluator: true);
        using var deniedClient = denied.CreateClient();
        using var refused = await CallbackAsync(deniedClient, malformed);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeAsync(refused)).ShouldBe("forbidden");

        await using var permitted = Host(org, admin, new FakeGitHubOAuth { UserId = ProbeUserId }, new FakeGitHubAppInstallations());
        using var permittedClient = permitted.CreateClient();
        using var invalid = await CallbackAsync(permittedClient, malformed);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeAsync(invalid)).ShouldBe("state_invalid");
    }

    /// <summary>Routing serves the callback with or without a trailing slash, so both must resolve the same org.</summary>
    [Fact]
    public async Task Callback_TrailingSlash_BindsTheSameState()
    {
        var (host, client, _, _, state, _) = await StartedAsync();
        await using var _ = host;

        using var response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/v1/github/installations/callback/?installation_id={InstallationId}&code=probe-code&state={state}",
            org: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The live-state cap survives concurrent starts of one user: nineteen states are live, eight starts race for
    /// the last slot, and exactly one wins — the count and the insert are serialized per (org, user).
    /// </summary>
    [Fact]
    public async Task Start_ConcurrentStarts_DoNotExceedTheLiveStateCap()
    {
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        await using var host = Host(org, admin, new FakeGitHubOAuth { UserId = ProbeUserId }, new FakeGitHubAppInstallations());
        using var client = host.CreateClient();

        for (var index = 0; index < 19; index++)
        {
            await InsertStateAsync(org, admin, $"cap-race-{index}", DateTimeOffset.UtcNow.AddMinutes(10));
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            SendAsync(client, HttpMethod.Post, "/v1/github/installations/start", org)));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(7);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        (await ScalarAsync<long>(
            """
            select count(*) from public.github_install_state
            where org_id = @org and user_id = @user and used_at is null and expires_at > now()
            """,
            ("org", org),
            ("user", admin))).ShouldBe(20);
    }
}
