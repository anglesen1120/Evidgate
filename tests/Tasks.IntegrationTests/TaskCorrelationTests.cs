using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Tasks.IntegrationTests;

/// <summary>
/// 10.3.1-T1.2 (AC1, NFR-10-1) on the payload carrier itself: the producing
/// activity's W3C context and the correlation ids are stamped inside the stored
/// JSON, and reading them back is total — a payload written before this task, by
/// SQL, or by a producer with nothing listening carries no context and is not an
/// error.
/// </summary>
public sealed class TaskCorrelationTests
{
    private const string Payload =
        """{"organization_id":"01J0F0RGANIZATION00000000","run_id":"0192f0ff-0000-7000-8000-00000000abcd"}""";

    /// <summary>Stamping needs an activity: a producer with nothing listening stores its payload verbatim.</summary>
    [Fact]
    public void Stamp_Without_An_Activity_Leaves_The_Payload_Untouched() =>
        TaskCorrelation.Stamp(Payload).ShouldBe(Payload);

    /// <summary>
    /// With a producing activity the carrier is one reserved top-level object and every
    /// member of the document survives. The run id comes from the payload the producer
    /// already wrote, so the task side needs no extra producer change.
    /// </summary>
    [Fact]
    public void Stamp_Adds_The_Carrier_And_Keeps_Every_Member()
    {
        using var listener = Listen();
        using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
        using var producer = source.StartActivity("http.request", ActivityKind.Server);
        producer.ShouldNotBeNull("the listener must record the producing span");
        producer.SetTag(TaskCorrelation.RequestIdAttribute, "req-correlation-stamp");

        var stamped = TaskCorrelation.Stamp(Payload);

        using var document = JsonDocument.Parse(stamped);
        var root = document.RootElement;
        root.GetProperty("organization_id").GetString().ShouldBe("01J0F0RGANIZATION00000000");
        root.GetProperty(TaskCorrelation.RunIdPayloadMember).GetString()
            .ShouldBe("0192f0ff-0000-7000-8000-00000000abcd");

        var carrier = root.GetProperty(TaskCorrelation.CarrierKey);
        carrier.GetProperty(TaskCorrelation.TraceParentField).GetString().ShouldBe(producer.Id);
        carrier.GetProperty(TaskCorrelation.RequestIdField).GetString().ShouldBe("req-correlation-stamp");
        carrier.GetProperty(TaskCorrelation.RunIdField).GetString()
            .ShouldBe("0192f0ff-0000-7000-8000-00000000abcd");
    }

    /// <summary>A document that is not JSON comes back exactly as it went in: correlation never rewrites a producer's text.</summary>
    [Fact]
    public void Stamp_Returns_A_Malformed_Document_Unchanged()
    {
        using var listener = Listen();
        using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
        using var producer = source.StartActivity("http.request", ActivityKind.Server);

        TaskCorrelation.Stamp("{not json").ShouldBe("{not json");
    }

    /// <summary>
    /// Reading a payload without a carrier is never an exception: the W3C context is absent,
    /// while the run id still comes from the payload member the producer already wrote (a
    /// run-scoped task is correlatable to its run even when no trace context crossed the hop).
    /// </summary>
    [Fact]
    public void Read_Returns_No_Trace_Context_For_A_Payload_Without_A_Carrier()
    {
        var read = TaskCorrelation.Read(Payload);
        read.TraceParent.ShouldBeNull();
        read.TraceState.ShouldBeNull();
        read.RequestId.ShouldBeNull();
        read.RunId.ShouldBe("0192f0ff-0000-7000-8000-00000000abcd");

        TaskCorrelation.Read("{}").ShouldBe(default(CorrelationCarrier));
        TaskCorrelation.Read("{not json").ShouldBe(default(CorrelationCarrier));
    }

