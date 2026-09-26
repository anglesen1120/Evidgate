using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 01.5.1-T4.1/§6/§7/§8 S3 (AC4): the retry cap ends a poisoned task in
/// `dead_letter` with the redacted `last_error`; every `dead_letter` transition —
/// the consumer's retry-cap path and the claimer's expired-lease path — notifies
/// the <see cref="ITaskAlertSink"/> hook; and a sink that throws cannot break the
/// consumer loop.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskDeadLetterTests
{
    private const string Queue = "dead-letter";

    private readonly PostgresTasksFixture _fixture;

    public TaskDeadLetterTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// AC4: a handler that throws every time is retried until
    /// <c>max_attempts</c>, then the row is `dead_letter` with `last_error` set
    /// and the lease released.
    /// </summary>
    [Fact]
    public async Task Handler_AlwaysThrows_DeadLetterAfterMaxAttempts()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"poison"}""",
            idempotencyKey: "always-throws",
            maxAttempts: 3);

        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new InvalidOperationException(TaskRunRecorder.PoisonMessage);

        // Each poll runs the row's next attempt once it is due; the backoff gate
        // (`available_at`) is forced here rather than waited on, so the test
        // exercises the cap without depending on the real clock.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await TaskSql.ExecuteAsync(
                owner,
                "update public.task set available_at = now() where idempotency_key = 'always-throws'");
            (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        }

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'always-throws'"))
            .ShouldBe("dead_letter");
        (await TaskSql.ScalarAsync<int>(owner, "select attempts from public.task where idempotency_key = 'always-throws'"))
            .ShouldBe(3);
        (await TaskSql.ScalarAsync<string>(owner, "select last_error from public.task where idempotency_key = 'always-throws'"))
            .ShouldBe($"InvalidOperationException: {TaskRunRecorder.PoisonMessage}");
        (await TaskSql.ScalarAsync<bool>(owner, "select locked_until is null from public.task where idempotency_key = 'always-throws'"))
            .ShouldBeTrue();
        host.Recorder.Runs["poison"].ShouldBe(3, "each attempt ran once, then the row gave up");
    }

    /// <summary>
    /// 00 §5.7 S3: `last_error` is the exception's type name plus the first line
    /// of its message — the detail lines a stack-looking or secret-bearing
    /// payload rides on never reach the row, and the value is bounded by the
    /// <c>task_last_error_length_check</c> of 0008.
    /// </summary>
    [Fact]
    public async Task DeadLetter_ErrorIsRedacted()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"leak"}""",
            idempotencyKey: "redaction",
            maxAttempts: 1);

        var detail = "password=hunter2 connectionstring=Host=x;Password=y";
        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new InvalidOperationException($"head of the message\n{detail}\nmore detail");

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        var error = (await TaskSql.ScalarAsync<string>(
            owner,
            "select last_error from public.task where idempotency_key = 'redaction'")).ShouldNotBeNull();
        error.ShouldBe("InvalidOperationException: head of the message");
        error.ShouldNotContain("password=", Case.Sensitive);
        error.ShouldNotContain("more detail", Case.Sensitive);
        error.Length.ShouldBeLessThanOrEqualTo(1000);
    }

    /// <summary>
    /// §7: the consumer's `active → dead_letter` transition notifies the alert
    /// sink once, with the redacted error and the row's identity — and a retry
    /// (still inside the cap) sends nothing.
    /// </summary>
    [Fact]
    public async Task AlertSink_Notified_OnConsumerDeadLetter()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"alert"}""",
            idempotencyKey: "consumer-alert",
            maxAttempts: 2);

        var sink = new RecordingTaskAlertSink();
        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            services: services => services.AddSingleton<ITaskAlertSink>(sink));
        host.Recorder.Failure = new InvalidOperationException(TaskRunRecorder.PoisonMessage);

        // The first attempt lands in `retry`: the hook stays silent until the
        // transition is actually `dead_letter`.
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        sink.Alerts.ShouldBeEmpty();

        await TaskSql.ExecuteAsync(
            owner,
            "update public.task set available_at = now() where idempotency_key = 'consumer-alert'");
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        var alert = sink.Alerts.ShouldHaveSingleItem();
        alert.Source.ShouldBe(TaskDeadLetterSource.Consumer);
        alert.Queue.ShouldBe(Queue);
        alert.TaskType.ShouldBe(TaskHandlerTypes.Recording);
        alert.Attempts.ShouldBe(2);
        alert.MaxAttempts.ShouldBe(2);
        alert.LastError.ShouldBe($"InvalidOperationException: {TaskRunRecorder.PoisonMessage}");

        var taskId = await TaskSql.ScalarAsync<Guid>(
            owner,
            "select id from public.task where idempotency_key = 'consumer-alert'");
        alert.TaskId.ShouldBe(taskId);
    }

    /// <summary>
    /// §7, the second transition: a row whose lease expired with every attempt
    /// used (a consumer that died holding it) is moved to `dead_letter` by the
    /// claimer's reclaim path and each row produces one alert — per row, because
    /// a batch can hold more than one poison task.
    /// </summary>
    [Fact]
    public async Task AlertSink_Notified_OnExpiredLeaseDeadLetter()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"expired-one"}""",
            idempotencyKey: "expired-lease-one",
            state: "active",
            attempts: 2,
            maxAttempts: 2,
            lockedUntil: DateTime.UtcNow.AddSeconds(-5));
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"expired-two"}""",
            idempotencyKey: "expired-lease-two",
            state: "active",
            attempts: 8,
            maxAttempts: 8,
            lockedUntil: DateTime.UtcNow.AddSeconds(-5));

        var sink = new RecordingTaskAlertSink();
        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            services: services => services.AddSingleton<ITaskAlertSink>(sink));

        (await host.Claimer.ReclaimExpiredAsync(Queue, Token)).ShouldBe(0, "both rows are at the cap, so nothing comes back");

        sink.Alerts.Count.ShouldBe(2, "each dead-lettered row produces one alert");
        sink.Alerts.All(alert => alert.Source == TaskDeadLetterSource.ExpiredLease).ShouldBeTrue();
        sink.Alerts.All(alert => alert.Queue == Queue).ShouldBeTrue();
        sink.Alerts.Select(alert => alert.LastError).ShouldAllBe(error =>
            error != null && error.Contains("lease expired", StringComparison.Ordinal));
    }

    /// <summary>
    /// §7's contract: a sink that throws (an alert channel being down) must not
    /// break the consumer loop — the row is already `dead_letter`, the poll
    /// completes and the same consumer keeps working the queue afterwards.
    /// </summary>
    [Fact]
    public async Task AlertSink_ThrowingSink_DoesNotBreakConsumer()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"poison"}""",
            idempotencyKey: "throwing-sink",
            maxAttempts: 1);

        await using var host = TasksTestHost.Start(
            _fixture,
            database,
            services: services => services.AddSingleton<ITaskAlertSink>(new ThrowingTaskAlertSink()));
        host.Recorder.Failure = new InvalidOperationException(TaskRunRecorder.PoisonMessage);

        // The transition still lands and the poll still completes: the sink's
        // exception is contained by the mechanism's guard.
        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'throwing-sink'"))
            .ShouldBe("dead_letter");

        // The same consumer keeps working: the next pass runs the next row.
        host.Recorder.Failure = null;
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"healthy"}""",
            idempotencyKey: "healthy");

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);
        (await TaskSql.ScalarAsync<string>(owner, "select state from public.task where idempotency_key = 'healthy'"))
            .ShouldBe("done");
    }

    /// <summary>
    /// 01.5.1 §7 / 00 §11: the default sink publishes
    /// <c>task_dead_letter_total</c> on the application meter — the name the
    /// alert rules consume — for each `dead_letter` transition.
    /// </summary>
    [Fact]
    public async Task DeadLetter_Counter_Incremented()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);
        await TaskSql.SeedTaskAsync(
            owner,
            Queue,
            TaskHandlerTypes.Recording,
            payload: """{"value":"counted"}""",
            idempotencyKey: "counter",
            maxAttempts: 1);

        var counted = new ConcurrentQueue<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, publisher) =>
            {
                if (instrument.Meter.Name == TaskQueueMetrics.MeterName
                    && instrument.Name == TaskQueueMetrics.DeadLetterCounterName)
                {
                    publisher.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == TaskQueueMetrics.DeadLetterCounterName)
            {
                counted.Enqueue(measurement);
            }
        });
        listener.Start();

        // The sink (and therefore the meter and its counter) is created when the
        // consumer is resolved, so the listener must already be running before
        // the host is built — the counter only ever increments from then on.
        await using var host = TasksTestHost.Start(_fixture, database);
        host.Recorder.Failure = new InvalidOperationException(TaskRunRecorder.PoisonMessage);

        (await host.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        counted.Sum().ShouldBe(1, "one dead_letter transition is one +1 on task_dead_letter_total");
    }
}
