using System.Net;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Testing;
using Identity.IntegrationTests.Login;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// F55 over HTTP: <c>DELETE /v1/auth/sessions/{s}</c> with <c>X-Evidgate-Org</c> appends ONE
/// <c>auth.session_revoke_admin</c> row — written by the definer, in the revoke's own transaction, and only when the
/// caller is an admin ending ANOTHER member's session.
/// </summary>
/// <remarks>
/// <para>
/// <b>The row, and the transaction it shares.</b> The actor is the admin whose session made the call, the chain is
/// the org named by the header, the object is the revoked <c>session</c> row and the metadata names the member it
/// belonged to (F55). Because <c>evidgate_resolve.session_revoke_one</c> appends it inside its own transaction, the
/// two states this suite may ever observe are "revoked with one row" and "live with none" — including after a
/// fault inside the append and after a caller that leaves mid-flight.
/// </para>
/// <para>
/// <b>What the stored row may say.</b> Every column, read back as JSON, is asserted to carry no cookie value, no
/// cookie HMAC in either spelling and no pepper: the audit says who ended which session of whose, never how the
/// credential was proved.
/// </para>
/// <para>
/// <b>The row also says which call wrote it (F58).</b> The request id the edge accepted (or minted) and the W3C
/// trace id of the call are stored on the row from the same two sources the C# audit writer reads, so a SQL row
/// and an application row of one request join on both; a caller-supplied id the edge refused reaches neither the
/// response nor the row.
/// </para>
/// <para>
/// <b>What must write nothing.</b> A self revoke (the current row, or a second device of the caller, with or without
/// the header), <c>logout</c>, <c>revoke-all</c> and every refusal (401, 403, 404) are the account acting on its own
/// sessions or not acting at all; each is measured against the org's chain, and no line of the deployment's log may
/// name the session either (02.3.2 D9).
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class AdminRevokeAuditTests : HarnessTest
{
    /// <summary>The single-session route of AC6.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>The logout route (no admin row: it ends the caller's own session).</summary>
    private const string LogoutPath = "/v1/auth/logout";

    /// <summary>The revoke-all route (no admin row either).</summary>
    private const string RevokeAllPath = "/v1/auth/sessions/revoke-all";

    /// <summary>The action the definer writes; after F55 the string lives in SQL, so it is pinned here.</summary>
    private const string AdminRevokeAction = "auth.session_revoke_admin";

    /// <summary>The uuid text format every row assertion uses: <c>audit_log</c> stores ids as <c>"D"</c> text.</summary>
    private const string UuidText = "D";

    /// <summary>The edge's request-id header (00 §9.1): honoured when safe, echoed on every response (F58).</summary>
    private const string RequestIdHeaderName = "X-Request-Id";

    /// <summary>The 32-hex trace id a case joins, in the form <c>Activity.TraceId.ToString()</c> writes.</summary>
    private const string JoinedTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>The 16-hex parent span id of that trace, as <c>traceparent</c> carries it.</summary>
    private const string JoinedSpanId = "00f067aa0ba902b7";

    /// <summary>The shape every stored trace id has: what <c>Activity.TraceId</c> renders.</summary>
    private const string TraceIdShape = "^[0-9a-f]{32}$";

    public AdminRevokeAuditTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>Resets the harness database, then restores the migration-seeded permission catalogue.</summary>
    public override async ValueTask InitializeAsync()
    {
        await Fixture.ResetAsync(Token);
        await CatalogueSeed.ApplyAsync(Fixture, Token);
    }

    /// <summary>
    /// The administered case: an admin revoking a member's session in the org it administers answers 204, ends the
    /// row, and appends exactly one <c>auth.session_revoke_admin</c> row — action, actor, object, target user, org,
    /// nothing credentialed in the stored payload — which the production verifier accepts between the application
    /// rows written before and after it.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_Writes_Exactly_One_Row_In_The_Revoke_Transaction()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        var before = await AuditSnapshotAsync(victim.OrgId);

        using (var revoked = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            AssertNoSessionCookie(revoked, "an admin revoke leaves the admin's own cookies alone");
        }

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldNotBeNull(
            "the addressed row is the one revoked");

        var after = await AuditSnapshotAsync(victim.OrgId);
        after.Rows.ShouldBe(before.Rows + 1, "exactly one row, not one per org and not a second on the same call");
        after.MaxSeq.ShouldBe(before.MaxSeq + 1);

        var row = await NewestAuditRowAsync(victim.OrgId);
        row.Seq.ShouldBe(after.MaxSeq);
        row.Action.ShouldBe(AdminRevokeAction);
        row.ActorType.ShouldBe("user");
        row.ActorId.ShouldBe(admin.UserId.ToString(UuidText), "the actor is the admin, resolved from its own cookie");
        row.ObjectType.ShouldBe("session");
        row.ObjectId.ShouldBe(victimSessionId.ToString(UuidText));

        using (var document = JsonDocument.Parse(row.MetadataJson))
        {
            document.RootElement.EnumerateObject().Select(property => property.Name)
                .ShouldBe(["target_user_id"], "the row names the member, and nothing else beyond its columns");
            document.RootElement.GetProperty("target_user_id").GetString()
                .ShouldBe(victim.UserId.ToString(UuidText), "F55 records the target user the definer resolved");
        }

        AssertCarriesNoCredential(row.Payload, host, victim.SessionCookie);

        // An application row AFTER the definer's, in the same org: the SQL row has to link with the C# writer's
        // rows, which is the one thing that makes it trustworthy to the existing verifier.
        await host.SignInMemberInOrgAsync(client, victim.OrgId, roleCode: null);

        (await AuditSnapshotAsync(victim.OrgId)).Rows.ShouldBe(before.Rows + 2);
        (await VerifyAsync(victim.OrgId)).ShouldBeNull("the definer's row verifies inside the org's chain");
    }

    /// <summary>
    /// The owner side, with the header present: the caller's own second device and the caller's current row both
    /// revoke normally — 204, the row dead — and neither writes an admin row, because the addressed row is the
    /// caller's own. <c>logout</c> and <c>revoke-all</c> write none either.
    /// </summary>
    [Fact]
    public async Task Self_Revoke_Logout_And_RevokeAll_Write_No_Admin_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        // A second device of the same account, with the header: still the owner path.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

        using (var revoked = await RevokeAsync(client, member, otherId, member.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "a member may always end its own second device");
            AssertNoSessionCookie(revoked, "the caller's own session is still the credential");
        }

        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldNotBeNull();
        (await AdminRowCountAsync(member.OrgId)).ShouldBe(0L, "a self revoke is not an administrative action");

        // The same second device without the header.
        var withoutHeader = await host.CreateSessionAsync(member.UserId);
        var withoutHeaderId = await host.SessionIdOfAsync(withoutHeader.CookieValue);

        using (var revoked = await RevokeAsync(client, member, withoutHeaderId))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(withoutHeaderId)).RevokedAt.ShouldNotBeNull();
        (await AdminRowCountAsync(member.OrgId)).ShouldBe(0L);

        // A fresh sign-in for the logout half: the login path writes `auth.login` rows, and the count below is of
        // this action only.
        var forLogout = await host.SignInMemberAsync(client);

        using (var loggedOut = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            LogoutPath,
            SessionTestHost.CookieHeaderFor(forLogout.SessionCookie, forLogout.Xsrf),
            xsrfToken: forLogout.Xsrf.RequestToken))
        {
            loggedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AdminRowCountAsync(forLogout.OrgId)).ShouldBe(0L, "a logout is the account ending its own session");

        var forRevokeAll = await host.SignInMemberAsync(client);

        using (var revoked = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            RevokeAllPath,
            SessionTestHost.CookieHeaderFor(forRevokeAll.SessionCookie, forRevokeAll.Xsrf),
            xsrfToken: forRevokeAll.Xsrf.RequestToken))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AdminRowCountAsync(forRevokeAll.OrgId)).ShouldBe(0L, "revoke-all acts on the caller's own sessions");

        // Last, because it ends the caller's own credential: the CURRENT row WITH the header writes nothing either.
        using (var revoked = await RevokeAsync(client, member, currentId, member.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldNotBeNull();
        (await AdminRowCountAsync(member.OrgId)).ShouldBe(0L);
    }

    /// <summary>
    /// Every refusal keeps its documented answer and writes nothing: a member without <c>membership:write</c>, an
    /// admin naming an org it does not administer, an unknown id, a malformed id, a session of another org's member
    /// (all 404), a mutation without the <c>X-XSRF-TOKEN</c> header (403 <c>csrf_failed</c>) and a dead caller
    /// cookie (401 <c>session_revoked</c>).
    /// </summary>
    [Fact]
    public async Task Refused_Revokes_Answer_401_403_404_And_Write_No_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var plain = await host.SignInMemberInOrgAsync(client, victim.OrgId, roleCode: null);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");
        var otherOrg = await host.SeedOrganizationAsync();

        // A live session of an account that is a member of ANOTHER org only: the admin's org must refuse it.
        var stranger = await host.SignInMemberAsync(client);
        var strangerSessionId = await host.SessionIdOfAsync(stranger.SessionCookie);
        stranger.OrgId.ShouldNotBe(victim.OrgId);

        var beforeRow = (await host.SessionRowAsync(victimSessionId)).RevokedAt;
        var beforeStranger = (await host.SessionRowAsync(strangerSessionId)).RevokedAt;
        var beforeVictimOrg = await AuditSnapshotAsync(victim.OrgId);
        var beforeOtherOrg = await AuditSnapshotAsync(otherOrg);

        // A membership is not the permission: the same org, no role.
        using (var refused = await RevokeAsync(client, plain, victimSessionId, victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        // An admin of the victim's org naming a DIFFERENT org it does not administer.
        using (var refused = await RevokeAsync(client, admin, victimSessionId, otherOrg.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        // An id that never existed, addressed by a real admin in the org it administers.
        using (var refused = await RevokeAsync(client, admin, Guid.CreateVersion7(), victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // A value that is not a uuid is the route's one 404, not a binding 400.
        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/not-a-uuid",
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            xsrfToken: admin.Xsrf.RequestToken,
            orgHeader: victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        // A live session of ANOTHER org's member, with this org's header.
        using (var refused = await RevokeAsync(client, admin, strangerSessionId, victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        // 403: the antiforgery pair's cookie without its header never reaches a handler.
        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + victimSessionId,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie, admin.Xsrf),
            orgHeader: victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.CsrfFailed);
        }

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldBe(beforeRow);
        (await host.SessionRowAsync(strangerSessionId)).RevokedAt.ShouldBe(beforeStranger);
        (await AuditSnapshotAsync(victim.OrgId)).ShouldBe(
            beforeVictimOrg,
            "no refusal may append a row to the org the caller named");
        (await AuditSnapshotAsync(otherOrg)).ShouldBe(beforeOtherOrg);

        // 401 last, because it ends the admin's own credential: a dead cookie judges before anything else does.
        var adminSessionId = await host.SessionIdOfAsync(admin.SessionCookie);
        await host.RevokeSessionAsync(adminSessionId);

        using (var refused = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + victimSessionId,
            SessionTestHost.CookieHeaderFor(admin.SessionCookie),
            orgHeader: victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.SessionRevoked);
        }

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldBe(beforeRow);
        (await AdminRowCountAsync(victim.OrgId)).ShouldBe(0L, "not one refusal may leave an administrative row");
    }

    /// <summary>
    /// A second administrative call for the SAME session writes no second row: the row is already dead, so the
    /// definer answers <c>not_found</c> before it appends anything.
    /// </summary>
    [Fact]
    public async Task A_Second_Admin_Revoke_Of_The_Same_Dead_Session_Writes_No_Second_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        using (var revoked = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var afterFirst = await AuditSnapshotAsync(victim.OrgId);
        (await AdminRowCountAsync(victim.OrgId)).ShouldBe(1L);

        using (var replay = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the target is dead, so the definer answers not_found");
            (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.NotFound);
        }

        var afterReplay = await AuditSnapshotAsync(victim.OrgId);

        afterReplay.Rows.ShouldBe(afterFirst.Rows, "a 404 revokes nothing, so it records nothing");
        afterReplay.MaxSeq.ShouldBe(afterFirst.MaxSeq);
    }

    /// <summary>
    /// The definer's own fault: a throwaway trigger on <c>audit_log</c> that refuses this action's insert makes the
    /// whole call abort — 503 <c>unavailable</c>, the member's session STILL live, no row, no cookie touched — and
    /// the retry, with the fault gone, records the revoke exactly once.
    /// </summary>
    [Fact]
    public async Task A_Faulted_Audit_Rolls_The_Revoke_Back_And_Answers_503()
    {
        using var logs = new CapturingLogProvider();
        using var host = SessionTestHost.Start(Fixture, logProvider: logs);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        var before = await AuditSnapshotAsync(victim.OrgId);
        logs.Clear();

        try
        {
            await InstallAuditFaultAsync(victim.OrgId);

            using var failed = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString());

            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await SessionTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);
            AssertNoSessionCookie(failed, "the admin's own credential is never ended by a refused revoke");
        }
        finally
        {
            await DropAuditFaultAsync();
        }

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldBeNull(
            "the revoke shares the definer's transaction, so a failed append leaves the session live");
        (await AuditSnapshotAsync(victim.OrgId)).ShouldBe(before, "the faulted call committed nothing");

        // No line may name the session, in either spelling, whatever the fault produced.
        foreach (var entry in logs.Entries)
        {
            entry.Message.ShouldNotContain(victimSessionId.ToString(UuidText), Case.Sensitive);
            entry.Message.ShouldNotContain(victimSessionId.ToString("N"), Case.Sensitive);
        }

        // The retry, with the fault gone, is an ordinary successful administrative revoke.
        using (var retried = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            retried.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the failed call left nothing behind");
        }

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldNotBeNull();
        (await AuditSnapshotAsync(victim.OrgId)).Rows.ShouldBe(before.Rows + 1, "the retry records the revoke once");

        var row = await NewestAuditRowAsync(victim.OrgId);
        row.Action.ShouldBe(AdminRevokeAction);
        row.ObjectId.ShouldBe(victimSessionId.ToString(UuidText));
        (await VerifyAsync(victim.OrgId)).ShouldBeNull();
    }

    /// <summary>
    /// A caller that leaves mid-flight can no longer tear the two halves apart: at every abort point, either the
    /// answer is 204 and the row exists, or the session is still live and no row does.
    /// </summary>
    /// <remarks>
    /// The abort delay sweeps the interesting window — before the definer starts, while it is running and after it
    /// committed — and each iteration mints its own session, so a committed revoke never confuses the next probe.
    /// The assertion is the invariant, not a timing guess: the two states a transaction can leave are the only two
    /// this suite accepts.
    /// </remarks>
    [Fact]
    public async Task An_Aborted_Request_Never_Leaves_A_Revoked_Session_Unrecorded()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        var sawNoContent = false;

        foreach (var delay in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(50),
        })
        {
            var probe = await host.CreateSessionAsync(victim.UserId);
            var probeId = await host.SessionIdOfAsync(probe.CookieValue);

            using var abort = new CancellationTokenSource();
            abort.CancelAfter(delay);

            HttpResponseMessage? response = null;
            try
            {
                response = await RevokeAsync(client, admin, probeId, victim.OrgId.ToString(), abort.Token);
            }
            catch (OperationCanceledException)
            {
                // The caller left before a response existed: the invariant below still has to hold.
            }

            using (response)
            {
                // Both facts in ONE statement, so they come from one snapshot: the server may still be finishing a
                // request the client already dropped, and two reads could straddle that commit.
                var state = await ProbeStateAsync(victim.OrgId, probeId);

                if (response?.StatusCode == HttpStatusCode.NoContent)
                {
                    sawNoContent = true;
                    state.Revoked.ShouldBeTrue("a 204 is a committed revoke");
                    state.Rows.ShouldBe(1L, "and the row committed with it, whatever the caller did next");
                }
                else
                {
                    state.Rows.ShouldBe(
                        state.Revoked ? 1L : 0L,
                        "the revoke and its row are one transaction: a live session has no row and a record has a revoke");
                }
            }
        }

        sawNoContent.ShouldBeTrue("the sweep must include at least one request that finished");

        // The control: an unaborted call on a fresh session still answers 204 and records it, so a sweep in which
        // every probe happened to be cancelled cannot pass this case by being vacuous.
        var control = await host.CreateSessionAsync(victim.UserId);
        var controlId = await host.SessionIdOfAsync(control.CookieValue);

        using (var finished = await RevokeAsync(client, admin, controlId, victim.OrgId.ToString()))
        {
            finished.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var controlState = await ProbeStateAsync(victim.OrgId, controlId);
        controlState.Revoked.ShouldBeTrue("the control request ran to completion");
        controlState.Rows.ShouldBe(1L);
    }

    /// <summary>
    /// F58: the administrative row carries the call's correlation ids — the request id the response echoes and the
    /// trace id of the trace the caller joined through <c>traceparent</c> — and the org's chain still verifies.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_Records_The_Request_And_Trace_Ids_Of_The_Call()
    {
        // A listener makes the host open a server activity per request, which is what a traceparent needs to be
        // continued into: without one the request runs outside a trace and the row would have no trace id at all.
        using var spans = new SpanCollector();
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        const string InboundRequestId = "f58-http-request-1";

        using (var revoked = await SendRevokeAsync(
            client,
            admin,
            victimSessionId,
            victim.OrgId.ToString(),
            requestId: InboundRequestId,
            traceParent: TraceParentOf(JoinedTraceId)))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            ResponseRequestId(revoked).ShouldBe(
                InboundRequestId,
                "a safe inbound id is the effective one, and the response echoes it");
        }

        var row = await NewestAuditRowAsync(victim.OrgId);
        row.Action.ShouldBe(AdminRevokeAction);
        row.ObjectId.ShouldBe(victimSessionId.ToString(UuidText));
        row.RequestId.ShouldBe(
            InboundRequestId,
            "F58 stores the same request id the C# audit writer records for this request");
        row.TraceId.ShouldBe(
            JoinedTraceId,
            "F58 stores the trace id of the trace the caller joined, so the row joins its trace");

        (await VerifyAsync(victim.OrgId)).ShouldBeNull(
            "a row carrying both correlation ids still canonicalises exactly as the C# writer hashes it");
    }

    /// <summary>
    /// F58 boundaries: an absent, over-long or space-carrying <c>X-Request-Id</c> is replaced by a minted id at the
    /// edge, so the row stores the MINTED id — the one the response echoes — and never the raw value the caller
    /// sent, while an id the definer would refuse can never abort the revoke.
    /// </summary>
    [Fact]
    public async Task An_Absent_Or_Unsafe_Request_Id_Is_Minted_And_The_Row_Keeps_The_Minted_One()
    {
        using var spans = new SpanCollector();
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");

        // No header at all, one character over the edge's 128 bound, and a printable value that is not a token
        // (CorrelationNames.IsSafeRequestId refuses the space): each is replaced, never echoed raw.
        string?[] inbound = [null, new string('a', 129), "f58 request with a space"];

        foreach (var raw in inbound)
        {
            host.Clock.Advance(TimeSpan.FromSeconds(5));
            var probe = await host.CreateSessionAsync(victim.UserId);
            var probeId = await host.SessionIdOfAsync(probe.CookieValue);

            using var revoked = await SendRevokeAsync(
                client,
                admin,
                probeId,
                victim.OrgId.ToString(),
                requestId: raw);

            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var minted = ResponseRequestId(revoked);
            minted.ShouldNotBeNullOrEmpty("the edge mints an id when it accepts none");

            if (raw is not null)
            {
                minted.ShouldNotBe(raw, "a value the edge refused is never echoed");
            }

            var row = await NewestAuditRowAsync(victim.OrgId);
            row.Action.ShouldBe(AdminRevokeAction);
            row.ObjectId.ShouldBe(probeId.ToString(UuidText));
            row.RequestId.ShouldBe(minted, "the row stores the minted id, the same one the response echoes");
            row.TraceId.ShouldNotBeNull("the request ran inside a trace");
            row.TraceId!.ShouldMatch(TraceIdShape);
        }

        (await VerifyAsync(victim.OrgId)).ShouldBeNull("a minted id canonicalises as any other");
    }

    /// <summary>
    /// F58 on the owner path: a self revoke that carries a request id and joins a trace writes no administrative
    /// row, so neither id is stored anywhere — the account acted on its own session (F48, F55, D-S102-Q3).
    /// </summary>
    [Fact]
    public async Task A_Self_Revoke_Carrying_Correlation_Ids_Still_Records_No_Row()
    {
        using var spans = new SpanCollector();
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);

        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var second = await host.CreateSessionAsync(member.UserId);
        var secondId = await host.SessionIdOfAsync(second.CookieValue);
        var before = await AuditSnapshotAsync(member.OrgId);

        using (var revoked = await SendRevokeAsync(
            client,
            member,
            secondId,
            member.OrgId.ToString(),
            requestId: "f58-self-request-1",
            traceParent: TraceParentOf(JoinedTraceId)))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            ResponseRequestId(revoked).ShouldBe("f58-self-request-1");
        }

        (await host.SessionRowAsync(secondId)).RevokedAt.ShouldNotBeNull("the caller may end its own second device");
        (await AdminRowCountAsync(member.OrgId)).ShouldBe(0L, "a self revoke is not an administrative action");
        (await AuditSnapshotAsync(member.OrgId)).ShouldBe(
            before,
            "no row was written, so no request id or trace id was stored either");
    }

    /// <summary>One <c>DELETE /sessions/{s}</c> as <paramref name="caller"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account.</param>
    /// <param name="sessionId">The addressed row.</param>
    /// <param name="orgHeader">The <c>X-Evidgate-Org</c> value, or null for the owner path only.</param>
    /// <param name="cancellationToken">The request's own token, or null for the suite's.</param>
    private static Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        SignedInMember caller,
        Guid sessionId,
        string? orgHeader = null,
        CancellationToken? cancellationToken = null) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + sessionId,
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken,
            orgHeader: orgHeader,
            cancellationToken: cancellationToken);

    /// <summary>One org's chain as a case compares it before and after an action.</summary>
    /// <param name="Rows">How many rows the org's chain holds.</param>
    /// <param name="MaxSeq">The newest position, or 0 for an empty chain.</param>
    private sealed record OrgAuditSnapshot(long Rows, long MaxSeq);

    /// <summary>
    /// The same <c>DELETE /sessions/{s}</c>, carrying the edge's correlation headers (F58): the
    /// <c>X-Request-Id</c> the middleware judges and the <c>traceparent</c> a caller joins, built here because the
    /// suite's own sender fixes its header set.
    /// </summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account.</param>
    /// <param name="sessionId">The addressed row.</param>
    /// <param name="orgHeader">The <c>X-Evidgate-Org</c> value, or null for the owner path only.</param>
    /// <param name="requestId">The <c>X-Request-Id</c> value to send, or null for none.</param>
    /// <param name="traceParent">The <c>traceparent</c> value to send, or null for none.</param>
    private static async Task<HttpResponseMessage> SendRevokeAsync(
        HttpClient client,
        SignedInMember caller,
        Guid sessionId,
        string? orgHeader,
        string? requestId,
        string? traceParent = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, SessionsPath + "/" + sessionId);
        request.Headers.Add("User-Agent", SessionTestHost.UserAgent);
        request.Headers.Add("Cookie", SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf));
        request.Headers.Add(SessionTestHost.XsrfHeaderName, caller.Xsrf.RequestToken);

        if (orgHeader is not null)
        {
            request.Headers.Add("X-Evidgate-Org", orgHeader);
        }

        if (requestId is not null)
        {
            // A case sends values the edge must judge itself (one over the bound, one carrying a space), so the
            // client's own validation is bypassed: the request has to carry exactly what a hostile caller sends.
            request.Headers.TryAddWithoutValidation(RequestIdHeaderName, requestId).ShouldBeTrue(
                "the request-id header name is valid");
        }

        if (traceParent is not null)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceParent).ShouldBeTrue(
                "the traceparent header name is valid");
        }

        // Awaited inside the using scope, the request must outlive the send (the SessionTestHost rule).
        return await client.SendAsync(request, Token).ConfigureAwait(false);
    }

    /// <summary>A <c>traceparent</c> joining <paramref name="traceId"/> as a sampled client call.</summary>
    /// <param name="traceId">The 32-hex trace id to join.</param>
    private static string TraceParentOf(string traceId) => "00-" + traceId + "-" + JoinedSpanId + "-01";

    /// <summary>The effective request id the edge echoed: every response of this host carries one (00 §9.1).</summary>
    /// <param name="response">The response.</param>
    private static string ResponseRequestId(HttpResponseMessage response)
    {
        response.Headers.TryGetValues(RequestIdHeaderName, out var values).ShouldBeTrue(
            "the request-id middleware echoes the effective id on every response");

        return values!.ShouldHaveSingleItem();
    }

    /// <summary>The one row these cases read back, every column included.</summary>
    /// <param name="Seq">Position in the org's chain.</param>
    /// <param name="ActorType">Who acted.</param>
    /// <param name="ActorId">The actor's id as text.</param>
    /// <param name="Action">The audited action string.</param>
    /// <param name="ObjectType">The object kind, or null.</param>
    /// <param name="ObjectId">The object id, or null.</param>
    /// <param name="RequestId">The 00 §9.1 request id the row was stamped with (F58), or null.</param>
    /// <param name="TraceId">The W3C trace id the row was stamped with (F58), or null.</param>
    /// <param name="MetadataJson">The stored metadata as JSON text.</param>
    /// <param name="Payload">Every stored column as JSON text, which is what the secrecy assertion inspects.</param>
    private sealed record StoredAuditRow(
        long Seq,
        string ActorType,
        string ActorId,
        string Action,
        string? ObjectType,
        string? ObjectId,
        string? RequestId,
        string? TraceId,
        string MetadataJson,
        string Payload);

    /// <summary>The org's chain size and newest position, read as the owner.</summary>
    /// <param name="orgId">The org whose chain is measured.</param>
    private async Task<OrgAuditSnapshot> AuditSnapshotAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*), coalesce(max(seq), 0) from public.audit_log where org_id = @org";
        command.Parameters.AddWithValue("org", orgId);

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        return new OrgAuditSnapshot(reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>How many administrative-revoke rows the org's chain holds.</summary>
    /// <param name="orgId">The org whose chain is counted.</param>
    private Task<long> AdminRowCountAsync(Guid orgId) =>
        OwnerScalarAsync<long>(
            "select count(*) from public.audit_log where org_id = @org and action = @action",
            ("org", orgId),
            ("action", AdminRevokeAction));

    /// <summary>
    /// The two facts the abort case compares, read in ONE statement so both come from one snapshot — a commit that
    /// lands between two statements would otherwise report a live session with a recorded revoke.
    /// </summary>
    /// <param name="orgId">The org whose chain is counted.</param>
    /// <param name="sessionId">The session row under test.</param>
    private async Task<ProbeState> ProbeStateAsync(Guid orgId, Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select exists (
                     select 1
                       from public.session s
                      where s.id = @id
                        and s.revoked_at is not null
                   )
                 , (
                     select count(*)
                       from public.audit_log a
                      where a.org_id = @org
                        and a.action = @action
                        and a.object_id = @oid
                   )
            """;
        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("org", orgId);
        command.Parameters.AddWithValue("action", AdminRevokeAction);
        command.Parameters.AddWithValue("oid", sessionId.ToString(UuidText));

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue();

        return new ProbeState(reader.GetBoolean(0), reader.GetInt64(1));
    }

    /// <summary>Whether the session is revoked, and how many administrative-revoke rows name it.</summary>
    /// <param name="Revoked">The <c>session.revoked_at</c> is set.</param>
    /// <param name="Rows">The org's <c>auth.session_revoke_admin</c> rows for that session.</param>
    private sealed record ProbeState(bool Revoked, long Rows);

    /// <summary>The newest row of the org's chain, read as the owner with every column.</summary>
    /// <param name="orgId">The org whose chain is read.</param>
    private async Task<StoredAuditRow> NewestAuditRowAsync(Guid orgId)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            select a.seq
                 , a.actor_type
                 , a.actor_id
                 , a.action
                 , a.object_type
                 , a.object_id
                 , a.request_id
                 , a.trace_id
                 , coalesce(a.metadata, '{}'::jsonb)::text
                 , to_jsonb(a)::text
              from public.audit_log a
             where a.org_id = @org
             order by a.seq desc
             limit 1
            """;
        command.Parameters.AddWithValue("org", orgId);

        await using var reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).ShouldBeTrue("the org's chain must hold the row under test");

        return new StoredAuditRow(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            await reader.IsDBNullAsync(4, Token) ? null : reader.GetString(4),
            await reader.IsDBNullAsync(5, Token) ? null : reader.GetString(5),
            await reader.IsDBNullAsync(6, Token) ? null : reader.GetString(6),
            await reader.IsDBNullAsync(7, Token) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9));
    }

    /// <summary>Recomputes the org's chain with the production verifier, inside that org's scope.</summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    private async Task<AuditChainFinding?> VerifyAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(
            orgId,
            readOnlyTransaction: true,
            cancellationToken: Token);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token);
    }

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that refuses the administrative-revoke action of
    /// this org only, so no other write of the harness is disturbed. Created as the owner (the table's owner), under
    /// names no production object uses.
    /// </summary>
    /// <param name="orgId">The org whose row must be refused.</param>
    private async Task InstallAuditFaultAsync(Guid orgId)
    {
        await OwnerExecuteAsync(
            """
            create or replace function public.f55_http_audit_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 'f55 http audit fault' using errcode = '58000';
              end
              $$;
            """,
            []);

        // A utility statement takes no bind parameters, so the org travels as a literal uuid (a Guid's "D" text can
        // carry no SQL) and the action is the pinned constant.
        await OwnerExecuteAsync(
            "create trigger f55_http_audit_fault before insert on public.audit_log "
            + "for each row when (new.action = '" + AdminRevokeAction + "' and new.org_id = '" + orgId.ToString(UuidText) + "'::uuid) "
            + "execute function public.f55_http_audit_fault()",
            []);
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the test's outcome was.</summary>
    private async Task DropAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists f55_http_audit_fault on public.audit_log", []);
        await OwnerExecuteAsync("drop function if exists public.f55_http_audit_fault()", []);
    }

    /// <summary>Runs one statement on the owner connection (arrangement only: a test may not arrange via the tenant path).</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="values">Its parameters.</param>
    private async Task OwnerExecuteAsync(string sql, (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>One scalar, read as the owner.</summary>
    /// <typeparam name="T">The scalar's type.</typeparam>
    /// <param name="sql">The query.</param>
    /// <param name="values">Its parameters.</param>
    private async Task<T> OwnerScalarAsync<T>(string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>
    /// Nothing credential-shaped in a stored row or a log line: not the cookie value, not its HMAC in either
    /// spelling, not the pepper in either spelling.
    /// </summary>
    /// <param name="text">The stored row's JSON, or the formatted log message.</param>
    /// <param name="host">The host whose pepper is checked.</param>
    /// <param name="sessionCookie">The victim's cookie value.</param>
    private static void AssertCarriesNoCredential(string text, SessionTestHost host, string sessionCookie)
    {
        var hmac = host.HmacOf(sessionCookie);

        text.ShouldNotContain(sessionCookie, Case.Sensitive);
        text.ShouldNotContain(Convert.ToHexString(hmac), Case.Insensitive);
        text.ShouldNotContain(Convert.ToBase64String(hmac), Case.Sensitive);
        text.ShouldNotContain(Convert.ToBase64String(host.Pepper), Case.Sensitive);
        text.ShouldNotContain(Convert.ToHexString(host.Pepper), Case.Insensitive);
    }

    /// <summary>
    /// The complement of the sign-out shape: a response that must NOT touch the caller's cookies — neither an
    /// expiry nor a re-issue — because the session it presented is still the credential.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="because">Why the response must leave the cookies alone.</param>
    private static void AssertNoSessionCookie(HttpResponseMessage response, string because)
    {
        foreach (var cookieName in new[]
        {
            SessionTestHost.SessionCookieName,
            SessionTestHost.XsrfCookieName,
            SessionTestHost.AntiforgeryCookieName,
        })
        {
            SessionTestHost.Cookies(response, cookieName).ShouldBeEmpty($"{cookieName}: {because}");
        }
    }
}
