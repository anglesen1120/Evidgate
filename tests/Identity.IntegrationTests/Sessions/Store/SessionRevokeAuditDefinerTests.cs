using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Login.Audit;
using Evidgate.Modules.Identity.Sessions;
using Evidgate.Testing;
using Npgsql;
using Shouldly;
using Xunit;

namespace Identity.IntegrationTests.Sessions.Store;

/// <summary>
/// F55 (02.3.2-T2.1 AC6, completing coordinator row F48): the administrative session revoke and its
/// <c>audit_log</c> row are ONE transaction, written by the definer itself (migration 0050).
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
[Collection(HarnessSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SessionRevokeAuditDefinerTests : SessionHardeningTestBase
{
    /// <summary>The action string the definer writes; it lives in SQL after F55, so the literal is pinned here.</summary>
    private const string AdminRevokeAction = "auth.session_revoke_admin";

    public SessionRevokeAuditDefinerTests(DatabaseFixture fixture)
        : base(fixture)
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

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token))
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

        // 2. The administratrix ends the member's session: the definer's own row.
        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token))
            .ShouldBe(SessionRouteStatus.Done);

        // 3. A second application row, which must link onto the hash SQL computed.
        await writer.WriteAsync(
            new IdentityAuditEvent(IdentityAuditActions.Link, member, "github"),
            [org],
            Token);

        var chain = await RowsAsync(org);

        chain.Select(row => row.Seq).ShouldBe([1L, 2L, 3L], "seq stays dense across both writers");
        chain[1].Action.ShouldBe(AdminRevokeAction, "the middle row is the definer's");
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

        (await CreateRoutes(clock).RevokeOneAsync(ProofOf(ticket), ticket.SessionId, orgId: null, Token))
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

        (await routes.RevokeOneAsync(ProofOf(adminTicket), Guid.CreateVersion7(), org, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "an unknown session id");

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, orgId: null, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "another member's session needs the org header");

        (await routes.RevokeOneAsync(ProofOf(plainTicket), memberTicket.SessionId, org, Token))
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

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token))
            .ShouldBe(SessionRouteStatus.Done);

        (await routes.RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token))
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

        (await routes.RevokeOneAsync(ProofOf(adminTicket), foreignTicket.SessionId, adminOrg, Token))
            .ShouldBe(SessionRouteStatus.NotFound, "the target is not a member of the admin's org");

        (await routes.RevokeOneAsync(ProofOf(adminTicket), foreignTicket.SessionId, otherOrg, Token))
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
                () => CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token).AsTask());

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
    /// of ending a session it cannot record.
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

        // The holder is the owner connection of the harness; the key is the writer's own
        // (`hashtext('audit:' || <org in "D" format>)`).
        await using var holder = new NpgsqlConnection(Fixture.OwnerConnectionString);
        await holder.OpenAsync(Token);
        await using var held = await holder.BeginTransactionAsync(Token);

        await using (var lockCommand = holder.CreateCommand())
        {
            lockCommand.Transaction = held;
            lockCommand.CommandText = "select pg_advisory_xact_lock(hashtext('audit:' || @org))";
            lockCommand.Parameters.AddWithValue("org", org.ToString("D"));
            await lockCommand.ExecuteNonQueryAsync(Token);
        }

        try
        {
            var refusal = await Should.ThrowAsync<PostgresException>(
                () => CreateRoutes(clock).RevokeOneAsync(ProofOf(adminTicket), memberTicket.SessionId, org, Token).AsTask());

            refusal.SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable, "lock_timeout is 2000 ms, as the writer's is");
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
    /// The two writers of one org serialise on the same advisory lock: a definer append (SQL) and an
    /// application append (C#) started together both commit, and the chain is dense and linked.
    /// </summary>
    [Fact]
    public async Task A_Definer_Append_And_An_Application_Append_In_One_Org_Both_Commit()
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
        var revoke = Task.Run(
            () => routes.RevokeOneAsync(ProofOf(adminTicket), firstTicket.SessionId, org, Token).AsTask());

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

        await Task.WhenAll(revoke, append);

        (await revoke).ShouldBe(SessionRouteStatus.Done, "the definer's revoke committed");

        var chain = await RowsAsync(org);

        chain.Count.ShouldBe(2, "both writers committed exactly one row each");
        chain.Select(row => row.Seq).ShouldBe([1L, 2L], "the shared advisory lock kept every seq");
        chain.ShouldAllBe(row => row.Action == AdminRevokeAction);
        chain.ShouldAllBe(row => row.ObjectType == "session");

        chain.Select(row => TargetUserIdOf(row.MetadataJson))
            .ShouldBe(new[] { first, second }, ignoreOrder: true, "one row per revoked session's member");

        (await VerifyAsync(org)).ShouldBeNull("concurrent writers left one unbroken chain");
    }

    /// <summary>A store over the app-role data source; the route statements never open the sweep connection.</summary>
    /// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
    private SessionRoutesStore CreateRoutes(TimeProvider clock) =>
        new(AppDataSource, new UnusedSweep(), clock);

    /// <summary>The production identity writer over the harness's real scope factory and a fresh audit buffer.</summary>
    private IdentityAuditWriter CreateWriter() =>
        new(Fixture.Scopes, Fixture.Db, new AuditTrail());

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
                 , coalesce(metadata, '{}'::jsonb)::text as "MetadataJson"
                 , prev_hash as "PrevHash"
                 , entry_hash as "EntryHash"
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
    /// <param name="MetadataJson">The stored metadata, as JSON text.</param>
    /// <param name="PrevHash">The previous row's entry hash.</param>
    /// <param name="EntryHash">This row's entry hash.</param>
    private sealed record StoredRow(
        long Seq,
        Guid OrgId,
        string ActorType,
        string ActorId,
        string Action,
        string? ObjectType,
        string? ObjectId,
        string MetadataJson,
        byte[] PrevHash,
        byte[] EntryHash);
}
