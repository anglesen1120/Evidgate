using Evidgate.BuildingBlocks.Persistence.Tasks;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The shutdown half of the consumer's cancellation contract: a handler cancelled by the
/// host's stopping token is released, not failed. The row goes back to the queue with its
/// attempt given back, so a deploy neither burns a retry nor dead-letters healthy work.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskShutdownReleaseTests
{
    private const string Queue = "shutdown-release";
    private const string Key = "shutdown-mid-handler";

    private readonly PostgresTasksFixture _fixture;

    public TaskShutdownReleaseTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The stopping token of the host fires while the handler is still inside its run: the
    /// pass returns instead of failing the attempt, the row is `queued` again with
    /// <c>attempts</c> unchanged and no lease, and the very next poll claims and runs it.
    /// </summary>
    [Fact]
    public async Task Shutdown_MidHandler_ReleasesTask_AttemptsUnchanged_Claimable()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Blocking,
            payload: """{"marker":"shutdown"}""",
            idempotencyKey: Key);

        await using var host = TasksTestHost.Start(_fixture, database);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = host.Consumer.PollOnceAsync(Queue, stopping.Token);
        await host.Probe.WaitEnteredAsync(TimeSpan.FromSeconds(30), Token);

        (await AttemptsAsync(owner))
            .ShouldBe(1, "the claim counted the attempt that is now in flight");

        await stopping.CancelAsync();

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token))
            .ShouldBe(1, "the released row is this pass's work, not a skipped claim");

        host.Probe.HasCancelled.ShouldBeTrue("the handler's token fires on shutdown");
        (await StateAsync(owner))
            .ShouldBe("queued", "a shutdown hands the row back instead of failing it");
        (await AttemptsAsync(owner)).ShouldBe(0, "the released attempt is given back");
        (await TaskSql.ScalarAsync<bool>(
            owner,
            $"select locked_until is null and available_at <= now() from public.task where idempotency_key = '{Key}'"))
            .ShouldBeTrue("no lease is left behind and the row is due at once");
        (await LastErrorAsync(owner)).ShouldBeNull("a released row is not a failed attempt");

        // Claimable again on the next poll, which is the whole point of the release.
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await StateAsync(owner)).ShouldBe("done");
        (await AttemptsAsync(owner))
            .ShouldBe(1, "the reclaimed row spends exactly the one attempt it now uses");
        host.Probe.Runs.ShouldBe(2);
        host.Probe.MaxRunning.ShouldBe(1, "the released row never ran twice at the same time");
        host.Probe.CancelledAtEntry.ShouldBe(0, "the reclaimed run started with a live budget, not a spent one");
    }

    /// <summary>
    /// A shutdown in the middle of a batch releases the rest of the claimed batch too, not
    /// only the row whose handler was running: every row goes back to `queued` with its
    /// attempt given back, so one deploy does not park a whole batch behind a full lease.
    /// </summary>
    [Fact]
    public async Task Shutdown_MidBatch_ReleasesTheTail_AllRowsClaimable()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        // Explicit due times, a second apart: the claim orders by `available_at`, and rows
        // enqueued in one instant are ties whose order that index does not define.
        string[] keys = ["batch-first", "batch-second", "batch-third"];
        for (var index = 0; index < keys.Length; index++)
        {
            await TaskSql.SeedTaskAsync(
                owner,
                Queue,
                TaskHandlerTypes.Blocking,
                payload: """{"marker":"batch"}""",
                idempotencyKey: keys[index],
                availableAt: DateTime.UtcNow.AddSeconds(index - keys.Length));
        }

        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TasksOptions.SectionName + ":BatchSize"] = "3",
            });

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = host.Consumer.PollOnceAsync(Queue, stopping.Token);
        await host.Probe.WaitEnteredAsync(TimeSpan.FromSeconds(30), Token);

        await stopping.CancelAsync();

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token))
            .ShouldBe(1, "only the row whose turn came ran, and it is this pass's work");

        host.Probe.Runs.ShouldBe(1, "the rest of the batch never started");
        // The release stamps `available_at = now()` on each row, so the queue's order after
        // it is the release order and no longer the seeded due order: compare the set.
        (await QueuedKeysAsync(owner)).ShouldBe(
            keys,
            ignoreOrder: true,
            customMessage: "every released row is queued again");
        (await QueueAttemptsAsync(owner)).ShouldBe(0, "each released row got its attempt back");
        (await ClaimableCountAsync(owner)).ShouldBe(3, "no lease is left behind, so all three are due at once");

        // Claimable again on the next poll, which is the whole point of the release.
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(3);

        host.Probe.Runs.ShouldBe(4);
        (await DoneCountAsync(owner)).ShouldBe(3);
        (await QueueAttemptsAsync(owner))
            .ShouldBe(3, "each reclaimed row spends exactly the one attempt it now uses");
    }

    /// <summary>
    /// The release does not depend on the exception type a cancelled handler raises: one
    /// that reports its abort as a plain exception, with the host stopping and the lease
    /// still live, is a shutdown release too — not a failed attempt.
    /// </summary>
    [Fact]
    public async Task Shutdown_HandlerThrowsNonCancellation_StillReleases()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.FaultingBlocking,
            payload: """{"marker":"shutdown-fault"}""",
            idempotencyKey: Key);

        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            services: collection =>
                collection.AddTaskHandler<BlockingPayload, FaultingBlockingTaskHandler>());

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = host.Consumer.PollOnceAsync(Queue, stopping.Token);
        await host.Probe.WaitEnteredAsync(TimeSpan.FromSeconds(30), Token);

        await stopping.CancelAsync();

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token)).ShouldBe(1);
        host.Probe.HasCancelled.ShouldBeTrue("the handler's token fires on shutdown");

        (await StateAsync(owner)).ShouldBe("queued", "the row is handed back, not failed");
        (await AttemptsAsync(owner)).ShouldBe(0, "the released attempt is given back");
        (await LastErrorAsync(owner)).ShouldBeNull("a released row records no failure");
        (await ClaimableCountAsync(owner)).ShouldBe(1);
    }

    /// <summary>
    /// A row that carries an <c>org_id</c> is released the way it is run: inside the tenant
    /// scope opened from that id. The scope the cancelled handler leaves is rolled back with
    /// it, and the row goes back to the queue instead of spending the attempt of a deploy.
    /// </summary>
    [Fact]
    public async Task Shutdown_TenantTask_ReleasesRowRunUnderTenantScope()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Blocking,
            payload: """{"marker":"shutdown-tenant"}""",
            orgId: Guid.CreateVersion7(),
            idempotencyKey: Key);

        await using var host = TasksTestHost.Start(_fixture, database);

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var running = host.Consumer.PollOnceAsync(Queue, stopping.Token);
        await host.Probe.WaitEnteredAsync(TimeSpan.FromSeconds(30), Token);

        await stopping.CancelAsync();

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token)).ShouldBe(1);
        host.Probe.HasCancelled.ShouldBeTrue("the handler ran inside the row's scope until shutdown");

        (await StateAsync(owner)).ShouldBe("queued", "a tenant row is handed back like a platform one");
        (await AttemptsAsync(owner)).ShouldBe(0, "the released attempt is given back");
        (await LastErrorAsync(owner)).ShouldBeNull("a released row records no failure");
        (await ClaimableCountAsync(owner)).ShouldBe(1);
    }

    private static Task<string?> StateAsync(string owner) =>
        TaskSql.ScalarAsync<string>(owner, $"select state from public.task where idempotency_key = '{Key}'");

    private static Task<int> AttemptsAsync(string owner) =>
        TaskSql.ScalarAsync<int>(owner, $"select attempts from public.task where idempotency_key = '{Key}'");

    private static Task<string?> LastErrorAsync(string owner) =>
        TaskSql.ScalarAsync<string>(owner, $"select last_error from public.task where idempotency_key = '{Key}'");

    /// <summary>The queue's rows still waiting, in the order the next claim takes them.</summary>
    private static Task<IReadOnlyList<string>> QueuedKeysAsync(string owner) =>
        TaskSql.QueryAsync<string>(
            owner,
            $"select idempotency_key from public.task where queue = '{Queue}' and state = 'queued' order by available_at, idempotency_key");

    /// <summary>Attempts the queue's rows hold, which a release gives back.</summary>
    private static Task<int> QueueAttemptsAsync(string owner) =>
        TaskSql.ScalarAsync<int>(
            owner,
            $"select coalesce(sum(attempts), 0)::int from public.task where queue = '{Queue}'");

    /// <summary>Rows the next claim pass takes: queued, due, and holding no lease.</summary>
    private static Task<int> ClaimableCountAsync(string owner) =>
        TaskSql.ScalarAsync<int>(
            owner,
            $"select count(*)::int from public.task where queue = '{Queue}' and state = 'queued' and locked_until is null and available_at <= now()");

    private static Task<int> DoneCountAsync(string owner) =>
        TaskSql.ScalarAsync<int>(
            owner,
            $"select count(*)::int from public.task where queue = '{Queue}' and state = 'done'");
}
