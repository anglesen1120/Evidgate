// 04.1.1-T3 independent QA pass (negative, boundary and exploratory cases over the events route limits).
// Complements IngestLimitsTests: asserts the closed reason set of the rejection counter per refusal kind,
// that every refusal writes nothing, and that a gzip body that is not a single member is refused.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class IngestLimitsQaTests
{
    private readonly PostgresTenancyFixture _fixture;

    public IngestLimitsQaTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task qa_501_events_refused_413_with_no_write_and_event_count_reason()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        using var listener = RejectionListener(out var reasons);

        var answer = await PostAsync(world, IngestQaRequests.Batch(Events(world, 501)));

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("batch_too_large");
        (await world.CountAsync("event_batch")).ShouldBe(0);
        (await world.CountAsync("event")).ShouldBe(0);
        reasons.ToArray().ShouldBe(new[] { "event_count" });
    }

    [Fact]
    public async Task qa_exactly_500_events_is_accepted_and_emits_no_rejection_metric()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        using var listener = RejectionListener(out var reasons);

        var answer = await PostAsync(world, IngestQaRequests.Batch(Events(world, 500)));

        answer.Status.ShouldBe(HttpStatusCode.OK);
        reasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task qa_exactly_1_MiB_raw_body_is_accepted()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        using var listener = RejectionListener(out var reasons);
        var padded = IngestQaRequests.PaddedBody(IngestQaRequests.Batch(Events(world, 2)), 1_048_576);

        var answer = await PostAsync(world, padded);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await world.CountAsync("event_batch")).ShouldBe(1);
        reasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task qa_raw_body_one_byte_over_1_MiB_refused_413_with_body_too_large_reason()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        using var listener = RejectionListener(out var reasons);
        var padded = IngestQaRequests.PaddedBody(IngestQaRequests.Batch(Events(world, 2)), 1_048_577);

        var answer = await PostAsync(world, padded);

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        answer.ErrorCode.ShouldBe("batch_too_large");
        (await world.CountAsync("event_batch")).ShouldBe(0);
        reasons.ToArray().ShouldBe(new[] { "body_too_large" });
    }

    [Fact]
    public async Task qa_malformed_gzip_refused_422_with_gzip_invalid_reason_and_no_write()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        using var listener = RejectionListener(out var reasons);

        var answer = await PostGzipAsync(world, [0x1f, 0x8b, 0x08, 0x00, 0x01, 0x02]);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await world.CountAsync("event_batch")).ShouldBe(0);
        reasons.ToArray().ShouldBe(new[] { "gzip_invalid" });
    }

    [Fact]
    public async Task qa_stacked_content_encoding_refused_422_with_encoding_unsupported_reason()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        using var listener = RejectionListener(out var reasons);
        var compressed = GzipOf(Encoding.UTF8.GetBytes(IngestQaRequests.Json(IngestQaRequests.Batch(Events(world, 1)))));

        var answer = await PostGzipAsync(world, compressed, encoding: "gzip, identity");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("request_invalid");
        (await world.CountAsync("event_batch")).ShouldBe(0);
        reasons.ToArray().ShouldBe(new[] { "encoding_unsupported" });
    }

    [Fact]
    public async Task qa_every_refusal_writes_no_event_rows_across_kinds()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);

        (await PostAsync(world, IngestQaRequests.Batch(Events(world, 501)))).Status
            .ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await PostGzipAsync(world, [0x1f, 0x8b, 0x00])).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await PostGzipAsync(world, GzipOf(Encoding.UTF8.GetBytes("{}")), encoding: "br")).Status
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await world.CountAsync("event")).ShouldBe(0);
        (await world.CountAsync("event_batch")).ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private static object[] Events(IngestTestWorld world, int count)
    {
        var events = new object[count];
        for (var i = 0; i < count; i++)
        {
            events[i] = IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, i % 5),
                sequence: i + 1,
                attemptNo: (i % 100) + 1,
                type: i % 5 == 0 ? "test.finished" : "test.step",
                status: i % 5 == 0 ? "passed" : null);
        }

        return events;
    }

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, object body) =>
        IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, string rawBody) =>
        IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            new StringContent(rawBody, Encoding.UTF8, "application/json"));

    private static Task<ApiAnswer> PostGzipAsync(
        IngestTestWorld world,
        byte[] compressed,
        string encoding = "gzip")
    {
        var content = new ByteArrayContent(compressed);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);

        return IngestQaRequests.PostAsync(world.Client, world.RunId, content);
    }

    private static byte[] GzipOf(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return output.ToArray();
    }

    private static MeterListener RejectionListener(out ConcurrentBag<string> reasons)
    {
        var bag = new ConcurrentBag<string>();
        reasons = bag;
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == EvidgateTelemetry.MeterName
                    && instrument.Name == EvidgateTelemetry.IngestBatchRejectedMetricName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason)
                {
                    bag.Add(reason);
                }
            }
        });
        listener.Start();

        return listener;
    }
}
