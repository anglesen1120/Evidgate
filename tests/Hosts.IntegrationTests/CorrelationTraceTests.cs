using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Evidgate.Api.OperatorIngress;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Evidgate.BuildingBlocks.Observability.Redaction;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.2 (AC1): the correlation a request carries is the correlation the trace
/// carries. One request produces one trace, its server span is tagged with the
/// request id and the trace id, the spans of the pipeline and of Npgsql join that
/// trace, and a client value that is not a request id is replaced instead of exported
/// (00 §9.1, NFR-10-1).
/// </summary>
[Collection(PostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class CorrelationTraceTests
{
    // A deliberately unreachable endpoint: these facts never reach a database, so the
    // string carries no credential at all (the secret scan rejects a literal password).
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    private const string RequestIdHeader = "X-Request-Id";
    private const string RequestIdTag = "evidgate.request_id";
    private const string TraceIdTag = "evidgate.trace_id";
    private const string RunIdTag = "evidgate.run_id";
    private const string JobIdTag = "evidgate.job_id";

    private readonly PostgresFixture _postgres;

    public CorrelationTraceTests(PostgresFixture postgres) => _postgres = postgres;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The HTTP → Mediator leg: the slice's request is one trace, the caller's request id
    /// is on the server span, and the span names its own trace so an operator can pivot
    /// from a log line to the trace (AC1, AC2).
    /// </summary>
    [Fact]
    public async Task Slice_Request_Is_One_Trace_Tagged_With_Its_RequestId()
    {
        var exported = new List<Activity>();
        await using var factory = Tracing(DeadConnectionString, exported);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(RequestIdHeader, "req-correlation-slice");

        using var response = await client.PostAsJsonAsync(
            "/v1/slice/run",
            new { project_ref = "slice/correlation", event_count = 2 },
            Token);
        response.EnsureSuccessStatusCode();
        response.Headers.GetValues(RequestIdHeader).ShouldHaveSingleItem()
            .ShouldBe("req-correlation-slice");

        var server = await WaitForAsync(
            exported,
            a => a.Kind == ActivityKind.Server
                && a.Tags.Any(t => t.Key == RequestIdTag && (string?)t.Value == "req-correlation-slice"));
        server.ShouldNotBeNull("the request's server span carries the request id");
        var span = server!;

        span.GetTagItem(TraceIdTag).ShouldBe(span.TraceId.ToHexString(), "the server span names its trace");
        span.GetTagItem(RunIdTag).ShouldBeNull("a request that names no run carries no run id");
        span.GetTagItem(JobIdTag).ShouldBeNull("a request is not a task");

        exported.Any(a => a.TraceId == span.TraceId && !ReferenceEquals(a, span)).ShouldBeTrue(
            "the pipeline spans of the same request join the server span's trace");
    }

    /// <summary>
    /// The HTTP → Npgsql leg: a request whose query runs on the migrated database keeps
    /// the database command inside its trace instead of starting a second one.
    /// </summary>
    [Fact]
    public async Task Operator_Query_Joins_The_Request_Trace_Through_Npgsql()
    {
        var connectionString = await _postgres.CreateMigratedDatabaseAsync();
        var exported = new List<Activity>();

        await using var factory = new EvidgateApiFactory(
            connectionString,
            configureServices: services =>
            {
                services.AddSingleton<IOperatorGate>(new PermitAllOperatorGate());
                services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing.AddInMemoryExporter(exported));
            });

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(RequestIdHeader, "req-correlation-db");

        using var response = await client.GetAsync(
            "/internal/operator/tasks/dead-letter?queue=correlation", Token);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var server = await WaitForAsync(
            exported,
            a => a.Kind == ActivityKind.Server
                && a.Tags.Any(t => t.Key == RequestIdTag && (string?)t.Value == "req-correlation-db"));
        server.ShouldNotBeNull("the request's server span carries the request id");
        var traceId = server!.TraceId;

        var command = await WaitForAsync(
            exported,
            a => a.TraceId == traceId && a.Source.Name.Equals("Npgsql", StringComparison.Ordinal));
        command.ShouldNotBeNull("the database command must be part of the request's trace");
        command!.ParentSpanId.ShouldNotBe(default(ActivitySpanId), "the command hangs off the request span");
    }

    /// <summary>
    /// A client value that cannot be a request id (too long, or carrying a control
    /// character) is replaced at the edge: the response echoes a safe generated id and
    /// the raw value reaches neither a header nor the exported span (00 §9.1).
    /// </summary>
    [Fact]
    public async Task Oversized_Or_Control_Character_RequestId_Is_Replaced_Not_Exported()
    {
        var exported = new List<Activity>();
        await using var factory = Tracing(DeadConnectionString, exported);
        using var client = factory.CreateClient();

        // Both values are wire-legal header text that is not a request id shape: one is
        // past the 128-character cap, the other carries a character the id grammar
        // excludes. A control character cannot be used here at all — Kestrel rejects CTL
        // header values with 400 before the middleware runs.
        foreach (var hostile in new[] { new string('a', 300), "req with space" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/slice/run")
            {
                Content = JsonContent.Create(new { project_ref = "slice/hostile", event_count = 1 }),
            };
            request.Headers.TryAddWithoutValidation(RequestIdHeader, hostile);

            using var response = await client.SendAsync(request, Token);
            response.EnsureSuccessStatusCode();

            var echoed = response.Headers.GetValues(RequestIdHeader).ShouldHaveSingleItem();
            echoed.ShouldNotBe(hostile, "the hostile value must not be echoed");
            echoed.Length.ShouldBeLessThanOrEqualTo(128);
            echoed.Any(char.IsControl).ShouldBeFalse("the replacement is a header-safe id");

            var server = await WaitForAsync(
                exported,
                a => a.Kind == ActivityKind.Server
                    && a.Tags.Any(t => t.Key == RequestIdTag && (string?)t.Value == echoed));
            server.ShouldNotBeNull($"the replacement id '{echoed}' is what the span carries");
            server!.TraceId.ToHexString().ShouldBe(server.GetTagItem(TraceIdTag));

            exported.Any(
                    a => a.Tags.Any(t => t.Key == RequestIdTag && (string?)t.Value == hostile))
                .ShouldBeFalse("the hostile value never becomes an exported attribute");
        }
    }

    /// <summary>A malformed traceparent starts a fresh trace instead of crashing the request or the pipeline.</summary>
    [Fact]
    public async Task Malformed_Traceparent_Starts_A_Fresh_Trace()
    {
        var exported = new List<Activity>();
        await using var factory = Tracing(DeadConnectionString, exported);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/slice/run")
        {
            Content = JsonContent.Create(new { project_ref = "slice/traceparent", event_count = 1 }),
        };
        request.Headers.TryAddWithoutValidation("traceparent", "00-not-a-trace-id-not-a-parent-01");
        request.Headers.TryAddWithoutValidation("tracestate", "malformed");

        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();

        var server = await WaitForAsync(
            exported,
            a => a.Kind == ActivityKind.Server && a.Tags.Any(t => t.Key == TraceIdTag));
        server.ShouldNotBeNull("the request's server span is exported");
        server!.ParentSpanId.ShouldBe(
            default(ActivitySpanId),
            "an unparsable parent cannot be honoured, so the request starts its own trace");
    }

    private static EvidgateApiFactory Tracing(string connectionString, List<Activity> exported) =>
        new(connectionString, configureServices: services =>
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(exported)));

    /// <summary>
    /// Waits for one exported activity. The in-memory exporter is batch-based, so the
    /// assertion polls the exported list (the convention in <see cref="TelemetryTests"/>).
    /// </summary>
    private static async Task<Activity?> WaitForAsync(List<Activity> exported, Func<Activity, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (exported.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50, Token);
        }

        return null;
    }

    /// <summary>The test double that lets every request through (the 02.x operator gate's stand-in).</summary>
    private sealed class PermitAllOperatorGate : IOperatorGate
    {
        public ValueTask<bool> IsOperatorAsync(HttpContext httpContext) => ValueTask.FromResult(true);
    }
}

