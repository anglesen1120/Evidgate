using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// <c>0100_identity_shared_keys.sql</c> applied forward onto a database at the previous head: only that file is
/// applied, it adds both tables under forced row level security, and a second run of the runner changes nothing.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class SharedKeysMigrationTests
{
    private const string KeysFile = "0100_identity_shared_keys.sql";

    private const string ForcedTableCount =
        "select count(*) from pg_class where relkind = 'r' and relnamespace = 'public'::regnamespace "
        + "and relname in ('data_protection_key', 'login_state_replay') and relforcerowsecurity";

    private readonly PostgresMigratorFixture _fixture;

    public SharedKeysMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_AppliesOnThePreviousHead_AndAddsBothTables()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create();

        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => string.CompareOrdinal(name, KeysFile) < 0)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await _fixture.ScalarAsync<long>(database, ForcedTableCount)).ShouldBe(0L);

        migrations.CopyReal(KeysFile);
        var applied = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([KeysFile], "nothing before it is re-applied");
        (await _fixture.ScalarAsync<long>(database, ForcedTableCount)).ShouldBe(2L);

        var again = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBeEmpty();
    }
}
