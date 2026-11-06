using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// The deferral answer of a task handler: work that has to wait for a window the
/// handler names (a quota period, an upstream reset) moves the row to `retry` at
/// that time with its attempt given back — no backoff, no dead letter at the cap,
/// no alert. The ordinary-exception test is the control that still spends an attempt.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskDeferralTests
{
    private const string Queue = "deferral";

    private readonly PostgresTasksFixture _fixture;

    public TaskDeferralTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deferred_Handler_Keeps_The_Attempt_And_Waits_For_The_Delay()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"later"}""",
            idempotencyKey: "deferred");

        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new TaskDeferredException(TimeSpan.FromMinutes(30));

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'deferred'"))
            .ShouldBe("retry");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'deferred'"))
            .ShouldBe(0, "a deferral does not consume an attempt");
        (await TaskSql.ScalarAsync<bool>(
            owner,
            """
            select available_at between now() + interval '29 minutes 50 seconds'
                                  and now() + interval '30 minutes 10 seconds'
               and locked_until is null
              from public.task where idempotency_key = 'deferred'
            """))
            .ShouldBeTrue("the row waits for the delay the handler asked for and releases its lease");
        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where idempotency_key = 'deferred'"))
            .ShouldNotBeNull()
            .ShouldStartWith($"{nameof(TaskDeferredException)}: ");

        // The delay is all that stands between the row and its next attempt.
        (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Count
            .ShouldBe(0, "a deferred row is not claimable before its window");

        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set available_at = now() where idempotency_key = 'deferred'");

        var claim = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();
        claim.Attempts.ShouldBe(1, "the redelivered attempt is the first one the handler will spend");
    }

    /// <summary>
    /// The claim that defers can be the last allowed attempt: the deferral gives that
    /// attempt back, so the row neither dead-letters nor alerts and runs again when its
    /// window opens.
    /// </summary>
    [Fact]
    public async Task Deferred_At_The_Attempt_Cap_Does_Not_DeadLetter()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"capped-defer"}""",
            idempotencyKey: "capped-defer",
            maxAttempts: 1);

        var sink = new RecordingTaskAlertSink();
        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            services: services => services.AddSingleton<ITaskAlertSink>(sink));
        host.Recorder.Failure = new TaskDeferredException(TimeSpan.FromMinutes(5));

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'capped-defer'"))
            .ShouldBe("retry");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'capped-defer'"))
            .ShouldBe(0, "the last allowed attempt is given back, not spent on a deferral");
        sink.Alerts.ShouldBeEmpty("a deferral is not a dead letter and must not alert");

        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set available_at = now() where idempotency_key = 'capped-defer'");

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        host.Recorder.Runs["capped-defer"].ShouldBe(2, "the deferred row runs again instead of sitting at the cap");
        sink.Alerts.ShouldBeEmpty();
    }

    /// <summary>
    /// The lease token still decides: a claim whose row was reclaimed cannot defer the
    /// newer attempt's work, and the current claim can.
    /// </summary>
    [Fact]
    public async Task Stale_Claim_Cannot_Defer_The_Newer_Attempt()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"stale-defer"}""",
            idempotencyKey: "stale-defer",
            maxAttempts: 5);

        await using var host = TasksTestHost.Start(_fixture, database);

        var stale = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();
        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set locked_until = now() - interval '1 second' where id = @id",
            ("id", stale.Id));

        var newer = (await host.Claimer.ClaimAsync(Queue, [TaskHandlerTypes.Recording], 1, Token)).Single();
        newer.Id.ShouldBe(stale.Id);
        newer.Attempts.ShouldBe(2);

        (await host.Leases.DeferAsync(stale, TimeSpan.FromMinutes(30), "stale", Token))
            .ShouldBeFalse("a superseded claim must not defer the newer attempt's row");

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where id = @id", ("id", stale.Id)))
            .ShouldBe("active");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where id = @id", ("id", stale.Id)))
            .ShouldBe(2, "the newer attempt keeps the count it was claimed with");

        (await host.Leases.DeferAsync(newer, TimeSpan.FromMinutes(30), "waiting", Token)).ShouldBeTrue();
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where id = @id", ("id", stale.Id)))
            .ShouldBe("retry");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where id = @id", ("id", stale.Id)))
            .ShouldBe(1);
    }

    /// <summary>A sub-second request never parks the row for less than a second.</summary>
    [Fact]
    public async Task Deferred_SubSecond_Delay_Is_Clamped_To_One_Second()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"clamped"}""",
            idempotencyKey: "clamped");

        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new TaskDeferredException(TimeSpan.FromMilliseconds(100));

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<bool>(
            owner,
            """
            select available_at > now() + interval '900 milliseconds'
               and available_at < now() + interval '2 seconds'
              from public.task where idempotency_key = 'clamped'
            """))
            .ShouldBeTrue("the queue never parks a row for less than a second");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'clamped'"))
            .ShouldBe(0);
    }

    /// <summary>
    /// Control: a plain exception is still a failure — the attempt is spent, the row
    /// backs off and the redacted error is stored, so the deferral path has not
    /// weakened the ordinary failure semantics.
    /// </summary>
    [Fact]
    public async Task Ordinary_Exception_Still_Spends_An_Attempt()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"ordinary"}""",
            idempotencyKey: "ordinary");

        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new InvalidOperationException(TaskRunRecorder.PoisonMessage);

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'ordinary'"))
            .ShouldBe("retry");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'ordinary'"))
            .ShouldBe(1, "a failure spends the attempt the claim counted");
        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where idempotency_key = 'ordinary'"))
            .ShouldBe($"InvalidOperationException: {TaskRunRecorder.PoisonMessage}");
        (await TaskSql.ScalarAsync<bool>(
            owner,
            "select available_at > now() and locked_until is null from public.task where idempotency_key = 'ordinary'"))
            .ShouldBeTrue("a failure waits for its backoff");
    }
}
