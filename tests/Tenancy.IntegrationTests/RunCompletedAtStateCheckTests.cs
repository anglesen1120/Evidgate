// `run.completed_at`'s state CHECK, made to fire: the two invoker guards answer first in production, so these cases
// disable both inside a transaction that rolls back and prove the constraint itself refuses an instant on a non-complete
// row. Each case owns a database, because disabling a trigger locks the run table against every concurrent writer of it.
using Shouldly;
using Xunit;

namespace Tenancy.IntegrationTests;

[Trait("Category", "Docker")]
public sealed class RunCompletedAtStateCheckTests
{
    private readonly PostgresTenancyFixture _fixture;

    public RunCompletedAtStateCheckTests(PostgresTenancyFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task StateCheck_RefusesAnUpdatedInstantOnANonCompleteRow_WhenTheGuardsAreDisabled()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);
        await world.SetRunStateAsync("collecting");

        await using (var scope = await OwnerScopeAsync(world))
        {
            await scope.ExecuteAsync("alter table public.run disable trigger run_completed_at_guard_trg");
            await scope.ExecuteAsync("alter table public.run disable trigger run_completed_at_insert_guard_trg");

            var refused = await scope.CaptureErrorAsync(
                $"update public.run set completed_at = now() where id = '{world.RunId:D}'");

            refused.SqlState.ShouldBe("23514");
            refused.ConstraintName.ShouldBe(
                "run_completed_at_state_check", "the CHECK binds the instant to the terminal state");
        }

        // The DDL above is transactional, so the rollback put both guards back: the column is immutable again.
        await using (var control = await OwnerScopeAsync(world))
        {
            var refused = await control.CaptureErrorAsync(
                $"update public.run set completed_at = now() where id = '{world.RunId:D}'");

            refused.MessageText.ShouldStartWith("run_completed_at_immutable");
        }
    }

    [Fact]
    public async Task StateCheck_RefusesAnInsertedInstantOnANonCompleteRow_WhenTheGuardsAreDisabled()
    {
        await using var owned = await _fixture.CreateDedicatedDatabaseAsync();
        await using var world = await StartAsync(owned.Name);

        await using (var scope = await OwnerScopeAsync(world))
        {
            await scope.ExecuteAsync("alter table public.run disable trigger run_completed_at_guard_trg");
            await scope.ExecuteAsync("alter table public.run disable trigger run_completed_at_insert_guard_trg");

            var refused = await scope.CaptureErrorAsync(
                RunCompletedAtTests.InsertRunWithInstantSql,
                ("id", Guid.CreateVersion7()),
                ("seed", world.RunId),
                ("state", "collecting"));

            refused.SqlState.ShouldBe("23514");
            refused.ConstraintName.ShouldBe(
                "run_completed_at_state_check", "the CHECK binds the instant to the terminal state");
        }

        // Positive control for the rollback: with the insert guard restored, the same insert is refused before the CHECK.
        await using (var control = await OwnerScopeAsync(world))
        {
            var refused = await control.CaptureErrorAsync(
                RunCompletedAtTests.InsertRunWithInstantSql,
                ("id", Guid.CreateVersion7()),
                ("seed", world.RunId),
                ("state", "collecting"));

            refused.MessageText.ShouldStartWith("run_completed_at_insert_refused");
        }
    }

    private Task<IngestTestWorld> StartAsync(string database) =>
        IngestTestWorld.StartAsync(_fixture, IngestTestWorld.Writer(), database: database);

    private Task<TenantScope> OwnerScopeAsync(IngestTestWorld world) =>
        TenantScope.BeginAsync(_fixture.OwnerConnectionString(world.Database));
}
