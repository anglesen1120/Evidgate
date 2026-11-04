// 05.4.1-T1 through the real host and a real PostgreSQL: the durable cancel request behind
// POST /v1/runs/{r}/cancel — one request row per run, the immediate canceled transition through 05.3.1's
// guard, the terminal no-op, and the isolation answers the story's security row names.
#pragma warning disable CA1861
using System.Net;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

/// <summary>
/// 05.4.1-T1 (AC1–AC4, AC6, AC8): <c>POST /v1/runs/{r}/cancel</c> records one durable
/// <c>run_cancel_request</c> row per run and moves a non-terminal run to <c>canceled</c> through the 05.3.1
/// transition guard. Because the row is unique per run and the state change goes through that guard, a
/// repeated, late or racing cancel can produce neither a second row nor a resurrected terminal run.
/// </summary>
[Trait("Category", "Docker")]
public sealed partial class CancelRunEndpointTests
{
    private readonly PostgresTenancyFixture _fixture;

    public CancelRunEndpointTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    /// <summary>
    /// AC1: a <c>queued</c> run accepts the cancel with an empty <c>202</c>, stores exactly one
    /// <c>run_cancel_request</c> row (requester, reason, ordered deadlines, the local <c>requested</c> state) and
    /// moves the run to <c>canceled</c> — the transition's own outbox row and the caller-credited audit entry
    /// exist in the same commit.
    /// </summary>
    [Fact]
    public async Task CancelRun_Accepted_202_RequestRowPersisted()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture);
        var runId = await CancelRunTestData.CreateQueuedRunAsync(world);

        var answer = await CancelRunTestData.CancelAsync(world, runId, reason: "stuck shard on checkout");

        answer.Status.ShouldBe(HttpStatusCode.Accepted, answer.ErrorMessage);
        answer.Body.EnumerateObject().ShouldBeEmpty("the route answers 202 with no body (contracts/openapi.yaml)");

        (await CancelRunTestData.StateAsync(_fixture, world, runId)).ShouldBe("canceled");
        (await CancelRunTestData.VersionAsync(_fixture, world, runId))
            .ShouldBe(1, "queued -> canceled is one applied transition");
        (await CancelRunTestData.OutboxTargetsAsync(_fixture, world, runId))
            .ShouldBe(["canceled"], "the cancel is the transition the outbox reports");

        (await CancelRunTestData.RowCountAsync(_fixture, world, runId)).ShouldBe(1);

        var row = await CancelRunTestData.RowAsync(_fixture, world, runId);
        row.RequestedBy.ShouldBe(world.User, "requested_by is the resolved caller, never a body field");
        row.Reason.ShouldBe("stuck shard on checkout");
        row.State.ShouldBe("requested", "the execution stop has its own sub-state machine (plan §6)");
        row.SoftStopDeadlineAt.ShouldBeGreaterThanOrEqualTo(row.RequestedAt);
        row.KillAt.ShouldBeGreaterThanOrEqualTo(
            row.SoftStopDeadlineAt, "the kill never precedes the soft-stop deadline");
        row.RequestedAt.ShouldBeGreaterThan(
            DateTimeOffset.UtcNow.AddMinutes(-5), "requested_at is the server's clock, not a constant");

        var actor = await _fixture.ScalarAsync<string>(
            world.Database,
            $"select actor_type || ':' || actor_id from public.audit_log "
            + $"where org_id = '{world.Org}' and action = '{CancelRunTestData.StateChangedAction}' "
            + $"and object_type = 'run' and object_id = '{runId}'");
        actor.ShouldBe($"user:{world.User:D}", "a cancel is a caller-driven edge, credited to its caller (05.3.1 AC5)");
    }
}