/// <summary>
/// 10.3.1-T1.2/T1.4: an exported log record carries the run's correlation and carries no secret.
/// </summary>
/// <remarks>
/// <para>
/// Processor order is registration order, and an export processor buffers the record at its own
/// <c>OnEnd</c> — anything registered after it never reaches the backend. This suite therefore pins
/// the order, not just the presence of the processors: it boots the host through the two real calls
/// (<see cref="ObservabilityExtensions.AddEvidgateTelemetry"/> and then
/// <see cref="EvidgateCorrelationExtensions.AddEvidgateCorrelation"/>) and registers the in-memory
/// exporter in the slot the OTLP export processor occupies, i.e. inside <c>AddEvidgateTelemetry</c>
/// before <c>AddEvidgateCorrelation</c> runs. With the correlation processor registered by the second
/// call instead of the first, the exported record below has no correlation ids.
/// </para>
/// <para>
/// The id/secret assertions are on one and the same exported record, so enrichment-before-export and
/// redaction-before-export are proven together: a pipeline that exported first would satisfy neither.
/// </para>
/// </remarks>
public sealed partial class CorrelationLogPipelineTests
{
    /// <summary>The probe's own activity source: only its spans are started by this suite.</summary>
    private const string ProbeSourceName = "Hosts.IntegrationTests.CorrelationLogSource";

    /// <summary>The probe logger's category; the capture keeps nothing else.</summary>
    private const string ProbeCategory = "Hosts.IntegrationTests.CorrelationLogProbe";