    /// <summary>
    /// A malformed traceparent inside a carrier stays text but never becomes a parent (W3C
    /// parse decides), and a run id that is not a GUID is dropped rather than exported
    /// (F5: the carrier is read through the same shape checks the edge applies).
    /// </summary>
    [Fact]
    public void Read_Keeps_A_Malformed_TraceParent_But_It_Is_Not_A_Parent()
    {
        var carrier = TaskCorrelation.Read(
            """{"_evidgate_correlation":{"traceparent":"00-nope","run_id":"r"}}""");

        carrier.TraceParent.ShouldBe("00-nope");
        carrier.RunId.ShouldBeNull("a run id is a GUID; free text never reaches a tag");
        ActivityContext.TryParse(carrier.TraceParent, carrier.TraceState, out _).ShouldBeFalse();
    }

    /// <summary>
    /// F5: every member read back is bounded and shaped — a request id with a control
    /// character, an over-long request id and an over-long <c>tracestate</c> are dropped,
    /// while a GUID run id survives in its canonical form.
    /// </summary>
    [Fact]
    public void Read_Drops_Unsafe_Ids_And_An_Oversized_Tracestate()
    {
        var hostile = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TaskCorrelation.CarrierKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [TaskCorrelation.TraceStateField] = new string('a', TaskCorrelation.MaxTraceStateLength + 1),
                [TaskCorrelation.RequestIdField] = "req\nforged",
                [TaskCorrelation.RunIdField] = "0192F0FF-0000-7000-8000-00000000ABCD",
            },
        });

        var carrier = TaskCorrelation.Read(hostile);

        carrier.TraceState.ShouldBeNull("a client-controlled tracestate is bounded, never carried");
        carrier.RequestId.ShouldBeNull("a control character cannot become a log attribute");
        carrier.RunId.ShouldBe("0192f0ff-0000-7000-8000-00000000abcd", "a GUID is canonicalised for search");

        var overLong = TaskCorrelation.Read(
            "{\"_evidgate_correlation\":{\"request_id\":\""
            + new string('r', TaskCorrelation.MaxRequestIdLength + 1)
            + "\"}}");
        overLong.RequestId.ShouldBeNull("the edge's 128-character bound applies to the carrier too");
    }

    /// <summary>
    /// F5: the carrier is the stamping activity's, not the document's. A producer that
    /// embeds a client-supplied object cannot pass a forged parent trace or a forged run
    /// through, and the document keeps exactly one carrier key.
    /// </summary>
    [Fact]
    public void Stamp_Replaces_A_Carrier_The_Payload_Already_Carried()
    {
        using var listener = Listen();
        using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
        using var producer = source.StartActivity("http.request", ActivityKind.Server);
        producer.ShouldNotBeNull("the listener must record the producing span");

        const string Forged =
            """{"organization_id":"01J0F0RGANIZATION00000000","_evidgate_correlation":{"traceparent":"00-forged","request_id":"forged","run_id":"0192f0ff-1111-7000-8000-00000000ffff"}}""";

        var stamped = TaskCorrelation.Stamp(Forged);

        stamped.Split(TaskCorrelation.CarrierKey, StringSplitOptions.None).Length.ShouldBe(2);
        var carrier = TaskCorrelation.Read(stamped);
        carrier.TraceParent.ShouldBe(producer!.Id, "the real producing activity wins over the payload's copy");
        carrier.RequestId.ShouldBeNull("a forged request id is not carried over");
        carrier.RunId.ShouldBeNull("a forged run id is not carried over");
    }

    /// <summary>
    /// F4: ambient baggage is a client-supplied header at the edge, so it is never a source
    /// of the run id or the request id; the span's attributes and the producer's own payload
    /// member are.
    /// </summary>
    [Fact]
    public void Stamp_Takes_The_Ids_From_Span_Attributes_Never_From_Baggage()
    {
        using var listener = Listen();
        using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
        using var producer = source.StartActivity("http.request", ActivityKind.Server);
        producer.ShouldNotBeNull("the listener must record the producing span");

        var forgedRunId = Guid.CreateVersion7().ToString("D");
        producer.SetBaggage(TaskCorrelation.RunIdBaggage, forgedRunId);
        producer.SetBaggage(TaskCorrelation.RequestIdBaggage, "req-from-baggage");

        var forged = TaskCorrelation.Read(TaskCorrelation.Stamp("""{"organization_id":"org"}"""));
        forged.RunId.ShouldBeNull("a client cannot put a run id into the carrier through baggage");
        forged.RequestId.ShouldBeNull("nor a request id");

        var routedRunId = Guid.CreateVersion7();
        producer.SetTag(TaskCorrelation.RequestIdAttribute, "req-routed");
        producer.SetTag(TaskCorrelation.RunIdAttribute, routedRunId.ToString("D"));

        var routed = TaskCorrelation.Read(TaskCorrelation.Stamp("""{"organization_id":"org"}"""));
        routed.RequestId.ShouldBe("req-routed");
        routed.RunId.ShouldBe(routedRunId.ToString("D"));
    }

    /// <summary>A payload member a producer wrote itself is the other trusted run-id source (and it must be a GUID).</summary>
    [Fact]
    public void Stamp_Reads_The_Run_From_The_Producers_Payload_Member()
    {
        using var listener = Listen();
        using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
        using var producer = source.StartActivity("http.request", ActivityKind.Server);
        producer.ShouldNotBeNull("the listener must record the producing span");

        var runId = Guid.CreateVersion7();
        TaskCorrelation.Read(TaskCorrelation.Stamp($$"""{"run_id":"{{runId:D}}"}"""))
            .RunId.ShouldBe(runId.ToString("D"));

        TaskCorrelation.Read(TaskCorrelation.Stamp("""{"run_id":"not-a-guid"}"""))
            .RunId.ShouldBeNull("the payload member is validated like every other source");
    }

    /// <summary>
    /// F6: the <c>tracestate</c> a client sent is bounded at the edge and again in the
    /// carrier; the <c>traceparent</c> stays honoured (the documented trade-off).
    /// </summary>
    [Fact]
    public void Stamp_Carries_A_Bounded_Tracestate_Only()
    {
        using var listener = Listen();
        using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
        using var producer = source.StartActivity("http.request", ActivityKind.Server);
        producer.ShouldNotBeNull("the listener must record the producing span");

        producer.TraceStateString = "vendor=ok";
        TaskCorrelation.Read(TaskCorrelation.Stamp("""{"organization_id":"org"}"""))
            .TraceState.ShouldBe("vendor=ok");

        producer.TraceStateString = new string('v', TaskCorrelation.MaxTraceStateLength + 1);
        TaskCorrelation.Read(TaskCorrelation.Stamp("""{"organization_id":"org"}"""))
            .TraceState.ShouldBeNull("an oversized tracestate is dropped, never persisted");

        TaskCorrelation.IsSafeTraceState("vendor=a,b").ShouldBeTrue();
        TaskCorrelation.IsSafeTraceState("vendor=a\nb").ShouldBeFalse("a control character cannot be carried");
    }

    /// <summary>
    /// F5: a carrier built by a caller (not read from a payload) is validated when the
    /// consumer span and the log scope are opened, so an unsafe value cannot reach a tag,
    /// a baggage item or a log attribute through that path either.
    /// </summary>
    [Fact]
    public void StartTaskActivity_Drops_Unsafe_Carrier_Values()
    {
        using var listener = Listen();
        var claim = new TaskClaim(
            Guid.CreateVersion7(),
            null,
            "queue",
            "noop",
            "{}",
            "idem",
            null,
            null,
            "active",
            1,
            8,
            DateTime.UtcNow,
            DateTime.UtcNow.AddMinutes(1),
            1);
        var hostile = new CorrelationCarrier(
            TraceParent: null,
            TraceState: new string('v', TaskCorrelation.MaxTraceStateLength + 1),
            RequestId: "req\r\nforged",
            RunId: "not-a-guid");

        using var activity = TaskCorrelation.StartTaskActivity(claim, hostile);

        activity.ShouldNotBeNull();
        activity!.GetTagItem(TaskCorrelation.RequestIdAttribute).ShouldBeNull();
        activity.GetTagItem(TaskCorrelation.RunIdAttribute).ShouldBeNull();
        activity.GetBaggageItem(TaskCorrelation.RunIdBaggage).ShouldBeNull();
    }

    /// <summary>
    /// CR-F7: the Persistence copies of the correlation vocabulary are the Observability
    /// names by value, because the assembly deliberately does not reference it. This is the
    /// one place that pins the two sides together.
    /// </summary>
    [Fact]
    public void Constants_Match_The_Observability_Vocabulary()
    {
        TaskCorrelation.ActivitySourceName.ShouldBe(EvidgateTelemetry.ActivitySourceName);
        TaskCorrelation.RequestIdAttribute.ShouldBe(CorrelationNames.RequestId);
        TaskCorrelation.RunIdAttribute.ShouldBe(CorrelationNames.RunId);
        TaskCorrelation.JobIdAttribute.ShouldBe(CorrelationNames.JobId);
        TaskCorrelation.TraceIdAttribute.ShouldBe(CorrelationNames.TraceId);
        TaskCorrelation.RequestIdBaggage.ShouldBe(CorrelationNames.RequestIdBaggage);
        TaskCorrelation.RunIdBaggage.ShouldBe(CorrelationNames.RunIdBaggage);
        TaskCorrelation.MaxRequestIdLength.ShouldBe(CorrelationNames.MaxRequestIdLength);
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskCorrelation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>
/// 10.3.1-T1.2 (AC1) over the real mechanism and real PostgreSQL: a producer's
/// activity is stamped into the outbox payload, the real dispatcher copies that
/// payload into the task row, and the real consumer opens a span that is a child of
/// the producer — one trace across the outbox hop — carrying <c>job_id</c>, the run
/// and the request id.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskCorrelationPipelineTests
{
    private const string Queue = "correlation";
    private const string TaskType = "identity.organization.provision";
    private const string RequestId = "req-correlation-task";

    private static readonly Guid Org = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly PostgresTasksFixture _fixture;

    public TaskCorrelationPipelineTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The AC1 path of the queue: enqueue inside a producing activity, dispatch, claim, run.</summary>
    [Fact]
    public async Task OutboxTask_Continues_The_Producing_Trace_And_Carries_JobId()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var runId = Guid.CreateVersion7();
        var payloadJson = JsonSerializer.Serialize(new
        {
            organization_id = Guid.CreateVersion7(),
            run_id = runId,
        });

        using var listener = Listen();

        string producerId;
        string producerTraceId;
        string producerSpanId;
        Guid outboxId;

        using (var source = new ActivitySource(TaskCorrelation.ActivitySourceName))
        using (var producing = source.StartActivity("http.request", ActivityKind.Server))
        {
            producing.ShouldNotBeNull("the listener must record the producing span");
            producing.SetTag(TaskCorrelation.RequestIdAttribute, RequestId);
            producerId = producing.Id!;
            producerTraceId = producing.TraceId.ToHexString();
            producerSpanId = producing.SpanId.ToHexString();

            await using var outboxHost = OutboxIdempotencyTestHost.Start(_fixture, database);
            await using var scope = await outboxHost.Scopes.BeginAsync(Org, cancellationToken: Token);
            outboxId = await outboxHost.Outbox.EnqueueAsync(
                TaskType,
                payloadJson,
                "organization",
                Guid.CreateVersion7(),
                "correlation:" + runId.ToString("N"),
                Token);
            await scope.CommitAsync(Token);
        }

        // The carrier is in what the producer committed, before any dispatch.
        await using (var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database), Token))
        {
            var storedTraceParent = await OutboxIdempotencyTestHost.ScalarAsync<string>(
                owner,
                "select payload -> @carrier ->> 'traceparent' from public.outbox where id = @Id",
                Token,
                ("carrier", TaskCorrelation.CarrierKey),
                ("Id", outboxId));
            storedTraceParent.ShouldBe(producerId);
        }

        // Dispatch copies the payload verbatim into the task row (0006/0009 contract).
        var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        await using (dataSource)
        {
            using var dispatcher = new OutboxDispatcher(
                dataSource,
                Options.Create(new TasksOptions
                {
                    DispatchBatchSize = 50,
                    QueueRoutes = [new QueueRouteOptions { TaskType = TaskType, Queue = Queue }],
                }),
                NullLogger<OutboxDispatcher>.Instance);

            var dispatched = await dispatcher.DispatchBatchAsync(Token);
            dispatched.Claimed.ShouldBe(1);
            dispatched.Enqueued.ShouldBe(1);
        }

        var recorder = new CorrelationRecorder();
        await using var consumerHost = TasksTestHost.Start(
            _fixture,
            database,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{TasksOptions.SectionName}:Queues:0:Name"] = Queue,
            },
            services: services =>
            {
                services.AddSingleton(recorder);
                services.AddTaskHandler<CorrelationPayload, CorrelationTaskHandler>();
            });

        (await consumerHost.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        var seen = recorder.Seen.ShouldNotBeNull("the handler must have run");
        var jobId = await ScalarAsync<string>(
            database,
            "select id::text from public.task where idempotency_key = @key",
            ("key", "outbox:" + outboxId));

        seen.TraceId.ShouldBe(producerTraceId, "the outbox hop must not start a new trace");
        seen.ParentSpanId.ShouldBe(producerSpanId, "the consumer span is a child of the producing span");
        seen.JobId.ShouldBe(jobId, "job_id is the claimed task row's id");
        seen.RunId.ShouldBe(runId.ToString("D"));
        seen.RequestId.ShouldBe(RequestId);
    }

    /// <summary>
    /// The negative boundary: a row written by SQL (a replay, a sweep, an older
    /// release) carries no context, so the consumer starts a root span that still has
    /// <c>job_id</c> — missing correlation degrades, it never fails the row.
    /// </summary>
    [Fact]
    public async Task Task_Without_A_Carrier_Starts_A_Root_Span_And_Still_Runs()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var taskId = Guid.CreateVersion7();

        await using (var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database), Token))
        {
            await OutboxIdempotencyTestHost.ExecuteAsync(
                owner,
                """
                insert into public.task (id, org_id, queue, task_type, payload, idempotency_key)
                values (@id, @org, @queue, @taskType, '{"organization_id":"11111111-1111-7111-8111-111111111111"}'::jsonb, @key)
                """,
                Token,
                ("id", taskId),
                ("org", Org),
                ("queue", Queue),
                ("taskType", TaskType),
                ("key", "correlation:seeded:" + taskId.ToString("N")));
        }

        using var listener = Listen();

        var recorder = new CorrelationRecorder();
        await using var consumerHost = TasksTestHost.Start(
            _fixture,
            database,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{TasksOptions.SectionName}:Queues:0:Name"] = Queue,
            },
            services: services =>
            {
                services.AddSingleton(recorder);
                services.AddTaskHandler<CorrelationPayload, CorrelationTaskHandler>();
            });

        (await consumerHost.Consumer.PollOnceAsync(Queue, Token)).ShouldBe(1);

        var seen = recorder.Seen.ShouldNotBeNull("the handler must have run");
        seen.ParentSpanId.ShouldBeNull("a row without a carrier is the root of its own trace");
        seen.JobId.ShouldBe(taskId.ToString("D"));
        seen.RequestId.ShouldBeNull("a row with no carrier names no request");
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskCorrelation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private async Task<T?> ScalarAsync<T>(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
            _fixture.OwnerConnectionString(database), Token);
        return await OutboxIdempotencyTestHost.ScalarAsync<T>(owner, sql, Token, parameters);
    }
}

