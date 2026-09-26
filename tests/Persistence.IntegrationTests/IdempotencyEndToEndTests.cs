using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Mediator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Persistence.IntegrationTests;

/// <summary>
/// 01.5.1-T5.2 / AC6 (<c>plans/features/01-foundation/01.5.1-durable-internal-tasks.md</c> §2, §9):
/// the <c>Idempotency-Key</c> contract end to end through the <b>real C2 pipeline</b>
/// — the behaviour claims the key inside the tenant transaction
/// (<c>IdempotencyBehaviour</c> → <c>IIdempotencyStore</c> → <c>idempotency_record</c>),
/// and this test reads back the record the pipeline itself wrote: one row even
/// though the operation is org-scoped (<c>project_id</c> NULL, T94), a ≥ 24 h
/// retention floor, and the stored 201/body a replay returns instead of running the
/// handler again.
/// </summary>
/// <remarks>
/// The store-level halves of AC6 already exist and are not repeated:
/// <c>IdempotencyTests.Idempotency_SameKeySameHash_ReplaysResponse</c> /
/// <c>SameKeyDifferentHash_409</c> / <c>Record_ExpiresAt_AtLeast24h</c> (including the
/// database's own check constraint) / <c>Idempotency_OrgScoped_NullProject_Dedupes</c>,
/// <c>PipelineTests.Idempotent_Command_Replays_And_Conflicts</c> (the replay and
/// conflict arms through the pipeline) and
/// <c>PurgeTaskTests.PurgeTask_DeletesOnlyExpired</c> (the purge task clause). What
/// this test adds is the AC's request-shaped proof: the handler's run is recorded in
/// the database, so "the stored response was replayed" and "nothing executed twice"
/// are observed together on the record the pipeline created.
/// </remarks>
[Collection(PersistenceSuite.Name)]
[Trait("Category", "Docker")]
public sealed class IdempotencyEndToEndTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private const string Key = "acceptance-idempotency-key";

    private readonly PostgresPersistenceFixture _fixture;

    public IdempotencyEndToEndTests(PostgresPersistenceFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC6: the same key with the same canonical request replays the stored status
    /// and body without a second handler run; a different payload under that key is
    /// 409 <c>idempotency_conflict</c> and also does not run; the record's
    /// <c>expires_at ≥ created_at + 24 h</c>; and the org-scoped
    /// (<c>project_id</c> NULL) key deduped to a single record.
    /// </summary>
    [Fact]
    public async Task Idempotency_ReplayConflictRetentionAndOrgScopedDedupe_ThroughThePipeline()
    {
        var database = await _fixture.CreateDatabaseAsync();
        await using var host = PipelineTestHost.Start(_fixture, database, new RecordingPermissionChecker(granted: true));

        // Org-scoped operation: project_id carries NULL (T94).
        using var tenant = host.TenantContext.Publish(OrgA, projectId: null);
        var id = Guid.CreateVersion7();

        var first = await host.Mediator.Send(new AcceptanceIdempotentCommand(id, "payload-a", Key), Token);
        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        first.Status.ShouldBe(201);
        first.Value.ShouldBe(id);
        (await CountHandlerRunsAsync(database)).ShouldBe(1);

        // Same key, same payload: the stored 201/body comes back verbatim and the
        // handler is not executed again.
        var replay = await host.Mediator.Send(new AcceptanceIdempotentCommand(id, "payload-a", Key), Token);
        replay.IsSuccess.ShouldBeTrue(replay.Error?.Message);
        replay.Status.ShouldBe(201);
        replay.Value.ShouldBe(id);
        (await CountHandlerRunsAsync(database)).ShouldBe(1, "a replay returns the stored response, it does not re-run the command");

        // Same key, different payload: the conflict arm, still with no execution.
        var conflict = await host.Mediator.Send(
            new AcceptanceIdempotentCommand(Guid.CreateVersion7(), "payload-b", Key), Token);
        conflict.IsSuccess.ShouldBeFalse();
        conflict.Error!.Code.ShouldBe(ApiErrorCode.IdempotencyConflict);
        conflict.Error.Status.ShouldBe(409);
        (await CountHandlerRunsAsync(database)).ShouldBe(1);

        // The record the pipeline wrote (read as the owner, whose policy is `using (true)`):
        // one row for the org-scoped key, the 24 h floor, and the stored response the
        // replay served.
        (await OwnerScalarAsync<long>(
            database,
            "select count(*) from public.idempotency_record where org_id = @org and key = @key",
            ("org", OrgA),
            ("key", Key))).ShouldBe(1, "a NULL project_id must still dedupe (unique nulls not distinct)");

        (await OwnerScalarAsync<bool>(
            database,
            """
            select bool_and(expires_at >= created_at + interval '24 hours')
            from public.idempotency_record
            where org_id = @org and key = @key
            """,
            ("org", OrgA),
            ("key", Key))).ShouldBeTrue();

        (await OwnerScalarAsync<bool>(
            database,
            "select bool_and(project_id is null) from public.idempotency_record where org_id = @org and key = @key",
            ("org", OrgA),
            ("key", Key))).ShouldBeTrue();

        (await OwnerScalarAsync<bool>(
            database,
            """
            select bool_and(response_status = 201 and response_body is not null)
            from public.idempotency_record
            where org_id = @org and key = @key
            """,
            ("org", OrgA),
            ("key", Key))).ShouldBeTrue("the replay's stored status and body are the first call's response");
    }

    /// <summary>How often the acceptance handler ran — one <c>rls_probe</c> row per run, counted as the owner.</summary>
    private async Task<long> CountHandlerRunsAsync(string database) =>
        await OwnerScalarAsync<long>(
            database,
            "select count(*) from public.rls_probe where label = @label",
            ("label", AcceptanceIdempotentCommandHandler.Label));

    /// <summary>Runs a scalar query on the owner connection (the pipeline's records are tenant-scoped).</summary>
    private async Task<T?> OwnerScalarAsync<T>(
        string database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(Token);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue("@" + name, value);
        }

        var scalar = await command.ExecuteScalarAsync(Token);
        return scalar is null or DBNull ? default : (T)scalar;
    }
}

/// <summary>
/// The AC6 test's own idempotent command: two sends with the same key and the same
/// <c>Payload</c> are the same canonical request, a different <c>Payload</c> is a
/// different one (00 §9.1).
/// </summary>
internal sealed record AcceptanceIdempotentCommand(Guid Id, string Payload, string Key)
    : ICommand<Result<Guid>>, IIdempotentCommand
{
    /// <inheritdoc />
    public string IdempotencyKey => Key;
}

/// <summary>
/// Writes one probe row per run, so "the handler did not run again" is a database
/// count rather than an inference, and answers the stored 201 the replay must serve.
/// </summary>
[Permission("acceptance:write")]
internal sealed class AcceptanceIdempotentCommandHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : ICommandHandler<AcceptanceIdempotentCommand, Result<Guid>>
{
    /// <summary>The label that identifies this handler's rows in <c>public.rls_probe</c>.</summary>
    internal const string Label = "acceptance-idempotency";

    /// <inheritdoc />
    public async ValueTask<Result<Guid>> Handle(AcceptanceIdempotentCommand command, CancellationToken cancellationToken)
    {
        await db.ExecuteAsync(
            "insert into public.rls_probe (org_id, id, label) values (@OrgId, @Id, @Label)",
            new { OrgId = scopes.Require().OrgId, Id = Guid.CreateVersion7(), Label },
            cancellationToken).ConfigureAwait(false);

        return Result.Success(command.Id, 201);
    }
}
