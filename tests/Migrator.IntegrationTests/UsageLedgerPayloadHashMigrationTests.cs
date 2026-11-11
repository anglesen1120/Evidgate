// 0098's upgrade path on a fresh Testcontainers database: a ledger that already holds rows at 0097 (all with no hash)
// must keep every row and its NULL hash when the column and its CHECK arrive, and the CHECK must be live afterwards.
// S366 owns the rest of this project's migration surface; this file only adds 0098.
using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// <c>0098_usage_ledger_payload_hash.sql</c> applied forward onto a database that already holds ledger rows: the column
/// is added nullable, existing rows keep NULL (no backfill, no invented digest), and the CHECK is validated in the same
/// file so a later writer cannot store a non-canonical value.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class UsageLedgerPayloadHashMigrationTests
{
    private const string HashFile = "0098_usage_ledger_payload_hash.sql";

    private const string Constraint = "usage_ledger_entry_payload_hash_check";

    private readonly PostgresMigratorFixture _fixture;

    public UsageLedgerPayloadHashMigrationTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_AddsTheColumn_AndKeepsEveryLegacyRowAtNullHash()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();

        // Head at 0097: every real file but this one, applied by the real runner.
        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames.Where(name => name != HashFile)]);
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        var orgId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var firstLedgerId = Guid.CreateVersion7();
        var redatedLedgerId = Guid.CreateVersion7();

        await ExecuteAsync(
            database,
            """
            insert into public.organization (id, slug, name) values (@org, 'acme', 'Acme');
            insert into public.project (id, org_id, name, slug) values (@project, @org, 'Checkout', 'checkout');
            insert into public.usage_ledger_entry
                (id, org_id, project_id, metric, quantity, unit, period, billing_key, occurred_at)
            values
                (@first, @org, @project, 'results', 1.5, 'batch', '2026-10', 'batch:1:results', now()),
                (@redated, @org, @project, 'results', 2, 'batch', '2026-11', 'batch:2:results', now())
            """,
            [("org", orgId), ("project", projectId), ("first", firstLedgerId), ("redated", redatedLedgerId)],
            token);

        (await ScalarAsync(
            database,
            "select count(*) from information_schema.columns where table_schema = 'public' "
            + "and table_name = 'usage_ledger_entry' and column_name = 'payload_hash'",
            [],
            token)).ShouldBe(0L, "the column arrives with 0098, so this database is at head-minus-one");

        migrations.CopyReal(HashFile);
        var applied = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        applied.ExitCode.ShouldBe(MigratorExitCodes.Success, applied.Transcript);
        applied.Result.AppliedNow.ShouldContain(HashFile);
        applied.Result.AppliedNow.ShouldBe([HashFile], "nothing before it is re-applied");

        (await ScalarAsync(
            database,
            "select count(*) from public.usage_ledger_entry",
            [],
            token)).ShouldBe(2L, "every row written before the migration survives it");
        (await ScalarAsync(
            database,
            "select count(*) from public.usage_ledger_entry where payload_hash is null",
            [],
            token)).ShouldBe(2L, "a legacy row keeps a NULL hash: there is no backfill and no invented constant");
        (await ScalarAsync(
            database,
            "select count(*) from public.usage_ledger_entry where payload_hash is not null",
            [],
            token)).ShouldBe(0L);

        (await ScalarAsync(
            database,
            "select count(*) from information_schema.columns "
            + "where table_schema = 'public' and table_name = 'usage_ledger_entry' "
            + "and column_name = 'payload_hash' and is_nullable = 'YES' and data_type = 'text'",
            [],
            token)).ShouldBe(1L, "the new column is nullable text");

        (await ScalarAsync(
            database,
            "select count(*) from pg_catalog.pg_constraint "
            + $"where conname = '{Constraint}' and convalidated",
            [],
            token)).ShouldBe(1L, "the digest CHECK exists and is validated in the same file");

        // The CHECK is live for the rows this database will write from now on.
        var refused = await CaptureErrorAsync(
            database,
            "update public.usage_ledger_entry set payload_hash = 'sha256:abc' where id = @id",
            [("id", firstLedgerId)],
            token);
        refused.SqlState.ShouldBe("23514");
        refused.ConstraintName.ShouldBe(Constraint);
    }

    private async Task ExecuteAsync(
        string database,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<long> ScalarAsync(
        string database,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private async Task<PostgresException> CaptureErrorAsync(
        string database,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(_fixture.SuperuserConnectionString(database));
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
    }
}
