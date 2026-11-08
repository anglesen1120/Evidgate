using System.Text.Json;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// The administrative revoke-all at the seam the route uses: one call ends every live session of a target member
/// and appends ONE audit row inside the same transaction, in the target's org chain.
/// </summary>
/// <remarks>
/// A row is only accepted when the existing <c>AuditChainVerifier</c> reads the chain as unbroken, so nothing here
/// re-implements the hash. The refusals of a custom role's scope and the definer's grants are in
/// <see cref="SessionRevokeAllAdminAclTests"/>; its lock order and its concurrency in
/// <see cref="SessionRevokeAllAdminLockTests"/>.
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRevokeAllAdminDefinerTests : SessionRevokeAllAdminTestBase
{
    /// <summary>Own database: the rollback case installs a trigger on <c>public.audit_log</c>.</summary>
    /// <param name="fixture">The assembly fixture.</param>
    public SessionRevokeAllAdminDefinerTests(DatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// The administered call: both live rows of the target are revoked, the caller's own row is not, and exactly
    /// one row is appended — the admin as actor, the TARGET user as object, the count and the target in its
    /// metadata, and the call's correlation ids on the row.
    /// </summary>
    [Fact]
    public async Task An_Admin_Call_Writes_One_Row_For_The_Target_Member()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var first = await CreateSessionAsync(member, clock);
        var second = await CreateSessionAsync(member, clock);

        var (status, revoked) = await CreateRoutes(clock)
            .RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), member, org, SampleRequestId, SampleTraceId, Token);

        status.ShouldBe(SessionRouteStatus.Done);
        revoked.ShouldBe(2, "both live rows of the target are ended by one call");
        (await LiveCountAsync(member)).ShouldBe(0L);
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue("the caller's own session is never the target");
        (await IsLiveAsync(first.SessionId)).ShouldBeFalse();
        (await IsLiveAsync(second.SessionId)).ShouldBeFalse();

        (await AdminRowCountAsync(org)).ShouldBe(1L, "exactly one row per call, not one per revoked session");

        var row = await NewestAdminRowAsync(org);

        row[0].ShouldBe("user");
        row[1].ShouldBe(admin.ToString("D"), "the actor is the admin whose proof made the call");
        row[2].ShouldBe(AdminRevokeAction);
        row[3].ShouldBe("user", "the object is the account whose sessions ended");
        row[4].ShouldBe(member.ToString("D"));
        row[5].ShouldBe(SampleRequestId);
        row[6].ShouldBe(SampleTraceId);

        var metadata = ((string?)row[7]).ShouldNotBeNull("every audit row of this action carries its metadata object");
        using var document = JsonDocument.Parse(metadata);
        string.Join(",", document.RootElement.EnumerateObject().Select(property => property.Name))
            .ShouldBe(
                "revoked_count,target_user_id",
                "the canonical string sorts the keys by UTF-16 code unit, which the row's hash depends on");
        document.RootElement.GetProperty("revoked_count").GetString().ShouldBe("2", "metadata values are strings, never numbers");
        document.RootElement.GetProperty("target_user_id").GetString().ShouldBe(member.ToString("D"));
    }

    /// <summary>
    /// Two calls leave two linked rows — the second built on the first's entry hash — and the production verifier
    /// reads the chain as unbroken, so a row this definer writes is what the <c>audit.verify_chain</c> task
    /// accepts, not a second hash implementation's opinion.
    /// </summary>
    [Fact]
    public async Task The_Definer_Rows_Chain_And_The_Audit_Verifier_Accepts_Them()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var first = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var second = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(first, clock);
        await CreateSessionAsync(second, clock);

        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), first, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Done);
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), second, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Done);

        (await AdminRowCountAsync(org)).ShouldBe(2L, "one row per call, never one per revoked session");

        // Every row after the first continues from the one before it: one chain, not two independent rows.
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from (
                    select prev_hash
                         , lag(entry_hash) over (order by seq) as expected_prev
                      from public.audit_log
                     where org_id = @org
                   ) r
             where r.expected_prev is not null
               and r.prev_hash <> r.expected_prev
            """,
            ("org", org))).ShouldBe(0L, "each row is built on its predecessor's entry hash");

        (await VerifyAsync(org)).ShouldBeNull("the existing verifier accepts what the definer hashed");
    }

    /// <summary>
    /// The escaping edge: `"` and `\` are legal in a request id, so the row stores the value RAW and hashes the
    /// escaped JSON string. A definer that stored the escaped text, or hashed the raw one, would leave a chain the
    /// production verifier refuses — which the verifier's acceptance here rules out for this call.
    /// </summary>
    [Fact]
    public async Task A_Quoted_Request_Id_Is_Stored_Raw_And_The_Verifier_Accepts_The_Chain()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        await CreateSessionAsync(member, clock);

        var (status, revoked) = await CreateRoutes(clock).RevokeAllForUserAsAdminAsync(
            ProofOf(adminTicket), member, org, QuotedRequestId, SampleTraceId, Token);

        status.ShouldBe(SessionRouteStatus.Done);
        revoked.ShouldBe(1);

        var row = await NewestAdminRowAsync(org);

        row[5].ShouldBe(QuotedRequestId, "the column holds the raw id: escaping belongs to the canonical bytes only");
        row[6].ShouldBe(SampleTraceId);

        (await VerifyAsync(org)).ShouldBeNull(
            "the recomputed hash uses the RFC 8785 escaped string, so SQL and C# canonicalise this id identically");
    }

    /// <summary>
    /// Every refusal is <c>not_found</c> and writes nothing: a caller holding no <c>membership:write</c>, a caller
    /// naming an org it does not administer, an unknown user, a target that is not an ACTIVE member of the org, and
    /// the caller addressing itself (the self route is the only way to end your own sessions).
    /// </summary>
    [Fact]
    public async Task A_Caller_Without_The_Permission_Foreign_Org_Unknown_User_Or_Own_Id_Answers_NotFound()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var foreignOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var plain = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var removed = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, org, "viewer", status: "removed", cancellationToken: Token);
        var foreignAdmin = await IdentityTestData.SeedActiveMemberAsync(
            Fixture, foreignOrg, "tenant_admin", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var plainTicket = await CreateSessionAsync(plain, clock);
        var foreignTicket = await CreateSessionAsync(foreignAdmin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var removedTicket = await CreateSessionAsync(removed, clock);

        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(plainTicket), member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "a membership is not the permission");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(foreignTicket), member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "the caller administers no membership in this org");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), Guid.CreateVersion7(), org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "an id that never existed");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), removed, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "a removed membership is not an administered member");
        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), admin, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.NotFound, "the self route is the only way to end your own sessions");

        (await IsLiveAsync(memberTicket.SessionId)).ShouldBeTrue("a refused call revokes nothing");
        (await IsLiveAsync(removedTicket.SessionId)).ShouldBeTrue();
        (await IsLiveAsync(adminTicket.SessionId)).ShouldBeTrue();
        (await AdminRowCountAsync(org)).ShouldBe(0L, "no refusal writes a row");
        (await AdminRowCountAsync(foreignOrg)).ShouldBe(0L);
    }

    /// <summary>
    /// A proof that names no live session answers <c>unauthenticated</c> — an absent proof without a statement and
    /// a revoked session's proof alike — and nothing is revoked or recorded.
    /// </summary>
    [Fact]
    public async Task A_Dead_Proof_Answers_Unauthenticated_And_Revokes_Nothing()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeAllForUserAsAdminAsync([], member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Unauthenticated, "no cookie is no caller");

        await OwnerExecuteAsync(
            "update public.session set revoked_at = @now where id = @id",
            ("now", clock.GetUtcNow().ToUniversalTime()),
            ("id", adminTicket.SessionId));

        (await routes.RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), member, org, null, null, Token))
            .Status.ShouldBe(SessionRouteStatus.Unauthenticated, "a dead row proves no caller either");

        (await IsLiveAsync(memberTicket.SessionId)).ShouldBeTrue();
        (await AdminRowCountAsync(org)).ShouldBe(0L);
    }

    /// <summary>
    /// The append and the revoke are one transaction: a throwaway trigger on <c>audit_log</c> that aborts the
    /// insert makes the whole call fail, and EVERY session of the target is still live afterwards — a revocation
    /// without its record cannot exist.
    /// </summary>
    [Fact]
    public async Task A_Fault_In_The_Audit_Append_Rolls_Every_Revoke_Back()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var first = await CreateSessionAsync(member, clock);
        var second = await CreateSessionAsync(member, clock);

        await InstallAuditFaultAsync();

        try
        {
            var refusal = await Should.ThrowAsync<PostgresException>(
                () => CreateRoutes(clock)
                    .RevokeAllForUserAsAdminAsync(ProofOf(adminTicket), member, org, null, null, Token)
                    .AsTask());

            refusal.SqlState.ShouldBe(PostgresErrorCodes.RaiseException, "the append aborted the whole statement");
        }
        finally
        {
            await DropAuditFaultAsync();
        }

        (await IsLiveAsync(first.SessionId)).ShouldBeTrue("the revokes share the definer's transaction");
        (await IsLiveAsync(second.SessionId)).ShouldBeTrue();
        (await LiveCountAsync(member)).ShouldBe(2L);
        (await AdminRowCountAsync(org)).ShouldBe(0L);
    }
}
