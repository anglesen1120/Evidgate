using System.Net;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.Modules.Identity.Endpoints;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Testing;
using Identity.IntegrationTests.Login.Linking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Http;

/// <summary>
/// F48 over HTTP: <c>DELETE /v1/auth/sessions/{s}</c> with <c>X-Evidgate-Org</c> writes ONE
/// <c>auth.session_revoke_admin</c> row — and only when the caller is an admin ending ANOTHER member's session.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the row may say.</b> The actor is the admin whose session made the call, the chain is the org named by
/// the header, and the object is the revoked <c>session</c> row. The target user is deliberately absent (the
/// definer answers a status only; F55 returns it), so the stored row — every column, read back as JSON — is also
/// asserted to carry no cookie value, no cookie HMAC in either spelling and no pepper: the audit says who ended
/// which session where, never how the credential was proved.
/// </para>
/// <para>
/// <b>What must write nothing.</b> A self revoke (the current row, or a second device of the caller, with or
/// without the header), <c>logout</c>, <c>revoke-all</c> and every refusal are the account acting on its own
/// sessions or not acting at all; each is measured as a before/after snapshot of the org's chain, and each refusal
/// also keeps the target row live and its answer identical to the pre-F48 behaviour.
/// </para>
/// <para>
/// <b>The failure case is the honest one.</b> The writer opens its own transaction after the definer committed, so
/// a fault leaves the revoke in place and the row missing: the route answers 503, the response touches no cookie,
/// one Error line names the actor and the org and never the session id (and nothing else), and the retry answers 404
/// without reaching the writer — the window F55 closes with a definer that returns <c>target_user_id</c> and writes the
/// row in its own transaction.
/// </para>
/// </remarks>
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class AdminRevokeAuditTests : HarnessTest
{
    /// <summary>The single-session route of AC6.</summary>
    private const string SessionsPath = "/v1/auth/sessions";

    /// <summary>The logout route (no audit row: it ends the caller's own session).</summary>
    private const string LogoutPath = "/v1/auth/logout";

    /// <summary>The revoke-all route (no audit row either).</summary>
    private const string RevokeAllPath = "/v1/auth/sessions/revoke-all";

    /// <summary>The cookie-independent part of every row assertion: <c>audit_log</c> speaks uuid text.</summary>
    private const string UuidText = "D";

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
    /// The administered case: an admin revoking a member's session in the org it administers writes exactly one
    /// <c>auth.session_revoke_admin</c> row — action, actor, object, org, metadata and no credential anywhere in the
    /// stored row — and the revoked row really is dead.
    /// </summary>
    [Fact]
    public async Task An_Admin_Revoke_Writes_Exactly_One_Row_For_The_Org()
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

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldNotBeNull("the addressed row is the one revoked");

        var after = await AuditSnapshotAsync(victim.OrgId);
        after.Rows.ShouldBe(before.Rows + 1, "exactly one row, not one per org and not a second on the same call");
        after.MaxSeq.ShouldBe(before.MaxSeq + 1);

        var row = await NewestAuditRowAsync(victim.OrgId);
        row.Seq.ShouldBe(after.MaxSeq);
        row.Action.ShouldBe(IdentityAuditActions.SessionRevokeByAdmin);
        row.ActorType.ShouldBe("user");
        row.ActorId.ShouldBe(admin.UserId.ToString(UuidText), "the actor is the admin, resolved from its own cookie");
        row.ObjectType.ShouldBe("session");
        row.ObjectId.ShouldBe(victimSessionId.ToString(UuidText));
        row.MetadataJson.ShouldBe("{}", "the row carries no metadata: nothing to allowlist for a revoke");

        // The documented F48 limit: the definer does not return the target user, so the member's id is nowhere in the
        // stored row (F55 adds it, inside the definer's own transaction).
        row.Payload.ShouldNotContain(victim.UserId.ToString(UuidText), Case.Insensitive);

        AssertCarriesNoCredential(row.Payload, host, victim.SessionCookie);
    }

    /// <summary>
    /// The owner side, with the header present: the caller's own second device and the caller's current row both
    /// revoke normally — 204, the row dead — and neither writes an admin row, because the addressed row is in the
    /// caller's own live list.
    /// </summary>
    [Fact]
    public async Task A_Self_Revoke_Writes_No_Admin_Row_Even_With_The_Org_Header()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();
        var member = await host.SignInMemberAsync(client);
        var currentId = await host.SessionIdOfAsync(member.SessionCookie);

        // A second device of the same account, with the header: still the owner path, because the list says so.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var other = await host.CreateSessionAsync(member.UserId);
        var otherId = await host.SessionIdOfAsync(other.CookieValue);

        var beforeOther = await AuditSnapshotAsync(member.OrgId);

        using (var revoked = await RevokeAsync(client, member, otherId, member.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent, "a member may always end its own second device");
            AssertNoSessionCookie(revoked, "the caller's own session is still the credential");
        }

        (await host.SessionRowAsync(otherId)).RevokedAt.ShouldNotBeNull();
        (await AuditSnapshotAsync(member.OrgId)).ShouldBe(
            beforeOther,
            "a self revoke wearing the header is still the owner path: no administrative row");

        // The same second device without the header: the owner path the route always had.
        var otherWithoutHeader = await host.CreateSessionAsync(member.UserId);
        var otherWithoutHeaderId = await host.SessionIdOfAsync(otherWithoutHeader.CookieValue);
        var beforeNoHeader = await AuditSnapshotAsync(member.OrgId);

        using (var revoked = await RevokeAsync(client, member, otherWithoutHeaderId))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(otherWithoutHeaderId)).RevokedAt.ShouldNotBeNull();
        (await AuditSnapshotAsync(member.OrgId)).ShouldBe(beforeNoHeader);

        // Last, because it ends the caller's own credential: the CURRENT row WITH the header is still no admin row.
        var beforeCurrent = await AuditSnapshotAsync(member.OrgId);

        using (var revoked = await RevokeAsync(client, member, currentId, member.OrgId.ToString()))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await host.SessionRowAsync(currentId)).RevokedAt.ShouldNotBeNull();
        (await AuditSnapshotAsync(member.OrgId)).ShouldBe(beforeCurrent);
    }

    /// <summary>
    /// <c>logout</c> and <c>revoke-all</c> write no administrative row: they end the caller's own sessions, which the
    /// <c>session</c> rows already record.
    /// </summary>
    [Fact]
    public async Task Logout_And_RevokeAll_Write_No_Admin_Row()
    {
        using var host = SessionTestHost.Start(Fixture);
        using var client = host.CreateSessionClient();

        var first = await host.SignInMemberAsync(client);
        var beforeLogout = await AuditSnapshotAsync(first.OrgId);

        using (var loggedOut = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            LogoutPath,
            SessionTestHost.CookieHeaderFor(first.SessionCookie, first.Xsrf),
            xsrfToken: first.Xsrf.RequestToken))
        {
            loggedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AuditSnapshotAsync(first.OrgId)).ShouldBe(beforeLogout, "a logout is the account ending its own session");

        // A fresh sign-in for the second half: the login path writes its own `auth.login` rows, and the snapshot is
        // taken after it, so the delta belongs to `revoke-all` alone.
        var second = await host.SignInMemberAsync(client);
        var beforeRevokeAll = await AuditSnapshotAsync(second.OrgId);

        using (var revoked = await SessionTestHost.SendAsync(
            client,
            HttpMethod.Post,
            RevokeAllPath,
            SessionTestHost.CookieHeaderFor(second.SessionCookie, second.Xsrf),
            xsrfToken: second.Xsrf.RequestToken))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AuditSnapshotAsync(second.OrgId)).ShouldBe(
            beforeRevokeAll,
            "revoke-all is the account acting on its own sessions, not an administrator ending someone else's");
    }

    /// <summary>
    /// Every refusal keeps its pre-F48 answer and writes nothing: a member without <c>membership:write</c>, an admin
    /// naming an org it does not administer, an unknown id, a malformed id, and an admin naming its own org for a
    /// session that belongs to another org's member.
    /// </summary>
    [Fact]
    public async Task Refused_Administrative_Revokes_Answer_404_And_Write_No_Row()
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

        var beforeVictimOrg = await AuditSnapshotAsync(victim.OrgId);
        var beforeOtherOrg = await AuditSnapshotAsync(otherOrg);
        var beforeStrangerOrg = await AuditSnapshotAsync(stranger.OrgId);

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

        // A live session of ANOTHER org's member, with this org's header: the target's membership in the named org
        // is what the definer checks, not the member's anywhere else.
        using (var refused = await RevokeAsync(client, admin, strangerSessionId, victim.OrgId.ToString()))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await SessionTestHost.ErrorCodeAsync(refused)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldBeNull("a refused revoke writes no row state");
        (await host.SessionRowAsync(strangerSessionId)).RevokedAt.ShouldBeNull();

        (await AuditSnapshotAsync(victim.OrgId)).ShouldBe(
            beforeVictimOrg,
            "no refusal may append an administrative row to the org the caller named");
        (await AuditSnapshotAsync(otherOrg)).ShouldBe(beforeOtherOrg);
        (await AuditSnapshotAsync(stranger.OrgId)).ShouldBe(beforeStrangerOrg);
    }

    /// <summary>
    /// A second administrative call for the SAME session writes no second row: the row is already dead, so the
    /// definer answers <c>not_found</c> before the writer is ever asked.
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
        afterFirst.Rows.ShouldBeGreaterThan(0L);

        using (var replay = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            replay.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the target is dead, so the definer answers not_found");
            (await SessionTestHost.ErrorCodeAsync(replay)).ShouldBe(ApiErrorCode.NotFound);
        }

        (await AuditSnapshotAsync(victim.OrgId)).ShouldBe(afterFirst, "a 404 revokes nothing, so it records nothing");
    }

    /// <summary>
    /// The failure case, in the three fault shapes seen in practice — a database error, the org's busy chain and an
    /// unexpected writer defect: any writer throw makes the route answer 503
    /// <c>unavailable</c> with no cookie touched and no row written, leaves the revoke committed, logs one Error line
    /// that names the actor and the org but not the session id (and no credential), and the retry answers 404 without
    /// reaching the writer.
    /// </summary>
    [Theory]
    [InlineData("database")]
    [InlineData("busy-chain")]
    [InlineData("unexpected")]
    public async Task A_Failed_Audit_Answers_503_Leaves_The_Revoke_Committed_And_Logs_Only_Ids(string fault)
    {
        using var logs = new CapturingLogProvider();
        FailingSessionRevokeAuditWriter? writer = null;
        using var host = SessionTestHost.Start(
            Fixture,
            logProvider: logs,
            overrides: services =>
            {
                // The production writer behind a switch that fails only the revoke call: the sign-ins below keep
                // writing their real `auth.login` rows, so the snapshot delta belongs to this route alone.
                services.RemoveAll<IIdentityAuditWriter>();
                services.AddSingleton<IIdentityAuditWriter>(provider =>
                    writer = new FailingSessionRevokeAuditWriter(
                        AuditWriterFactory.Create(provider),
                        fault switch
                        {
                            "busy-chain" => () => new AuditBusyException(new NpgsqlException("lock not available (injected)")),
                            "unexpected" => () => new InvalidOperationException("injected writer defect (F48 test)"),
                            _ => () => new NpgsqlException("injected audit fault (F48 test)"),
                        }));
            });
        using var client = host.CreateSessionClient();
        var victim = await host.SignInMemberAsync(client);
        var victimSessionId = await host.SessionIdOfAsync(victim.SessionCookie);
        var admin = await host.SignInMemberInOrgAsync(client, victim.OrgId, "tenant_admin");
        writer.ShouldNotBeNull("the sign-in resolves the writer from the container");

        var before = await AuditSnapshotAsync(victim.OrgId);
        logs.Clear();

        using (var failed = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await SessionTestHost.ErrorCodeAsync(failed)).ShouldBe(ApiErrorCode.Unavailable);
            AssertNoSessionCookie(failed, "the admin's own credential is never ended by a failed audit");
        }

        // The revoke committed (the definer's transaction) and the row did not (the writer's): that window is what
        // the 503 reports, and F55 closes it by writing inside the definer's transaction.
        (await host.SessionRowAsync(victimSessionId)).RevokedAt.ShouldNotBeNull();
        (await AuditSnapshotAsync(victim.OrgId)).ShouldBe(before);
        writer!.Calls.ShouldBe(1);

        var errors = logs.Entries
            .Where(entry => entry.Level == LogLevel.Error
                && string.Equals(entry.Category, typeof(SessionEndpoints).FullName, StringComparison.Ordinal))
            .ToList();
        var error = errors.ShouldHaveSingleItem("one committed-but-unrecorded revoke is one Error line");

        // The line names who and where, never which row: 02.3.2 D9 puts the session id (like the cookie value) out of
        // every log line, in both of its spellings, even though the audit row itself carries it (D-S102-Q3).
        error.Message.ShouldContain(admin.UserId.ToString(UuidText));
        error.Message.ShouldContain(victim.OrgId.ToString(UuidText));
        error.Message.ShouldNotContain(victimSessionId.ToString(UuidText), Case.Sensitive);
        error.Message.ShouldNotContain(victimSessionId.ToString("N"), Case.Sensitive);
        AssertCarriesNoCredential(error.Message, host, victim.SessionCookie);

        // The documented retry: the session is dead, so the answer is 404 and the writer is never asked again.
        using (var retry = await RevokeAsync(client, admin, victimSessionId, victim.OrgId.ToString()))
        {
            retry.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        (await AuditSnapshotAsync(victim.OrgId)).ShouldBe(before);
        writer.Calls.ShouldBe(1, "the retry never reaches the writer");
    }

    /// <summary>One <c>DELETE /sessions/{s}</c> as <paramref name="caller"/>'s browser sends it.</summary>
    /// <param name="client">A client of the host.</param>
    /// <param name="caller">The signed-in account.</param>
    /// <param name="sessionId">The addressed row.</param>
    /// <param name="orgHeader">The <c>X-Evidgate-Org</c> value, or null for the owner path only.</param>
    private static Task<HttpResponseMessage> RevokeAsync(
        HttpClient client,
        SignedInMember caller,
        Guid sessionId,
        string? orgHeader = null) =>
        SessionTestHost.SendAsync(
            client,
            HttpMethod.Delete,
            SessionsPath + "/" + sessionId,
            SessionTestHost.CookieHeaderFor(caller.SessionCookie, caller.Xsrf),
            xsrfToken: caller.Xsrf.RequestToken,
            orgHeader: orgHeader);

    /// <summary>One org's chain as a case compares it before and after an action.</summary>
    /// <param name="Rows">How many rows the org's chain holds.</param>
    /// <param name="MaxSeq">The newest position, or 0 for an empty chain.</param>
    private sealed record OrgAuditSnapshot(long Rows, long MaxSeq);

    /// <summary>The one row these cases read back, every column included.</summary>
    /// <param name="Seq">Position in the org's chain.</param>
    /// <param name="ActorType">Who acted.</param>
    /// <param name="ActorId">The actor's id as text.</param>
    /// <param name="Action">The audited action string.</param>
    /// <param name="ObjectType">The object kind, or null.</param>
    /// <param name="ObjectId">The object id, or null.</param>
    /// <param name="MetadataJson">The stored metadata as JSON text.</param>
    /// <param name="Payload">Every stored column as JSON text, which is what the secrecy assertion inspects.</param>
    private sealed record StoredAuditRow(
        long Seq,
        string ActorType,
        string ActorId,
        string Action,
        string? ObjectType,
        string? ObjectId,
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
            reader.GetString(6),
            reader.GetString(7));
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

