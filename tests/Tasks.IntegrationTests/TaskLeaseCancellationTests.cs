using System.Globalization;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The lease half of the consumer's cancellation contract: a handler never runs past the
/// lease its claim took, so a row can never be in flight on two consumers at once. The
/// budget is driven by the injected <see cref="TimeProvider"/>, so the expiry is an
/// <c>Advance</c> instead of a wait.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskLeaseCancellationTests
{
    private const string Queue = "lease-cancellation";
    private const string Key = "lease-outlived";

    /// <summary>
    /// A lease whose margin (<c>min(5 s, lease/10)</c> = 1 s) and lease end are far enough
    /// apart that the handler is cancelled well before <c>locked_until</c>.
    /// </summary>
    private const int VisibilityTimeoutSeconds = 10;

    private readonly PostgresTasksFixture _fixture;

    public TaskLeaseCancellationTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A handler still inside its run when the lease budget runs out is cancelled and its
    /// attempt becomes a lease failure (retry, not success); while the lease was live, a
    /// second consumer could not claim the row, and its reclaim afterwards never had two
    /// runs of the row in flight.
    /// </summary>
    [Fact]
    public async Task Handler_OutlivingLease_IsCancelled_AndNeverRunsOnTwoConsumers()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Blocking,
            payload: """{"marker":"lease"}""",
            idempotencyKey: Key);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = StartHost(database, clock);

        // The second consumer runs on real time and shares the probe, so the runs of the
        // row are counted wherever they happen.
        await using var second = TasksTestHost.Start(
            _fixture,
            database,
            services: collection => collection.AddSingleton(host.Probe));

        var running = host.Consumer.PollOnceAsync(Queue, Token);
        await host.Probe.Entered.WaitAsync(TimeSpan.FromSeconds(30), Token);

        // Before the budget runs out the run is still inside the handler, so the
        // cancellation below is caused by the advance and not by a budget already spent.
        host.Probe.Runs.ShouldBe(1);
        host.Probe.Cancelled.IsCompleted.ShouldBeFalse("the handler is still waiting");
        running.IsCompleted.ShouldBeFalse("the pass is still running the handler");

        // The claim is `active` under a live lease, so a second consumer takes nothing:
        // the row is in flight on exactly one consumer (AC7's no-double-execute rule).
        (await second.Consumer.PollOnceAsync(Queue, Token))
            .ShouldBe(0, "a live lease belongs to the consumer that took it");
        host.Probe.Runs.ShouldBe(1, "the second consumer must not run the row");
        (await StateAsync(owner)).ShouldBe("active");

        // Past the lease the handler is cancelled inside its run and the attempt fails.
        clock.Advance(TimeSpan.FromSeconds(VisibilityTimeoutSeconds + 1));

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token)).ShouldBe(1);
        host.Probe.Cancelled.IsCompleted.ShouldBeTrue("the handler's token fires at its lease budget");

        await AssertLeaseAttemptFailedAsync(owner);

        // The retry made due is claimed by the second consumer and runs to completion —
        // one run at a time, which is what the probe's high-water mark proves.
        await TaskSql.ExecuteAsync(
            owner,
            $"update public.task set available_at = now() where idempotency_key = '{Key}'");

        (await second.Consumer.PollOnceAsync(Queue, Token))
            .ShouldBe(1, "the released retry is claimable by the other consumer");

        host.Probe.Runs.ShouldBe(2);
        host.Probe.MaxRunning.ShouldBe(1, "two consumers never ran this row at the same time");
        host.Probe.CancelledAtEntry.ShouldBe(0, "no run started already cancelled, so both ran for real");
        (await StateAsync(owner)).ShouldBe("done");
        (await AttemptsAsync(owner)).ShouldBe(2);
    }

    /// <summary>
    /// A run cancelled by its lease is a failed attempt like any other: `retry` with the
    /// attempt counted, the lease released, and an error that names the cause.
    /// </summary>
    private static async Task AssertLeaseAttemptFailedAsync(string owner)
    {
        (await StateAsync(owner))
            .ShouldBe("retry", "a run cancelled by the lease is a failed attempt, not a done row");
        (await AttemptsAsync(owner)).ShouldBe(1);
        (await TaskSql.ScalarAsync<bool>(
            owner,
            $"select locked_until is null from public.task where idempotency_key = '{Key}'"))
            .ShouldBeTrue("the failure transition releases the lease");
        var leaseError = await TaskSql.ScalarAsync<string>(
            owner,
            $"select last_error from public.task where idempotency_key = '{Key}'");
        leaseError.ShouldNotBeNull().ShouldContain("task lease");
    }

    /// <summary>
    /// Starts the consumer whose clock is the case's: only it can be advanced past the lease,
    /// so the expiry happens on command instead of after a real wait.
    /// </summary>
    private TasksTestHost StartHost(string database, FakeTimeProvider clock) =>
        TasksTestHost.Start(
            _fixture,
            database,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TasksOptions.SectionName + ":VisibilityTimeoutSeconds"] =
                    VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            },
            services: collection => collection.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));

    private static Task<string?> StateAsync(string owner) =>
        TaskSql.ScalarAsync<string>(owner, $"select state from public.task where idempotency_key = '{Key}'");

    private static Task<int> AttemptsAsync(string owner) =>
        TaskSql.ScalarAsync<int>(owner, $"select attempts from public.task where idempotency_key = '{Key}'");
}
