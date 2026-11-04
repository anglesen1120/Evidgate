// 10.3.1-T1.2 (AC1) on the path the criterion names: one POST /v1/runs/{r}/events, one trace. The caller's W3C
// traceparent is adopted by the host, the request's span and the ingest handler's span carry request_id/run_id/trace_id,
// so does every exported log record of the request, the outbox payload carries the same trace to the queue, and the
// consumer of the resulting task runs inside that trace with job_id — on the real host and a real PostgreSQL.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 10.3.1-T1.2 / AC1 through the real Api host, a real PostgreSQL and the real queue mechanism: the batch the caller
/// posts is acknowledged inside the trace the caller started, the module span of the ingest handler carries the same
/// correlation, the outbox row's payload carries it into the task row, and the consumer of that row is a child of the
/// same trace with <c>job_id</c> stamped on the claim.
/// </summary>
/// <remarks>
/// The two halves the Api host does not run by itself are driven explicitly with the production classes: the
/// dispatcher (<see cref="OutboxDispatcher.DispatchBatchAsync"/>, the Worker's loop body) and the consumer
/// (<see cref="TaskConsumer.PollOnceAsync"/>). The task's own handler is a probe that replaces 04.8.1's production
/// meter handler, because what this test reads is the queue's correlation, not what the handler bills.
/// <para>
/// The span capture is an <see cref="ActivityListener"/> over this request's trace only, and the log capture a
/// processor on the host's own OpenTelemetry logging pipeline (the one <c>AddEvidgateTelemetry</c> builds), so what
/// the test reads is what a collector would receive, not a re-implementation of it.
/// </para>
/// </remarks>
[Trait("Category", "Docker")]
public sealed class IngestTraceCorrelationTests
{
    /// <summary>The <c>X-Request-Id</c> the caller sends; the trace must carry it to the task (00 §9.1).</summary>
    private const string RequestId = "req-ingest-trace-1";

    /// <summary>The trace the caller started (W3C <c>traceparent</c>'s trace-id).</summary>
    private const string CallerTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    /// <summary>The caller's own span, so the server span can be shown to be its child.</summary>
    private const string CallerSpanId = "00f067aa0ba902b7";

    /// <summary>A sampled W3C <c>traceparent</c>, as a CI adapter sends it.</summary>
    private const string CallerTraceParent = "00-" + CallerTraceId + "-" + CallerSpanId + "-01";

    /// <summary>The probe logger's category, so the capture reads only the lines this suite wrote.</summary>
    internal const string ProbeCategory = "Tenancy.IntegrationTests.IngestTraceProbe";

    private readonly PostgresTenancyFixture _fixture;

    public IngestTraceCorrelationTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Event_Batch_Keeps_One_Trace_From_The_Client_Traceparent_Through_The_Task_Consumer()
    {
        var spans = new ConcurrentQueue<Activity>();
        var logs = new TraceLogCapture();
        var probe = new TraceConsumerProbe();

        using var listener = Listen(spans);

        // The dispatcher claims and the consumer polls the whole queue, so their counts need this test's own database.
        await using var database = await _fixture.CreateDedicatedDatabaseAsync();

        await using var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            database: database,
            services: services => services
                // The queue mechanism, exactly as the Worker composes it; no hosted service is
                // registered by this call, so nothing claims the row before this test does.
                .AddEvidgateTasks(new ConfigurationBuilder().Build())
                .AddTaskHandler<UsageTaskPayload, TraceUsageHandler>()
                .AddSingleton(probe)
                .AddSingleton<IStartupFilter>(new IngestTraceProbeStartupFilter())
                .Configure<OpenTelemetryLoggerOptions>(options => options.AddProcessor(logs))
                // The Api host now registers the production meter handler for this task type (04.8.1), and the registry
                // rejects a second handler for one type: drop the production adapter so the probe is the only one.
                .ReplaceUsageTaskHandlerWithProbe());

