// AC7 of the ingest rate-limit slice over the real host and a real PostgreSQL: every refusal bumps the one
// ingest rejection counter with a closed-vocabulary reason, the accepted batch's span carries run_id,
// producer_id, request_id and event_count, and neither the counter labels nor the span tags carry the token.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Observability.Correlation;
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
        var batch = await WaitForSpanAsync(captured, "IngestEventBatch");
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

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, long sequence, string requestId) =>
        IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence))),
            requestId);

    /// <summary>The tag value of <paramref name="span"/> under <paramref name="key"/>, as the exporter would read it.</summary>
    private static string? Tag(Activity span, string key) =>
        span.TagObjects.FirstOrDefault(tag => tag.Key == key).Value?.ToString();

    /// <summary>Waits for the stopped span of the given operation; the listener reports on stop, so a short poll is enough.</summary>
    private static async Task<Activity?> WaitForSpanAsync(ConcurrentQueue<Activity> spans, string operation)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (spans.FirstOrDefault(span => span.OperationName == operation) is { } found)
            {
                return found;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
