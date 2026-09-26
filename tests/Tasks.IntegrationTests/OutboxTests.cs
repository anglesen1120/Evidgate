using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T2: the transactional outbox write path (AC: a row written inside the
/// command's tenant transaction commits with the business change, and the
/// undispatched scan has its index). Docker-backed: the row has to be written on
/// real PostgreSQL 18 through the real Migrator's schema (migrations/0006).
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class OutboxTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OrgB = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private const string TaskType = "identity.organization.provision";
    private const string Payload = """{"organization_id":"01J0F0RGANIZATION00000000"}""";

    private readonly PostgresTasksFixture _fixture;

    public OutboxTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 01.5.1-T2.2, "OutboxFlush_CommitsAtomicallyWithBusinessRow": the outbox row
    /// is written on the scope's connection and transaction, so it is there when
    /// the business row is — same tenant, undispatched, payload intact.
    /// </summary>
    [Fact]
    public async Task Outbox_WrittenInCommandTransaction_CommitsTogether()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);
        var businessRow = Guid.CreateVersion7();
        Guid outboxId;

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            await host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@OrgId, @Id, @Label)",
                new { OrgId = OrgA, Id = businessRow, Label = "commit-together" },
                Token);

            outboxId = await host.Outbox.EnqueueAsync(
                TaskType,
                Payload,
                "organization",
                businessRow,
                "identity:organization:created",
                Token);

            await scope.CommitAsync(Token);
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var business = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.rls_probe where id = @Id",
            Token,
            ("Id", businessRow));
        business.ShouldBe(1);

        var rows = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.outbox where id = @Id",
            Token,
            ("Id", outboxId));
        rows.ShouldBe(1);

        var org = await OutboxIdempotencyTestHost.ScalarAsync<Guid>(
            owner,
            "select org_id from public.outbox where id = @Id",
            Token,
            ("Id", outboxId));
        org.ShouldBe(OrgA);

        // Undispatched is what makes the row the dispatcher's work; a row that
        // arrived already stamped would never be queued.
        var undispatched = await OutboxIdempotencyTestHost.ScalarAsync<bool>(
            owner,
            "select dispatched_at is null from public.outbox where id = @Id",
            Token,
            ("Id", outboxId));
        undispatched.ShouldBeTrue();

        var payload = await OutboxIdempotencyTestHost.ScalarAsync<string>(
            owner,
            "select payload ->> 'organization_id' from public.outbox where id = @Id",
            Token,
            ("Id", outboxId));
        payload.ShouldBe("01J0F0RGANIZATION00000000");
    }

    /// <summary>
    /// The rollback half of the same contract: the writer's affected-row guard has
    /// already proven the INSERT ran inside the transaction (it throws otherwise),
    /// and disposing the scope without <c>CommitAsync</c> must undo it — otherwise a
    /// failed command would leave work queued for a change that never happened.
    /// </summary>
    [Fact]
    public async Task Outbox_Rollback_LeavesNoRow()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);
        var businessRow = Guid.CreateVersion7();
        Guid outboxId;

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            await host.Db.ExecuteAsync(
                "insert into public.rls_probe (org_id, id, label) values (@OrgId, @Id, @Label)",
                new { OrgId = OrgA, Id = businessRow, Label = "rolled-back" },
                Token);

            outboxId = await host.Outbox.EnqueueAsync(
                TaskType,
                Payload,
                "organization",
                businessRow,
                "identity:organization:created",
                Token);

            scope.IsActive.ShouldBeTrue();
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var business = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.rls_probe where id = @Id",
            Token,
            ("Id", businessRow));
        business.ShouldBe(0);

        var rows = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.outbox where id = @Id",
            Token,
            ("Id", outboxId));
        rows.ShouldBe(0);
    }

    /// <summary>
    /// 00 §5.7 P4 / 01.5.1-T2.1: the dispatcher's claim shape — undispatched rows in
    /// <c>created_at</c> order, locked with the queue pattern — is served by the
    /// partial index the migration ships, not by a scan of the whole outbox.
    /// </summary>
    [Fact]
    public async Task Outbox_UndispatchedIndex_Used()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        await OutboxIdempotencyTestHost.ExecuteAsync(
            owner,
            """
            insert into public.outbox
              (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
            select gen_random_uuid(), @OrgId, 'organization', gen_random_uuid(), @TaskType,
                   '{}'::jsonb, 'seed:' || g
              from generate_series(1, 100) g
            """,
            Token,
            ("OrgId", OrgA),
            ("TaskType", TaskType));

        // The plan is what is asserted here, not the planner's row estimate, so the
        // sequential alternative is disabled for this session.
        await OutboxIdempotencyTestHost.ExecuteAsync(owner, "set enable_seqscan = off", Token);

        var plan = await OutboxIdempotencyTestHost.ScalarAsync<string>(
            owner,
            """
            explain (format json, costs off)
            select id from public.outbox
             where dispatched_at is null
             order by created_at
             limit 200
               for update skip locked
            """,
            Token);

        plan.ShouldNotBeNull();
        plan.ShouldContain("outbox_undispatched_idx");
    }

    /// <summary>
    /// The producer-side dedupe key of 00 §5.3 A10: one key per organization, so a
    /// command that flushes twice cannot queue the same work twice — while the same
    /// key in another tenant is a different piece of work and must be accepted.
    /// </summary>
    [Fact]
    public async Task Outbox_IdempotencyKeyUniquePerOrg()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);
        var aggregateId = Guid.CreateVersion7();

        await using (var scope = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            await host.Outbox.EnqueueAsync(
                TaskType,
                Payload,
                "organization",
                aggregateId,
                "identity:organization:created",
                Token);

            await scope.CommitAsync(Token);
        }

        await using (var duplicate = await host.Scopes.BeginAsync(OrgA, cancellationToken: Token))
        {
            var error = await Should.ThrowAsync<PostgresException>(
                () => host.Outbox.EnqueueAsync(
                    TaskType,
                    Payload,
                    "organization",
                    aggregateId,
                    "identity:organization:created",
                    Token));

            error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }

        await using (var otherOrg = await host.Scopes.BeginAsync(OrgB, cancellationToken: Token))
        {
            await host.Outbox.EnqueueAsync(
                TaskType,
                Payload,
                "organization",
                aggregateId,
                "identity:organization:created",
                Token);

            await otherOrg.CommitAsync(Token);
        }

        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database),
            Token);

        var all = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.outbox where idempotency_key = @Key",
            Token,
            ("Key", "identity:organization:created"));
        all.ShouldBe(2);

        var orgA = await OutboxIdempotencyTestHost.ScalarAsync<long>(
            owner,
            "select count(*) from public.outbox where idempotency_key = @Key and org_id = @OrgId",
            Token,
            ("Key", "identity:organization:created"),
            ("OrgId", OrgA));
        orgA.ShouldBe(1);
    }

    /// <summary>
    /// 00 §5.5 C7 / 01.5.1 §8 S1: the write path refuses to run without a tenant
    /// transaction — an outbox row that is not in the command's transaction is
    /// exactly the second, non-transactional write path the contract forbids.
    /// </summary>
    [Fact]
    public async Task Outbox_WithoutTenantScope_Throws()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = OutboxIdempotencyTestHost.Start(_fixture, database);

        await Should.ThrowAsync<TenantScopeMissingException>(
            () => host.Outbox.EnqueueAsync(
                TaskType,
                Payload,
                "organization",
                Guid.CreateVersion7(),
                "identity:organization:created",
                Token));
    }
}
