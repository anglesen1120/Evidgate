// 04.1.1-T1 independent QA (S33): boundary, negative, race and authorization cases the implementer's
// IngestEventBatchTests does not cover. Written against the task file's acceptance criteria (AC1-AC7) and the
// 00 §9.3/§9.4 wire contract, not against the handler internals.
#pragma warning disable CA1861
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// The transport, dedupe, ordering, run-state and authorization boundaries of <c>POST /v1/runs/{r}/events</c>
/// through the real host and a real PostgreSQL.
/// </summary>
[Trait("Category", "Docker")]
[Collection(ExclusiveTenancySuite.Name)]
public sealed class IngestQaBoundaryTests
{
    private readonly PostgresTenancyFixture _fixture;

    public IngestQaBoundaryTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    private static Task<long> EventRowsAsync(IngestTestWorld world) =>
        world.CountAsync("event", $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'");

    // ---------------------------------------------------------------- AC2/AC1: duplicate event_id inside one batch

    [Fact]
    public async Task duplicate_event_id_within_one_batch_is_counted_and_acked_as_a_duplicate()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // Two identical envelopes: the same event_id, the same payload, twice in one request. AC2's rule is
        // "the same event_id with the identical canonical payload" -> duplicate; the second element must be
        // acked `duplicate` and must not inflate `accepted`.
        var envelope = IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1);

