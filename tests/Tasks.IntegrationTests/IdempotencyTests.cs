using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Idempotency;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T6.1: <c>idempotency_record</c> as the sole arbiter of
/// <c>Idempotency-Key</c> (TL T11, T94) — replay on the same canonical request,
/// 409 on a different one, ≥ 24 h retention, one record per organization even when
/// the operation is org-scoped (<c>project_id</c> NULL), and no cross-tenant read.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IdempotencyTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private const string Operation = "runs.create";
    private const string Key = "6b0f1e6c-4a2f-4c2d-9c1e-8f1f2a3b4c5d";
    private const string RequestJson = """{"run":{"purpose":"pr","suite":"smoke"}}""";
    private const string ResponseJson = """{"run_id":"01J0RUN0000000000000000"}""";

    private readonly PostgresTasksFixture _fixture;

    public IdempotencyTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC6, "Idempotency_SameKeySameHash_ReplaysResponse": a retry with the same key
    /// and the same request — spelled differently, because the identity is the RFC
    /// 8785 canonical digest (01.3.2-T4) — is answered from the stored status and
    /// body instead of executing again.
    /// </summary>
    [Fact]
    public async Task Idempotency_SameKeySameHash_ReplaysResponse()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            begin.Outcome.ShouldBe(IdempotencyOutcome.Proceed);
            begin.RecordId.ShouldNotBeNull();

            await host.Idempotency.CompleteAsync(begin.RecordId.Value, 201, ResponseJson, Token);
            await scope.CommitAsync(Token);
        }

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var replayBegin = await host.Idempotency.TryBeginAsync(
                Operation,
                Key,
                """{ "run" : { "suite" : "smoke", "purpose" : "pr" } }""",
                Token);
            replayBegin.Outcome.ShouldBe(IdempotencyOutcome.Replay);
            replayBegin.RecordId.ShouldBeNull();

            var replay = await host.Idempotency.ReplayAsync(Operation, Key, Token);
            replay.ShouldNotBeNull();
            replay.ResponseStatus.ShouldBe(201);
            JsonDocument.Parse(replay.ResponseBody).RootElement.GetProperty("run_id").GetString()
                .ShouldBe("01J0RUN0000000000000000");
        }
    }

    /// <summary>
    /// AC6, "SameKeyDifferentHash_409": the same key with a different request is a
    /// conflict (00 §9.1/§9.2 <c>idempotency_conflict</c>) — never a silent
    /// execution of a second, different command under the first one's key.
    /// </summary>
    [Fact]
    public async Task Idempotency_SameKeyDifferentHash_409()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            begin.Outcome.ShouldBe(IdempotencyOutcome.Proceed);
            await host.Idempotency.CompleteAsync(begin.RecordId!.Value, 201, ResponseJson, Token);
            await scope.CommitAsync(Token);
        }

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var conflicting = await host.Idempotency.TryBeginAsync(
                Operation,
                Key,
                """{"run":{"purpose":"nightly","suite":"smoke"}}""",
                Token);

            conflicting.Outcome.ShouldBe(IdempotencyOutcome.Conflict);
            conflicting.RecordId.ShouldBeNull();
        }
    }

    /// <summary>
    /// AC6 / T11: the store writes the 24 h floor itself and the database enforces
    /// it, so no caller can shorten the retention window of the arbiter.
    /// </summary>
    [Fact]
    public async Task Record_ExpiresAt_AtLeast24h()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);
        Guid recordId;

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            recordId = begin.RecordId!.Value;
            await scope.CommitAsync(Token);
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var retained = await OutboxIdempotencyTestHost.ScalarAsync<bool>(
            owner,
            "select expires_at - created_at >= interval '24 hours' from public.idempotency_record where id = @Id",
            Token,
            ("Id", recordId));
        retained.ShouldBeTrue();

        // The floor is a database constraint, not a convention of the store: an
        // expiring record inserted by hand is refused.
        var now = DateTimeOffset.UtcNow;
        var error = await Should.ThrowAsync<PostgresException>(
            () => OutboxIdempotencyTestHost.ExecuteAsync(
                owner,
                """
                insert into public.idempotency_record
                  (id, org_id, operation, key, request_hash, created_at, expires_at)
                values
                  (@Id, @OrgId, 'runs.create', 'short-lived', 'sha256:seed', @CreatedAt, @ExpiresAt)
                """,
                Token,
                ("Id", Guid.CreateVersion7()),
                ("OrgId", OrgA),
                ("CreatedAt", now),
                ("ExpiresAt", now.AddHours(1))));

        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    /// <summary>
    /// AC6 / T94, "Idempotency_OrgScoped_NullProject_Dedupes": an org-scoped
    /// operation carries a NULL <c>project_id</c>, and
    /// <c>unique nulls not distinct (org_id, project_id, operation, key)</c> still
    /// dedupes it — without that, two NULLs would never conflict and the key would
    /// silently execute twice.
    /// </summary>
    [Fact]
    public async Task Idempotency_OrgScoped_NullProject_Dedupes()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            begin.Outcome.ShouldBe(IdempotencyOutcome.Proceed);
            await host.Idempotency.CompleteAsync(begin.RecordId!.Value, 201, ResponseJson, Token);
            await scope.CommitAsync(Token);
        }

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var again = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            again.Outcome.ShouldBe(IdempotencyOutcome.Replay);
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var records = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            """
            select count(*) from public.idempotency_record
             where org_id = @OrgId and project_id is null and operation = @Operation and key = @Key
            """,
            Token,
            ("OrgId", OrgA),
            ("Operation", Operation),
            ("Key", Key));
        records.ShouldBe(1);
    }

    /// <summary>
    /// 00 §5.4.1: `project_id` is a composite tenant FK to
    /// <c>project (org_id, id)</c>, so a record can never reserve a key inside
    /// another organization's project scope — the row would be invisible under RLS
    /// and still block that project's key.
    /// </summary>
    [Fact]
    public async Task Idempotency_ProjectOfAnotherOrg_Rejected()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var projectOfOrgB = Guid.CreateVersion7();
        await OutboxIdempotencyTestHost.ExecuteAsync(
            owner,
            "insert into public.organization (id, name, slug) values (@Id, 'org-b', 'org-b-tenant')",
            Token,
            ("Id", OrgB));

        await OutboxIdempotencyTestHost.ExecuteAsync(
            owner,
            "insert into public.project (id, org_id, name, slug) values (@Id, @OrgId, 'b-project', 'b-project')",
            Token,
            ("Id", projectOfOrgB),
            ("OrgId", OrgB));

        var now = DateTimeOffset.UtcNow;
        var error = await Should.ThrowAsync<PostgresException>(
            () => OutboxIdempotencyTestHost.ExecuteAsync(
                owner,
                """
                insert into public.idempotency_record
                  (id, org_id, project_id, operation, key, request_hash, created_at, expires_at)
                values
                  (@Id, @OrgId, @ProjectId, 'runs.create', 'cross-tenant-project', 'sha256:seed',
                   @CreatedAt, @ExpiresAt)
                """,
                Token,
                ("Id", Guid.CreateVersion7()),
                ("OrgId", OrgA),
                ("ProjectId", projectOfOrgB),
                ("CreatedAt", now),
                ("ExpiresAt", now.AddHours(24))));

        error.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);

        // The name matters: the refusal has to come from this table's composite
        // tenant FK, not from another constraint on the same insert.
        error.ConstraintName.ShouldBe("idempotency_record_project_fk");
    }

    /// <summary>
    /// The key is unique per organization only (00 §7): the same key from another
    /// tenant is another piece of work, and one tenant's records — RLS with FORCE —
    /// are invisible to it.
    /// </summary>
    [Fact]
    public async Task Idempotency_CrossTenant_Isolated()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            await host.Idempotency.CompleteAsync(begin.RecordId!.Value, 201, ResponseJson, Token);
            await scope.CommitAsync(Token);
        }

        await using (var otherTenant = await host.Scopes.BeginAsync(OrgB, cancellationToken: Token))
        {
            // Org A's committed record is not readable from org B's scope, neither
            // by the key (Replay) nor by scanning the table.
            (await host.Idempotency.ReplayAsync(Operation, Key, Token)).ShouldBeNull();

            var visible = await host.Db.ScalarAsync<long>(
                "select count(*) from public.idempotency_record",
                cancellationToken: Token);
            visible.ShouldBe(0);

            // Same operation and key, different tenant: its own record, so Proceed.
            var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            begin.Outcome.ShouldBe(IdempotencyOutcome.Proceed);
            await host.Idempotency.CompleteAsync(begin.RecordId!.Value, 201, ResponseJson, Token);
            await otherTenant.CommitAsync(Token);
        }

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var visible = await host.Db.ScalarAsync<long>(
                "select count(*) from public.idempotency_record",
                cancellationToken: Token);
            visible.ShouldBe(1);
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var total = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.idempotency_record",
            Token);
        total.ShouldBe(2);
    }

    /// <summary>
    /// The key's identity includes the operation (00 §7): the same key reused for a
    /// different operation is a different command with its own record, so this does
    /// not depend on clients never reusing a key across endpoints.
    /// </summary>
    [Fact]
    public async Task Idempotency_SameKeyDifferentOperation_IsIndependent()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var first = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
            first.Outcome.ShouldBe(IdempotencyOutcome.Proceed);
            await host.Idempotency.CompleteAsync(first.RecordId!.Value, 201, ResponseJson, Token);
            await scope.CommitAsync(Token);
        }

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var second = await host.Idempotency.TryBeginAsync("projects.create", Key, RequestJson, Token);
            second.Outcome.ShouldBe(IdempotencyOutcome.Proceed);
            second.RecordId.ShouldNotBeNull();
            await host.Idempotency.CompleteAsync(second.RecordId!.Value, 201, ResponseJson, Token);
            await scope.CommitAsync(Token);
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var records = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.idempotency_record where key = @Key",
            Token,
            ("Key", Key));
        records.ShouldBe(2);
    }

    /// <summary>
    /// 00 §5.4.3 / M1W3-05: the app role inserts a begin row and updates it with the
    /// response, and can never hard-delete one — deleting an arbiter row would let
    /// the same key execute again, and expiry is the purge function's job. The
    /// privilege is refused before RLS is consulted.
    /// </summary>
    [Fact]
    public async Task Idempotency_AppRoleCannotDelete()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await using var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token);
        var begin = await host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token);
        await host.Idempotency.CompleteAsync(begin.RecordId!.Value, 201, ResponseJson, Token);

        var denied = await Should.ThrowAsync<PostgresException>(
            () => host.Db.ExecuteAsync(
                "delete from public.idempotency_record where id = @Id",
                new { Id = begin.RecordId!.Value },
                Token));

        denied.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// The store writes tenant data through the same accessor as every other write
    /// path (01.4.2-T1.5): without a tenant transaction there is no record to read
    /// or write, so the guard fails closed instead of writing an unowned row.
    /// </summary>
    [Fact]
    public async Task Idempotency_WithoutTenantScope_Throws()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await Should.ThrowAsync<TenantScopeMissingException>(
            () => host.Idempotency.TryBeginAsync(Operation, Key, RequestJson, Token));
    }
}
