using System.Globalization;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Identity.IntegrationTests.Sessions.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Membership;

/// <summary>
/// AC3 over the real Api composition and real session cookies: removing a member (<c>DELETE</c>) or
/// disabling it (<c>PATCH</c>) revokes that user's live sessions in the same transaction, so the next
/// request that presents the cookie answers 401 <c>session_revoked</c>. Another member's session
/// survives, and a revocation seam that fails takes the membership write back with it.
/// </summary>
/// <remarks>
/// <para>
/// The member's credential is a real sign-in through the session host (cookie, row and authenticator),
/// while the administrative mutation runs on a host that publishes the caller and its permission, the
/// arrangement the membership endpoint suite uses: the route, the pipeline and the tenant transaction are
/// the deployed ones.
/// </para>
/// <para>
/// Sessions are user-wide — <c>public.session</c> carries no org — so a removal in ONE org ends the
/// user's sessions everywhere. That is pinned by exactly one case, named for it.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class RemovalTests : TenantTest
{
    /// <summary>The session list route, used as the authenticated request after a revocation.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    public RemovalTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// AC3: the deleted member's live session is stamped in the transaction that removes the
    /// membership — not rotation-flagged, ended — and the very next request with that cookie is 401.
    /// </summary>
    [Fact]
    public async Task Remove_Member_Revokes_The_Live_Session_And_The_Next_Request_Is_401()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var membershipId = await MembershipIdAsync(host, member.OrgId, member.UserId);

        var (status, errorCode) = await RemoveAsync(member.OrgId, membershipId, "remove-session-1");

        status.ShouldBe(HttpStatusCode.NoContent);
        errorCode.ShouldBeNull();
        (await MembershipStatusAsync(host, membershipId)).ShouldBe("removed");

        var session = await host.SessionRowAsync(sessionId);
        session.RevokedAt.ShouldNotBeNull("losing the membership ends the member's live sessions");
        session.RotationRequiredAt.ShouldBeNull("the session is ended, not asked to rotate");

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// AC3, the disable path: the same transaction contract on <c>PATCH status=removed</c>, so the two
    /// routes cannot drift apart.
    /// </summary>
    [Fact]
    public async Task Disable_Member_Revokes_The_Live_Session_And_The_Next_Request_Is_401()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var membershipId = await MembershipIdAsync(host, member.OrgId, member.UserId);
        var version = (int)(await host.ScalarAsync(
            "select version from public.membership where id = @id",
            [("id", membershipId)]))!;

        var (status, errorCode) = await PatchAsync(member.OrgId, membershipId, version, "disable-session-1");

        status.ShouldBe(HttpStatusCode.OK);
        errorCode.ShouldBeNull();
        (await MembershipStatusAsync(host, membershipId)).ShouldBe("removed");

        var session = await host.SessionRowAsync(sessionId);
        session.RevokedAt.ShouldNotBeNull("disabling the membership ends the member's live sessions");
        session.RotationRequiredAt.ShouldBeNull();

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// The revocation is scoped to the removed user: a second member of the same org keeps their
    /// session row untouched and stays signed in.
    /// </summary>
    [Fact]
    public async Task Removal_Leaves_Another_Members_Session_Live()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var removed = await host.SignInMemberAsync(client);
        var bystander = await host.SignInMemberInOrgAsync(client, removed.OrgId, roleCode: null);
        var removedSessionId = await host.SessionIdOfAsync(removed.SessionCookie);
        var bystanderSessionId = await host.SessionIdOfAsync(bystander.SessionCookie);
        var membershipId = await MembershipIdAsync(host, removed.OrgId, removed.UserId);

        var (status, _) = await RemoveAsync(removed.OrgId, membershipId, "remove-bystander-1");

        status.ShouldBe(HttpStatusCode.NoContent);
        (await host.SessionRowAsync(removedSessionId)).RevokedAt.ShouldNotBeNull();

        var kept = await host.SessionRowAsync(bystanderSessionId);
        kept.RevokedAt.ShouldBeNull("another member's session must outlive the removal");
        kept.RotationRequiredAt.ShouldBeNull("nothing asked the bystander's session to rotate");

        using var list = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(bystander.SessionCookie));

        list.StatusCode.ShouldBe(HttpStatusCode.OK, "the bystander is still signed in");
    }

    /// <summary>
    /// The accepted user-wide behaviour, pinned: the member signed in through org A; removing the same
    /// user's membership in org B ends that session too, because a session belongs to the user and not
    /// to an org. Org A's membership is untouched, so only the credential is what the org B removal ends.
    /// </summary>
    [Fact]
    public async Task Removal_In_Another_Org_Revokes_The_Session_Opened_For_The_First_Org()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var firstOrgMembership = await MembershipIdAsync(host, member.OrgId, member.UserId);

        var secondOrg = await host.SeedOrganizationAsync();
        await host.SeedMembershipAsync(secondOrg, member.UserId);
        var secondOrgMembership = await MembershipIdAsync(host, secondOrg, member.UserId);

        var (status, _) = await RemoveAsync(secondOrg, secondOrgMembership, "remove-cross-org-1");

        status.ShouldBe(HttpStatusCode.NoContent);
        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldNotBeNull(
            "a session belongs to the user, so a removal in one org ends it in every org");
        (await MembershipStatusAsync(host, firstOrgMembership)).ShouldBe(
            "active", "the first org's membership is a different row and is not the target");

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SessionTestHost.ErrorCodeAsync(next)).ShouldBe(ApiErrorCode.SessionRevoked);
    }

    /// <summary>
    /// The removal and its revocation are one unit of work: when the revocation throws — after it wrote
    /// its own update — the membership stays <c>active</c>, the session's <c>revoked_at</c> goes back to
    /// NULL, and the member is still signed in.
    /// </summary>
    [Fact]
    public async Task Remove_When_The_Revocation_Fails_Rolls_Back_The_Membership_And_The_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var membershipId = await MembershipIdAsync(host, member.OrgId, member.UserId);

        var (status, errorCode) = await RemoveAsync(
            member.OrgId,
            membershipId,
            "remove-rollback-1",
            revocationFails: true);

        status.ShouldBe(HttpStatusCode.InternalServerError);
        errorCode.ShouldBe(ApiErrorCode.InternalError);

        (await MembershipStatusAsync(host, membershipId)).ShouldBe(
            "active", "the failed revocation must take the membership write back with it");
        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldBeNull(
            "the revocation's own update rolled back with the caller's transaction");

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.OK, "the member is still a member, with a live session");
    }

    /// <summary>AC3's rollback contract on the disable path, with the same failing seam.</summary>
    [Fact]
    public async Task Disable_When_The_Revocation_Fails_Rolls_Back_The_Membership_And_The_Session()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var sessionId = await host.SessionIdOfAsync(member.SessionCookie);
        var membershipId = await MembershipIdAsync(host, member.OrgId, member.UserId);
        var version = (int)(await host.ScalarAsync(
            "select version from public.membership where id = @id",
            [("id", membershipId)]))!;

        var (status, errorCode) = await PatchAsync(
            member.OrgId,
            membershipId,
            version,
            "disable-rollback-1",
            revocationFails: true);

        status.ShouldBe(HttpStatusCode.InternalServerError);
        errorCode.ShouldBe(ApiErrorCode.InternalError);

        (await MembershipStatusAsync(host, membershipId)).ShouldBe(
            "active", "the failed revocation must take the status change back with it");
        (await host.SessionRowAsync(sessionId)).RevokedAt.ShouldBeNull();

        using var next = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Get,
            SessionsPath,
            SessionTestHost.CookieHeaderFor(member.SessionCookie));

        next.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The revocation covers the sessions that exist when it runs; a session minted afterwards — the
    /// successor a rotation would have written for a request already in flight — is still refused by the
    /// membership check, because removability is decided per request from the org's active memberships
    /// (the real <c>PermissionEvaluator</c> member path), not from the credential.
    /// </summary>
    [Fact]
    public async Task A_Session_Minted_After_The_Removal_Is_403_NoMembership_On_The_Org_Route()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var sessionClient = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(sessionClient);
        var membershipId = await MembershipIdAsync(host, member.OrgId, member.UserId);

        var (status, _) = await RemoveAsync(member.OrgId, membershipId, "remove-after-mint-1");
        status.ShouldBe(HttpStatusCode.NoContent);

        // Written after the revoke-all ran, so the removal cannot have stamped it: only the membership
        // check can refuse the next request.
        var successor = await host.CreateSessionAsync(member.UserId);
        (await host.SessionRowAsync(successor.SessionId)).RevokedAt.ShouldBeNull(
            "the successor was written after the revocation, so it is live for this case to mean anything");

        using var factory = IdentityApiFactory.Start(
                Fixture,
                new IdentityHostSetup(member.OrgId, Principal: RequestPrincipal.Member(member.UserId)))
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    // The session host minted the cookie with its own fresh pepper; this host must hash
                    // with the same one, or the request is 401 before the membership check is reached.
                    ["EVIDGATE:SESSION:PEPPER"] = Convert.ToBase64String(host.Pepper),
                    ["EVIDGATE:SESSION:PEPPERVERSION"] =
                        SessionTestHost.PepperVersion.ToString(CultureInfo.InvariantCulture),
                })));
        using var orgClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/orgs/{member.OrgId}/memberships");
        request.Headers.Add("Cookie", SessionTestHost.CookieHeaderFor(successor.CookieValue));

        using var response = await orgClient.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SessionTestHost.ErrorCodeAsync(response)).ShouldBe(
            ApiErrorCode.NoMembership,
            "an unrevoked session of a removed member is no member of the org");
    }

    /// <summary>
    /// Two removals of one non-admin member race each other with different idempotency keys — the one
    /// arrangement the route does not serialise for the client. Both answer 204 (the loser reads the
    /// winner's <c>removed</c> row and answers what the serial replay would have), and the chain carries
    /// exactly ONE <c>membership.remove</c> row for that membership, because the loser suppresses the
    /// ambient audit unit instead of describing a change it did not make.
    /// </summary>
    [Fact]
    public async Task Two_Concurrent_Removals_Of_One_Member_Both_Answer_204_And_Write_One_Audit_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var sessionClient = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(sessionClient);
        var membershipId = await MembershipIdAsync(host, member.OrgId, member.UserId);
        var path = $"/v1/orgs/{member.OrgId}/memberships/{membershipId}";

        await using var factory = IdentityApiFactory.Start(Fixture, member.OrgId);
        using var client = factory.CreateClient();
        using var first = new HttpRequestMessage(HttpMethod.Delete, path);
        first.Headers.Add("Idempotency-Key", "concurrent-remove-1");
        using var second = new HttpRequestMessage(HttpMethod.Delete, path);
        second.Headers.Add("Idempotency-Key", "concurrent-remove-2");

        var raced = await Task.WhenAll(
            client.SendAsync(first, TestContext.Current.CancellationToken),
            client.SendAsync(second, TestContext.Current.CancellationToken));

        try
        {
            raced.ShouldAllBe(
                response => response.StatusCode == HttpStatusCode.NoContent,
                "both removals answer the route's idempotent 204");
            (await MembershipStatusAsync(host, membershipId)).ShouldBe("removed");
            ((long)(await host.ScalarAsync(
                """
                select count(*)
                  from public.audit_log
                 where org_id = @org_id
                   and action = 'membership.remove'
                """,
                [("org_id", member.OrgId)]))!).ShouldBe(
                1L,
                "one membership was removed, so exactly one audit row exists, and none without an object");
        }
        finally
        {
            foreach (var response in raced)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// One <c>DELETE …/memberships/{m}</c> through the real Api as the resolved caller of
    /// <paramref name="orgId"/>, optionally over a host whose revocation seam fails after it writes.
    /// </summary>
    /// <param name="orgId">The tenant the request runs in.</param>
    /// <param name="membershipId">The membership the route names.</param>
    /// <param name="idempotencyKey">The key the mutation carries.</param>
    /// <param name="revocationFails">Whether the host's revocation seam throws after writing.</param>
    private async Task<(HttpStatusCode Status, string? ErrorCode)> RemoveAsync(
        Guid orgId,
        Guid membershipId,
        string idempotencyKey,
        bool revocationFails = false)
    {
        await using var factory = IdentityApiFactory.Start(Fixture, orgId);
        using var failing = revocationFails ? WithFailingRevocation(factory) : null;
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/orgs/{orgId}/memberships/{membershipId}");
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        return await SendAsync(failing ?? factory, request);
    }

    /// <summary>The <c>PATCH status=removed</c> form of <see cref="RemoveAsync"/> (with its <c>If-Match</c>).</summary>
    /// <param name="orgId">The tenant the request runs in.</param>
    /// <param name="membershipId">The membership the route names.</param>
    /// <param name="version">The version the caller holds.</param>
    /// <param name="idempotencyKey">The key the mutation carries.</param>
    /// <param name="revocationFails">Whether the host's revocation seam throws after writing.</param>
    private async Task<(HttpStatusCode Status, string? ErrorCode)> PatchAsync(
        Guid orgId,
        Guid membershipId,
        int version,
        string idempotencyKey,
        bool revocationFails = false)
    {
        await using var factory = IdentityApiFactory.Start(Fixture, orgId);
        using var failing = revocationFails ? WithFailingRevocation(factory) : null;
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/v1/orgs/{orgId}/memberships/{membershipId}")
        {
            Content = new StringContent("""{"status":"removed"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");

        return await SendAsync(failing ?? factory, request);
    }

    /// <summary>Sends one request on a started host and reads its status and envelope code before disposal.</summary>
    /// <param name="factory">The host to send on.</param>
    /// <param name="request">The built request.</param>
    private static async Task<(HttpStatusCode Status, string? ErrorCode)> SendAsync(
        WebApplicationFactory<Program> factory,
        HttpRequestMessage request)
    {
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        return (response.StatusCode, await SessionTestHost.ErrorCodeAsync(response));
    }

    /// <summary>
    /// The host with a revocation seam that writes and then throws: the caller's transaction must take
    /// both writes back, which a seam that threw before writing could not prove.
    /// </summary>
    /// <param name="factory">The host whose composition is otherwise unchanged.</param>
    private static WebApplicationFactory<Program> WithFailingRevocation(IdentityApiFactory factory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISessionRevocation>();
            services.AddSingleton<ISessionRevocation>(provider =>
                new RevocationThatFailsAfterWriting(provider.GetRequiredService<SessionRevocationStore>()));
        }));

    /// <summary>The membership row of one (org, user) pair, read as the owner.</summary>
    private static async Task<Guid> MembershipIdAsync(SessionTestHost host, Guid orgId, Guid userId) =>
        (Guid)(await host.ScalarAsync(
            "select id from public.membership where org_id = @org_id and user_id = @user_id",
            [("org_id", orgId), ("user_id", userId)]))!;

    /// <summary>The membership's status, read as the owner.</summary>
    private static async Task<string> MembershipStatusAsync(SessionTestHost host, Guid membershipId) =>
        (string)(await host.ScalarAsync(
            "select status from public.membership where id = @id",
            [("id", membershipId)]))!;

    /// <summary>Delegates to the real revocation store, then fails: the write is what must be rolled back.</summary>
    private sealed class RevocationThatFailsAfterWriting(ISessionRevocation inner) : ISessionRevocation
    {
        /// <inheritdoc />
        public async ValueTask<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            await inner.RevokeAllForUserAsync(userId, cancellationToken);

            throw new InvalidOperationException("the revocation failed after it wrote");
        }
    }
}
