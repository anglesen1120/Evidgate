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
        await host.Probe.Entered.WaitAsync(TimeSpan.FromSeconds(30), Token);

        (await AttemptsAsync(owner))
            .ShouldBe(1, "the claim counted the attempt that is now in flight");

        await stopping.CancelAsync();

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Token))
            .ShouldBe(1, "the released row is this pass's work, not a skipped claim");

        host.Probe.Cancelled.IsCompleted.ShouldBeTrue("the handler's token fires on shutdown");
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

    private static Task<string?> StateAsync(string owner) =>
        TaskSql.ScalarAsync<string>(owner, $"select state from public.task where idempotency_key = '{Key}'");

    private static Task<int> AttemptsAsync(string owner) =>
        TaskSql.ScalarAsync<int>(owner, $"select attempts from public.task where idempotency_key = '{Key}'");

    private static Task<string?> LastErrorAsync(string owner) =>
        TaskSql.ScalarAsync<string>(owner, $"select last_error from public.task where idempotency_key = '{Key}'");
}