        var answer = await IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1))),
            requestId: RequestId,
            mutate: request => request.Headers.TryAddWithoutValidation("traceparent", CallerTraceParent));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);

        // ---- the request: the host adopts the caller's trace, and the edge tags it ------------
        var server = await WaitForAsync(spans, span => span.Kind == ActivityKind.Server);
        server.ShouldNotBeNull("the request must export its server span");
        server!.TraceId.ToHexString().ShouldBe(CallerTraceId, "the client's traceparent is adopted, not replaced");
        server.ParentSpanId.ToHexString().ShouldBe(CallerSpanId);
        Tag(server, CorrelationNames.RequestId).ShouldBe(RequestId);
        Tag(server, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D"), "the route names the run");
        Tag(server, CorrelationNames.TraceId).ShouldBe(CallerTraceId);

        // ---- the ingest handler's own span: same trace, same correlation ----------------------
        var handler = await WaitForAsync(
            spans,
            span => span.Source.Name == TaskCorrelation.ActivitySourceName && span.OperationName == "IngestEventBatch");
        handler.ShouldNotBeNull("the ingest handler starts a module span (01.1.2-T1.2)");
        handler!.TraceId.ToHexString().ShouldBe(CallerTraceId);
        handler.ParentSpanId.ShouldNotBe(default(ActivitySpanId), "the handler span hangs off the request's trace");
        Tag(handler, CorrelationNames.RequestId).ShouldBe(RequestId);
        Tag(handler, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D"));
        Tag(handler, CorrelationNames.TraceId).ShouldBe(CallerTraceId);

        // ---- logs of the request: the ids a call site never passed ---------------------------
        var requestLog = await logs.WaitForAsync(record =>
            record.Category == ProbeCategory
            && Attribute(record, CorrelationNames.RequestId) == RequestId
            && Attribute(record, CorrelationNames.TraceId) == CallerTraceId);
        requestLog.ShouldNotBeNull("a log line written during the request exports the request's correlation");
        Attribute(requestLog!, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D"));

        // ---- the carrier the producer committed, before any dispatch -------------------------
        var outboxId = await world.ScalarAsync<Guid>(
            "select id from public.outbox where task_type = 'usage.batch_accepted'");
        var carrierTraceParent = await CarrierAsync(world, outboxId, TaskCorrelation.TraceParentField);
        var carrierRequestId = await CarrierAsync(world, outboxId, TaskCorrelation.RequestIdField);
        var carrierRunId = await CarrierAsync(world, outboxId, TaskCorrelation.RunIdField);

        carrierTraceParent.ShouldNotBeNull("the outbox payload carries the W3C context of the request");
        carrierTraceParent!.ShouldStartWith("00-" + CallerTraceId + "-", customMessage: "one trace across the outbox hop");
        spans.Any(span => span.Id == carrierTraceParent).ShouldBeTrue(
            "the carrier names a span of this request's trace, not a span of its own");
        carrierRequestId.ShouldBe(RequestId);
        carrierRunId.ShouldBe(world.RunId.ToString("D"));

        // ---- dispatch and consume: the task runs inside the request's trace ------------------
        var dispatched = await world.Host.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(Token);
        dispatched.Enqueued.ShouldBe(1, "the usage row reaches the queue");

        var taskId = await world.ScalarAsync<Guid>(
            $"select id from public.task where idempotency_key = 'outbox:{outboxId:D}'");

        (await world.Host.Services.GetRequiredService<TaskConsumer>().PollOnceAsync("default", Token))
            .ShouldBe(1, "the consumer runs the task it can handle");

        var seen = probe.Seen.ShouldNotBeNull("the task handler must have run");
        seen.TraceId.ShouldBe(CallerTraceId, "the outbox hop must not start a new trace");
        seen.JobId.ShouldBe(taskId.ToString("D"), "job_id is the claimed task row's id");
        seen.RequestId.ShouldBe(RequestId);
        seen.RunId.ShouldBe(world.RunId.ToString("D"));

        var taskSpan = await WaitForAsync(spans, span => span.OperationName == TaskCorrelation.TaskOperationName);
        taskSpan.ShouldNotBeNull("the consumer opens a span per claimed task");
        taskSpan!.TraceId.ToHexString().ShouldBe(CallerTraceId);
        Tag(taskSpan, CorrelationNames.JobId).ShouldBe(taskId.ToString("D"));
        Tag(taskSpan, CorrelationNames.RequestId).ShouldBe(RequestId);
        Tag(taskSpan, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D"));
        Tag(taskSpan, CorrelationNames.TraceId).ShouldBe(CallerTraceId);

        var taskLog = await logs.WaitForAsync(record =>
            record.Category == typeof(TraceUsageHandler).FullName
            && Attribute(record, CorrelationNames.JobId) == taskId.ToString("D"));
        taskLog.ShouldNotBeNull("the handler's log line carries the job it ran");
        Attribute(taskLog!, CorrelationNames.TraceId).ShouldBe(CallerTraceId);
        Attribute(taskLog!, CorrelationNames.RequestId).ShouldBe(RequestId);
        Attribute(taskLog!, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D"));
    }

    /// <summary>One member of the payload's correlation carrier (10.3.1-T1.2), read as an operator would out of the row.</summary>
    private static async Task<string?> CarrierAsync(IngestTestWorld world, Guid outboxId, string field) =>
        await world.ScalarAsync<string>(
            $"select payload -> '{TaskCorrelation.CarrierKey}' ->> '{field}' from public.outbox where id = '{outboxId:D}'");

    /// <summary>One span attribute, or null when the span carries no such tag.</summary>
    private static string? Tag(Activity span, string key) => span.GetTagItem(key) as string;

    /// <summary>One exported log attribute, or null when the record carries no such pair.</summary>
    private static string? Attribute(RecordedLog record, string key) =>
        record.Attributes.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>
    /// Listens to every source so the request's own trace (ASP.NET Core's server span) and the application's spans
    /// both reach the capture; only this request's trace is kept, so spans of the test host's boot and of the
    /// dispatcher's separate pass never enter the assertions.
    /// </summary>
    private static ActivityListener Listen(ConcurrentQueue<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded,
            // A source started from a parent id string (not a parsed context) consults this delegate
            // instead of Sample; without it such a span would not be recorded.
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId.ToHexString() == CallerTraceId)
                {
                    spans.Enqueue(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Waits for the first captured span that matches; the listener reports on stop, so a short poll is enough.</summary>
    private static async Task<Activity?> WaitForAsync(ConcurrentQueue<Activity> spans, Func<Activity, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (spans.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50, Token);
        }

        return null;
    }
}

/// <summary>
/// The probe the Api host runs last: one log line per request, written after the pipeline so the edge has already
/// tagged the request's span. It is the "call site that knows nothing about the ids" AC1 talks about — the exported
/// record only carries them because the host's log pipeline put them there (10.3.1-T1.2).
/// </summary>
internal sealed class IngestTraceProbeStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(async (context, nextMiddleware) =>
        {
            await nextMiddleware().ConfigureAwait(false);

            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(IngestTraceCorrelationTests.ProbeCategory);
            TraceProbeLog.RequestServed(logger);
        });

        next(app);
    };
}

/// <summary>The probe's one source-generated log method (CA1848: no format strings at the call site).</summary>
internal static partial class TraceProbeLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "ingest trace probe: the request was served")]
    internal static partial void RequestServed(ILogger logger);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "usage batch {EventBatchId} accepted {Quantity} event(s)")]
    internal static partial void UsageHandled(ILogger logger, Guid eventBatchId, long quantity);
}

