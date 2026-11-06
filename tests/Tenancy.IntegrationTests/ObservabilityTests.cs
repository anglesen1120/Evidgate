// AC7 of the ingest rate-limit slice over the real host and a real PostgreSQL: every refusal bumps the one
// ingest rejection counter with a closed-vocabulary reason, a refused request's span carries run_id and
// request_id, the accepted batch's span carries run_id, producer_id, request_id and event_count, and neither
// the counter labels nor the span tags carry the token.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// <c>ObservabilityTests.rejection_counter_and_span_attrs</c>: the counter and the batch span of the events
/// route, read through the application's own meter and activity source.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class ObservabilityTests
{
    private const string RequestId = "req-ingest-rate-1";

    private readonly PostgresTenancyFixture _fixture;

    public ObservabilityTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task rejection_counter_and_span_attrs()
    {
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);
        using var spans = IngestTelemetryCapture.ModuleSpans(out var captured);

        await using var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            planned: 5,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:RateLimit:PermitLimit"] = "1",
                ["EVIDGATE:Ingestion:RateLimit:WindowSeconds"] = "3600",
            });

        var accepted = await PostAsync(world, sequence: 1, RequestId);
        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);

        // The accepted batch's span carries the four attributes AC7 names, and nothing of the credential.
        var batch = await WaitForSpanAsync(captured, span => span.OperationName == "IngestEventBatch");
        batch.ShouldNotBeNull("the handler starts one module span per batch");
        Tag(batch!, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D", CultureInfo.InvariantCulture));
        Tag(batch!, EvidgateTelemetry.ProducerIdTag).ShouldBe(IngestTestWorld.ProducerId);
        Tag(batch!, CorrelationNames.RequestId).ShouldBe(RequestId);
        Tag(batch!, EvidgateTelemetry.EventCountTag).ShouldBe("1");
        batch!.TagObjects.ShouldAllBe(tag => !Equals(tag.Value, IngestTestWorld.Jti), "the token id never reaches a span");

        // An accepted batch is not a rejection: the counter stays untouched.
        reasons.ShouldBeEmpty();

        var refused = await PostAsync(world, sequence: 2, "req-ingest-rate-2");
        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("rate_limited");
        reasons.ShouldContain("rate_limited");
        reasons.Count(reason => string.Equals(reason, "rate_limited", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task handler_refusal_counts_once_under_the_mapped_reason()
    {
        using var counter = IngestTelemetryCapture.RejectedBatches(out var reasons);

        // A budget large enough that neither refusal below can be the rate limiter's.
        await using var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            planned: 2,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:RateLimit:PermitLimit"] = "5",
                ["EVIDGATE:Ingestion:RateLimit:WindowSeconds"] = "3600",
            });

        var invalid = await IngestQaRequests.PostAsync(
            world.Client, world.RunId, new StringContent("{}", Encoding.UTF8, "application/json"), "req-ingest-invalid-1");
        invalid.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, invalid.ErrorMessage);
        invalid.ErrorCode.ShouldBe("event_invalid");

        var body = IngestQaRequests.Batch(IngestQaRequests.Event(
            Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1));
        var absent = await IngestQaRequests.PostEventsAsync(world.Client, Guid.CreateVersion7(), body, "req-ingest-absent-1");
        absent.Status.ShouldBe(HttpStatusCode.NotFound, absent.ErrorMessage);
        absent.ErrorCode.ShouldBe("not_found");

        reasons.Count(reason => string.Equals(reason, "event_invalid", StringComparison.Ordinal)).ShouldBe(1);
        reasons.Count(reason => string.Equals(reason, "not_found", StringComparison.Ordinal)).ShouldBe(1);
        reasons.Count.ShouldBe(2, "one measurement per rejection, counted once");
    }

    [Fact]
    public async Task rate_refusal_span_carries_run_and_request_ids()
    {
        using var spans = IngestTelemetryCapture.AllSpans(out var captured);

        await using var world = await IngestTestWorld.StartAsync(
            _fixture,
            IngestTestWorld.Writer(),
            planned: 2,
            configuration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:RateLimit:PermitLimit"] = "1",
                ["EVIDGATE:Ingestion:RateLimit:WindowSeconds"] = "3600",
            });

        (await PostAsync(world, sequence: 1, "req-ingest-span-accepted")).Status.ShouldBe(HttpStatusCode.OK);
        const string refusedRequest = "req-ingest-span-refused";

        var refused = await PostAsync(world, sequence: 2, refusedRequest);

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        // The refusal never reaches the handler, so the request's own span is the only place the ids can be.
        var requestSpan = await WaitForSpanAsync(captured, span => span.Kind == ActivityKind.Server
            && string.Equals(Tag(span, CorrelationNames.RequestId), refusedRequest, StringComparison.Ordinal));
        requestSpan.ShouldNotBeNull("a rate-refused request still exports its server span with the request id");
        Tag(requestSpan!, CorrelationNames.RunId).ShouldBe(
            world.RunId.ToString("D", CultureInfo.InvariantCulture), "the refused request names its run");
    }

    [Fact]
    public async Task quota_refusal_counts_once_and_its_batch_span_is_correlated()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 1);
        await world.SetRunStateAsync("running");
        await using var counters = await UsageQuotaWorld.StartAsync(_fixture);
        await SeedFullQuotaAsync(world, counters);
        using var denied = IngestTelemetryCapture.QuotaDeniedBatches(out var kinds);
        using var rejected = IngestTelemetryCapture.RejectedBatches(out var reasons);
        using var spans = IngestTelemetryCapture.ModuleSpans(out var captured);
        const string refusedRequest = "req-ingest-quota-1";

        var refused = await PostAsync(world, sequence: 1, refusedRequest);

        refused.Status.ShouldBe(HttpStatusCode.TooManyRequests, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("quota_exceeded");
        kinds.ShouldHaveSingleItem().ShouldBe("quota_exceeded");
        reasons.ShouldHaveSingleItem().ShouldBe("quota_exceeded");

        // The request's own span and the handler's module span both name this batch, so the correlation is read
        // from the module span (the one that also carries the producer and the event count); the token is on neither.
        var batch = await WaitForSpanAsync(captured, span => span.OperationName == "IngestEventBatch"
            && span.GetTagItem(EvidgateTelemetry.ModuleTag) is not null
            && Equals(Tag(span, CorrelationNames.RequestId), refusedRequest));
        batch.ShouldNotBeNull("a batch the quota blocked still exports its module span");
        Tag(batch!, CorrelationNames.RunId).ShouldBe(world.RunId.ToString("D", CultureInfo.InvariantCulture));
        Tag(batch!, EvidgateTelemetry.ProducerIdTag).ShouldBe(IngestTestWorld.ProducerId);
        Tag(batch!, EvidgateTelemetry.EventCountTag).ShouldBe("1");
        batch!.TagObjects.ShouldAllBe(tag => !Equals(tag.Value, IngestTestWorld.Jti));

        // The block is reported once and commits nothing, quota and events alike.
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(0L);
    }

    /// <summary>A counter with no room at all, so the route's very first batch is the one the gate blocks.</summary>
    private static Task<Guid> SeedFullQuotaAsync(IngestTestWorld world, UsageQuotaWorld counters) =>
        counters.SeedCounterAsync(new CounterSeed(
            Guid.CreateVersion7(),
            world.Project,
            world.Host.Services.GetRequiredService<TimeProvider>().GetUtcNow()
                .ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "results",
            10m,
            10m,
            0.8m,
            Org: world.Org));

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, long sequence, string requestId) =>
        IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence))),
            requestId);

    /// <summary>The tag value of <paramref name="span"/> under <paramref name="key"/>, as the exporter would read it.</summary>
    private static string? Tag(Activity span, string key) => span.GetTagItem(key)?.ToString();

    /// <summary>Waits for the stopped span of the given operation; the listener reports on stop, so a short poll is enough.</summary>
    private static Task<Activity?> WaitForSpanAsync(ConcurrentQueue<Activity> spans, string operation) =>
        WaitForSpanAsync(spans, span => span.OperationName == operation);

    /// <summary>Waits for the first stopped span that matches; the listener reports on stop, so a short poll is enough.</summary>
    private static async Task<Activity?> WaitForSpanAsync(ConcurrentQueue<Activity> spans, Func<Activity, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (spans.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