/// <summary>
/// CR-F2 (10.3.1-T1.1): a poll that finds nothing emits no span, and a poll that finds
/// work emits one, carrying the queue and — for a single claimed row — its
/// <c>job_id</c>. The consumer and dispatcher loops poll once per second by default, so
/// an idle span per poll would bury the traces that matter.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class QueuePollSpanTests
{
    private const string ClaimQueue = "poll-spans";
    private const string OutboxTaskType = "correlation.poll";
    private const string OutboxQueue = "correlation-poll";
    private static readonly Guid Org = Guid.Parse("11111111-1111-7111-8111-111111111111");

    private readonly PostgresTasksFixture _fixture;

    public QueuePollSpanTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>An empty queue claims nothing and opens no claim span; a queued row claims and opens one.</summary>
    [Fact]
    public async Task Claim_Opens_A_Span_Only_When_A_Row_Is_Claimed()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        await using (dataSource)
        {
            var claimer = new TaskClaimer(
                dataSource,
                new RecordingTaskAlertSink(),
                Options.Create(new TasksOptions { VisibilityTimeoutSeconds = 30, BatchSize = 10 }),
                NullLogger<TaskClaimer>.Instance);

            (await claimer.ClaimAsync(ClaimQueue, ["correlation.noop"], 10, Token))
                .ShouldBeEmpty("the queue starts empty");
            activities.ShouldNotContain(
                activity => activity.OperationName == TaskClaimer.ClaimOperationName,
                "an idle poll must not export a span");

            var taskId = Guid.CreateVersion7();
            await using (var owner = await OutboxIdempotencyTestHost.OpenAsync(
                _fixture.OwnerConnectionString(database), Token))
            {
                await OutboxIdempotencyTestHost.ExecuteAsync(
                    owner,
                    """
                    insert into public.task (id, org_id, queue, task_type, payload, idempotency_key)
                    values (@id, @org, @queue, 'correlation.noop', '{"organization_id":"11111111-1111-7111-8111-111111111111"}'::jsonb, @key)
                    """,
                    Token,
                    ("id", taskId),
                    ("org", Org),
                    ("queue", ClaimQueue),
                    ("key", "poll-spans:" + taskId.ToString("N")));
            }

            var claimed = await claimer.ClaimAsync(ClaimQueue, ["correlation.noop"], 10, Token);
            claimed.Count.ShouldBe(1);

            var span = activities.ShouldHaveSingleItem(
                $"exactly one '{TaskClaimer.ClaimOperationName}' span: " +
                string.Join(", ", activities.Select(activity => activity.OperationName)));
            span.OperationName.ShouldBe(TaskClaimer.ClaimOperationName);
            span.GetTagItem("task.queue").ShouldBe(ClaimQueue);
            span.GetTagItem(TaskCorrelation.JobIdAttribute).ShouldBe(taskId.ToString("D"));
        }
    }

    /// <summary>An empty outbox dispatches nothing and opens no dispatch span; a pending row opens one.</summary>
    [Fact]
    public async Task Dispatch_Opens_A_Span_Only_When_A_Batch_Is_Claimed()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = Listen(activities);
        var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        await using (dataSource)
        {
            using var dispatcher = new OutboxDispatcher(
                dataSource,
                Options.Create(new TasksOptions
                {
                    DispatchBatchSize = 10,
                    QueueRoutes = [new QueueRouteOptions { TaskType = OutboxTaskType, Queue = OutboxQueue }],
                }),
                NullLogger<OutboxDispatcher>.Instance);

            var empty = await dispatcher.DispatchBatchAsync(Token);
            empty.Claimed.ShouldBe(0);
            activities.ShouldNotContain(
                activity => activity.OperationName == OutboxDispatcher.DispatchOperationName,
                "an idle poll must not export a span");

            var outboxId = Guid.CreateVersion7();
            await using (var owner = await OutboxIdempotencyTestHost.OpenAsync(
                _fixture.OwnerConnectionString(database), Token))
            {
                await OutboxIdempotencyTestHost.ExecuteAsync(
                    owner,
                    """
                    insert into public.outbox (id, org_id, aggregate_type, aggregate_id, task_type, payload, idempotency_key)
                    values (@id, @org, 'run', @aggregate, @taskType, '{"organization_id":"11111111-1111-7111-8111-111111111111"}'::jsonb, @key)
                    """,
                    Token,
                    ("id", outboxId),
                    ("org", Org),
                    ("aggregate", Guid.CreateVersion7()),
                    ("taskType", OutboxTaskType),
                    ("key", "poll-spans:" + outboxId.ToString("N")));
            }

            var batch = await dispatcher.DispatchBatchAsync(Token);
            batch.Claimed.ShouldBe(1);

            var span = activities.ShouldHaveSingleItem(
                $"exactly one '{OutboxDispatcher.DispatchOperationName}' span: " +
                string.Join(", ", activities.Select(activity => activity.OperationName)));
            span.OperationName.ShouldBe(OutboxDispatcher.DispatchOperationName);
            span.GetTagItem("outbox.claimed").ShouldBe(1);
            span.GetTagItem("outbox.enqueued").ShouldBe(1);
        }
    }

    private static ActivityListener Listen(ConcurrentQueue<Activity> captured)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskCorrelation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>