/// <summary>The usage row's payload (04.8.1 §4: <c>event_batch_id</c>, <c>metric</c>, <c>quantity</c>).</summary>
internal sealed record UsageTaskPayload(Guid EventBatchId, string Metric, long Quantity);

/// <summary>What the task handler saw of its own span: the correlation the task's trace is supposed to carry.</summary>
internal sealed record ConsumerSnapshot(
    string TraceId,
    string? ParentSpanId,
    string? JobId,
    string? RunId,
    string? RequestId);

/// <summary>Collects the one snapshot the probe handler records.</summary>
internal sealed class TraceConsumerProbe
{
    /// <summary>The snapshot of the last run, or null when the handler never ran.</summary>
    public ConsumerSnapshot? Seen { get; set; }
}

/// <summary>
/// Stands in for 04.8.1's production meter handler: it records the correlation of the span the consumer opened
/// around it and logs one line, so both the span and the exported record can be checked against the request.
/// </summary>
internal sealed class TraceUsageHandler(TraceConsumerProbe probe, ILogger<TraceUsageHandler> logger)
    : ITaskHandler<UsageTaskPayload>
{
    /// <inheritdoc />
    public string TaskType => "usage.batch_accepted";

    /// <inheritdoc />
    public ValueTask HandleAsync(
        UsageTaskPayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;

        probe.Seen = new ConsumerSnapshot(
            activity?.TraceId.ToHexString() ?? string.Empty,
            activity is null || activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
            activity?.GetTagItem(TaskCorrelation.JobIdAttribute) as string,
            activity?.GetTagItem(TaskCorrelation.RunIdAttribute) as string,
            activity?.GetTagItem(TaskCorrelation.RequestIdAttribute) as string);

        TraceProbeLog.UsageHandled(logger, payload.EventBatchId, payload.Quantity);

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Keeps the probe as the only handler of the usage task type: the Api host now registers 04.8.1's production meter
/// handler, and <see cref="TaskHandlerRegistry"/> fails a duplicate rather than dispatching to an arbitrary one.
/// </summary>
internal static class TraceUsageHandlerRegistration
{
    private const string UsageTaskType = "usage.batch_accepted";

    /// <summary>Replaces the registry with one built from every adapter but the production usage one.</summary>
    public static IServiceCollection ReplaceUsageTaskHandlerWithProbe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<ITaskHandlerRegistry>();
        services.AddSingleton<ITaskHandlerRegistry>(provider => new TaskHandlerRegistry(
            provider.GetServices<ITaskHandlerInvoker>()
                .Where(invoker => invoker.TaskType != UsageTaskType || IsProbeAdapter(invoker))));

        return services;
    }

    /// <summary>The adapter is parameterised by the payload it binds, which is how the probe's own registration is told apart.</summary>
    private static bool IsProbeAdapter(ITaskHandlerInvoker invoker) =>
        invoker.GetType().IsGenericType
        && invoker.GetType().GetGenericArguments()[0] == typeof(UsageTaskPayload);
}

/// <summary>One exported log record, flattened to the category and attribute pairs a collector would receive.</summary>
/// <param name="Category">The <c>ILogger</c> category the record was written under.</param>
/// <param name="Attributes">The record's attributes, including the correlation pairs the host's processor added.</param>
internal sealed record RecordedLog(string Category, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// Captures what the host's own OpenTelemetry logging pipeline exports (10.3.1-T1.2/T1.4): registered after the
/// redaction and correlation processors, so it sees exactly the record a collector would get.
/// </summary>
internal sealed class TraceLogCapture : BaseProcessor<LogRecord>
{
    private readonly ConcurrentQueue<RecordedLog> _records = new();

    /// <summary>Everything captured so far, in order.</summary>
    public IReadOnlyList<RecordedLog> Records => [.. _records];

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (data.Attributes is { } pairs)
        {
            foreach (var pair in pairs)
            {
                attributes[pair.Key] = pair.Value;
            }
        }

        _records.Enqueue(new RecordedLog(data.CategoryName ?? string.Empty, attributes));
    }

    /// <summary>Waits for the first captured record that matches; the provider processes records synchronously, so this is a safety net.</summary>
    public async Task<RecordedLog?> WaitForAsync(Func<RecordedLog, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (_records.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
