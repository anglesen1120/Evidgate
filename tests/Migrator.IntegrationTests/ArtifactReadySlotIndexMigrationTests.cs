using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class ArtifactReadySlotIndexMigrationTests
{
    private const string Migration = "0113_artifact_ready_slot_index.sql";
    private const string Index = "public.artifact_run_slot_ready_idx";
    private readonly PostgresMigratorFixture _fixture;

    public ArtifactReadySlotIndexMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_PreviousHead_AddsReadyOnlyIndexInSlotOrder()
    {
        var database = await PreviousHeadAsync();
        (await _fixture.ScalarAsync<bool>(database, $"select to_regclass('{Index}') is null")).ShouldBeTrue();

        var applied = RunHead(database);
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([Migration]);
        await AssertShapeAsync(database);
    }

    [Fact]
    public async Task Migration_JournalAndDirectReplay_PreserveTheSameIndex()
    {
        var database = await PreviousHeadAsync();
        var applied = RunHead(database);
        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldBe([Migration]);
        var originalOid = await _fixture.ScalarAsync<long>(database, $"select '{Index}'::regclass::oid::bigint");

        var again = RunHead(database);
        again.ExitCode.ShouldBe(MigratorExitCodes.Success, again.Transcript);
        again.Result.AppliedNow.ShouldBeEmpty();
        await using var connection = new NpgsqlConnection(_fixture.OwnerConnectionString(database));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var replay = connection.CreateCommand();
        replay.Transaction = transaction;
        replay.CommandText = await File.ReadAllTextAsync(
            Path.Combine(PostgresMigratorFixture.RepoMigrationsPath, Migration), TestContext.Current.CancellationToken);
        await replay.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        (await _fixture.ScalarAsync<long>(database, $"select '{Index}'::regclass::oid::bigint")).ShouldBe(originalOid);
        await AssertShapeAsync(database);
    }

    private async Task AssertShapeAsync(string database)
    {
        (await _fixture.ScalarAsync<long>(database,
            "select count(*) from pg_indexes where schemaname = 'public' and indexname = 'artifact_run_slot_ready_idx'"))
            .ShouldBe(1L);
        (await _fixture.ScalarAsync<string>(database,
            $"select pg_get_indexdef('{Index}'::regclass)"))
            .ShouldBe("CREATE INDEX artifact_run_slot_ready_idx ON public.artifact USING btree "
                + "(org_id, run_id, execution_key, attempt_no) WHERE (state = 'ready'::text)");
        (await _fixture.ScalarAsync<bool>(database,
            $"select indisvalid and indisready and not indisunique and indnkeyatts = 4 and indnatts = 4 "
            + $"and indrelid = 'public.artifact'::regclass from pg_index where indexrelid = '{Index}'::regclass"))
            .ShouldBeTrue();
        (await _fixture.ScalarAsync<string>(database,
            $"select pg_get_expr(indpred, indrelid) from pg_index where indexrelid = '{Index}'::regclass"))
            .ShouldBe("(state = 'ready'::text)");
    }

    private async Task<string> PreviousHeadAsync()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames
            .Where(name => string.CompareOrdinal(name, Migration) < 0)]);
        var outcome = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, outcome.Transcript);
        return database;
    }

    private RunOutcome RunHead(string database)
    {
        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames
            .Where(name => string.CompareOrdinal(name, Migration) <= 0)]);
        return MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
    }
}
