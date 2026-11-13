using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

public sealed partial class RunStateEventTests
{
    /// <summary>
    /// The table's own policy, read as the app role: the owning org sees its history, a second org's context and an
    /// unset context see none of it — the guard travels with the table, not with the API's run lookup.
    /// </summary>
    [Fact]
    public async Task TablePolicy_ScopesRowsToTheCallingOrgAndYieldsNoneWithoutOne()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await using var rival = await RunCreationTests.StartAsync(_fixture, slug: "history-rls-rival", hosts: _hosts);
        var runId = await RunningRunAsync(world);

        await using (var own = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), world.Org))
        {
            (await own.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(2, "the owning org reads its run's marker and transition");
        }

        await using (var foreign = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database), rival.Org))
        {
            (await foreign.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(0, "a second org's context reads nothing, even when it names the run id");
            (await foreign.ScalarAsync<long>("select count(*) from public.run_state_event")).ShouldBe(0);
        }

        await using (var unset = await TenantScope.BeginAsync(_fixture.AppConnectionString(world.Database)))
        {
            (await unset.ScalarAsync<bool>("select evidgate.current_org_id() is null")).ShouldBeTrue(
                "the helper answers NULL, never an error, when the setting is missing");
            (await unset.ScalarAsync<long>($"select count(*) from public.run_state_event where run_id = '{runId:D}'"))
                .ShouldBe(0, "no tenant context is no rows");
        }

        (await _fixture.ScalarAsync<long>(
            world.Database,
            $"select count(*) from public.run_state_event where run_id = '{runId:D}'")).ShouldBe(
            2,
            "the rows exist, so the zero counts above are the policy's");
    }

    /// <summary>
    /// The tenant key is part of the reference: a row whose org is not its run's org reaches no run, so even the owner
    /// seam cannot attach history to another tenant's run.
    /// </summary>
    [Fact]
    public async Task OwnerSeamInsert_WithARunOfAnotherOrg_IsRefusedByTheCompositeForeignKey()
    {
        await using var world = await RunCreationTests.StartAsync(_fixture, hosts: _hosts);
        await using var rival = await RunCreationTests.StartAsync(_fixture, slug: "history-fk-rival", hosts: _hosts);
        var runId = await CreateQueuedRunAsync(world);

        await using var owner = await TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
        var error = await owner.CaptureErrorAsync($"""
            insert into public.run_state_event (id, org_id, run_id, seq, from_state, to_state)
            values (uuidv7(), '{rival.Org:D}', '{runId:D}', 2, 'queued', 'running')
            """);

        error.SqlState.ShouldBe("23503", "the run exists, but not for this org, so the composite key reaches no run");
        error.ConstraintName.ShouldBe("run_state_event_run_fk");
        (await RowCountAsync(world, runId)).ShouldBe(1, "the refusal leaves the history as the trigger wrote it");
    }
}
