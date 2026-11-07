// The migration's marker rule against a real PostgreSQL: rows are not backfilled, so a run created before the table
// existed has no creation anchor, and the trigger cannot invent the state it was created in. The first row a later
// transition appends to such a run is sequence 1 with a from state, which is exactly what the marker rules out — the
// run's history stays unknown and the served member is null, never an empty array claiming it never transitioned.
#pragma warning disable CA1861
using System.Net;
using System.Text.Json;
using Evidgate.Modules.Orchestrator.Contracts;
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RunStateEventTests
{
    /// <summary>
    /// A run without its creation row — the shape of one created before the table existed — keeps an unknown history
    /// after it transitions: the appended row is sequence 1 with a non-null from state, the creation marker stays
    /// absent rather than fabricated, and the read answers null for it.
    /// </summary>
    [Fact]
    public async Task RunWithoutItsCreationRow_StaysUnknownAfterATransition()
    {
        // Dedicated: re-enabling the append-only guard is DDL and takes a table-wide lock the suite must not share.
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await RunCreationTests.StartAsync(_fixture, database: owned.Name);
        var runId = await CreateQueuedRunAsync(world);
        (await HasCreationMarkerAsync(world, runId)).ShouldBeTrue();

        // The shape of a pre-migration run: no row at all. Removing them is what the erasure handover does — the
        // guard is disabled for the one statement and restored before the commit, so the table is append-only again.
        await using (var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database)))
        {
            await owner.ExecuteAsync("alter table public.run_state_event disable trigger run_state_event_immutable");
            (await owner.ExecuteAsync($"delete from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(1);
            await owner.ExecuteAsync("alter table public.run_state_event enable trigger run_state_event_immutable");
            await owner.CommitAsync();
        }

        (await HasCreationMarkerAsync(world, runId)).ShouldBeFalse();

        var step = await TransitionAsync(world, runId, "running");
        step.IsSuccess.ShouldBeTrue(step.Error?.Message);
        step.Value.Outcome.ShouldBe(TransitionOutcome.Applied);

        (await HistoryAsync(world, runId)).ShouldBe(
            "queued->running",
            "the appended row names the state it left, so the marker is absent, not invented");
        (await SequencesAsync(world, runId)).ShouldBe([1]);
        (await HasCreationMarkerAsync(world, runId)).ShouldBeFalse("a run without its marker keeps an unknown history");

        var answer = await GetRunAsync(world, runId);
        answer.Status.ShouldBe(HttpStatusCode.OK, answer.ErrorMessage);
        answer.Body.GetProperty("state_history").ValueKind.ShouldBe(
            JsonValueKind.Null,
            "unknown, never an empty array: the run has rows but none that anchors its creation");
    }

    /// <summary>Whether the run carries the creation anchor: the sequence 1 row that has no from state.</summary>
    private Task<bool> HasCreationMarkerAsync(RunCreationTests.World world, Guid runId) =>
        _fixture.ScalarAsync<bool>(
            world.Database,
            $"select exists (select 1 from public.run_state_event where run_id = '{runId:D}' "
            + "and seq = 1 and from_state is null)");

    /// <summary>One <c>GET /v1/runs/{r}</c> through the real host, as the world's admin caller.</summary>
    private static Task<ApiAnswer> GetRunAsync(RunCreationTests.World world, Guid runId) =>
        world.Client.SendAsync(HttpMethod.Get, $"/v1/runs/{runId:D}", world.Admin);
}
