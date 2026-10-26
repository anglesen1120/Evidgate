using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace Hosts.IntegrationTests;

/// <summary>
/// 10.3.1-T1.2 (F4, F6) at the edge: the two values a caller can still put into the
/// trace — a <c>baggage</c> header and a <c>tracestate</c> — are bounded before anything
/// is tagged, logged, exported or carried into an outbox payload. The W3C
/// <c>traceparent</c> stays honoured (a client trace is worth keeping whole; the head
/// sampler decides the rest), so the tests pin both halves: what is accepted and what is
/// dropped.
/// </summary>
public sealed class CorrelationHeaderTests
{
    // A deliberately unreachable endpoint: these facts never reach a database, so the
    // string carries no credential at all (the secret scan rejects a literal password).
    private const string DeadConnectionString =
        "Host=127.0.0.1;Port=1;Username=evidgate_app;Database=evidgate;Timeout=2";

    private const string RequestIdHeader = "X-Request-Id";
    private const string RequestIdTag = "evidgate.request_id";
    private const string RequestIdBaggage = "evidgate.request_id";
    private const string RunIdBaggage = "evidgate.run_id";
    private const string RunIdTag = "evidgate.run_id";
    private const string CallerTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string CallerTraceParent = "00-" + CallerTraceId + "-00f067aa0ba902b7-01";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// F4: a caller's <c>baggage</c> is not a source of the correlation ids. A planted
    /// <c>evidgate.run_id</c> is stripped at the edge (the route names no run, so nothing
    /// re-adds one) and a planted <c>evidgate.request_id</c> is replaced by the id the
    /// middleware computed, which is what the host echoes and tags.
    /// </summary>
    [Fact]
    public async Task Forged_Correlation_Baggage_Is_Stripped_At_The_Edge()
    {
        var exported = new List<Activity>();
        await using var factory = Tracing(exported);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/slice/run")
        {
            Content = JsonContent.Create(new { project_ref = "slice/baggage", event_count = 1 }),
        };
        request.Headers.TryAddWithoutValidation(RequestIdHeader, "req-baggage-edge");
        // A neutral key the edge does not own travels beside them: it proves the propagator
        // really did populate Activity.Baggage, so the two assertions below cannot pass
        // merely because inbound baggage extraction stopped working.
        request.Headers.TryAddWithoutValidation(
            "baggage",
            $"vendor=keep,{RunIdBaggage}={Guid.CreateVersion7():D},{RequestIdBaggage}=req-forged-by-client");

        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        response.Headers.GetValues(RequestIdHeader).ShouldHaveSingleItem().ShouldBe("req-baggage-edge");

        var server = await WaitForServerSpan(exported, "req-baggage-edge");
        server.GetBaggageItem("vendor").ShouldBe(
            "keep",
            "the propagator populated the request's baggage, so this test is not vacuous");
        server.GetBaggageItem(RunIdBaggage).ShouldBeNull(
            "a route that names no run must not inherit a client's run id");
        server.GetTagItem(RunIdTag).ShouldBeNull("nor may one reach the span's attributes");
        server.GetBaggageItem(RequestIdBaggage).ShouldBe(
            "req-baggage-edge",
            "the edge's own request id replaces whatever the caller planted");
    }

    /// <summary>
    /// F6: an over-long (client-controlled) <c>tracestate</c> is dropped at the edge, so it
    /// reaches neither a span nor a payload; the <c>traceparent</c> beside it is still
    /// adopted, which is the documented trade-off this task keeps.
    /// </summary>
    [Fact]
    public async Task Oversized_Tracestate_Is_Dropped_While_The_Traceparent_Is_Honoured()
    {
        var exported = new List<Activity>();
        await using var factory = Tracing(exported);
        using var client = factory.CreateClient();

        // Two valid W3C entries, each value short enough for the runtime's own extractor
        // (which drops a tracestate holding any value over 256 characters), but a total
        // longer than the carrier's bound. This is the shape that reaches the edge intact
        // as a whole and therefore has to be bounded here.
        var oversized = "vendor1=" + new string('a', 140) + ",vendor2=" + new string('b', 140);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/slice/run")
        {
            Content = JsonContent.Create(new { project_ref = "slice/tracestate", event_count = 1 }),
        };
        request.Headers.TryAddWithoutValidation("traceparent", CallerTraceParent);
        request.Headers.TryAddWithoutValidation("tracestate", oversized);

        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        var echoed = response.Headers.GetValues(RequestIdHeader).ShouldHaveSingleItem();

        var server = await WaitForServerSpan(exported, echoed);
        server.TraceStateString.ShouldBeNull("an unbounded client tracestate is dropped, never exported");
        server.TraceId.ToHexString().ShouldBe(
            CallerTraceId,
            "the traceparent is still adopted: a client trace stays one trace");
    }

    /// <summary>
    /// The other half of F6: bounding must not throw away a normal <c>tracestate</c> — a
    /// caller's vendor state survives when it is within the bound.
    /// </summary>
    [Fact]
    public async Task Bounded_Tracestate_Survives_The_Edge()
    {
        var exported = new List<Activity>();
        await using var factory = Tracing(exported);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/slice/run")
        {
            Content = JsonContent.Create(new { project_ref = "slice/tracestate-ok", event_count = 1 }),
        };
        request.Headers.TryAddWithoutValidation("traceparent", CallerTraceParent);
        request.Headers.TryAddWithoutValidation("tracestate", "vendor1=value1,vendor2=value2");

        using var response = await client.SendAsync(request, Token);
        response.EnsureSuccessStatusCode();
        var echoed = response.Headers.GetValues(RequestIdHeader).ShouldHaveSingleItem();

        var server = await WaitForServerSpan(exported, echoed);
        server.TraceStateString.ShouldBe("vendor1=value1,vendor2=value2");
    }

    private static EvidgateApiFactory Tracing(List<Activity> exported) =>
        new(DeadConnectionString, configureServices: services =>
            services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddInMemoryExporter(exported)));

    /// <summary>
    /// The exported server span of <paramref name="requestId"/>'s request. The in-memory
    /// exporter is batch-based, so it polls. The span is matched on the correlation tag the
    /// middleware writes (<c>RequestIdMiddleware</c> sets it, the response echoes the same
    /// id), never on its kind alone: matching on kind alone returned whichever server span a
    /// host exported first, which is what made these facts flaky while another instrumented
    /// host was live in the same process — the borrowed span had a foreign trace id and
    /// neither the caller's <c>tracestate</c> nor its baggage.
    /// </summary>
    private static async Task<Activity> WaitForServerSpan(List<Activity> exported, string requestId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (exported.FirstOrDefault(activity =>
                    activity.Kind == ActivityKind.Server
                    && string.Equals(
                        (string?)activity.GetTagItem(RequestIdTag),
                        requestId,
                        StringComparison.Ordinal)) is { } found)
            {
                return found;
            }

            await Task.Delay(50, Token);
        }

        throw new InvalidOperationException(
            $"the request '{requestId}' produced no exported server span");
    }
}