    /// <summary>A request id the edge would accept (printable ASCII, under the cap).</summary>
    private const string RequestId = "req-correlation-log";

    /// <summary>A run id in the canonical <c>D</c> form.</summary>
    private const string RunId = "018f2b7c-9d1e-7a3b-9d5f-3a6c2b1d0e4f";

    /// <summary>A job id (the claimed task row's <c>task.id</c>) in the canonical <c>D</c> form.</summary>
    private const string JobId = "0199a1f4-6c2e-7c1a-9f0e-5b3d2c1e0f4a";

    /// <summary>A password-shaped canary: the same shape the redaction suite uses.</summary>
    private const string Secret = "Sup3r-Secret-P4ss";

    private static readonly ActivitySource ProbeSource = new(ProbeSourceName);

    [Fact]
    public async Task An_Exported_Record_Carries_The_Correlation_And_No_Secret()
    {
        await using var harness = await LogPipelineHarness.StartAsync();
        using var listener = Listen();
        var activity = StartProbeSpan("probe");
        using (activity)
        {
            activity.SetTag(CorrelationNames.RequestId, RequestId);
            activity.SetTag(CorrelationNames.RunId, RunId);

            Log.Probe(harness.Logger, Secret);

            var record = await harness.WaitForRecordAsync();

            record.Attribute(CorrelationNames.RequestId).ShouldBe(RequestId);
            record.Attribute(CorrelationNames.RunId).ShouldBe(RunId);
            record.Attribute(CorrelationNames.TraceId).ShouldBe(activity.TraceId.ToHexString());
            record.Attribute("Password").ShouldBe(SecretRedactor.Mask);
            record.Text.ShouldNotContain(Secret);
        }
    }

    [Fact]
    public async Task A_Forged_Baggage_Value_Never_Becomes_A_Log_Attribute()
    {
        await using var harness = await LogPipelineHarness.StartAsync();
        using var listener = Listen();
        var activity = StartProbeSpan("forged");
        using (activity)
        {
            // Baggage is client-controlled on the request path: a caller can send these values.
            activity.SetBaggage(CorrelationNames.RequestIdBaggage, "not an id");
            activity.SetBaggage(CorrelationNames.RunIdBaggage, "not-a-guid");

            Log.Probe(harness.Logger, Secret);

            var record = await harness.WaitForRecordAsync();

            record.Attribute(CorrelationNames.RequestId).ShouldBeNull();
            record.Attribute(CorrelationNames.RunId).ShouldBeNull();
            record.Attribute(CorrelationNames.TraceId).ShouldBe(activity.TraceId.ToHexString());
        }
    }

    [Fact]
    public void A_Forged_Baggage_Value_Is_Not_Copied_Onto_A_Child_Span()
    {
        using var listener = Listen();
        using (var parent = StartProbeSpan("parent"))
        {
            parent.SetBaggage(CorrelationNames.RequestIdBaggage, "has a space");
            parent.SetBaggage(CorrelationNames.RunIdBaggage, "not-a-guid");
            parent.SetTag(CorrelationNames.JobId, "not-a-guid");

            using var child = StartProbeSpan("child");
            EvidgateCorrelationExtensions.ApplyAmbient(child, parent);

            child.GetTagItem(CorrelationNames.RequestId).ShouldBeNull();
            child.GetTagItem(CorrelationNames.RunId).ShouldBeNull();
            child.GetTagItem(CorrelationNames.JobId).ShouldBeNull("a job id is a task row id, so only a GUID is one");
            child.GetTagItem(CorrelationNames.TraceId).ShouldBe(child.TraceId.ToHexString());
        }
    }

    [Fact]
    public void A_Valid_Baggage_Value_Is_Copied_Onto_A_Child_Span()
    {
        using var listener = Listen();
        using (var parent = StartProbeSpan("parent"))
        {
            parent.SetBaggage(CorrelationNames.RequestIdBaggage, RequestId);
            parent.SetBaggage(CorrelationNames.RunIdBaggage, RunId.ToUpperInvariant());
            parent.SetTag(CorrelationNames.JobId, JobId.ToUpperInvariant());

            using var child = StartProbeSpan("child");
            EvidgateCorrelationExtensions.ApplyAmbient(child, parent);

            child.GetTagItem(CorrelationNames.RequestId).ShouldBe(RequestId);
            child.GetTagItem(CorrelationNames.RunId).ShouldBe(
                RunId,
                "a run id is canonically the D form, whichever casing the baggage used");
            child.GetTagItem(CorrelationNames.JobId).ShouldBe(JobId);
        }
    }