/// F2 / CR-F3 / perf F3 (10.3.1-T1.2, AC1): the task enqueue stamps exactly once and
/// applies the same size fallback as the outbox writer — a carrier that would cross the
/// column's 64 KiB contract is dropped, never turned into a producer failure — while a
/// document that is too large on its own keeps failing, named.
/// </summary>
[Collection(TasksSuite.Name)]
[Trait("Category", "Docker")]
public sealed class TaskQueueWriterPayloadTests
{
    private const string Queue = "payload-cap";
    private const int MaxPayloadBytes = 65536;

    private readonly PostgresTasksFixture _fixture;

    public TaskQueueWriterPayloadTests(PostgresTasksFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A payload one carrier away from the cap is stored without the carrier instead of failing.</summary>
    [Fact]
    public async Task Enqueue_Falls_Back_To_The_Unstamped_Payload_When_The_Carrier_Crosses_The_Cap()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        await using (dataSource)
        {
            using var listener = Listen();
            using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
            using var producer = source.StartActivity("http.request", ActivityKind.Server);
            producer.ShouldNotBeNull("the listener must record the producing span");
            producer.SetTag(TaskCorrelation.RequestIdAttribute, "req-payload-cap");

            // Close enough to the cap that the carrier (traceparent + request id + run id)
            // cannot fit beside it, yet small enough that the database accepts the document
            // pg_column_size measures.
            var payload = PayloadOf(MaxPayloadBytes - 100);
            Encoding.UTF8.GetByteCount(TaskCorrelation.Stamp(payload)).ShouldBeGreaterThan(
                MaxPayloadBytes,
                "the fixture only proves the fallback if stamping would cross the cap");

            var writer = new TaskQueueWriter(dataSource);
            var id = await writer.EnqueueAsync(
                new TaskEnqueue
                {
                    Queue = Queue,
                    TaskType = "correlation.noop",
                    PayloadJson = payload,
                    IdempotencyKey = "payload-cap:" + Guid.CreateVersion7().ToString("N"),
                },
                Token);

            id.ShouldNotBeNull("an oversized carrier must never fail the producer");

            await using var owner = await OutboxIdempotencyTestHost.OpenAsync(
                _fixture.OwnerConnectionString(database), Token);
            var stored = await OutboxIdempotencyTestHost.ScalarAsync<string>(
                owner,
                "select payload::text from public.task where id = @id",
                Token,
                ("id", id.Value));

            stored.ShouldNotBeNull();
            stored.Contains(TaskCorrelation.CarrierKey, StringComparison.Ordinal)
                .ShouldBeFalse("the trace link is what gets dropped, not the producer's document");
            Encoding.UTF8.GetByteCount(stored).ShouldBeLessThanOrEqualTo(MaxPayloadBytes);
        }
    }