/// <summary>
/// The production identity audit writer behind a switch that fails only the administrative session-revoke call
/// (F48): the sign-in path keeps writing its real rows, so a case can prove the revoke route's fail-closed answer
/// without turning the member's sign-in into the failure.
/// </summary>
/// <param name="inner">The writer every other call reaches unchanged.</param>
/// <param name="fault">
/// The fault the revoke call raises: a database error (<see cref="NpgsqlException"/>) or the org's busy chain
/// (<see cref="AuditBusyException"/>), the two shapes the handler must answer and log the same way.
/// </param>
internal sealed class FailingSessionRevokeAuditWriter(IIdentityAuditWriter inner, Func<Exception> fault) : IIdentityAuditWriter
{
    /// <summary>How many times the revoke route reached the writer.</summary>
    internal int Calls { get; private set; }

    /// <inheritdoc />
    public ValueTask WriteAsync(
        IdentityAuditEvent auditEvent,
        IReadOnlyCollection<Guid> orgIds,
        CancellationToken cancellationToken) =>
        inner.WriteAsync(auditEvent, orgIds, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Fails like the deployment's writer does on a busy chain or a vanished org.</remarks>
    public ValueTask WriteSessionRevokedAsync(
        Guid actorUserId,
        Guid orgId,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        Calls++;

        throw fault();
    }
}
