using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// F55 (02.3.2-T2.1 AC6, completing coordinator row F48) and F58: the administrative session revoke and its
/// <c>audit_log</c> row are ONE transaction, written by the definer itself (migration 0050), and since 0051 the
/// row carries the caller's request and trace ids so it joins the HTTP request that caused it.
/// </summary>
/// <remarks>
/// <para>
/// Every write under test runs through the production statements: the revoke through
/// <see cref="SessionRoutesStore"/> over the app-role data source (the route's own call), and the
/// application rows around it through the real <see cref="IdentityAuditWriter"/> and
/// <see cref="AuditTrail"/>, so the chain guard, the advisory lock and the append path are the
/// production ones. Rows are read through a real read-only tenant scope and the chain is recomputed
/// by <see cref="AuditChainVerifier"/> — the same verifier the <c>audit.verify_chain</c> task runs.
/// The hash rule is never re-implemented here: a row this migration writes has to be accepted by the
/// EXISTING verifier, which is the whole point of doing it in SQL.
/// </para>
/// <para>
/// Arrangement uses the owner seam (00 §5.5 C3): orgs, memberships and the two throwaway objects of
/// the atomicity cases are seeded there, because a test may not arrange through the tenant path.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class SessionRevokeAuditDefinerTests : SessionHardeningTestBase
{
    /// <summary>The action string the definer writes; it lives in SQL after F55, so the literal is pinned here.</summary>
    private const string AdminRevokeAction = "auth.session_revoke_admin";

    /// <summary>
    /// A request id the edge accepts (<c>CorrelationNames.IsSafeRequestId</c>: visible ASCII, 1..128), of the
    /// shape <c>RequestIdMiddleware</c> mints and <c>AuditTrail.Record</c> reads back off the baggage.
    /// </summary>
    private const string SampleRequestId = "req-4f1a2b3c-S122";

    /// <summary>A W3C trace id: 32 lowercase hex digits, and not the all-zero value the standard refuses.</summary>
    private const string SampleTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>
    /// A request id with the two characters RFC 8785 escaping must handle, and nothing else: `"` and `\`. It is
    /// a legal id (both are visible ASCII), so the stored value is the raw string while the HASHED value is the
    /// escaped JSON string — the pair the C# writer produces for the same input.
    /// </summary>
    private const string QuotedRequestId = "req-\"quoted\"-back\\slash";

    /// <summary>
    /// The boundary cases of 0051's validation: what a caller may pass, and what the definer must store
    /// instead — (request id, trace id, expected stored request id, expected stored trace id). Every pair has
    /// at least one malformed member, and the valid member must survive: nulling the whole pair would hide a
    /// validator that gives up on one bad value.
    /// </summary>
    private static readonly (string? Request, string? Trace, string? ExpectedRequest, string? ExpectedTrace)[] MalformedIds =
    [
        (new string('a', 129), SampleTraceId, null, SampleTraceId),
        ("req-with-cr\r\n", SampleTraceId, null, SampleTraceId),
        ("req with space", SampleTraceId, null, SampleTraceId),
        (string.Empty, SampleTraceId, null, SampleTraceId),
        (SampleRequestId, SampleTraceId.ToUpperInvariant(), SampleRequestId, null),
        (SampleRequestId, SampleTraceId[..31], SampleRequestId, null),
        (SampleRequestId, new string('0', 32), SampleRequestId, null),
        ("req-with-del\u007f", SampleTraceId, null, SampleTraceId),
        ("req-with-\u00e9", SampleTraceId, null, SampleTraceId),
    ];

    /// <summary>
    /// The accepted side of the same boundary: request ids the validator KEEPS, so the stored value must equal
    /// the argument — one character, exactly 128 characters (the cap, not the cap plus one), the two endpoints of
    /// visible ASCII <c>!</c> (0x21) and <c>~</c> (0x7E), and one id that pairs the two characters RFC 8785 must
    /// escape (<c>"</c> and <c>\</c>) with json-sensitive punctuation that must NOT be escaped
    /// (<c>&lt; &gt; &amp; ' + /</c>).
    /// </summary>
    private static readonly string[] AcceptedRequestIds =
    [
        "a",
        new string('a', 128),
        "!",
        "~",
        "req-\"q\"-\\\\-<&>'+/",
    ];

    /// <summary>Own database: the atomicity cases install an <c>audit_log</c> trigger and one case counts every row.</summary>
    public SessionRevokeAuditDefinerTests(DatabaseFixture fixture)
        : base(fixture, DatabaseIsolation.Dedicated)
    {
    }

    /// <summary>
    /// AC6: an admin ending a member's session appends exactly ONE audit row — the admin as actor,
    /// the revoked <c>session</c> as object, the member in <c>target_user_id</c>, the org from the
    /// request — and the row carries no credential and no deadline of the session it names.
    /// </summary>
    [Fact]
    public async Task Admin_Revoke_Writes_One_Audit_Row_With_The_Admin_The_Session_And_The_Member()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, null, null, Token))
            .ShouldBe(SessionRouteStatus.Done);

        var row = (await RowsAsync(org)).ShouldHaveSingleItem();

        row.Seq.ShouldBe(1L, "the row is the org chain's first");
        row.ActorType.ShouldBe("user");
        row.ActorId.ShouldBe(admin.ToString("D"), "the actor is the administrator whose credential made the call");
        row.Action.ShouldBe(AdminRevokeAction);
        row.ObjectType.ShouldBe("session");
        row.ObjectId.ShouldBe(memberTicket.SessionId.ToString("D"), "the object is the row that was ended");

        using var document = JsonDocument.Parse(row.MetadataJson);
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["target_user_id"]);
        document.RootElement.GetProperty("target_user_id").GetString().ShouldBe(member.ToString("D"));

        // Nothing of the credential reaches the audit trail: not the cookie value, not its HMAC (in
        // any of the encodings a careless hand would use), not the pepper, and not a deadline.
        var serialized = await SerializedRowAsync(org);
        var hmac = HmacOf(Pepper, memberTicket.CookieValue);

        serialized.ShouldNotContain(memberTicket.CookieValue, Case.Sensitive);
        serialized.ShouldNotContain(Convert.ToHexString(hmac), Case.Insensitive);
        serialized.ShouldNotContain(Convert.ToBase64String(hmac), Case.Sensitive);
        serialized.ShouldNotContain(PepperBase64, Case.Sensitive);
        serialized.ShouldNotContain("cookie", Case.Insensitive);
        serialized.ShouldNotContain("pepper", Case.Insensitive);
        serialized.ShouldNotContain("absolute_expires", Case.Insensitive);

        (await VerifyAsync(org)).ShouldBeNull("the row the definer appended verifies with the production verifier");
    }

    /// <summary>
    /// The interoperability contract: a row written in SQL must link with rows the APPLICATION wrote
    /// in the same org. An app row before and an app row after — the first carrying metadata with
    /// non-ASCII text, a double quote, a backslash and an empty value, i.e. the canonicalisation
    /// edges — and the chain still verifies: seq dense, prev_hash linked, entry_hash recomputed.
    /// </summary>
    [Fact]
    public async Task The_Definer_Row_Chains_Between_Application_Rows_And_The_Verifier_Accepts_It()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        var writer = CreateWriter();

        // 1. An application row, with the four canonicalisation edges in allowlisted keys.
        await writer.WriteAsync(
            new IdentityAuditEvent(
                IdentityAuditActions.Login,
                member,
                "google",
                new Dictionary<string, string>
                {
                    ["reason"] = "lý do: \"quoted\" back\\slash",
                    ["status"] = string.Empty,
                }),
            [org],
            Token);

        // 2. The administratrix ends the member's session: the definer's own row, carrying the correlation
        // ids the route passes (0051) exactly as the application writer would stamp them.
        (await CreateRoutes(clock).RevokeOneAsync(
                ProofOf(adminTicket), memberTicket.SessionId, org, SampleRequestId, SampleTraceId, Token))
            .ShouldBe(SessionRouteStatus.Done);

        // 3. A second application row, which must link onto the hash SQL computed.
        await writer.WriteAsync(
            new IdentityAuditEvent(IdentityAuditActions.Link, member, "github"),
            [org],
            Token);

        var chain = await RowsAsync(org);

        chain.Select(row => row.Seq).ShouldBe([1L, 2L, 3L], "seq stays dense across both writers");
        chain[1].Action.ShouldBe(AdminRevokeAction, "the middle row is the definer's");
        chain[1].RequestId.ShouldBe(SampleRequestId,
            "the definer stamped the request id the route passed, as the application writer does");
        chain[1].TraceId.ShouldBe(SampleTraceId, "and the trace id, so the row joins the HTTP request that caused it");
        chain[2].PrevHash.SequenceEqual(chain[1].EntryHash)
            .ShouldBeTrue("the application writer chains onto the hash SQL computed");
        chain[1].PrevHash.SequenceEqual(chain[0].EntryHash).ShouldBeTrue();

        // The escaped metadata of the first row round-trips through jsonb, so the verifier really
        // canonicalises those bytes rather than a normalised copy.
        using (var document = JsonDocument.Parse(chain[0].MetadataJson))
        {
            document.RootElement.GetProperty("reason").GetString()
                .ShouldBe("lý do: \"quoted\" back\\slash");
            document.RootElement.GetProperty("status").GetString().ShouldBeEmpty();
        }

        (await VerifyAsync(org)).ShouldBeNull(
            "the existing verifier recomputes the SQL-written row exactly as the application's hashing does");
    }

    /// <summary>
    /// A self revoke is every caller's own logout, not an administrative act: it ends the row and
    /// writes nothing.
    /// </summary>
    [Fact]
    public async Task Self_Revoke_Writes_No_Audit_Row()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var user = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var ticket = await CreateSessionAsync(user, clock);

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(ticket), ticket.SessionId, orgId: null, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.Done, "the owner path still answers `revoked`");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is not null from public.session where id = @id",
            ("id", ticket.SessionId))).ShouldBeTrue();

        (await TotalRowsAsync()).ShouldBe(0L, "a self revoke is not an audited administrative act");
    }

    /// <summary>
    /// Every refusal keeps 0048's answer and writes nothing: an unknown id, the admin's own org
    /// header missing, and an actor without <c>membership:write</c>.
    /// </summary>
    [Fact]
    public async Task Refusals_Write_No_Audit_Row()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var plain = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var plainTicket = await CreateSessionAsync(plain, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), Guid.CreateVersion7(), org, null, null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "an unknown session id");

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, orgId: null, requestId: null, traceId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "another member's session needs the org header");

        (await routes.RevokeOneAsync(ProofOf(plainTicket), memberTicket.SessionId, org, null, null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "a viewer holds no membership:write");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", memberTicket.SessionId))).ShouldBeTrue("no refusal touched the target");

        (await TotalRowsAsync()).ShouldBe(0L, "no refusal may leave an audit row behind");
    }

    /// <summary>
    /// A session that is already dead is the same <c>not_found</c>, and the second call writes no
    /// second row: the chain holds exactly the one revocation that happened.
    /// </summary>
    [Fact]
    public async Task A_Second_Revoke_Of_The_Same_Session_Writes_No_Second_Row()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, null, null, Token))
            .ShouldBe(SessionRouteStatus.Done);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, null, null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the target is dead; the actor is still live");

        var rows = await RowsAsync(org);
        rows.ShouldHaveSingleItem();
        rows[0].ObjectId.ShouldBe(memberTicket.SessionId.ToString("D"));
        (await VerifyAsync(org)).ShouldBeNull();
    }

    /// <summary>
    /// An admin of org A cannot revoke a member of org B — through either org header — and neither
    /// chain gains a row while the target stays live.
    /// </summary>
    [Fact]
    public async Task A_Foreign_Orgs_Session_Writes_No_Row_In_Either_Org()
    {
        var clock = ClockAt();
        var adminOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var otherOrg = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, adminOrg, "tenant_admin", cancellationToken: Token);
        var foreignMember = await IdentityTestData.SeedActiveMemberAsync(Fixture, otherOrg, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var foreignTicket = await CreateSessionAsync(foreignMember, clock);
        var routes = CreateRoutes(clock);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), foreignTicket.SessionId, adminOrg, null, null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the target is not a member of the admin's org");

        (await routes.RevokeOneAsync(ProofOf(adminTicket), foreignTicket.SessionId, otherOrg, null, null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the admin holds no membership in the target's org");

        (await RowsAsync(adminOrg)).ShouldBeEmpty("the admin's own chain gains nothing");
        (await RowsAsync(otherOrg)).ShouldBeEmpty("the target's org chain gains nothing");

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", foreignTicket.SessionId))).ShouldBeTrue("the foreign session stays live");
    }

    /// <summary>
    /// Atomicity, one transaction: when the audit append faults, the revoke rolls back with it. A
    /// throwaway trigger on <c>audit_log</c> is the cheapest faithful fault (it aborts the definer's
    /// statement exactly as a real insert fault would), and it is dropped again in <c>finally</c>.
    /// </summary>
    [Fact]
    public async Task A_Fault_In_The_Audit_Append_Rolls_The_Revoke_Back()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        await InstallAuditFaultAsync();

        try
        {
            var refusal = await Should.ThrowAsync<PostgresException>(
                () => CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, null, null, Token).AsTask());

            refusal.SqlState.ShouldBe(PostgresErrorCodes.RaiseException, "the append aborted the whole statement");
        }
        finally
        {
            await DropAuditFaultAsync();
        }

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", memberTicket.SessionId))).ShouldBeTrue(
            "the revoke shares the definer's transaction, so a failed append leaves the session live");

        (await TotalRowsAsync()).ShouldBe(0L, "the faulted append committed nothing");
    }

    /// <summary>
    /// Atomicity, bounded wait: while another transaction holds the org's audit advisory lock, the
    /// definer's append waits at most 2000 ms (<c>AuditSql.LockTimeout</c>) and then fails as
    /// <c>55P03</c>, which rolls the revoke back — a busy org refuses the administrative act instead
    /// of ending a session it cannot record. The bound is measured, not assumed: a call that
    /// returned <c>55P03</c> immediately (a wrong lock, a stale <c>lock_timeout</c>) or one that
    /// parked far past the budget would both be defects of the bounded wait.
    /// </summary>
    [Fact]
    public async Task A_Held_Chain_Lock_Times_Out_And_Leaves_The_Session_Live()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        // The holder is the owner connection of the harness; the statement and the key it hashes are
        // the application writer's own (read out of AuditSql, not copied), so this case cannot pass
        // against a lock the production writer does not take.
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);

        await using (var lockCommand = holder.CreateCommand())
        {
            lockCommand.Transaction = held;
            lockCommand.CommandText = ProductionAdvisoryLockSql();
            lockCommand.Parameters.AddWithValue("org", org.ToString("D"));
            await lockCommand.ExecuteNonQueryAsync(Token);
        }

        try
        {
            var elapsed = Stopwatch.StartNew();
            var refusal = await Should.ThrowAsync<PostgresException>(
                () => CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, null, null, Token).AsTask());
            elapsed.Stop();

            refusal.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable, "lock_timeout is 2000 ms, as the writer's is");

            // The wait really happened, and it really was bounded: not an immediate refusal on some
            // other condition, not a parked connection.
            elapsed.Elapsed.TotalSeconds.ShouldBeGreaterThanOrEqualTo(
                1.5,
                $"the append must wait out most of its 2000 ms budget before refusing (waited {elapsed.Elapsed})");
            elapsed.Elapsed.TotalSeconds.ShouldBeLessThanOrEqualTo(
                8,
                $"the append must refuse inside its bounded wait, not park (waited {elapsed.Elapsed})");
        }
        finally
        {
            await held.RollbackAsync(Token);
        }

        (await OwnerScalarAsync<bool>(
            "select revoked_at is null from public.session where id = @id",
            ("id", memberTicket.SessionId))).ShouldBeTrue("no revocation without its record");

        (await TotalRowsAsync()).ShouldBe(0L);
    }

    /// <summary>
    /// The two writers of one org serialise on the same advisory lock, and the case PROVES the
    /// serialisation instead of assuming it: a holder transaction takes the org's key, both writers
    /// are started, and the test waits until both of their backends are queued on that same key
    /// (<c>pg_locks</c>, bounded poll) before releasing it. Both then commit, the chain is dense and
    /// linked, and the verifier accepts it — so neither writer can have read a tip the other was
    /// about to change. A version that only started them together (the F55 round-1 shape) could
    /// pass on a lucky interleaving; this one cannot pass unless both really waited.
    /// </summary>
    [Fact]
    public async Task Both_Writers_Queue_On_The_Org_Lock_And_Then_Both_Commit()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var first = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);
        var second = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var firstTicket = await CreateSessionAsync(first, clock);
        var secondTicket = await CreateSessionAsync(second, clock);

        var routes = CreateRoutes(clock);

        // The holder: the organisation's chain lock, taken with the writer's own statement and key
        // (ProductionAdvisoryLockSql), held open for as long as this test needs it.
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);

        await using (var lockCommand = holder.CreateCommand())
        {
            lockCommand.Transaction = held;
            lockCommand.CommandText = ProductionAdvisoryLockSql();
            lockCommand.Parameters.AddWithValue("org", org.ToString("D"));
            await lockCommand.ExecuteNonQueryAsync(Token);
        }

        // The probe connection is opened BEFORE the writers start, so its connect time cannot eat into
        // their 2000 ms lock budget while the test is watching for them.
        await using var probe = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await probe.OpenAsync(Token);

        var revoke = Task.Run(
            () => routes.RevokeOneAsync(ProofOf(adminTicket), firstTicket.SessionId, org, null, null, Token).AsTask());

        // The application's own append, through the real buffer and tenant scope the pipeline uses:
        // the same action, object type and metadata the definer writes, so the rows differ in nothing
        // but their writer. (After F55 no handler writes this row; the C# side of the race is exactly
        // the path every other audited command takes.)
        var append = Task.Run(
            async () =>
            {
                var audit = new AuditTrail();

                await using var scope = await Fixture.BeginTenantScopeAsync(org, cancellationToken: Token);

                using var unit = audit.BeginUnit();
                audit.Describe(new AuditObject(
                    "session",
                    secondTicket.SessionId.ToString("D"),
                    Metadata: new Dictionary<string, string> { ["target_user_id"] = second.ToString("D") }));
                audit.Record(AdminRevokeAction, new AuditActor(AuditActorTypes.User, admin.ToString("D")));

                await audit.FlushAsync(org, scope.Db, Token);
                await scope.CommitAsync(Token);
            },
            Token);

        var queued = 0L;
        var revokeDone = false;
        var appendDone = false;
        var bothBlocked = false;

        try
        {
            // Both writers must be queued on the org's key before anything is let through. The budget
            // stays under the writers' own 2000 ms lock_timeout: a writer that is not waiting by then
            // is a writer that failed, not a slow one.
            queued = await QueuedAdvisoryWaitersAsync(probe, org, expected: 2);

            revokeDone = revoke.IsCompleted;
            appendDone = append.IsCompleted;
            bothBlocked = queued >= 2 && !revokeDone && !appendDone;
        }
        finally
        {
            // Released whatever the probe reported, so neither writer is left parked on this test.
            await held.RollbackAsync(Token);
        }

        // Both writers were started against a HELD lock, so their outcome is only meaningful in
        // sequence: let them finish, then judge what the probe saw and what they wrote.
        await Task.WhenAll(revoke, append);

        bothBlocked.ShouldBeTrue(
            "both writers must be queued on the org's audit lock before it is released; "
            + $"observed {queued} queued backend(s), revoke finished at the probe: {revokeDone}, "
            + $"append finished at the probe: {appendDone}");

        (await revoke).ShouldBe(SessionRouteStatus.Done, "the definer's revoke committed once the lock was free");

        var chain = await RowsAsync(org);

        chain.Count.ShouldBe(2, "both writers committed exactly one row each");
        chain.Select(row => row.Seq).ShouldBe([1L, 2L], "the shared advisory lock kept every seq");
        chain.ShouldAllBe(row => row.Action == AdminRevokeAction);
        chain.ShouldAllBe(row => row.ObjectType == "session");

        chain.Select(row => TargetUserIdOf(row.MetadataJson))
            .ShouldBe(new[] { first, second }, ignoreOrder: true, "one row per revoked session's member");

        (await VerifyAsync(org)).ShouldBeNull("concurrent writers left one unbroken chain");
    }

    /// <summary>
    /// The monotone floor, against a skewed writer: another node's append can leave the org's tip a
    /// day ahead of THIS database's clock (the writer keeps the floor, AuditTrail.cs:144-156). The
    /// definer must do the same thing the writer does — <c>greatest(clock_timestamp(), tip)</c> — and
    /// not stamp the row with its own earlier clock: the chain guard refuses a row that moves the
    /// chain back in time, so a definer that ignored the floor would fail the whole revoke, and one
    /// that wrote the level instant would publish a row the verifier hashes differently.
    /// </summary>
    [Fact]
    public async Task A_Tip_Ahead_Of_The_Clock_Still_Takes_The_Revoke_And_Sets_The_Floor()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        // One application row, hashed by AuditHashing and chained off the genesis hash, stamped one day
        // ahead of this database's clock: exactly the tip a writer on a fast node leaves behind.
        var skewed = AuditHashing.Truncate(DateTimeOffset.UtcNow.AddDays(1));
        await SeedApplicationRowAsync(org, admin, skewed);

        (await RowsAsync(org)).ShouldHaveSingleItem().CreatedAt.ShouldBe(
            skewed,
            "the arrangement put the tip one day ahead of the clock");

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, null, null, Token))
            .ShouldBe(SessionRouteStatus.Done, "a skewed tip is a floor to raise, not a reason to refuse");

        var chain = await RowsAsync(org);

        chain.Select(row => row.Seq).ShouldBe([1L, 2L], "the definer appended the next row of the chain");
        chain[1].CreatedAt.ShouldBe(
            chain[0].CreatedAt,
            "the new row takes the tip's instant (the greatest), not this database's clock");
        chain[1].CreatedAt.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow,
            "keeping the floor is what proves the value came from the tip");

        (await VerifyAsync(org)).ShouldBeNull("the verifier accepts the row stamped at the tip's instant");
    }

    /// <summary>
    /// 0051: the correlation ids the route passed reach the row, and the row still verifies. The ids are NOT
    /// decoration — they are part of the canonical field set (`AuditHashing.CanonicalFields` hashes
    /// <c>request_id</c>/<c>trace_id</c>), so a definer that stored them but hashed the old literal <c>null</c>
    /// would produce a row this verifier rejects.
    /// </summary>
    [Fact]
    public async Task Admin_Revoke_Stores_The_Request_And_Trace_Ids_And_Still_Verifies()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        (await CreateRoutes(clock).RevokeOneAsync(
                ProofOf(adminTicket), memberTicket.SessionId, org, SampleRequestId, SampleTraceId, Token))
            .ShouldBe(SessionRouteStatus.Done);

        var row = (await RowsAsync(org)).ShouldHaveSingleItem();

        row.RequestId.ShouldBe(SampleRequestId, "the request id is stored verbatim, not escaped and not truncated");
        row.TraceId.ShouldBe(SampleTraceId, "the trace id is stored verbatim");

        (await VerifyAsync(org)).ShouldBeNull(
            "the existing verifier hashes the ids exactly as the C# writer would, so the row belongs to the chain");
    }

    /// <summary>
    /// 0051's refusals of malformed input, one case each: an over-long request id, a control character, a
    /// space, a DEL (0x7F, one past the last visible ASCII character), a non-ASCII character, an empty request
    /// id, an uppercase trace id, a short trace id and the W3C-invalid all-zero trace id. Every one is NULLED —
    /// never an error, never a refusal — the VALID member of the pair is still stored, the revoke answers
    /// <c>revoked</c>, and every row verifies: an attacker cannot block a revocation or steer the chain by
    /// sending a malformed correlation id.
    /// </summary>
    [Fact]
    public async Task Malformed_Correlation_Ids_Are_Nulled_And_The_Revoke_Still_Revokes()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var routes = CreateRoutes(clock);

        foreach (var (requestId, traceId, _, _) in MalformedIds)
        {
            var memberTicket = await CreateSessionAsync(member, clock);

            (await routes.RevokeOneAsync(
                    ProofOf(adminTicket), memberTicket.SessionId, org, requestId, traceId, Token))
                .ShouldBe(SessionRouteStatus.Done, $"a malformed correlation id must never block a revoke ({requestId})");
        }

        var rows = await RowsAsync(org);

        rows.Count.ShouldBe(MalformedIds.Length, "one row per revocation, none suppressed");

        for (var index = 0; index < MalformedIds.Length; index++)
        {
            var (requestId, traceId, expectedRequest, expectedTrace) = MalformedIds[index];

            rows[index].RequestId.ShouldBe(expectedRequest, $"request id {requestId} of case {index} is nulled as it stands");
            rows[index].TraceId.ShouldBe(expectedTrace, $"trace id {traceId} of case {index} is nulled as it stands");
        }

        (await VerifyAsync(org)).ShouldBeNull("nulled ids are hashed as `null`, which is exactly what the rows hold");
    }

    /// <summary>
    /// The escaping edge: `"` and `\` are legal in a request id, so the value is stored RAW and hashed as the
    /// escaped JSON string. A definer that stored the escaped text, or hashed the raw one, would fail here —
    /// the row's hash would not recompute — so this case is what proves the SQL canonicalisation matches
    /// <c>JsonCanonicalizer</c> for the only two characters visible ASCII allows to need an escape.
    /// </summary>
    [Fact]
    public async Task A_Request_Id_With_A_Quote_And_A_Backslash_Is_Stored_Raw_And_Hashed_Escaped()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        (await CreateRoutes(clock).RevokeOneAsync(
                ProofOf(adminTicket), memberTicket.SessionId, org, QuotedRequestId, SampleTraceId, Token))
            .ShouldBe(SessionRouteStatus.Done);

        var row = (await RowsAsync(org)).ShouldHaveSingleItem();

        row.RequestId.ShouldBe(QuotedRequestId, "the column holds the raw id: escaping belongs to the canonical bytes only");
        row.TraceId.ShouldBe(SampleTraceId);

        (await VerifyAsync(org)).ShouldBeNull(
            "the recomputed hash uses the RFC 8785 escaped string, so SQL and C# canonicalise this id identically");
    }

    /// <summary>
    /// The accepted side of the same validator, one case each (`AcceptedRequestIds`): a single character, exactly
    /// 128 characters — the cap, where 129 is refused — the two endpoints of visible ASCII, <c>!</c> (0x21) and
    /// <c>~</c> (0x7E), and an id that pairs the two characters RFC 8785 escapes (<c>"</c> and <c>\</c>) with
    /// punctuation that must stay literal (<c>&lt; &gt; &amp; ' + /</c>). Every one is stored RAW — not nulled,
    /// not rewritten — and the chain still verifies, so the SQL canonicalisation holds for the whole visible
    /// ASCII range and not only for the letters a minted id happens to use.
    /// </summary>
    [Fact]
    public async Task Accepted_Request_Id_Boundaries_Are_Stored_Raw_And_The_Chain_Verifies()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var routes = CreateRoutes(clock);

        foreach (var requestId in AcceptedRequestIds)
        {
            var memberTicket = await CreateSessionAsync(member, clock);

            (await routes.RevokeOneAsync(
                    ProofOf(adminTicket), memberTicket.SessionId, org, requestId, SampleTraceId, Token))
                .ShouldBe(SessionRouteStatus.Done, $"an accepted request id must not block a revoke ({requestId})");
        }

        var rows = await RowsAsync(org);

        rows.Count.ShouldBe(AcceptedRequestIds.Length, "one row per revocation, none suppressed");

        for (var index = 0; index < AcceptedRequestIds.Length; index++)
        {
            var requestId = AcceptedRequestIds[index];

            rows[index].RequestId.ShouldBe(
                requestId, $"case {index} is a valid id, so it is stored raw and never nulled or rewritten");
            rows[index].TraceId.ShouldBe(SampleTraceId, $"case {index} keeps the valid member of the pair");
        }

        (await VerifyAsync(org)).ShouldBeNull(
            "each row's hash is recomputed from the same raw text, so every accepted boundary belongs to the chain");
    }

    /// <summary>
    /// Deploy safety, the expand direction: a caller that still binds FOUR arguments — the build that ran
    /// before 0051 — keeps working, because the two new arguments are defaulted. Its row is a normal
    /// administrative row with both ids absent, so the pre-0051 application behaves exactly as it did and no
    /// revoke is lost while the fleet rolls.
    /// </summary>
    [Fact]
    public async Task The_Previous_Four_Argument_Call_Still_Binds_Through_The_Defaults()
    {
        var clock = ClockAt();
        var org = await Fixture.SeedOrgAsync(cancellationToken: Token);
        var admin = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "tenant_admin", cancellationToken: Token);
        var member = await IdentityTestData.SeedActiveMemberAsync(Fixture, org, "viewer", cancellationToken: Token);

        var adminTicket = await CreateSessionAsync(admin, clock);
        var memberTicket = await CreateSessionAsync(member, clock);

        // The exact text the pre-0051 store issued: four arguments, no correlation ids. The owner seam is the
        // caller here because the C# surface has already moved to six; what is under test is the SQL binding of
        // the old shape. The proof is bound as an explicit `bytea[]`, the way the store binds it
        // (SessionRoutesStore.ProofParameter).
        string? status;
        await using (var connection = new NpgsqlConnection(Fixture.OwnerConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "select status from evidgate_resolve.session_revoke_one(cast(@h as bytea[]), @s, @o, @now)";
            command.Parameters.Add(new NpgsqlParameter("h", NpgsqlDbType.Array | NpgsqlDbType.Bytea)
            {
                Value = new[] { ProofOf(adminTicket) },
            });
            command.Parameters.AddWithValue("s", memberTicket.SessionId);
            command.Parameters.AddWithValue("o", org);
            command.Parameters.AddWithValue("now", clock.UtcNow);

            status = (string?)await command.ExecuteScalarAsync(Token);
        }

        status.ShouldBe("revoked", "the four-argument form still binds through `p_request_id`/`p_trace_id` defaults");

        var row = (await RowsAsync(org)).ShouldHaveSingleItem();

        row.Action.ShouldBe(AdminRevokeAction, "the old caller gets the same administrative row");
        row.RequestId.ShouldBeNull("an old caller passes no request id");
        row.TraceId.ShouldBeNull("an old caller passes no trace id");

        (await VerifyAsync(org)).ShouldBeNull("the ids are `null` in the row and in the hash alike");
    }

    /// <summary>
    /// The catalogue of 0051: exactly ONE <c>session_revoke_one</c> exists (the new six-argument definition; the
    /// four-argument one is dropped, not left behind as an overload that a stale grant could reach), and its
    /// privilege is the documented one — <c>evidgate_app</c> holds EXECUTE and no other non-owner role does.
    /// </summary>
    [Fact]
    public async Task Exactly_One_Six_Argument_Definer_Exists_And_Only_The_App_Role_May_Call_It()
    {
        (await OwnerScalarAsync<long>(
            """
            select count(*)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """)).ShouldBe(1L, "0051 replaces the definition; two overloads would leave an ungranted copy behind");

        (await OwnerScalarAsync<string>(
            """
            select pg_catalog.pg_get_function_identity_arguments(p.oid)
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """)).ShouldBe(
            "p_cookie_hmacs bytea[], p_session_id uuid, p_org_id uuid, p_now timestamp with time zone, "
            + "p_request_id text, p_trace_id text",
            "the correlation ids are the trailing text arguments, after 0050's four");

        (await OwnerScalarAsync<string>(
            """
            select array_to_string(p.proargnames[1:p.pronargs], ',') || '|' || p.pronargdefaults::text
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
            """)).ShouldBe(
            "p_cookie_hmacs,p_session_id,p_org_id,p_now,p_request_id,p_trace_id|2",
            "the six INPUT names, in order, and the two trailing ids are defaulted");

        (await OwnerScalarAsync<string>(
            """
            select coalesce(string_agg(
                     case when a.grantee = 0 then 'PUBLIC'
                          else pg_catalog.pg_get_userbyid(a.grantee) end
                     || ':' || a.privilege_type, ',' order by a.grantee)
                   , '')
              from pg_catalog.pg_proc p
              join pg_catalog.pg_namespace n on n.oid = p.pronamespace
              cross join lateral aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
             where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
               and a.privilege_type = 'EXECUTE'
               and a.grantee <> p.proowner
            """)).ShouldBe(
            "evidgate_app:EXECUTE",
            "`drop function` discarded 0050's ACL, so 0051 re-states it: the app role, and never PUBLIC");

        foreach (var role in new[] { "evidgate_operator", "evidgate_worker_sched" })
        {
            (await OwnerScalarAsync<bool>(
                """
                select exists (
                  select 1
                    from pg_catalog.pg_proc p
                    join pg_catalog.pg_namespace n on n.oid = p.pronamespace
                   where n.nspname = 'evidgate_resolve' and p.proname = 'session_revoke_one'
                     and pg_catalog.has_function_privilege(@role, p.oid, 'execute'))
                """,
                ("role", role))).ShouldBeFalse($"{role} must not execute the revoke");
        }
    }

    /// <summary>A store over the app-role data source; the route statements never open the sweep connection.</summary>
    /// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
    private SessionRoutesStore CreateRoutes(TimeProvider clock) =>
        new(AppDataSource, new UnusedSweep(), clock);

    /// <summary>The production identity writer over the harness's real scope factory and a fresh audit buffer.</summary>
    private IdentityAuditWriter CreateWriter() =>
        new(Fixture.Scopes, Fixture.Db, new AuditTrail());

    /// <summary>
    /// The application writer's OWN advisory-lock statement, read out of <c>AuditSql</c>
    /// (<c>AuditTrail.cs:241-245</c>, used by <c>AuditTrail.FlushAsync</c> with
    /// <c>new { org = orgId.ToString("D") }</c>). The type is internal to the persistence assembly and
    /// no test assembly holds an <c>InternalsVisibleTo</c> for it, so it is read by reflection instead
    /// of copied: a holder that took a key the production writer does not take would prove nothing.
    /// </summary>
    private static string ProductionAdvisoryLockSql()
    {
        var auditSql = typeof(AuditTrail).Assembly
            .GetType("Evidgate.BuildingBlocks.Persistence.Audit.AuditSql", throwOnError: true)!;

        var field = auditSql.GetField("AdvisoryLock", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "AuditSql.AdvisoryLock is gone: this case must hold the application writer's own chain key, "
                + "so re-point it at the statement the writer takes now (AuditTrail.FlushAsync)");

        var statement = (string)field.GetValue(null)!;

        statement.ShouldContain("pg_advisory_xact_lock", Case.Sensitive, "the field is the writer's chain lock");
        statement.ShouldContain("@org", Case.Sensitive, "the writer binds the org as a text parameter named `org`");

        return statement;
    }

    /// <summary>
    /// Polls, on a bounded budget, for the backends of THIS test database that are queued on the org's
    /// audit key, and returns the highest count it saw (the caller decides what the count means) — an
    /// explicit ordering signal, never a sleep. The probe is <c>pg_locks</c> and not
    /// <c>pg_stat_activity.wait_event</c>: another role's wait event is hidden from the harness owner
    /// (no <c>pg_read_all_stats</c>; the reason <c>ProducerSlotQaTests</c> keys on <c>pg_locks</c>
    /// too), while an ungranted lock row is visible.
    /// </summary>
    /// <param name="connection">The caller's already-open probe connection.</param>
    /// <param name="orgId">The org whose chain lock the writers contend for.</param>
    /// <param name="expected">How many queued backends the poll waits for.</param>
    private static async Task<long> QueuedAdvisoryWaitersAsync(NpgsqlConnection connection, Guid orgId, int expected)
    {
        await using var command = connection.CreateCommand();

        // The key halves: a one-argument advisory lock stores the low 32 bits of its bigint key in
        // `objid`, with `objsubid` 1, and `hashtext` is the expression both writers hash.
        command.CommandText =
            """
            select count(*)
              from pg_locks l
              join pg_stat_activity a on a.pid = l.pid
             where not l.granted
               and a.datname = current_database()
               and l.locktype = 'advisory'
               and l.objsubid = 1
               and l.objid::bigint = (hashtext('audit:' || @org)::bigint & 4294967295)
            """;
        command.Parameters.AddWithValue("org", orgId.ToString("D"));

        // Under the writers' own 2000 ms lock_timeout (AuditSql.LockTimeout) on purpose: once that
        // budget is spent they fail instead of waiting, so a longer poll could only ever watch a run in
        // which nobody waited.
        var budget = TimeSpan.FromMilliseconds(1800);
        var elapsed = Stopwatch.StartNew();
        var highest = 0L;

        while (elapsed.Elapsed < budget)
        {
            var waiting = (long)(await command.ExecuteScalarAsync(Token))!;
            highest = Math.Max(highest, waiting);
            if (highest >= expected)
            {
                return highest;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(15), Token);
        }

        return highest;
    }

    /// <summary>
    /// One row written the way the application writer writes it — <see cref="AuditHashing.EntryHash"/>
    /// over the fourteen canonical fields, <c>seq</c> 1 off the genesis hash — but inserted through the
    /// owner seam at an EXPLICIT instant, so a chain whose tip runs ahead of this database's clock can
    /// be arranged without touching either clock.
    /// </summary>
    /// <param name="orgId">The org whose chain gains the row.</param>
    /// <param name="actorId">The actor the row names.</param>
    /// <param name="createdAt">The instant to stamp, already at microsecond precision.</param>
    private async Task SeedApplicationRowAsync(Guid orgId, Guid actorId, DateTimeOffset createdAt)
    {
        var row = new AuditRow(
            Guid.CreateVersion7(),
            orgId,
            1,
            AuditActorTypes.User,
            actorId.ToString("D"),
            IdentityAuditActions.Login,
            ObjectType: "provider",
            ObjectId: "google",
            BeforeHash: null,
            AfterHash: null,
            RequestId: null,
            TraceId: null,
            Metadata: new Dictionary<string, string> { ["status"] = "ok" },
            PrevHash: AuditHashing.Genesis,
            EntryHash: [],
            CreatedAt: createdAt);

        var entryHash = AuditHashing.EntryHash(row.PrevHash, row);

        await OwnerExecuteAsync(
            """
            insert into public.audit_log (id, org_id, seq, actor_type, actor_id, action, object_type, object_id,
                                          before_hash, after_hash, request_id, trace_id, metadata,
                                          prev_hash, entry_hash, created_at)
            values (@id, @org_id, @seq, @actor_type, @actor_id, @action, @object_type, @object_id,
                    null, null, null, null, @metadata::jsonb, @prev_hash, @entry_hash, @created_at)
            """,
            ("id", row.Id),
            ("org_id", row.OrgId),
            ("seq", row.Seq),
            ("actor_type", row.ActorType),
            ("actor_id", row.ActorId),
            ("action", row.Action),
            ("object_type", (object)row.ObjectType!),
            ("object_id", (object)row.ObjectId!),
            ("metadata", JsonSerializer.Serialize(row.Metadata)),
            ("prev_hash", row.PrevHash),
            ("entry_hash", entryHash),
            ("created_at", row.CreatedAt));
    }

    /// <summary>The <c>target_user_id</c> of a stored row's metadata, parsed the way the verifier parses it.</summary>
    /// <param name="metadataJson">The row's metadata as JSON text.</param>
    private static Guid TargetUserIdOf(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);

        return Guid.Parse(document.RootElement.GetProperty("target_user_id").GetString()!);
    }

    /// <summary>The proof a route passes: the HMAC of the live cookie value under the current pepper.</summary>
    /// <param name="ticket">The session the store minted.</param>
    private static byte[] ProofOf(SessionTicket ticket) => HmacOf(Pepper, ticket.CookieValue);

    /// <summary>The org's audit rows, oldest first, read through a real read-only tenant scope (RLS applies).</summary>
    /// <param name="orgId">The org whose chain is read.</param>
    private async Task<IReadOnlyList<StoredRow>> RowsAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        return await scope.Db.QueryAsync<StoredRow>(
            """
            select seq as "Seq"
                 , org_id as "OrgId"
                 , actor_type as "ActorType"
                 , actor_id as "ActorId"
                 , action as "Action"
                 , object_type as "ObjectType"
                 , object_id as "ObjectId"
                 , request_id as "RequestId"
                 , trace_id as "TraceId"
                 , coalesce(metadata, '{}'::jsonb)::text as "MetadataJson"
                 , prev_hash as "PrevHash"
                 , entry_hash as "EntryHash"
                 , created_at as "CreatedAt"
              from public.audit_log
             where org_id = @org_id
             order by seq
            """,
            new { org_id = orgId },
            Token);
    }

    /// <summary>One org's audit row materialised as JSON text, so a secret added anywhere in it shows up.</summary>
    /// <param name="orgId">The org whose row is serialised.</param>
    private Task<string> SerializedRowAsync(Guid orgId) =>
        OwnerScalarAsync<string>(
            """
            select string_agg(to_jsonb(a)::text, E'\n' order by a.seq)
              from public.audit_log a
             where a.org_id = @org_id
            """,
            ("org_id", orgId));

    /// <summary>Recomputes the org's chain with the production verifier, inside that org's scope.</summary>
    /// <param name="orgId">The org whose chain is verified.</param>
    private async Task<AuditChainFinding?> VerifyAsync(Guid orgId)
    {
        await using var scope = await Fixture.BeginTenantScopeAsync(orgId, readOnlyTransaction: true, cancellationToken: Token);

        return await new AuditChainVerifier(scope.Db).VerifyAsync(orgId, Token);
    }

    /// <summary>Every audit row in the database, counted on the owner connection (arrangement bookkeeping).</summary>
    private async Task<long> TotalRowsAsync() =>
        await OwnerScalarAsync<long>("select count(*) from public.audit_log");

    /// <summary>
    /// Installs the throwaway fault: a trigger on <c>audit_log</c> that raises on every insert, so the
    /// definer's append aborts inside its own transaction. Created as the owner (the table's owner),
    /// under names no production object uses.
    /// </summary>
    private async Task InstallAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists f55_audit_fault on public.audit_log");

        await OwnerExecuteAsync(
            """
            create or replace function public.f55_audit_fault() returns trigger
              language plpgsql
              set search_path = ''
              as $$
              begin
                raise exception 'f55 audit fault';
              end
              $$;
            """);

        await OwnerExecuteAsync(
            "create trigger f55_audit_fault before insert on public.audit_log "
            + "for each row execute function public.f55_audit_fault()");
    }

    /// <summary>Drops the throwaway trigger and its function, whatever the test's outcome was.</summary>
    private async Task DropAuditFaultAsync()
    {
        await OwnerExecuteAsync("drop trigger if exists f55_audit_fault on public.audit_log");
        await OwnerExecuteAsync("drop function if exists public.f55_audit_fault()");
    }

    /// <summary>
    /// The sweep connection the four route statements never open: reaching it means the store called
    /// the wrong role for a route, which is a defect and not a fallback.
    /// </summary>
    private sealed class UnusedSweep : IWorkerSchedDataSource
    {
        public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("a route statement must never open the sweep connection");
    }

    /// <summary>The columns these cases assert on, in select order.</summary>
    /// <param name="Seq">Position in the org's chain.</param>
    /// <param name="OrgId">The org whose chain holds the row.</param>
    /// <param name="ActorType">Who acted.</param>
    /// <param name="ActorId">The actor's id as text.</param>
    /// <param name="Action">The audited action string.</param>
    /// <param name="ObjectType">The object kind.</param>
    /// <param name="ObjectId">The object's id as text.</param>
    /// <param name="RequestId">The request id the row was stamped with, when there was one.</param>
    /// <param name="TraceId">The W3C trace id the row was stamped with, when there was one.</param>
    /// <param name="MetadataJson">The stored metadata, as JSON text.</param>
    /// <param name="PrevHash">The previous row's entry hash.</param>
    /// <param name="EntryHash">This row's entry hash.</param>
    /// <param name="CreatedAt">The instant the row is stamped with (part of the hash).</param>
    private sealed record StoredRow(
        long Seq,
        Guid OrgId,
        string ActorType,
        string ActorId,
        string Action,
        string? ObjectType,
        string? ObjectId,
        string? RequestId,
        string? TraceId,
        string MetadataJson,
        byte[] PrevHash,
        byte[] EntryHash,
        DateTimeOffset CreatedAt);
}
