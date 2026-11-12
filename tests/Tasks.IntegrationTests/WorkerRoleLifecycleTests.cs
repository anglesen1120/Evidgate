using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The queue role end to end through the production components: the dispatch moves an
/// outbox row into <c>task</c> as <c>evidgate_worker</c>, the claim leases it as that
/// role, and the consumer completes it — no step needs the app role's DML.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class WorkerRoleLifecycleTests
{
    private const string Queue = "default";

    private readonly PostgresTasksFixture _fixture;

    public WorkerRoleLifecycleTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WorkerRole_CanClaimAckAndDispatch()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        var org = await SeedOrgAsync(owner);

        // A producer writes the outbox row with the app role: INSERT is the only DML it keeps.
        (await TaskSql.ExecuteAsync(
            _fixture.AppConnectionString(database),
            """
            insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
            values (@id, @org, 'record', @aggregate, @task_type, '{"value":"lifecycle"}'::jsonb, @key)
            """,
            ("id", Guid.CreateVersion7()),
            ("org", org),
            ("aggregate", Guid.CreateVersion7()),
            ("task_type", TaskHandlerTypes.Recording),
            ("key", "lifecycle:outbox"))).ShouldBe(1);

        await using var workerSource = _fixture.OpenWorkerDataSource(database);
        using var dispatcher = new OutboxDispatcher(
            workerSource, Options.Create(new TasksOptions()), NullLogger<OutboxDispatcher>.Instance);
        var dispatched = await dispatcher.DispatchBatchAsync(Token);

        dispatched.Claimed.ShouldBe(1);
        dispatched.Enqueued.ShouldBe(1);
        var taskId = await TaskSql.ScalarAsync<Guid>(
            owner,
            "select id from public.task where idempotency_key = 'lifecycle:outbox'");

        await using var host = TasksTestHost.Start(_fixture, database);

        // The consumer owns both halves of the lifecycle: its poll claims the row as the
        // queue role and acknowledges it after the handler returns, so a passing count of
        // one is the claim, and the persisted `done` below is the acknowledgement.
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        host.Recorder.Runs.ContainsKey("lifecycle").ShouldBeTrue("the consumer ran the handler once");

        await using var worker = new NpgsqlConnection(_fixture.WorkerConnectionString(database));
        await worker.OpenAsync(Token);
        (await TaskSql.ScalarAsync<string>(worker, "select state from public.task where id = @id", ("id", taskId)))
            .ShouldBe("done");
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select locked_until is null from public.task where id = @id",
            ("id", taskId))).ShouldBeTrue("the acknowledgement releases the lease");
    }

    private static async Task<Guid> SeedOrgAsync(string owner)
    {
        var org = Guid.CreateVersion7();
        await TaskSql.ExecuteAsync(
            owner,
            "insert into public.organization (id, name, slug) values (@id, 'lifecycle', @slug)",
            ("id", org),
            ("slug", "life-" + org.ToString("N")));
        return org;
    }
}
