// 04.1.1-T1: POST /v1/runs/{r}/events through the real host and a real PostgreSQL — the wire contract of the atomic
// batch ingest (AC1–AC7, D6/D9) as the task file §9 lists it. Request bodies are written inline so each test reads as
// the request it sends.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 04.1.1-T1 through the real C2 pipeline, a real PostgreSQL and a real host: one batch of ≤ 500 events is
/// validated, canonicalised and committed in one transaction, each event is acked <c>accepted</c>/<c>duplicate</c> in
/// request order, and any failing event rejects the whole batch (AC1–AC7). The caller is a service principal with
/// <c>event:write</c>, a project scope and a token id (the slot-binding identity, T9), published by a test startup
/// filter because no request header can express one (the <see cref="EvidenceUploadUrlTests"/> seam).
/// <para>
/// The 413 code is asserted as the task file's <c>batch_too_large</c> (§9 AC7): the events route's own cap, distinct
/// from the shared <c>payload_too_large</c> of the other routes. The gzip cases and the run-state transition are
/// other tasks (T3, 05.3.1).
/// </para>
/// </summary>
[Trait("Category", "Docker")]
public sealed class IngestEventBatchTests
{
    /// <summary>A well-formed execution key that no seeded plan carries: 64 lowercase hex (T10).</summary>
    private const string UnplannedKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

    private const string OccurredAt = "2026-09-26T10:15:30Z";

    private readonly PostgresTenancyFixture _fixture;

    public IngestEventBatchTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    // ------------------------------------------------------------------ request/response shapes

    /// <summary>
    /// One event envelope of the batch (00 §9.4 / <c>contracts/schemas/event-envelope.v1.json</c>). Optional members
    /// whose value is null are omitted, because the schema declares <c>status</c> and <c>duration_ms</c> as non-nullable
    /// (<c>additionalProperties: false</c>): a JSON null would be a schema error, not an absent member.
    /// </summary>
    internal static Dictionary<string, object?> Event(
        Guid eventId,
        string executionKey,
        long sequence,
        string producerId = IngestTestWorld.ProducerId,
        int attemptNo = 1,
        string type = "test.finished",
        string? status = "passed",
        object? ext = null,
        long? durationMs = null,
        Guid[]? artifactIds = null)
    {
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = 1,
            ["event_id"] = eventId,
            ["producer_id"] = producerId,
            ["sequence"] = sequence,
            ["job_id"] = null,
            ["epoch"] = 1,
            ["execution_key"] = executionKey,
            ["attempt_no"] = attemptNo,
            ["type"] = type,
            ["occurred_at"] = OccurredAt,
            ["artifact_ids"] = artifactIds ?? Array.Empty<Guid>(),
            ["ext"] = ext ?? new { schema = "playwright/1" },
        };

        if (status is not null)
        {
            envelope["status"] = status;
        }

        if (durationMs is not null)
        {
            envelope["duration_ms"] = durationMs.Value;
        }