        var answer = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, IngestQaRequests.Batch(envelope, envelope));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(1);

        // One row was written, so exactly one ack is `accepted` and one is `duplicate`.
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        answer.Body.GetProperty("duplicates").GetInt32().ShouldBe(1);
        IngestQaRequests.Acks(answer).Select(ack => ack.GetProperty("ack").GetString()).ShouldBe(["accepted", "duplicate"]);
        (await world.ScalarAsync<int>(
            $"select accepted from public.event_batch where run_id = '{world.RunId:D}'")).ShouldBe(1);
    }

    [Fact]
    public async Task duplicate_event_id_within_one_batch_different_payload_409()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var id = Guid.CreateVersion7();

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(id, IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed"),
                IngestQaRequests.Event(id, IngestQaRequests.KeyOf(world, 1), sequence: 2, status: "failed")));

        answer.Status.ShouldBe(HttpStatusCode.Conflict, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("duplicate_different_payload");
        IngestQaRequests.Details(answer).ShouldContain(detail => IngestQaRequests.IndexOf(detail) == 1);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(0);
    }

    // ---------------------------------------------------------------- AC6: same sequence twice in one batch

    [Fact]
    public async Task same_sequence_twice_in_one_batch_422_sequence_reused()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // Two distinct event_ids under the same (run, producer) reuse sequence 5: AC6 refuses the whole batch.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 5),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 5)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("sequence_reused");
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(0);
    }

    // ---------------------------------------------------------------- AC1/AC2: two concurrent identical batches

    [Fact]
    public async Task concurrent_identical_batches_never_double_insert()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // test.step envelopes (no attempt write) so the race under test is the event insert itself.
        var body = IngestQaRequests.Batch(
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, type: "test.step", status: null),
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2, type: "test.step", status: null),
            IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 3, type: "test.step", status: null));

        var answers = await Task.WhenAll(
            IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body),
            IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body));

        answers.ShouldAllBe(answer => answer.Status == HttpStatusCode.OK, string.Join(" | ", answers.Select(a => $"{a.Status}:{a.ErrorCode}")));
        (await EventRowsAsync(world)).ShouldBe(3);

        // Each of the three events is accepted exactly once across the two batches and a duplicate in the other.
        answers.Sum(answer => answer.Body.GetProperty("accepted").GetInt32()).ShouldBe(3);
        answers.Sum(answer => answer.Body.GetProperty("duplicates").GetInt32()).ShouldBe(3);
    }

    // ---------------------------------------------------------------- AC7: the 1 MiB cap without Content-Length

    [Fact]
    public async Task chunked_body_exactly_one_mib_accepted_and_one_byte_over_refused()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var exactly = IngestQaRequests.PaddedBody(
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)),
            1_048_576);

        var accepted = await IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            Chunked(exactly));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(1);

        var over = IngestQaRequests.PaddedBody(
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2)),
            1_048_577);

        var refused = await IngestQaRequests.PostAsync(world.Client, world.RunId, Chunked(over));

        refused.Status.ShouldBe(HttpStatusCode.RequestEntityTooLarge, refused.ErrorMessage);
        refused.ErrorCode.ShouldBe("batch_too_large");
        (await EventRowsAsync(world)).ShouldBe(1);
    }

    private static ChunkedJsonContent Chunked(string body)
    {
        var content = new ChunkedJsonContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return content;
    }

    // ---------------------------------------------------------------- Content-Encoding: gzip is supported (04.1.1-T3)

    [Fact]
    public async Task content_encoding_other_than_gzip_refused_422_request_invalid()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // 04.1.1-T3 accepts a single `gzip` (and `identity`) and keeps 422 request_invalid for anything else,
        // naming the header in the detail. `deflate` is a real encoding the route does not decode.
        var answer = await IngestQaRequests.PostAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.JsonBody(IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1))),
            mutate: request => request.Content!.Headers.TryAddWithoutValidation("Content-Encoding", "deflate"));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("request_invalid");
        IngestQaRequests.Details(answer).ShouldContain(detail => detail.GetProperty("path").GetString() == "Content-Encoding");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ---------------------------------------------------------------- D6/AC9: run states

    [Fact]
    public async Task run_state_boundary_unknown_404_created_accepted_incomplete_closed_canceled_marked()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // An unknown run id is the same answer as a foreign one: 404, no write.
        var missing = await IngestQaRequests.PostEventsAsync(
            world.Client,
            Guid.CreateVersion7(),
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        missing.Status.ShouldBe(HttpStatusCode.NotFound, missing.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(0);

        // created/queued/running/collecting accept events (only complete/incomplete/canceled are special).
        var sequence = 1L;
        foreach (var state in new[] { "created", "queued", "running", "collecting" })
        {
            await world.SetRunStateAsync(state);
            var open = await IngestQaRequests.PostEventsAsync(
                world.Client,
                world.RunId,
                IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence++)));

            open.Status.ShouldBe(HttpStatusCode.OK, $"{state}: {open.ErrorMessage}");
        }

        // T81/T91: an incomplete run still accepts late evidence inside its late window - the seeded run's
        // deadline is 2 h out and its late_window_minutes is 1440, so the window is open and the batch is
        // stored with late = true (04.1.2-T5's rule; 04.1.1's D6 fail-closed applied before the window was
        // computable).
        await world.SetRunStateAsync("incomplete");
        var lateSequence = sequence++;
        var lateAccepted = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: lateSequence)));

        lateAccepted.Status.ShouldBe(HttpStatusCode.OK, lateAccepted.ErrorMessage);
        lateAccepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.ScalarAsync<bool>(
            $"select late from public.event where run_id = '{world.RunId:D}' and sequence = {lateSequence}")).ShouldBeTrue();
        (await world.ScalarAsync<bool>(
            $"select after_cancel from public.event where run_id = '{world.RunId:D}' and sequence = {lateSequence}")).ShouldBeFalse();

        // Past the window the same incomplete run answers 409 run_closed (T81).
        await world.Fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '3 days' where id = '{world.RunId:D}'");
        var closed = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: sequence++)));

        closed.Status.ShouldBe(HttpStatusCode.Conflict, closed.ErrorMessage);
        closed.ErrorCode.ShouldBe("run_closed");

        // canceled still accepts and marks (T12). The run above is terminal (incomplete has no outgoing edge), so a
        // second world carries the canceled half: its run is seeded `created`, and created -> canceled is one edge.
        await using var canceledWorld = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await canceledWorld.SetRunStateAsync("canceled");
        var canceled = await IngestQaRequests.PostEventsAsync(
            canceledWorld.Client,
            canceledWorld.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(canceledWorld, 0), sequence: 1)));

        canceled.Status.ShouldBe(HttpStatusCode.OK, canceled.ErrorMessage);
        canceled.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await canceledWorld.ScalarAsync<bool>(
            $"select after_cancel from public.event where run_id = '{canceledWorld.RunId:D}' and sequence = 1")).ShouldBeTrue();
    }

    [Fact]
    public async Task after_cancel_accepted_run_terminal()
    {
        // AC5/T12: a canceled run keeps accepting events, marks them after_cancel, and STAYS canceled - the
        // write never moves the run's state, so a late event cannot resurrect it.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("canceled");

        var accepted = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        accepted.Status.ShouldBe(HttpStatusCode.OK, accepted.ErrorMessage);
        accepted.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.ScalarAsync<string>($"select state from public.run where id = '{world.RunId:D}'")).ShouldBe("canceled");
        (await world.ScalarAsync<bool>(
            $"select after_cancel from public.event where run_id = '{world.RunId:D}' and sequence = 1")).ShouldBeTrue();
        (await world.ScalarAsync<bool>(
            $"select late from public.event where run_id = '{world.RunId:D}' and sequence = 1")).ShouldBeFalse();
    }

    // ---------------------------------------------------------------- T81/T91: the late window

    [Fact]
    public async Task incomplete_run_late_events_marked_and_run_closed_after_window()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await world.SetRunStateAsync("incomplete");

        // The window's end is `collection_deadline_at + late_window_minutes` (RunEventScopeReader's SQL),
        // both columns of the locked run row. With late_window_minutes = 5 a deadline of now() less five
        // minutes puts the end one second in the future, so the boundary instant itself is inside the
        // window: `now() <= end` holds and the batch is stored with late = true.
        await world.Fixture.ExecuteAsync(
            world.Database,
            $"update public.run set late_window_minutes = 5, collection_deadline_at = now() - interval '5 minutes' + interval '1 second' where id = '{world.RunId:D}'");

        var inside = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        inside.Status.ShouldBe(HttpStatusCode.OK, inside.ErrorMessage);
        inside.Body.GetProperty("accepted").GetInt32().ShouldBe(1);
        (await world.ScalarAsync<bool>(
            $"select late from public.event where run_id = '{world.RunId:D}' and sequence = 1")).ShouldBeTrue();
        (await world.ScalarAsync<bool>(
            $"select after_cancel from public.event where run_id = '{world.RunId:D}' and sequence = 1")).ShouldBeFalse();

        // One second past the end (the deadline five minutes before, minus one second) the same incomplete
        // run answers 409 run_closed and writes nothing (T81).
        await world.Fixture.ExecuteAsync(
            world.Database,
            $"update public.run set collection_deadline_at = now() - interval '5 minutes' - interval '1 second' where id = '{world.RunId:D}'");

        var after = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2)));

        after.Status.ShouldBe(HttpStatusCode.Conflict, after.ErrorMessage);
        after.ErrorCode.ShouldBe("run_closed");
        (await EventRowsAsync(world)).ShouldBe(1);
    }

    // ---------------------------------------------------------------- AC4: a key planned in another run

    [Fact]
    public async Task execution_key_of_another_run_422_unknown_execution_key()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        var other = await world.SeedOtherRunAsync(world.Org);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), other.Executions[0].ExecutionKey, sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("unknown_execution_key");
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ---------------------------------------------------------------- S2/T5: no principal

    [Fact]
    public async Task no_principal_401_and_writes_nothing()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());
        await using var anonymous = TenancyApiFactory.RealEvaluator(_fixture.AppConnectionString(world.Database));
        using var client = anonymous.CreateClient();

        var answer = await IngestQaRequests.PostEventsAsync(
            client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Unauthorized, answer.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(0);
    }

    // ---------------------------------------------------------------- AC3: attempt_no bounds

    [Fact]
    public async Task attempt_no_zero_and_above_int_max_422_and_write_nothing()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        var zero = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, attemptNo: 0)));

        zero.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, zero.ErrorMessage);
        zero.ErrorCode.ShouldBe("event_invalid");
        IngestQaRequests.Details(zero).ShouldContain(detail => IngestQaRequests.IndexOf(detail) == 0);

        // 2^31 does not fit an int, which the parser reports as out_of_range (AC3).
        var huge = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 2, attemptNo: 2_147_483_648)));

        huge.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, huge.ErrorMessage);
        huge.ErrorCode.ShouldBe("event_invalid");

        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(0);
    }

    // ---------------------------------------------------------------- the closed type set

    [Fact]
    public async Task all_five_event_types_accepted_and_unknown_type_422()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), planned: 5);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, type: "test.started", status: null),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2, type: "test.step", status: null),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 2), sequence: 3, type: "test.finished", status: "passed"),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 3), sequence: 4, type: "run.log", status: null),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 4), sequence: 5, type: "producer.heartbeat", status: null)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("accepted").GetInt32().ShouldBe(5);
        (await EventRowsAsync(world)).ShouldBe(5);

        var unknown = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 6, type: "test.exploded")));

        unknown.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, unknown.ErrorMessage);
        unknown.ErrorCode.ShouldBe("event_invalid");
        (await EventRowsAsync(world)).ShouldBe(5);
    }

    // ---------------------------------------------------------------- rollback completeness

    [Fact]
    public async Task writer_refusal_leaves_no_rows_in_any_table()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer());

        // Index 1 names attempt_no 101: the attempt writer refuses, so the whole batch rolls back - no event,
        // no event_batch, no case_attempt and no outbox row (the metering row is written last).
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, status: "passed"),
                IngestQaRequests.Event(Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 1), sequence: 2, attemptNo: 101, status: "passed")));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        (await EventRowsAsync(world)).ShouldBe(0);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(0);
        (await world.CountAsync("case_attempt", $"org_id = '{world.Org:D}' and run_id = '{world.RunId:D}'")).ShouldBe(0);
        (await world.CountAsync("outbox", $"org_id = '{world.Org:D}'")).ShouldBe(0);
    }
}
