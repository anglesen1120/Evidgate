using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Evidgate.Api.OperatorIngress;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
