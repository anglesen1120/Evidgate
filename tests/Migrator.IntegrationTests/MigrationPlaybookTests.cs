using System.Globalization;
using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// AC7 / 01.4.1-T4.2: the expand/contract rename pair, applied through the
/// Migrator after the real migrations, with the old column path proven to work
/// while the new column already exists (docs/migrations-expand-contract.md).
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class MigrationPlaybookTests
{
    private static readonly Guid OrgA = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid OldWriterRow = Guid.Parse("aaaaaaaa-0000-7000-8000-0000000000a1");
    private static readonly Guid NewWriterRow = Guid.Parse("aaaaaaaa-0000-7000-8000-0000000000a2");

    private readonly PostgresMigratorFixture _fixture;

    public MigrationPlaybookTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Rename_Pair_Zero_Downtime()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var expandNumber = PostgresMigratorFixture.MaxRealNumber + 1;
        var contractNumber = expandNumber + 1;
        var expandFile = $"{Number(expandNumber)}_playbook_expand.sql";
        var contractFile = $"{Number(contractNumber)}_playbook_contract.sql";

        using var migrations = TempMigrations.Create();
        migrations.CopyReal([.. PostgresMigratorFixture.RealMigrationFileNames]);
        migrations.CopyFixture("playbook_expand.sql", expandNumber);

        // ---- expand only: the new column exists and the old code path still works.
        var expand = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        expand.ExitCode.ShouldBe(MigratorExitCodes.Success);
        expand.Result.AppliedNow.ShouldContain(expandFile);

        await _fixture.EnableApplicationLoginsAsync(database);

        await AsAppAsync(database, async (connection, transaction) =>
        {
            // The old code path: an INSERT that only knows `label`.
            (await ExecuteAsync(
                connection,
                transaction,
                $"insert into public.rls_probe (org_id, id, label) values ('{OrgA}', '{OldWriterRow}', 'old-writer')"))
                .ShouldBe(1);

            // Both columns are readable mid-sequence; nothing has been dropped yet.
            (await ScalarAsync<string?>(connection, transaction, "select label from public.rls_probe")).ShouldBe("old-writer");
            (await ScalarAsync<string?>(connection, transaction, "select title from public.rls_probe")).ShouldBeNull();
        });

        // ---- contract: backfill what the old writer left behind, then rename.
        migrations.CopyFixture("playbook_contract.sql", contractNumber);
        var contract = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        contract.ExitCode.ShouldBe(MigratorExitCodes.Success);
        contract.Result.AppliedNow.ShouldBe([contractFile]);

        // The old column is gone; the new one carries the old writer's value.
        (await _fixture.ScalarAsync<long>(
            database,
            "select count(*) from information_schema.columns where table_schema = 'public' and table_name = 'rls_probe' and column_name = 'label'"))
            .ShouldBe(0);
        (await _fixture.ScalarAsync<bool>(
            database,
            "select attnotnull from pg_attribute where attrelid = 'public.rls_probe'::regclass and attname = 'title'"))
            .ShouldBeTrue();

        await AsAppAsync(database, async (connection, transaction) =>
        {
            (await ScalarAsync<string?>(
                connection,
                transaction,
                $"select title from public.rls_probe where id = '{OldWriterRow}'")).ShouldBe("old-writer");

            (await ExecuteAsync(
                connection,
                transaction,
                $"insert into public.rls_probe (org_id, id, title) values ('{OrgA}', '{NewWriterRow}', 'new-writer')"))
                .ShouldBe(1);

            // The old code path is the one thing the contract file removes.
            var error = await CaptureErrorAsync(
                connection,
                transaction,
                $"insert into public.rls_probe (org_id, id, label) values ('{OrgA}', '{Guid.NewGuid()}', 'x')");
            error.SqlState.ShouldBe(PostgresErrorCodes.UndefinedColumn);
        });

        // The pair is journaled like any other migration, so the history stays honest.
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe([.. PostgresMigratorFixture.RealMigrationFileNames, expandFile, contractFile]);
    }

    /// <summary>Runs <paramref name="action"/> in one transaction as the app role with the tenant setting applied.</summary>
    private async Task AsAppAsync(string database, Func<NpgsqlConnection, NpgsqlTransaction, Task> action)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new NpgsqlConnection(_fixture.AppConnectionString(database));
        await connection.OpenAsync(token);
        await using var transaction = (NpgsqlTransaction)await connection.BeginTransactionAsync(token);

        await using (var setTenant = connection.CreateCommand())
        {
            setTenant.Transaction = transaction;
            setTenant.CommandText = "select set_config(@key, @value, true)";
            setTenant.Parameters.AddWithValue("key", "evidgate.org_id");
            setTenant.Parameters.AddWithValue("value", OrgA.ToString("D", CultureInfo.InvariantCulture));
            await setTenant.ExecuteScalarAsync(token);
        }

        await action(connection, transaction);
        await transaction.CommitAsync(token);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }

    private static async Task<PostgresException> CaptureErrorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        try
        {
            await ExecuteAsync(connection, transaction, sql);
        }
        catch (PostgresException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"statement was expected to fail but succeeded: {sql}");
    }

    private static string Number(int value) => value.ToString("0000", CultureInfo.InvariantCulture);
}
