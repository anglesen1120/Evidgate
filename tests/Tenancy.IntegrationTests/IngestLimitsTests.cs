// 04.1.1-T3 (AC7, DECISIONS §8.1 D-A8): the transport and decode caps of POST /v1/runs/{r}/events over the
// real host and a real PostgreSQL — the 500-event and 1 MiB boundaries, the gzip decode caps and the bomb,
// the wire answer of an unsupported or malformed encoding, and the one counter + one log line per refusal.
#pragma warning disable CA1861
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using Evidgate.BuildingBlocks.Observability;
using Evidgate.Modules.Ingestion.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
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

        // Exactly 1 MiB of as-sent body: accepted; 1 MiB + 1 byte: 413 before any write. The body is a
        // one-event batch object padded with trailing whitespace (PaddedBody serializes the object itself),
        // and its sequence does not collide with the 500 stored above (AC6).
        var oneMiB = await PostAsync(world, IngestQaRequests.PaddedBody(OneEvent(world, sequence: 501), 1_048_576));

        oneMiB.Status.ShouldBe(HttpStatusCode.OK, oneMiB.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(501);

        var overMiB = await PostAsync(world, IngestQaRequests.PaddedBody(OneEvent(world, sequence: 502), 1_048_577));

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

        // 50 MiB of zeros compresses to about 50 KiB (deflate caps near 1032:1), so the ratio cap is the first
        // guard under the 20:1 default and the decode aborts at about 1 MiB expanded (20 × 50 KiB) — no write,
        // no inflated body.
        var bomb = GzipOf(new byte[50 * 1024 * 1024]);

        var answer = await PostGzipAsync(world, bomb, requestId: "req-bomb-1");

        answer.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch")).ShouldBe(0);
        reasons.ShouldContain(IngestBatchRejection.RatioExceeded);
    }

    // ---------------------------------------------------------------- §9 row: limit_boundaries (gzip expanded cap)

    [Fact]
    public async Task gzip_expanded_cap_boundary_is_exact_and_one_byte_over_is_413()
    {
        // The expanded-size boundary is exercised through the real host with the option's lower bound (1 MiB)
        // configured — the same IngestionLimitsOptionsTests configuration seam. At the 8 MiB default the window
        // is a knife edge: the 1 MiB as-sent cap forces the compression ratio to sit between 8:1 and 20:1, and a
        // payload at the 8:1 edge compresses to (just over) the 1 MiB transport cap. The option's [Range] floor
        // is 1 MiB, where the window is wide (the payload below compresses to ~780 KiB, ratio ~1.3:1), so the
        // boundary is built there and the expanded-size cap — not the ratio cap — is the one that decides.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await using var host = world.Host.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["EVIDGATE:Ingestion:MaxExpandedBytes"] = ExpandedCap.ToString(CultureInfo.InvariantCulture),
            })));
        using var client = host.CreateClient();
        using var listener = RejectionListener(out var reasons);

        // Exactly the cap: decoded and accepted by both caps. The decoded bytes are not a valid batch, so the
        // route answers 422 event_invalid — never 413, and the limit counter is not touched.
        var atCap = await PostGzipAsync(client, world.RunId, GzipOf(RepetitiveText(ExpandedCap)));

        atCap.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, atCap.ErrorMessage);
        atCap.ErrorCode.ShouldBe("event_invalid");
        reasons.ShouldBeEmpty();
        (await EventRowsAsync(world)).ShouldBe(0);

        // One decoded byte over the cap: 413 expanded_too_large, one counter measurement, zero rows.
        var overCap = await PostGzipAsync(client, world.RunId, GzipOf(RepetitiveText(ExpandedCap + 1)));

        overCap.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, overCap.ErrorMessage);
        overCap.ErrorCode.ShouldBe("batch_too_large");
        reasons.ShouldHaveSingleItem().ShouldBe(IngestBatchRejection.ExpandedTooLarge);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch")).ShouldBe(0);
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

    /// <summary>One valid one-event batch as a serialized body (for the gzip and malformed cases).</summary>
    private static string Batch(IngestTestWorld world, long sequence) =>
        IngestQaRequests.Json(OneEvent(world, sequence));

    /// <summary>One valid one-event batch as an object, so a caller can pad or re-serialize it (≤ 500 events).</summary>
    private static object OneEvent(IngestTestWorld world, long sequence) =>
        IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence));

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
        PostGzipAsync(world.Client, world.RunId, compressed, requestId, encoding);

    /// <summary>One <c>Content-Encoding: gzip</c> request of the given compressed body on an arbitrary client.</summary>
    private static Task<ApiAnswer> PostGzipAsync(
        HttpClient client,
        Guid runId,
        byte[] compressed,
        string? requestId = null,
        string encoding = "gzip") =>
        IngestQaRequests.PostAsync(client, runId, GzipContent(compressed, encoding), requestId);

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

    /// <summary>The <c>IngestionLimits.MaxExpandedBytes</c> floor (its <c>[Range]</c> lower bound), 1 MiB.</summary>
    private const int ExpandedCap = 1_048_576;

    private static readonly byte[] Alphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8.ToArray();

    /// <summary>
    /// Deterministic bytes of <paramref name="length"/> drawn from a 62-symbol alphabet: a body at the
    /// expanded cap gzips to about 780 KiB, which is under the 1 MiB as-sent cap and far above the
    /// <c>cap / 20</c> a 20:1 ratio needs, so the expanded-size cap is the one that decides.
    /// </summary>
    private static byte[] RepetitiveText(int length)
    {
        var bytes = new byte[length];
        var state = 0x2545F491u;
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            bytes[i] = Alphabet[(byte)(state >> 24) % Alphabet.Length];
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
