// 04.1.1-T1 independent QA (S33): the stored-payload boundaries — unstorable text, the ext allow-list at depth,
// the residue cap, the number ranges and the xfail/normalized hand-over (task file §10 T1 Notes from #44).
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>The payload/text/number boundaries of <c>POST /v1/runs/{r}/events</c> over a real PostgreSQL.</summary>
[Collection(TenancySuite.Name)]
[Trait("Category", "Docker")]
public sealed class IngestQaPayloadTests
{
    private readonly PostgresTenancyFixture _fixture;

    public IngestQaPayloadTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static Task<long> EventRowsAsync(IngestTestWorld world) => world.CountAsync("event");

    [Fact]
    public async Task unicode_payload_round_trips_and_is_hashed()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 1,
                status: "passed",
                ext: new { schema = "playwright/1", title = "héllo → 世界 😀" })));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var payload = await world.ScalarAsync<string>("select payload::text from public.event");
        payload.ShouldContain("héllo → 世界 😀");

        // 00 §7: the content address is sha256:<64 hex>.
        var hash = await world.ScalarAsync<string>("select payload_hash from public.event");
        hash.ShouldMatch("^sha256:[0-9a-f]{64}$");
    }

    [Fact]
    public async Task nul_and_lone_surrogate_payload_text_rejected_422()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // U+0000 reaches neither jsonb nor the RFC 8785 form, so the batch is a 422, never a 500.
        var nul = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 1,
                status: "passed",
                ext: new { schema = "playwright/1", title = "a\u0000b" })));

        nul.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, nul.ErrorMessage);
        nul.ErrorCode.ShouldBe("event_invalid");

        // An unpaired UTF-16 surrogate is the same refusal (UnstorableText, 04.7.1-T2.2/T2.3). The body carries the
        // literal `\ud800` escape: JsonSerializer would sanitise a lone surrogate to U+FFFD before the bytes leave,
        // which the server could store, so the escape is substituted into the serialized document as written.
        var surrogateBody = IngestQaRequests.Json(IngestQaRequests.Batch(IngestQaRequests.Event(
            Guid.CreateVersion7(),
            IngestQaRequests.KeyOf(world, 0),
            sequence: 2,
            status: "passed",
            ext: new { schema = "playwright/1", title = "lone-surrogate-slot" })))
            .Replace("lone-surrogate-slot", "\\ud800", StringComparison.Ordinal);

        var surrogate = await IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            new StringContent(surrogateBody, System.Text.Encoding.UTF8, "application/json"));

        surrogate.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, surrogate.ErrorMessage);
        surrogate.ErrorCode.ShouldBe("event_invalid");

        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch")).ShouldBe(0);
    }

    [Fact]
    public async Task secret_shaped_key_at_nesting_depth_dropped_from_payload()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // `warn` is allow-listed for run.log; its nested `token` names a credential, so it is dropped at depth
        // while the harmless sibling survives (04 §6, 04 §8 S5).
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 2,
                type: "run.log",
                status: null,
                ext: new
                {
                    schema = "playwright/1",
                    warn = new { token = "super-secret-nested", keep = "visible" },
                })));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var payload = await world.ScalarAsync<string>("select payload::text from public.event");
        payload.ShouldContain("visible");
        payload.ShouldNotContain("super-secret-nested");
        payload.ShouldNotContain("token");
    }

    [Fact]
    public async Task ext_residue_over_eight_kib_rejected_422()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // The residue cap of 04 §8 S5 is 8192 bytes; a kept `title` past it is refused before the insert.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 1,
                status: "passed",
                ext: new { schema = "playwright/1", title = new string('x', 9000) })));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("event_invalid");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task duration_beyond_int_kept_on_event_dropped_from_attempt()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // 3e9 > int.MaxValue: the event row keeps the exact value, the attempt column (int) drops it (task file
        // §10 T1 Notes) rather than truncating.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 1,
                status: "passed",
                durationMs: 3_000_000_000L)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await world.ScalarAsync<long>("select duration_ms from public.event")).ShouldBe(3_000_000_000L);
        (await world.ScalarAsync<long?>("select duration_ms from public.case_attempt")).ShouldBeNull();
    }

    [Fact]
    public async Task out_of_range_numbers_rejected_422()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // 2^63 does not fit a long (the parser's out_of_range).
        var duration = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, durationMs: 9_223_372_036_854_775_808UL)));

        duration.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, duration.ErrorMessage);
        duration.ErrorCode.ShouldBe("event_invalid");

        var epoch = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2, epoch: 9_223_372_036_854_775_808UL)));

        epoch.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, epoch.ErrorMessage);
        epoch.ErrorCode.ShouldBe("event_invalid");

        // sequence is >= 0.
        var negative = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: -1)));

        negative.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, negative.ErrorMessage);
        negative.ErrorCode.ShouldBe("event_invalid");

        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task xfail_kept_and_normalized_dropped_in_payload()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // 05.3.3's decision (task file §10 T1 Notes from #44): the server recomputes the status, so ext.xfail
        // must stay in the allow-list and ext.normalized must be dropped.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, 0),
                sequence: 1,
                status: "failed",
                ext: new { schema = "playwright/1", xfail = true, normalized = "timed_out" })));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var payload = await world.ScalarAsync<string>("select payload::text from public.event");
        payload.ShouldContain("xfail");
        payload.ShouldNotContain("normalized");
        payload.ShouldNotContain("timed_out");

        // The server's own normalisation of the raw status with xfail flips the failed outcome to passed.
        (await world.ScalarAsync<string>("select normalized_status from public.event")).ShouldBe("passed");
        (await world.ScalarAsync<string>("select normalized_status from public.case_attempt")).ShouldBe("passed");
    }
}
