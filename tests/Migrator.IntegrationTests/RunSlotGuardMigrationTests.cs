using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// <c>0099_run_slot_transition_guards.sql</c> applied forward onto a database at the previous head: only that file is
/// applied, it adds both guard triggers, and a second run of the runner changes nothing.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class RunSlotGuardMigrationTests
{
    private const string GuardFile = "0099_run_slot_transition_guards.sql";

    private const string TriggerCount =
        "select count(*) from pg_trigger where not tgisinternal and tgname in "
        + "('run_guard_transition_trg', 'producer_slot_guard_trg')";

    private readonly PostgresMigratorFixture _fixture;

    public RunSlotGuardMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_AppliesOnThePreviousHead_AndAddsBothTriggers()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create();

        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, GuardFile) < 0)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await _fixture.ScalarAsync<long>(database, TriggerCount)).ShouldBe(0L);

        migrations.CopyReal(GuardFile);
        var applied = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([GuardFile], "nothing before it is re-applied");
        (await _fixture.ScalarAsync<long>(database, TriggerCount)).ShouldBe(2L);

        var again = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBeEmpty();
    }
}
