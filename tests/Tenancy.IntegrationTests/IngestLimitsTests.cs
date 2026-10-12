// 04.1.1-T3 (AC7, DECISIONS §8.1 D-A8): the transport and decode caps of POST /v1/runs/{r}/events over the
// real host and a real PostgreSQL — the 500-event and 1 MiB boundaries, the gzip decode caps and the bomb,
// the wire answer of an unsupported or malformed encoding, and the one counter + one log line per refusal.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Ingestion.Application;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The 04.1.1-T3 limit suite: every refusal of the events route is 413 <c>batch_too_large</c> with no write,
/// every decode refusal of a gzip body is 422 <c>request_invalid</c>, and each one bumps the single
/// <c>evidgate_ingest_batch_rejected_total</c> counter once with a closed-set reason.
/// </summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class IngestLimitsTests
{
    private readonly PostgresTenancyFixture _fixture;

    public IngestLimitsTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ---------------------------------------------------------------- §9 row: limit_boundaries

    [Fact]
    public async Task limit_boundaries()
    {
        // Five planned executions: Events() spans executions 0..4, and a key outside the plan is 422.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);

        // 500 events: accepted (AC7's lower boundary).
        var fiveHundred = await PostAsync(world, IngestQaRequests.Batch(Events(world, 500)));

        fiveHundred.Status.ShouldBe(HttpStatusCode.OK, fiveHundred.ErrorMessage);
        fiveHundred.Body.GetProperty("accepted").GetInt32().ShouldBe(500);
        (await EventRowsAsync(world)).ShouldBe(500);

        // 501 events: 413 with zero new rows and no second batch row.
        var fiveOhOne = await PostAsync(world, IngestQaRequests.Batch(Events(world, 501)));

        fiveOhOne.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        fiveOhOne.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(500);
        (await world.CountAsync("event_batch")).ShouldBe(1);

        // Exactly 1 MiB of as-sent body: accepted; 1 MiB + 1 byte: 413 before any write.
        var oneMiB = await PostAsync(world, IngestQaRequests.PaddedBody(Batch(world, sequence: 501), 1_048_576));

        oneMiB.Status.ShouldBe(HttpStatusCode.OK, oneMiB.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(501);

        var overMiB = await PostAsync(world, IngestQaRequests.PaddedBody(Batch(world, sequence: 502), 1_048_577));

        overMiB.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overMiB.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(501);

        // The cap applies to the bytes as sent, so a gzip body over 1 MiB compressed is 413 even though it
        // would decode to a small batch.
        var fatGzip = await PostGzipAsync(world, GzipOf(Incompressible(1_048_577)));

        fatGzip.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        fatGzip.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(501);
    }

    // ---------------------------------------------------------------- §9 row: gzip_bomb_rejected

    [Fact]
    public async Task gzip_bomb_rejected()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        using var listener = RejectionListener(out var reasons);

        // 50 MiB of zeros from a compressed body of ~200 KiB: the ratio cap is the first guard under the
        // 20:1 default, and the decode aborts there — no write, no inflated body.
        var bomb = GzipOf(new byte[50 * 1024 * 1024]);

        var answer = await PostGzipAsync(world, bomb, requestId: "req-bomb-1");

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch")).ShouldBe(0);
        reasons.ShouldContain(IngestBatchRejection.RatioExceeded);
    }

    // ---------------------------------------------------------------- a valid gzip batch

    [Fact]
    public async Task gzip_batch_is_accepted_with_real_byte_counts()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var body = IngestQaRequests.Json(IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1),
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2)));
        var compressed = GzipOf(Encoding.UTF8.GetBytes(body));

        var answer = await PostGzipAsync(world, compressed);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        (await EventRowsAsync(world)).ShouldBe(2);

        // bytes_in is the compressed size, bytes_expanded the decoded size (04.1.1 §7).
        var bytesIn = await world.ScalarAsync<long>("select bytes_in from public.event_batch");
        var expanded = await world.ScalarAsync<long>("select bytes_expanded from public.event_batch");

        bytesIn.ShouldBe(compressed.Length);
        expanded.ShouldBe(Encoding.UTF8.GetByteCount(body));
        bytesIn.ShouldBeLessThan(expanded);
    }

    // ---------------------------------------------------------------- the wire answers of the decode path

    [Fact]
    public async Task unsupported_encoding_is_422_request_invalid()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // `deflate` is not gzip: the route keeps the existing 422 request_invalid answer and its detail.
        var answer = await PostGzipAsync(
            world,
            GzipOf("{}"u8.ToArray()),
            encoding: "deflate");

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        IngestQaRequests.Details(answer).ShouldContain(detail => detail.GetProperty("path").GetString() == "Content-Encoding");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task malformed_gzip_is_422_invalid_gzip()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var member = GzipOf(Encoding.UTF8.GetBytes(IngestQaRequests.Json(Batch(world, sequence: 1))));
        var truncated = member.AsSpan(0, member.Length - 4).ToArray();

        var answer = await PostGzipAsync(world, truncated);

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        IngestQaRequests.Details(answer).ShouldContain(detail =>
            detail.GetProperty("path").GetString() == "Content-Encoding"
            && detail.GetProperty("reason").GetString() == "invalid_gzip");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ---------------------------------------------------------------- one counter + one log line per refusal

    [Fact]
    public async Task rejection_emits_one_counter_and_one_log_line_with_the_request_id()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);
        var sink = new IngestLogSink();
        await using var host = world.Host.WithWebHostBuilder(
            builder => builder.ConfigureTestServices(services => services.AddLogging(logging => logging.AddProvider(sink))));
        using var client = host.CreateClient();
        using var listener = RejectionListener(out var reasons);

        // 501 events: the event-count cap, refused by the parser inside the handler.
        var answer = await IngestQaRequests.PostAsync(
            client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(Events(world, 501))),
            requestId: "req-count-1");

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        reasons.ShouldHaveSingleItem().ShouldBe(IngestBatchRejection.EventCount);

        var rejection = sink.Lines.Where(line => line.Contains("event batch rejected", StringComparison.Ordinal)).ToList();
        rejection.ShouldHaveSingleItem();
        rejection[0].ShouldContain("req-count-1");
        rejection[0].ShouldContain(IngestBatchRejection.EventCount);
        rejection[0].ShouldNotContain("\"events\"");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<long> EventRowsAsync(IngestTestWorld world) => world.CountAsync("event");

    /// <summary>One valid batch whose body is padded with trailing whitespace to exactly <paramref name="bytes"/>.</summary>
    private static string Batch(IngestTestWorld world, long sequence) =>
        IngestQaRequests.Json(IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence)));

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

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, object body, string? requestId = null) =>
        IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body, requestId);

    private static Task<ApiAnswer> PostAsync(IngestTestWorld world, string rawBody, string? requestId = null) =>
        IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            new StringContent(rawBody, Encoding.UTF8, "application/json"),
            requestId);

    /// <summary>One <c>Content-Encoding: gzip</c> request of the given compressed body.</summary>
    private static Task<ApiAnswer> PostGzipAsync(
        IngestTestWorld world,
        byte[] compressed,
        string? requestId = null,
        string encoding = "gzip") =>
        IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            GzipContent(compressed, encoding),
            requestId);

    private static ByteArrayContent GzipContent(byte[] compressed, string encoding)
    {
        var content = new ByteArrayContent(compressed);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);

        return content;
    }

    /// <summary>One well-formed gzip member of <paramref name="payload"/>.</summary>
    private static byte[] GzipOf(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return output.ToArray();
    }

    /// <summary>Deterministic incompressible bytes, so a body of <paramref name="length"/> stays that size when gzipped.</summary>
    private static byte[] Incompressible(int length)
    {
        var bytes = new byte[length];
        var state = 0x2545F491u;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = (byte)(state >> 24);
        }

        return bytes;
    }

    /// <summary>The <c>reason</c> tags of every <c>evidgate_ingest_batch_rejected_total</c> measurement.</summary>
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