    [Fact]
    public void A_Run_Id_Tagged_As_A_Guid_Is_Copied_As_Its_D_Form()
    {
        using var listener = Listen();
        using (var parent = StartProbeSpan("parent"))
        {
            // Modules tag the run with the run's Guid object, not with its text (CreateRun,
            // PlanExecutions), so reading the tag with `as string` dropped it entirely.
            parent.SetTag(CorrelationNames.RunId, Guid.Parse(RunId));

            using var child = StartProbeSpan("child");
            EvidgateCorrelationExtensions.ApplyAmbient(child, parent);

            child.GetTagItem(CorrelationNames.RunId).ShouldBe(RunId);
        }
    }

    /// <summary>Lets the probe source start real activities, so a span exists to correlate with.</summary>
    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProbeSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static Activity StartProbeSpan(string operation)
    {
        var activity = ProbeSource.StartActivity(operation, ActivityKind.Internal);
        Assert.NotNull(activity);
        return activity;
    }

    /// <summary>The source-generated log call of this suite (CA1848: no format strings at the call site).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "correlation probe {Password}")]
        public static partial void Probe(ILogger logger, string password);
    }

    /// <summary>One exported record, flattened at export time to the attributes and text a collector would see.</summary>
    private sealed record ExportedRecord(IReadOnlyList<KeyValuePair<string, object?>> Attributes, string Text)
    {
        public string? Attribute(string key) =>
            Attributes
                .Where(pair => string.Equals(pair.Key, key, StringComparison.Ordinal))
                .Select(pair => pair.Value?.ToString())
                .FirstOrDefault();
    }

    /// <summary>
    /// The in-memory exporter's sink: it wants an <see cref="ICollection{T}"/>, the export thread adds
    /// and the test thread reads, so the storage is a queue — and each record is flattened in
    /// <see cref="Add"/>, before the SDK resets and reuses it.
    /// </summary>
    private sealed class ExportedRecords(string category) : ICollection<LogRecord>
    {
        private readonly ConcurrentQueue<LogRecord> _raw = new();
        private readonly ConcurrentQueue<ExportedRecord> _flattened = new();

        public ExportedRecord[] Snapshot() => _flattened.ToArray();

        public int Count => _raw.Count;

        public bool IsReadOnly => false;

        public void Add(LogRecord item)
        {
            // The host's lifecycle logs flow through the same pipeline; the capture keeps the probe only.
            if (!string.Equals(item.CategoryName, category, StringComparison.Ordinal))
            {
                return;
            }

            _raw.Enqueue(item);
            _flattened.Enqueue(Flatten(item));
        }

        public void Clear()
        {
            while (_raw.TryDequeue(out _))
            {
            }

            while (_flattened.TryDequeue(out _))
            {
            }
        }

        public bool Contains(LogRecord item) => _raw.Contains(item);

        public void CopyTo(LogRecord[] array, int arrayIndex) =>
            _raw.ToArray().CopyTo(array, arrayIndex);

        public bool Remove(LogRecord item) => false;

        public IEnumerator<LogRecord> GetEnumerator() => _raw.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Copies the fields a collector would see. This runs inside <see cref="Add"/>, before the SDK
        /// resets and reuses the record.
        /// </summary>
        private static ExportedRecord Flatten(LogRecord item)
        {
            var attributes = item.Attributes?.ToArray() ?? [];
            var parts = new List<string>();
            if (item.FormattedMessage is { } formatted)
            {
                parts.Add(formatted);
            }

            if (item.Body is not null)
            {
                parts.Add(item.Body.ToString() ?? string.Empty);
            }

            parts.AddRange(attributes.Select(pair => $"{pair.Key}={pair.Value}"));
            if (item.Exception is { } exception)
            {
                parts.Add(exception.ToString());
            }

            return new ExportedRecord(attributes, string.Join(" | ", parts));
        }
    }

    /// <summary>The started host and the exporter its logging pipeline feeds.</summary>
    private sealed class LogPipelineHarness(IHost host, ExportedRecords exported) : IAsyncDisposable
    {
        public ILogger Logger => host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ProbeCategory);

        public static async Task<LogPipelineHarness> StartAsync()
        {
            var builder = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = "Staging" });
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["EVIDGATE:Telemetry:SampleRatio"] = "1",
                });

            var otel = builder.AddEvidgateTelemetry("evidgate-test");

            var exported = new ExportedRecords(ProbeCategory);
            builder.Services.Configure<OpenTelemetryLoggerOptions>(
                options => options.AddInMemoryExporter(exported));

            otel.AddEvidgateCorrelation();

            var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);

            return new LogPipelineHarness(host, exported);
        }

        /// <summary>The export is asynchronous; poll until the probe's record lands.</summary>
        public async Task<ExportedRecord> WaitForRecordAsync()
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (exported.Snapshot() is { Length: > 0 } records)
                {
                    return records[0];
                }

                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            var late = exported.Snapshot();
            late.ShouldNotBeEmpty("no probe record reached the export collection within 20 s");
            return late[0];
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
        }
    }
}
