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
/// budget is measured from the monotonic reading the pass takes before its claim, so the
/// margin is an <c>Advance</c> of the injected <see cref="TimeProvider"/> and not a wait.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskLeaseCancellationTests
{
    private const string Queue = "lease-cancellation";
    private const string Key = "lease-outlived";

    /// <summary>
    /// The case's lease: its margin (<c>min(5 s, lease/10)</c>) is 1 s, so the budget is 9 s
    /// and an advance of 9.5 s cancels the handler while <c>locked_until</c> is still ahead.
    /// </summary>
    private const int VisibilityTimeoutSeconds = 10;

    private readonly PostgresTasksFixture _fixture;

    public TaskLeaseCancellationTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A handler still inside its run when its budget runs out is cancelled inside the
    /// margin, while the claim's lease is still live: at that instant the row is `active`
    /// and unexpired, so no consumer can run it a second time. The cancelled attempt then
    /// fails as a retry, and that retry runs on the other consumer.
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

        // A fixed instant: the budget comes from the monotonic reading the pass takes before
        // its claim, so the fake clock's epoch only has to be any real date, never "now".
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
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
        // the row is in flight on exactly one consumer.
        (await second.Consumer.PollOnceAsync(Queue, Token))
            .ShouldBe(0, "a live lease belongs to the consumer that took it");
        host.Probe.Runs.ShouldBe(1, "the second consumer must not run the row");
        (await StateAsync(owner)).ShouldBe("active");

        // The run is held once its token has fired, so the row can be read at the instant
        // the margin ends the budget: 9 s of a 10 s lease, the margin being its tenth.
        var held = host.Probe.ArmCancellationGate();

        clock.Advance(TimeSpan.FromSeconds(9.5));

        await host.Probe.Cancelled.WaitAsync(TimeSpan.FromSeconds(30), Token);

        try
        {
            (await TaskSql.ScalarAsync<bool>(
                owner,
                $"select locked_until > now() from public.task where idempotency_key = '{Key}'"))
                .ShouldBeTrue("the cancellation fires while the lease the claim took is still live");
            (await StateAsync(owner))
                .ShouldBe("active", "and before any transition has written the row");
            (await second.Consumer.PollOnceAsync(Queue, Token))
                .ShouldBe(0, "an unexpired lease keeps the row off every other consumer");
        }
        finally
        {
            held.SetResult();
        }

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token)).ShouldBe(1);
        host.Probe.Cancelled.IsCompleted.ShouldBeTrue("the handler's token fires at its budget");

        await AssertLeaseAttemptFailedAsync(owner);

        // The retry made due is claimed by the second consumer and runs to completion.
        await TaskSql.ExecuteAsync(
            owner,
            $"update public.task set available_at = now() where idempotency_key = '{Key}'");

        (await second.Consumer.PollOnceAsync(Queue, Token))
            .ShouldBe(1, "the released retry is claimable by the other consumer");

        host.Probe.Runs.ShouldBe(2);
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
