// The queue's half of the scan: the outbox row a completion writes for a ready artifact, dispatched by the real
// OutboxDispatcher and run by the real TaskConsumer, so `done`, `retry` and `dead_letter` are the mechanism's own
// persisted states rather than a test's reading of the handler.
using System.Text.Json;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class EvidenceSecretScanTaskTests
{
    /// <summary>The queue an unrouted task type lands in (`TasksOptions.DispatchDefaultQueue`).</summary>
    private const string DefaultQueue = "default";

    /// <summary>The aggregate type the completion names on the scan row it writes.</summary>
    private const string ArtifactAggregate = "artifact";

    /// <summary>
    /// The queue mechanism as the Worker composes it, with no hosted service, so nothing claims a row before the test
    /// does. Only the consumer tests register it, and each of them polls a database of its own: a poll claims every due
    /// row of a type this host handles, not only the row the test just enqueued.
    /// </summary>
    private static void AddTaskQueue(IServiceCollection services) =>
        services.AddEvidgateTasks(new ConfigurationBuilder().Build());

    /// <summary>
    /// A scan the consumer claims and runs reaches the verdict the handler writes: the planted key ends the artifact
    /// <c>flagged</c> and <c>sensitive</c> with one event, and the task row ends <c>done</c> with the lease released.
    /// </summary>
    [Fact]
    public async Task Scan_Claimed_By_The_Consumer_Flags_PlantedKey()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, AddTaskQueue);
        var seeded = await SeedReadyAsync(world, BodyWith(AwsPrefix + AwsBody));

        var outboxId = await EnqueueScanAsync(world, seeded.ArtifactId);
        await DispatchAsync(world);
        var taskId = await TaskIdAsync(world, outboxId);
        var queue = await TaskColumnAsync(world, taskId, "queue");
        queue.ShouldBe(DefaultQueue, "the unrouted type lands in the dispatch default queue");

        (await ConsumeAsync(world, queue!)).ShouldBe(1, "the dispatched row is the queue's only work here");

        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Flagged);
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Sensitive);
        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(1);
        (await TaskColumnAsync(world, taskId, "state")).ShouldBe("done");
        (await TaskColumnAsync(world, taskId, "attempts")).ShouldBe("1");
        (await TaskColumnAsync(world, taskId, "locked_until")).ShouldBeNull("a finished row holds no lease");
    }

    /// <summary>
    /// A store failure is not a verdict, and the queue says so in the row it persists: the attempt leaves the artifact
    /// <c>pending</c> with no event, the row becomes <c>retry</c> with the backoff and the redacted error, and the
    /// second attempt at the cap becomes <c>dead_letter</c> — never a scan state the handler never wrote.
    /// </summary>
    /// <remarks>
    /// The object is absent from the store, so the handler's read throws whatever the scanner would have done with the
    /// bytes: this test constrains the queue's mapping of a throwing handler, not the scan's own failure modes.
    /// </remarks>
    [Fact]
    public async Task Scan_ObjectMissing_ConsumerRetries_ThenDeadLetters()
    {
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(database, AddTaskQueue);
        var seeded = await SeedReadyAsync(world, CleanBody, storeBody: false);

        var outboxId = await EnqueueScanAsync(world, seeded.ArtifactId);
        await DispatchAsync(world);
        var taskId = await TaskIdAsync(world, outboxId);
        var queue = await TaskColumnAsync(world, taskId, "queue");

        // The cap is set after the dispatch because the row does not exist before it; two attempts show the retry
        // state before the row reaches the cap.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set max_attempts = 2 where id = '{taskId:D}'");

        (await ConsumeAsync(world, queue!)).ShouldBe(1, "a throwing handler still ran its row");

        (await TaskColumnAsync(world, taskId, "state")).ShouldBe("retry");
        (await TaskColumnAsync(world, taskId, "attempts")).ShouldBe("1");
        (await TaskColumnAsync(world, taskId, "last_error")).ShouldNotBeNullOrWhiteSpace();
        (await TaskColumnAsync(world, taskId, "locked_until")).ShouldBeNull("the retry released the lease");
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(
            ArtifactScanState.Pending,
            "a store failure is retried, never read as clean");
        (await ColumnAsync(world, seeded.ArtifactId, "sensitivity")).ShouldBe(ArtifactSensitivity.Normal);
        (await EventCountAsync(world, seeded.ArtifactId)).ShouldBe(0);

        // The backoff has not elapsed, so the row is not due and the next pass leaves it.
        (await ConsumeAsync(world, queue!)).ShouldBe(0);

        // Make the retry due: the second attempt reaches the cap.
        await _fixture.ExecuteAsync(
            world.Database,
            $"update public.task set available_at = now() where id = '{taskId:D}'");

        (await ConsumeAsync(world, queue!)).ShouldBe(1);

        (await TaskColumnAsync(world, taskId, "state")).ShouldBe("dead_letter");
        (await TaskColumnAsync(world, taskId, "attempts")).ShouldBe("2");
        (await TaskColumnAsync(world, taskId, "last_error")).ShouldNotBeNullOrWhiteSpace();
        (await ColumnAsync(world, seeded.ArtifactId, "scan_state")).ShouldBe(ArtifactScanState.Pending);
    }

    /// <summary>
    /// The scan row exactly as a completion writes it (<c>CompleteArtifactUpload.EnqueueAsync</c>): the same type, the
    /// same <c>{artifact_id}</c> payload and the same per-artifact idempotency key, through the production writer.
    /// </summary>
    /// <param name="world">The tenant the artifact belongs to.</param>
    /// <param name="artifactId">The artifact the row names.</param>
    private static async Task<Guid> EnqueueScanAsync(World world, Guid artifactId)
    {
        var outbox = world.Host.Services.GetRequiredService<IOutboxWriter>();
        var scopes = world.Host.Services.GetRequiredService<ITenantScopeFactory>();

        await using var scope = await scopes.BeginAsync(world.Org, cancellationToken: Token);
        var outboxId = await outbox.EnqueueAsync(
            ScanTask,
            JsonSerializer.Serialize(new { artifact_id = artifactId }),
            ArtifactAggregate,
            artifactId,
            $"evidence:artifact:{artifactId:D}:secret_scan",
            Token);
        await scope.CommitAsync();

        return outboxId;
    }

    /// <summary>The Worker's dispatch loop body: the undispatched outbox row becomes a task row.</summary>
    /// <param name="world">The host whose dispatcher runs.</param>
    private static async Task DispatchAsync(World world)
    {
        var dispatched = await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);

        dispatched.Enqueued.ShouldBe(1, "the scan row is the only undispatched work in this database");
    }

    /// <summary>The one claim-and-run pass the Worker's poller makes, over the queue the dispatcher routed to.</summary>
    /// <param name="world">The host whose consumer runs.</param>
    /// <param name="queue">The queue the dispatched row landed in.</param>
    private static async Task<int> ConsumeAsync(World world, string queue) =>
        await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync(queue, Token);

    /// <summary>The task row the dispatcher enqueued for <paramref name="outboxId"/> (01.5.1-T3's key).</summary>
    /// <param name="world">The tenant whose database is read.</param>
    /// <param name="outboxId">The outbox row the completion wrote.</param>
    private Task<Guid> TaskIdAsync(World world, Guid outboxId) =>
        _fixture.ScalarAsync<Guid>(
            world.Database,
            $"select id from public.task where idempotency_key = 'outbox:{outboxId:D}'");

    /// <summary>One column of the task row, rendered as text so a null stays a null.</summary>
    /// <param name="world">The tenant whose database is read.</param>
    /// <param name="taskId">The task row.</param>
    /// <param name="column">The column to read.</param>
    private Task<string?> TaskColumnAsync(World world, Guid taskId, string column) =>
        _fixture.ScalarAsync<string?>(
            world.Database,
            $"select {column}::text from public.task where id = '{taskId:D}'");
}