    /// <summary>A document that is too large before stamping is still a producer bug, failed with its column's name.</summary>
    [Fact]
    public async Task Enqueue_Still_Rejects_A_Payload_That_Is_Too_Large_On_Its_Own()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var dataSource = _fixture.OpenWorkerDataSource(database, maximumPoolSize: 2);
        await using (dataSource)
        {
            using var listener = Listen();
            using var source = new ActivitySource(TaskCorrelation.ActivitySourceName);
            using var producer = source.StartActivity("http.request", ActivityKind.Server);
            producer.ShouldNotBeNull("the listener must record the producing span");

            var writer = new TaskQueueWriter(dataSource);

            var failure = await Should.ThrowAsync<ArgumentException>(() => writer.EnqueueAsync(
                new TaskEnqueue
                {
                    Queue = Queue,
                    TaskType = "correlation.noop",
                    PayloadJson = PayloadOf(MaxPayloadBytes + 1),
                    IdempotencyKey = "payload-cap-too-large:" + Guid.CreateVersion7().ToString("N"),
                },
                Token));

            failure.Message.ShouldContain("task_payload_size_check");
        }
    }

    /// <summary>A JSON document of exactly <paramref name="bytes"/> UTF-8 bytes, as one string member.</summary>
    private static string PayloadOf(int bytes)
    {
        const string Prefix = "{\"blob\":\"";
        const string Suffix = "\"}";
        var filler = new string('a', bytes - Prefix.Length - Suffix.Length);
        var payload = Prefix + filler + Suffix;
        Encoding.UTF8.GetByteCount(payload).ShouldBe(bytes);
        return payload;
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskCorrelation.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>The payload of the correlation suite's task (snake_case members, 00 §7).</summary>
internal sealed record CorrelationPayload(Guid OrganizationId, Guid RunId);

/// <summary>What the handler saw of its own span — the correlation the trace is supposed to carry.</summary>
internal sealed record CorrelationSnapshot(
    string TraceId,
    string? ParentSpanId,
    string? JobId,
    string? RunId,
    string? RequestId);

/// <summary>Collects the one snapshot the suite's handler records.</summary>
internal sealed class CorrelationRecorder
{
    /// <summary>The snapshot of the last run, or null when the handler never ran.</summary>
    public CorrelationSnapshot? Seen { get; set; }
}

/// <summary>
/// Records the span the consumer opened around it: its trace, its parent and the three
/// correlation tags. Runs inside the consumer's tenant scope like a production handler.
/// </summary>
internal sealed class CorrelationTaskHandler(CorrelationRecorder recorder)
    : ITaskHandler<CorrelationPayload>
{
    /// <inheritdoc />
    public string TaskType => "identity.organization.provision";

    /// <inheritdoc />
    public ValueTask HandleAsync(
        CorrelationPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;

        recorder.Seen = new CorrelationSnapshot(
            activity?.TraceId.ToHexString() ?? string.Empty,
            activity is null || activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            activity?.GetTagItem(TaskCorrelation.JobIdAttribute) as string,
            activity?.GetTagItem(TaskCorrelation.RunIdAttribute) as string,
            activity?.GetTagItem(TaskCorrelation.RequestIdAttribute) as string);

        return ValueTask.CompletedTask;
    }
}
