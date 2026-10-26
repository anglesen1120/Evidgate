using System.Diagnostics;
using System.Text.Json;
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

    /// <summary>A malformed traceparent inside a carrier stays text but never becomes a parent (W3C parse decides).</summary>
    [Fact]
    public void Read_Keeps_A_Malformed_TraceParent_But_It_Is_Not_A_Parent()
    {
        var carrier = TaskCorrelation.Read(
            """{"_evidgate_correlation":{"traceparent":"00-nope","run_id":"r"}}""");

        carrier.TraceParent.ShouldBe("00-nope");
        carrier.RunId.ShouldBe("r");
        ActivityContext.TryParse(carrier.TraceParent, carrier.TraceState, out _).ShouldBeFalse();
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
        var dataSource = _fixture.OpenAppDataSource(database, maximumPoolSize: 2);
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
