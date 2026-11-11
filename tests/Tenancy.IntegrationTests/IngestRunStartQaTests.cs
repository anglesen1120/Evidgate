// 04.1.1-T4 independent QA (S43): negative, boundary and exploratory cases for the run-start path of
// POST /v1/runs/{r}/events (queued -> running, refusal rollback, cross-tenant and cross-project refusal,
// duplicate-only resend, concurrent first batches). Written by an agent that did not write the handler.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class IngestRunStartQaTests : IClassFixture<TenancyHostPool>
{
    private readonly PostgresTenancyFixture _fixture;

    private readonly TenancyHostPool _hosts;

    public IngestRunStartQaTests(PostgresTenancyFixture fixture, TenancyHostPool hosts) =>
        (_fixture, _hosts) = (fixture, hosts);

    private static Task<long> StateChangedAuditRowsAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync("audit_log", $"object_id = '{runId:D}' and action = 'run.state_changed'");

    private static Task<long> StateChangedOutboxRowsAsync(IngestTestWorld world, Guid runId) =>
        world.CountAsync(
            "outbox",
            $"aggregate_id = '{runId:D}' and task_type = 'orchestrator.run.state_changed'");

    private static Task<long> UsageOutboxRowsAsync(IngestTestWorld world) =>
        world.CountAsync("outbox", $"org_id = '{world.Org:D}' and task_type = 'usage.batch_accepted'");

    private static Task<long> OwnOrgStateChangedOutboxRowsAsync(IngestTestWorld world) =>
        world.CountAsync(
            "outbox",
            $"org_id = '{world.Org:D}' and task_type = 'orchestrator.run.state_changed'");

    private static Task<long> OwnOrgStateChangedAuditRowsAsync(IngestTestWorld world) =>
        world.CountAsync(
            "audit_log",
            $"org_id = '{world.Org:D}' and action = 'run.state_changed'");

    private static Task<string> RunStateAsync(IngestTestWorld world, Guid runId) =>
        world.ScalarAsync<string>($"select state from public.run where id = '{runId:D}'");

    // ------------------------------------------------------- negative: refusals after the transition

    [Fact]
    public async Task queued_run_unknown_producer_refusal_rolls_back_transition()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await world.SetRunStateAsync("queued");

        // shard-9 has no producer slot: the refusal (unknown_producer) lands after the transition step.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, producerId: "shard-9")));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("unknown_producer");
        (await RunStateAsync(world, world.RunId)).ShouldBe("queued");
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(0);
        (await StateChangedAuditRowsAsync(world, world.RunId)).ShouldBe(0);
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(0);
        (await world.CountAsync("event_batch", $"run_id = '{world.RunId:D}'")).ShouldBe(0);
    }

    [Fact]
    public async Task queued_run_event_invalid_refusal_never_reaches_transition()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await world.SetRunStateAsync("queued");

        // An event type outside the closed set is refused at parse, before the run-start step.
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1, type: "bogus.type")));

        answer.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("event_invalid");
        (await RunStateAsync(world, world.RunId)).ShouldBe("queued");
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(0);
        (await StateChangedAuditRowsAsync(world, world.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task running_run_sequence_reused_refusal_keeps_single_transition()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await world.SetRunStateAsync("queued");

        var first = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);

        // Same producer, same sequence, different event id: refused after the run is already running.
        var reused = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        reused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, reused.ErrorMessage);
        reused.ErrorCode.ShouldBe("sequence_reused");
        (await RunStateAsync(world, world.RunId)).ShouldBe("running");
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(1);
        (await StateChangedAuditRowsAsync(world, world.RunId)).ShouldBe(1);
    }

    // ------------------------------------------------------- boundary: states the transition must skip

    [Fact]
    public async Task running_run_batch_never_retransitions()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await world.SetRunStateAsync("running");

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        (await RunStateAsync(world, world.RunId)).ShouldBe("running");
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(0);
        (await StateChangedAuditRowsAsync(world, world.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task created_run_is_not_transitioned_by_ingest()
    {
        // D5: only `queued` drives the transition. A `created` run stays `created` and is never a 500.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);

        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), IngestQaRequests.KeyOf(world, 0), sequence: 1)));

        answer.Status.ShouldNotBe(HttpStatusCode.InternalServerError, answer.ErrorMessage);
        (await RunStateAsync(world, world.RunId)).ShouldBe("created");
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(0);
        (await StateChangedAuditRowsAsync(world, world.RunId)).ShouldBe(0);
    }

    [Fact]
    public async Task duplicate_only_resend_emits_no_second_usage_row()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        await world.SetRunStateAsync("queued");

        var eventId = Guid.CreateVersion7();
        var body = IngestQaRequests.Batch(IngestQaRequests.Event(
            eventId, IngestQaRequests.KeyOf(world, 0), sequence: 1));

        var first = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        first.Status.ShouldBe(HttpStatusCode.OK, first.ErrorMessage);
        (await UsageOutboxRowsAsync(world)).ShouldBe(1);

        // Identical resend: every event is already on file, so the batch is duplicate-only.
        var resend = await IngestQaRequests.PostEventsAsync(world.Client, world.RunId, body);
        resend.Status.ShouldBe(HttpStatusCode.OK, resend.ErrorMessage);

        (await UsageOutboxRowsAsync(world)).ShouldBe(1);
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(1);
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(1);
    }

    // ------------------------------------------------------- exploratory: tenant and project boundaries

    [Fact]
    public async Task foreign_org_queued_run_is_not_transitioned_by_this_tenant()
    {
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        var (rivalOrg, _) = await world.CreateOtherOrgAsync(Unique.Slug("rival"));
        var foreign = await world.SeedOtherRunAsync(rivalOrg);
        await world.Fixture.ExecuteAsync(
            world.Database, $"update public.run set state = 'queued' where id = '{foreign.RunId:D}'");

        // The unlocked RLS-scoped state read returns null for a foreign run: the transition is skipped and the
        // scope read answers 404 (the run is not visible to this tenant).
        var answer = await IngestQaRequests.PostEventsAsync(
            world.Client,
            foreign.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), foreign.Executions[0].ExecutionKey, sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.NotFound, answer.ErrorMessage);
        (await RunStateAsync(world, foreign.RunId)).ShouldBe("queued");
        (await StateChangedOutboxRowsAsync(world, foreign.RunId)).ShouldBe(0);
        (await StateChangedAuditRowsAsync(world, foreign.RunId)).ShouldBe(0);
        (await OwnOrgStateChangedOutboxRowsAsync(world)).ShouldBe(0);
        (await OwnOrgStateChangedAuditRowsAsync(world)).ShouldBe(0);
    }

    [Fact]
    public async Task cross_project_ci_token_refused_and_queued_run_stays_queued()
    {
        // The transition runs at step (b1), before the project-scope check. The refusal must roll it back.
        await using var world = await IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), hosts: _hosts);
        using var setup = world.Api.CreateClient();
        var otherProject = await RegistryApi.CreateProjectAsync(setup, world.User, world.Org, "other");
        var otherRun = await world.SeedRunInProjectAsync(otherProject);
        await world.Fixture.ExecuteAsync(
            world.Database, $"update public.run set state = 'queued' where id = '{otherRun.RunId:D}'");

        // The world's principal host already holds the slot for this test, so this second one comes back private.
        await using var scoped = _hosts.WithPrincipal(
            world.Database,
            world.Org,
            world.Project,
            IngestTestWorld.Writer(projectScope: world.Project));
        using var scopedClient = scoped.CreateClient();

        var answer = await IngestQaRequests.PostEventsAsync(
            scopedClient,
            otherRun.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(), otherRun.Executions[0].ExecutionKey, sequence: 1)));

        answer.Status.ShouldBe(HttpStatusCode.Forbidden, answer.ErrorMessage);
        answer.ErrorCode.ShouldBe("project_scope_mismatch");
        (await RunStateAsync(world, otherRun.RunId)).ShouldBe("queued");
        (await StateChangedOutboxRowsAsync(world, otherRun.RunId)).ShouldBe(0);
        (await StateChangedAuditRowsAsync(world, otherRun.RunId)).ShouldBe(0);
        (await world.CountAsync("event", $"run_id = '{otherRun.RunId:D}'")).ShouldBe(0);

        // The refusal rolls the whole transaction back: not even the caller's own org keeps a trigger or audit row.
        (await OwnOrgStateChangedOutboxRowsAsync(world)).ShouldBe(0);
        (await OwnOrgStateChangedAuditRowsAsync(world)).ShouldBe(0);
    }

    // ------------------------------------------------------- boundary: concurrency

    [Fact]
    public async Task eight_concurrent_first_batches_apply_one_transition()
    {
        const int racers = 8;
        await using var world = await IngestTestWorld.StartAsync(
            _fixture, IngestTestWorld.Writer(), planned: racers, slots: racers, hosts: _hosts);
        await world.SetRunStateAsync("queued");

        // One producer per racer (shard-1..shard-8, each with its own slot and sequence 1), so every batch is
        // accepted and the only shared point is the run row.
        var posts = Enumerable.Range(0, racers).Select(i => IngestQaRequests.PostEventsAsync(
            world.Client,
            world.RunId,
            IngestQaRequests.Batch(IngestQaRequests.Event(
                Guid.CreateVersion7(),
                IngestQaRequests.KeyOf(world, i),
                sequence: 1,
                producerId: $"shard-{i + 1}"))));

        var answers = await Task.WhenAll(posts);

        // A deadlock (40P01) or an unhandled fault would surface as a 500 on one of them.
        foreach (var answer in answers)
        {
            answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        }

        (await RunStateAsync(world, world.RunId)).ShouldBe("running");
        (await StateChangedOutboxRowsAsync(world, world.RunId)).ShouldBe(1);
        (await StateChangedAuditRowsAsync(world, world.RunId)).ShouldBe(1);
        (await world.CountAsync("event", $"run_id = '{world.RunId:D}'")).ShouldBe(racers);
        (await UsageOutboxRowsAsync(world)).ShouldBe(racers);
    }
}