        return envelope;
    }

    internal static object Batch(params object[] events) => new { schema_version = 1, events };

    /// <summary>The request body of a serialized batch; the synchronous <c>Serialize</c> stays out of the async path (VSTHRD103).</summary>
    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    /// <summary>One <c>POST /v1/runs/{r}/events</c> as the host's published principal.</summary>
    internal static async Task<ApiAnswer> PostEventsAsync(
        IngestTestWorld world,
        object body,
        Guid? runId = null,
        HttpClient? client = null,
        string? requestId = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/runs/{runId ?? world.RunId:D}/events");
        if (requestId is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);
        }

        request.Content = JsonBody(body);
        using var response = await (client ?? world.Client).SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    /// <summary>One raw-body <c>POST /v1/runs/{r}/events</c> (the exact-byte-size cases).</summary>
    private static async Task<ApiAnswer> PostRawAsync(IngestTestWorld world, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/runs/{world.RunId:D}/events");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);

        return new ApiAnswer(response.StatusCode, document.RootElement.Clone(), response.Headers);
    }

    private static List<JsonElement> Acks(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("acks").EnumerateArray()];

    private static List<JsonElement> Details(ApiAnswer answer) =>
        [.. answer.Body.GetProperty("error").GetProperty("details").EnumerateArray()];

    private static int IndexOf(JsonElement detail) => detail.GetProperty("index").GetInt32();

    /// <summary>The world's own rows: the database is shared, so every read names its tenant and run.</summary>
    private static string Scope(IngestTestWorld world) => $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'";

    /// <summary>Org-only tables (outbox, audit, capabilities) carry no run column.</summary>
    private static string OrgScope(IngestTestWorld world) => $"org_id = '{world.Org:D}'";

    private static Task<long> EventRowsAsync(IngestTestWorld world) => world.CountAsync("event", Scope(world));

    private static Task<long> BatchRowsAsync(IngestTestWorld world) => world.CountAsync("event_batch", Scope(world));

    private static Task<long> OutboxRowsAsync(IngestTestWorld world, string taskType) =>
        world.CountAsync("outbox", $"{OrgScope(world)} and task_type = '{taskType}'");

    /// <summary>One event of <paramref name="world"/>'s plan with its real execution key (T10).</summary>
    private static string KeyOf(IngestTestWorld world, int index) => world.Plan.Executions[index].ExecutionKey;

    // ------------------------------------------------------------------ AC1

    [Fact]
    public async Task valid_batch_commits_atomically_and_acks_in_order()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var requestId = Guid.NewGuid().ToString("N");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(first, KeyOf(world, 0), sequence: 1, status: "passed", durationMs: 1280),
                Event(second, KeyOf(world, 1), sequence: 2, status: "failed")),
            requestId: requestId);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        answer.Body.GetProperty("duplicates").GetInt32().ShouldBe(0);
        answer.Body.GetProperty("batch_id").GetGuid().ShouldNotBe(Guid.Empty);

        // AC1: one ack per event, request order, each accepted.
        var acks = Acks(answer);
        acks.Count.ShouldBe(2);
        acks.Select(ack => ack.GetProperty("index").GetInt32()).ShouldBe([0, 1]);
        acks.Select(ack => ack.GetProperty("event_id").GetGuid()).ShouldBe([first, second]);
        acks.Select(ack => ack.GetProperty("ack").GetString()).ShouldAllBe(ack => ack == "accepted");

        // The 00 §9.1 request id is echoed on the response header.
        answer.Headers.GetValues("X-Request-Id").Single().ShouldBe(requestId);

        // One transaction wrote every event row, the one batch row and the metering outbox row.
        (await EventRowsAsync(world)).ShouldBe(2);
        (await BatchRowsAsync(world)).ShouldBe(1);
        (await world.ScalarAsync<int>($"select accepted from public.event_batch where {Scope(world)}")).ShouldBe(2);
        (await world.ScalarAsync<int>($"select duplicate from public.event_batch where {Scope(world)}")).ShouldBe(0);
        (await world.ScalarAsync<int>($"select event_count from public.event_batch where {Scope(world)}")).ShouldBe(2);
        (await OutboxRowsAsync(world, "usage.batch_accepted")).ShouldBe(1);

        (await world.EventColumnAsync<Guid>("event_id")).ShouldBe([first, second]);
        (await world.EventColumnAsync<string>("execution_key")).ShouldBe([KeyOf(world, 0), KeyOf(world, 1)]);
        (await world.EventColumnAsync<long>("sequence")).ShouldBe([1L, 2L]);
        (await world.EventColumnAsync<string>("type")).ShouldAllBe(type => type == "test.finished");
        (await world.EventColumnAsync<string>("raw_status")).ShouldBe(["passed", "failed"]);
    }

    // ------------------------------------------------------------------ AC2

    [Fact]
    public async Task resend_is_all_duplicates_no_new_rows()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var body = Batch(
            Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1),
            Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2));

        (await PostEventsAsync(world, body)).Status.ShouldBe(HttpStatusCode.OK);
        var outboxAfterFirst = await OutboxRowsAsync(world, "usage.batch_accepted");

        var resend = await PostEventsAsync(world, body);

        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        resend.Body.GetProperty("accepted").GetInt32().ShouldBe(0);
        resend.Body.GetProperty("duplicates").GetInt32().ShouldBe(2);
        Acks(resend).Select(ack => ack.GetProperty("ack").GetString()).ShouldAllBe(ack => ack == "duplicate");

        // AC2/T83: no new event rows and no new metering row (accepted = 0 never bills).
        (await EventRowsAsync(world)).ShouldBe(2);
        (await OutboxRowsAsync(world, "usage.batch_accepted")).ShouldBe(outboxAfterFirst);
        (await OutboxRowsAsync(world, "usage.batch_accepted")).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC3

    [Fact]
    public async Task one_invalid_event_rejects_whole_batch()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // Index 1 violates the envelope schema (attempt_no minimum is 1), so the whole batch is refused.
        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1),
                Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2, attemptNo: 0)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("event_invalid");
        Details(answer).ShouldContain(detail => IndexOf(detail) == 1);

        // Nothing committed: zero event rows and zero batch rows.
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC4

    [Fact]
    public async Task unknown_execution_key_rejects_with_indices()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1),
                Event(Guid.CreateVersion7(), UnplannedKey, sequence: 2),
                Event(Guid.CreateVersion7(), UnplannedKey, sequence: 3)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("unknown_execution_key");
        Details(answer).Select(IndexOf).ShouldBe([1, 2]);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ AC5

    [Fact]
    public async Task different_payload_duplicate_rejects_409()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var existing = Guid.CreateVersion7();

        (await PostEventsAsync(world, Batch(Event(existing, KeyOf(world, 0), sequence: 1, status: "passed"))))
            .Status.ShouldBe(HttpStatusCode.OK);

        // The same event_id with a different canonical payload, plus one genuinely new event: the whole batch rolls
        // back, so the new event is not written either (T50).
        var conflict = await PostEventsAsync(
            world,
            Batch(
                Event(existing, KeyOf(world, 0), sequence: 1, status: "failed"),
                Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2)));

        conflict.Status.ShouldBe(HttpStatusCode.Conflict);
        conflict.ErrorCode.ShouldBe("duplicate_different_payload");
        Details(conflict).ShouldContain(detail => IndexOf(detail) == 0);
        (await EventRowsAsync(world)).ShouldBe(1);
        (await BatchRowsAsync(world)).ShouldBe(1);
        (await world.ScalarAsync<string>($"select raw_status from public.event where {Scope(world)}")).ShouldBe("passed");
    }

    // ------------------------------------------------------------------ AC6

    [Fact]
    public async Task reused_sequence_rejects_422()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        (await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 7))))
            .Status.ShouldBe(HttpStatusCode.OK);

        // A new event_id (so no dedupe) reusing (run, producer_id, sequence): the second batch is refused.
        var reused = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 7)));

        reused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        reused.ErrorCode.ShouldBe("sequence_reused");
        (await EventRowsAsync(world)).ShouldBe(1);
        (await BatchRowsAsync(world)).ShouldBe(1);
    }

    // ------------------------------------------------------------------ AC7: the caps

    [Fact]
    public async Task limit_boundaries_partial()
    {
        // Five planned executions: Events() spans executions 0..4 (i % 5), and a key outside the plan is 422.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);

        // Exactly 500 events: accepted (AC7's lower boundary).
        var fiveHundred = await PostEventsAsync(world, Batch(Events(world, 500)));
        fiveHundred.Status.ShouldBe(HttpStatusCode.OK, fiveHundred.ErrorMessage);
        fiveHundred.Body.GetProperty("accepted").GetInt32().ShouldBe(500);
        (await EventRowsAsync(world)).ShouldBe(500);

        // 501 events: 413 with zero new rows (the batch never reaches the commit).
        var fiveOhOne = await PostEventsAsync(world, Batch(Events(world, 501)));
        fiveOhOne.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        fiveOhOne.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(500);
        (await BatchRowsAsync(world)).ShouldBe(1);

        // Exactly 1 MiB of raw body: accepted.
        var oneMiB = await PostRawAsync(world, Padded(world, 1_048_576));
        oneMiB.Status.ShouldBe(HttpStatusCode.OK, oneMiB.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(501);

        // 1 MiB + 1 byte: refused before any write.
        var overMiB = await PostRawAsync(world, Padded(world, 1_048_577));
        overMiB.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        overMiB.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(501);
    }

    /// <summary><paramref name="count"/> events of one batch, spread over the plan's five executions and their attempts.</summary>
    private static object[] Events(IngestTestWorld world, int count)
    {
        var events = new object[count];
        for (var i = 0; i < count; i++)
        {
            // attempt_no is bounded to 1..100 by the attempt write, so the batch spans executions, attempts and steps.
            var execution = i % 5;
            events[i] = Event(
                Guid.CreateVersion7(),
                KeyOf(world, execution),
                sequence: i + 1,
                attemptNo: (i % 100) + 1,
                type: i % 5 == 0 ? "test.finished" : "test.step",
                status: i % 5 == 0 ? "passed" : null);
        }

        return events;
    }

    /// <summary>A valid one-event batch whose serialized body is padded with trailing whitespace to exactly <paramref name="bytes"/>.</summary>
    private static string Padded(IngestTestWorld world, int bytes)
    {
        // Sequence 501: the 500-event batch above stored 1..500 for this producer, and a reused sequence is
        // 422 sequence_reused (AC6), not the transport-cap answer this case is about.
        var body = JsonSerializer.Serialize(
            Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 501, status: "passed")));
        var length = Encoding.UTF8.GetByteCount(body);

        return length <= bytes ? body + new string(' ', bytes - length) : body;
    }

    // ------------------------------------------------------------------ D6: run states

    [Fact]
    public async Task canceled_run_accepts_and_marks_after_cancel()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("canceled");

        var answer = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await EventRowsAsync(world)).ShouldBe(1);
        (await world.ScalarAsync<bool>($"select after_cancel from public.event where {Scope(world)}")).ShouldBeTrue();
    }

    [Fact]
    public async Task complete_run_rejects_409_run_closed()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("complete");

        var answer = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Conflict);
        answer.ErrorCode.ShouldBe("run_closed");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ tenant and permission boundaries

    [Fact]
    public async Task tenant_isolation()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var (otherOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("globex"));
        var otherRun = await world.SeedOtherRunAsync(otherOrg);

        // The run belongs to another tenant: RLS hides it, so the route answers 404 and writes nothing.
        var answer = await PostEventsAsync(
            world,
            Batch(Event(Guid.CreateVersion7(), otherRun.Executions[0].ExecutionKey, sequence: 1)),
            runId: otherRun.RunId);

        answer.Status.ShouldBe(HttpStatusCode.NotFound);
        answer.ErrorCode.ShouldBe("not_found");
        (await EventRowsAsync(world)).ShouldBe(0);
        // The caller's run is not the only place a stray write could land: the rejected target must stay empty too.
        (await world.CountAsync("event", $"run_id = '{otherRun.RunId:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task denied_permission_returns_403()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Reader());

        var answer = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task project_scope_mismatch_for_ci_producer_403()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(projectScope: null));
        using var setup = world.Api.CreateClient();
        var otherProject = await RegistryApi.CreateProjectAsync(setup, world.User, world.Org, "other");
        var otherRun = await world.SeedRunInProjectAsync(otherProject);

        // A token scoped to one project may not ingest into another project's run (00 §9.1/§9.3).
        await using var scoped = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project,
            IngestTestWorld.Writer(projectScope: world.Project));
        using var scopedClient = scoped.CreateClient();

        var answer = await PostEventsAsync(
            world,
            Batch(Event(Guid.CreateVersion7(), otherRun.Executions[0].ExecutionKey, sequence: 1)),
            runId: otherRun.RunId,
            client: scopedClient);

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
        (await EventRowsAsync(world)).ShouldBe(0);
        // Same tenant, different project: the run the token could not reach must stay empty as well.
        (await world.CountAsync("event", $"{OrgScope(world)} and run_id = '{otherRun.RunId:D}'")).ShouldBe(0);
    }

    // ------------------------------------------------------------------ D9: the producer slot

    [Fact]
    public async Task slot_taken_by_other_token_403()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), boundJti: IngestTestWorld.OtherJti);

        var answer = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden);
        answer.ErrorCode.ShouldBe("producer_slot_taken");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task first_batch_binds_slot_then_foreign_token_403_producer_slot_taken()
    {
        // AC3 on the event path: the seeded slot starts unbound, the first ACCEPTED batch binds it to the
        // caller's jti, and a later batch from a different ci_producer identity is then refused 403
        // producer_slot_taken with no write (T9). Distinct from slot_taken_by_other_token_403, which pre-binds
        // the slot and never exercises the binding the first batch performs.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var accepted = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await world.ScalarAsync<string>($"select bound_token_jti from public.producer_slot where {Scope(world)}"))
            .ShouldBe(IngestTestWorld.Jti);
        (await EventRowsAsync(world)).ShouldBe(1);

        // The foreign credential is scoped to the same project on purpose: this case is about the producer
        // slot, and an unscoped token would be refused by the route's budget partition before it.
        await using var foreign = TenancyApiFactory.WithPrincipal(
            _fixture.AppConnectionString(world.Database), world.Org, world.Project,
            IngestTestWorld.Writer(jti: IngestTestWorld.OtherJti, projectScope: world.Project));
        using var foreignClient = foreign.CreateClient();

        var refused = await PostEventsAsync(
            world,
            Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 2)),
            client: foreignClient);

        refused.Status.ShouldBe(HttpStatusCode.Forbidden);
        refused.ErrorCode.ShouldBe("producer_slot_taken");
        (await EventRowsAsync(world)).ShouldBe(1);
        (await BatchRowsAsync(world)).ShouldBe(1);
    }

    [Fact]
    public async Task unknown_producer_422()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(
            world,
            Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1, producerId: "shard-9")));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("unknown_producer");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task mixed_producer_batch_422()
    {
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), slots: 2);

        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1, producerId: "shard-1"),
                Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 1, producerId: "shard-2")));

        // One batch = one producer (event_batch.producer_id is a single column): a batch mixing producers is refused.
        // The task file names the shape violation without pinning its code, so either shape error is accepted.
        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBeOneOf("event_invalid", "unknown_producer");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------------------ the first-send capability seam

    [Fact]
    public async Task first_send_batch_with_bad_major_422_unsupported_protocol_major()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(world, Batch(Carrier(world, major: 2)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        answer.ErrorCode.ShouldBe("unsupported_protocol_major");
        // The offending member is the capability's protocol major; the detail path is the seam's own (/ext/capability).
        Details(answer).ShouldNotBeEmpty();

        // The whole batch is rejected: no event row and no capability row.
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("adapter_capability", OrgScope(world))).ShouldBe(0);
    }

    [Fact]
    public async Task first_send_capability_registered_with_batch()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(
            world,
            Batch(
                Carrier(world, major: 1),
                Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(2);
        (await EventRowsAsync(world)).ShouldBe(2);
        (await world.CountAsync("adapter_capability", OrgScope(world))).ShouldBe(1);
        (await world.ScalarAsync<string>($"select name from public.adapter_capability where {OrgScope(world)}")).ShouldBe("playwright");
        (await world.ScalarAsync<int>($"select protocol_major from public.adapter_capability where {OrgScope(world)}")).ShouldBe(1);

        // S-1: the org-level adapter_capability write of a first-send batch is audited with the handler's audited
        // action, naming the capability row it registered (the ordinary-batch suppression is lifted for it).
        (await world.ScalarAsync<long>(
            $"select count(*) from public.audit_log where org_id = '{world.Org:D}' and action = 'event.batch.ingest' "
            + "and object_type = 'adapter_capability'")).ShouldBe(1);
    }

    [Fact]
    public async Task ordinary_batch_writes_no_audit_row()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // An ordinary batch carries no security decision: its event_batch row is the record, so no audit row.
        (await world.ScalarAsync<long>($"select count(*) from public.audit_log where {OrgScope(world)} and action = 'event.batch.ingest'"))
            .ShouldBe(0);
    }

    /// <summary>The sequence-1 <c>run.log</c> capability carrier of a run (04.7.1-T2.3), with a real execution key.</summary>
    private static object Carrier(IngestTestWorld world, int major) => new
    {
        schema_version = 1,
        event_id = Guid.CreateVersion7(),
        producer_id = IngestTestWorld.ProducerId,
        sequence = 1,
        job_id = (Guid?)null,
        epoch = 1,
        execution_key = KeyOf(world, 0),
        attempt_no = 1,
        type = "run.log",
        occurred_at = OccurredAt,
        artifact_ids = Array.Empty<Guid>(),
        ext = new
        {
            schema = $"playwright/{major}",
            capability = new
            {
                protocol = new { major, minor = 0 },
                engine = new { family = "playwright", version = "1.49.0" },
                platforms = new[] { "linux-x64" },
                supports = new { discovery = true, step_events = true, retry = true, cancel = true },
                mode = "full",
            },
        },
    };

    // ------------------------------------------------------------------ the stored payload

    [Fact]
    public async Task ext_secret_keys_never_reach_payload()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(
            world,
            Batch(Event(
                Guid.CreateVersion7(),
                KeyOf(world, 0),
                sequence: 1,
                status: "passed",
                ext: new
                {
                    schema = "playwright/1",
                    token = "super-secret-token-value",
                    password = "hunter2",
                    authorization = "Bearer abcdef",
                })));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var payload = await world.ScalarAsync<string>($"select payload::text from public.event where {Scope(world)}");
        payload.ShouldNotContain("super-secret-token-value");
        payload.ShouldNotContain("hunter2");
        payload.ShouldNotContain("Bearer abcdef");
    }

    // ------------------------------------------------------------------ the attempt write (05.3.3-T2)

    [Fact]
    public async Task attempt_written_for_test_finished()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // ext.xfail is the one allow-listed test.finished key the server honours (05.3.3-T1): a failed outcome the
        // producer expected flips to passed; ext.normalized (not allow-listed) is ignored.
        var answer = await PostEventsAsync(
            world,
            Batch(Event(
                Guid.CreateVersion7(),
                KeyOf(world, 0),
                sequence: 1,
                status: "failed",
                durationMs: 1200,
                ext: new { schema = "playwright/1", xfail = true, normalized = "timed_out" })));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await world.CountAsync("case_attempt", Scope(world))).ShouldBe(1);
        (await world.ScalarAsync<string>($"select raw_status from public.case_attempt where {Scope(world)}")).ShouldBe("failed");
        (await world.ScalarAsync<string>($"select normalized_status from public.case_attempt where {Scope(world)}")).ShouldBe("passed");
        (await world.ScalarAsync<int>($"select duration_ms from public.case_attempt where {Scope(world)}")).ShouldBe(1200);
        (await world.ScalarAsync<Guid>($"select case_id from public.case_attempt where {Scope(world)}"))
            .ShouldBe(world.Plan.Executions[0].CaseId);
    }

    [Fact]
    public async Task refused_attempt_rolls_back_events()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // Index 1 names an attempt_no above the writer's MaxAttemptNo (100): the refusal rolls back the whole batch,
        // so the otherwise-accepted event at index 0 is not written either.
        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1),
                Event(Guid.CreateVersion7(), KeyOf(world, 1), sequence: 2, attemptNo: 101)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await BatchRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("case_attempt", Scope(world))).ShouldBe(0);
    }

    [Fact]
    public async Task failed_setup_does_not_block_dependants()
    {
        // A is the setup, B depends on A (case_dependency); both are planned executions of the run. The events
        // route does not call IBlockedAttemptResolver: no producer signal marks a failed attempt as the final
        // one (contracts/adapter/playwright-mapping.md §Retries: every attempt is its own event pair, `attempt`
        // is not an ext key), and a non-final failed attempt of a setup that may still pass must never block its
        // dependants (ICaseAttemptWriter.cs). The blocked-dependants pass is deferred to 04.1.1-T4 / 05.3.3.
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 2, edges: [(0, 1)]);

        var failed = world.Plan.Executions[0];
        var dependant = world.Plan.Executions[1];

        var answer = await PostEventsAsync(
            world,
            Batch(Event(Guid.CreateVersion7(), failed.ExecutionKey, sequence: 1, status: "failed")));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        // Only the failed setup's own attempt is written; the dependant is neither blocked nor attempted.
        (await world.CountAsync("case_attempt", Scope(world))).ShouldBe(1);
        (await world.ScalarAsync<string>(
            $"select normalized_status from public.case_attempt where {Scope(world)} and execution_key = '{failed.ExecutionKey}'"))
            .ShouldBe("failed");
        (await world.CountAsync("case_attempt", where: $"{Scope(world)} and execution_key = '{dependant.ExecutionKey}'")).ShouldBe(0);
        (await world.CountAsync("case_attempt", where: $"{Scope(world)} and normalized_status = 'blocked'")).ShouldBe(0);
    }

    [Fact]
    public async Task retry_failed_then_passed_leaves_dependants_unblocked()
    {
        // A flaky setup: attempt 1 failed, attempt 2 passed, both in one batch. Without a final-attempt signal the
        // route must not block the dependant, and the retry's pass is stored as its own attempt slot.
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: 2, edges: [(0, 1)]);

        var setup = world.Plan.Executions[0];
        var dependant = world.Plan.Executions[1];

        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), setup.ExecutionKey, sequence: 1, attemptNo: 1, status: "failed"),
                Event(Guid.CreateVersion7(), setup.ExecutionKey, sequence: 2, attemptNo: 2, status: "passed")));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        (await world.CountAsync("case_attempt", Scope(world))).ShouldBe(2);
        (await world.ScalarAsync<string>(
            $"select normalized_status from public.case_attempt where {Scope(world)} and execution_key = '{setup.ExecutionKey}' and attempt_no = 1"))
            .ShouldBe("failed");
        (await world.ScalarAsync<string>(
            $"select normalized_status from public.case_attempt where {Scope(world)} and execution_key = '{setup.ExecutionKey}' and attempt_no = 2"))
            .ShouldBe("passed");
        (await world.CountAsync("case_attempt", where: $"{Scope(world)} and execution_key = '{dependant.ExecutionKey}'")).ShouldBe(0);
        (await world.CountAsync("case_attempt", where: $"{Scope(world)} and normalized_status = 'blocked'")).ShouldBe(0);
    }

    [Fact]
    public async Task resend_of_failed_batch_writes_no_attempt_and_no_slot_update()
    {
        // P2: the same batch resent is acked `duplicate`; an event already stored by an earlier batch must reach
        // neither the case-attempt writer nor the producer_slot high-water mark, and the skipped slot update
        // leaves producer_slot.version untouched (the shared trigger would bump it on a no-op update).
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var body = Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1, status: "failed"));

        var first = await PostEventsAsync(world, body);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        (await world.CountAsync("case_attempt", Scope(world))).ShouldBe(1);
        var version = await world.ScalarAsync<int>(
            $"select version from public.producer_slot where {Scope(world)} and producer_id = 'shard-1'");

        var resend = await PostEventsAsync(world, body);
        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);
        resend.Body.GetProperty("duplicates").GetInt32().ShouldBe(1);

        (await world.CountAsync("case_attempt", Scope(world))).ShouldBe(1);
        (await EventRowsAsync(world)).ShouldBe(1);
        (await world.ScalarAsync<int>(
            $"select version from public.producer_slot where {Scope(world)} and producer_id = 'shard-1'"))
            .ShouldBe(version);
    }

    [Fact]
    public async Task normalized_status_only_written_for_test_finished()
    {
        // F3: 00 §8.3 defines a normalised status only for a finished attempt's outcome; every other type carries
        // no verdict, so event.normalized_status stays NULL for it.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var answer = await PostEventsAsync(
            world,
            Batch(
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1, type: "test.step", status: null),
                Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 2, status: "failed")));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);

        (await world.ScalarAsync<string?>($"select normalized_status from public.event where {Scope(world)} and type = 'test.step'"))
            .ShouldBeNull();
        (await world.ScalarAsync<string>($"select normalized_status from public.event where {Scope(world)} and type = 'test.finished'"))
            .ShouldBe("failed");
    }

    // ------------------------------------------------------------------ S3: no event body in the logs

    [Fact]
    public async Task log_has_no_payload()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var sink = new IngestLogSink();
        await using var host = world.Host.WithWebHostBuilder(
            builder => builder.ConfigureTestServices(services => services.AddLogging(logging => logging.AddProvider(sink))));
        using var client = host.CreateClient();
        const string sentinel = "log-sentinel-9f3c1a";

        var answer = await PostEventsAsync(
            world,
            Batch(Event(
                Guid.CreateVersion7(),
                KeyOf(world, 0),
                sequence: 1,
                status: "passed",
                ext: new { schema = "playwright/1", token = sentinel })),
            client: client);

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        sink.Lines.ShouldAllBe(line => !line.Contains(sentinel, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the statement budget

    [Fact]
    public async Task statement_count_is_bounded()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        world.Host.Statements.Clear();

        var answer = await PostEventsAsync(world, Batch(Event(Guid.CreateVersion7(), KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        var writes = world.Host.Statements.Snapshot()
            .Where(statement => statement.Contains("insert into public.event", StringComparison.Ordinal)
                || statement.Contains("public.event_batch", StringComparison.Ordinal))
            .ToList();
        writes.Count.ShouldBeLessThanOrEqualTo(8, string.Join(Environment.NewLine, world.Host.Statements.Snapshot()));
    }
}
