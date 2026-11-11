using System.Diagnostics;
using System.Globalization;
using Evidgate.Migrator;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The server-side timeouts a script runs under: <c>lock_timeout</c> and
/// <c>statement_timeout</c> are sent as connection startup options, a
/// timeout fails the run with its own exit code without journalling anything,
/// and a script may override the value for its own transaction.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class MigratorTimeoutTests
{
    private const int SessionStatementTimeoutMs = 500;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WaitBeyondSessionTimeout = TimeSpan.FromSeconds(1.5);

    private readonly PostgresMigratorFixture _fixture;

    public MigratorTimeoutTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A script that waits for a table lock another session holds fails within
    /// the configured <c>lock_timeout</c> (exit 5), rolls back and is not
    /// journalled; the same directory applies once the blocker lets go.
    /// </summary>
    [Fact]
    public async Task Lock_Timeout_Fails_Rolls_Back_And_Applies_After_The_Blocker_Releases()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_table.sql", "create table if not exists public.lock_timeout_probe (id int primary key);");
        var owner = _fixture.OwnerConnectionString(database);
        MigratorHarness.Run(migrations.Path, owner).ExitCode.ShouldBe(MigratorExitCodes.Success);

        migrations.Add("0002_probe_column.sql", "alter table public.lock_timeout_probe add column note text;");

        await using var blocker = await _fixture.OpenSuperuserAsync(database);
        await using var transaction = (NpgsqlTransaction)await blocker.BeginTransactionAsync(token);
        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = "lock table public.lock_timeout_probe in access exclusive mode";
            await hold.ExecuteNonQueryAsync(token);
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = MigratorHarness.Run(
            migrations.Path,
            owner,
            MigrationCommand.Apply,
            lockTimeoutSeconds: 1);
        stopwatch.Stop();

        blocked.ExitCode.ShouldBe(MigratorExitCodes.LockTimeout);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1) + Bound);
        blocked.Transcript.ShouldContain("0002_probe_column.sql");
        blocked.Transcript.ShouldContain("lock_timeout");
        blocked.Messages.ShouldContain(line =>
            line.StartsWith(
                "0002_probe_column.sql hit lock_timeout (runner setting 1 s; the script may set its own)",
                StringComparison.Ordinal));
        blocked.Messages.ShouldContain("the failed script's transaction was rolled back and it was not journalled");

        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_table.sql"]);
        (await ColumnExistsAsync(database, "lock_timeout_probe", "note")).ShouldBeFalse();

        await transaction.RollbackAsync(token);

        var retried = MigratorHarness.Run(migrations.Path, owner);

        retried.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", retried.Messages));
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe(["0001_probe_table.sql", "0002_probe_column.sql"]);
    }

    /// <summary>
    /// A statement that outlasts <c>statement_timeout</c> exits with its own code
    /// (6), not the generic migration failure, and the file is rolled back whole.
    /// </summary>
    [Fact]
    public async Task Statement_Timeout_Fails_And_Is_Not_Journalled()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create().Add(
            "0001_probe_sleep.sql",
            "create table if not exists public.statement_timeout_probe (id int primary key);\nselect pg_sleep(5);");

        var stopwatch = Stopwatch.StartNew();
        var outcome = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Apply,
            statementTimeoutSeconds: 1);
        stopwatch.Stop();

        outcome.ExitCode.ShouldBe(MigratorExitCodes.StatementTimeout);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1) + Bound);
        outcome.Transcript.ShouldContain("0001_probe_sleep.sql");
        outcome.Transcript.ShouldContain("statement_timeout");
        outcome.Messages.ShouldContain(line =>
            line.StartsWith(
                "0001_probe_sleep.sql hit statement_timeout (runner setting 1 s; the script may set its own)",
                StringComparison.Ordinal));
        outcome.Messages.ShouldContain("the failed script's transaction was rolled back and it was not journalled");

        (await _fixture.ReadJournalAsync(database)).ShouldBeEmpty();
        (await _fixture.TableExistsAsync(database, "statement_timeout_probe")).ShouldBeFalse();
    }

    /// <summary>
    /// A statement timeout journals the earlier files and leaves the failed one
    /// pending; a rerun with a larger limit applies it without replaying 0001.
    /// </summary>
    [Fact]
    public async Task Statement_Timeout_Leaves_The_File_Pending_For_A_Rerun()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_table.sql", "create table if not exists public.statement_timeout_first (id int primary key);")
            .Add("0002_probe_sleep.sql", "select pg_sleep(2);");
        var owner = _fixture.OwnerConnectionString(database);

        var timedOut = MigratorHarness.Run(
            migrations.Path,
            owner,
            MigrationCommand.Apply,
            statementTimeoutSeconds: 1);

        timedOut.ExitCode.ShouldBe(MigratorExitCodes.StatementTimeout);
        timedOut.Messages.ShouldContain(line =>
            line.StartsWith(
                "0002_probe_sleep.sql hit statement_timeout (runner setting 1 s; the script may set its own)",
                StringComparison.Ordinal));
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_table.sql"]);

        var retried = MigratorHarness.Run(
            migrations.Path,
            owner,
            MigrationCommand.Apply,
            statementTimeoutSeconds: 30);

        retried.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", retried.Messages));
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe(["0001_probe_table.sql", "0002_probe_sleep.sql"]);
    }

    /// <summary>
    /// The runner's values reach every script, and a script that sets its own
    /// <c>lock_timeout</c> keeps it for its transaction.
    /// </summary>
    [Fact]
    public async Task Scripts_Capture_The_Configured_Timeouts_And_May_Override_Lock_Timeout()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add(
                "0001_probe_override.sql",
                "set local lock_timeout = '7s';\n" +
                "create table public.timeout_capture_override as select current_setting('lock_timeout') as lock_timeout;")
            .Add(
                "0002_probe_runner.sql",
                "create table public.timeout_capture_runner as select current_setting('lock_timeout') as lock_timeout, " +
                "current_setting('statement_timeout') as statement_timeout;");

        var outcome = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Apply,
            lockTimeoutSeconds: 1,
            statementTimeoutSeconds: 2);

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", outcome.Messages));
        (await _fixture.ScalarAsync<string>(
            database,
            "select lock_timeout from public.timeout_capture_override")).ShouldBe("7s");
        (await _fixture.ScalarAsync<string>(
            database,
            "select lock_timeout from public.timeout_capture_runner")).ShouldBe("1s");
        (await _fixture.ScalarAsync<string>(
            database,
            "select statement_timeout from public.timeout_capture_runner")).ShouldBe("2s");
    }

    /// <summary>
    /// A startup <c>options</c> value already in the operator's connection string
    /// still reaches the scripts, and the configured limits win over a duplicate key.
    /// </summary>
    [Fact]
    public async Task Operator_Startup_Options_Survive_And_The_Configured_Limits_Win()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create().Add(
            "0001_probe_options.sql",
            "create table public.operator_options_probe as select current_setting('application_name') as app, " +
            "current_setting('lock_timeout') as lock_timeout;");
        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.OwnerConnectionString(database))
        {
            Options = "-c application_name=operator_probe -c lock_timeout=1ms",
        }.ConnectionString;

        var outcome = MigratorHarness.Run(migrations.Path, connectionString, lockTimeoutSeconds: 3);

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", outcome.Messages));
        (await _fixture.ScalarAsync<string>(database, "select app from public.operator_options_probe")).ShouldBe("operator_probe");
        (await _fixture.ScalarAsync<string>(database, "select lock_timeout from public.operator_options_probe")).ShouldBe("3s");
    }

    /// <summary>
    /// A script may shorten <c>lock_timeout</c> for its own transaction: the
    /// script's one second fires even though the runner setting is thirty.
    /// </summary>
    [Fact]
    public async Task Script_Own_Lock_Timeout_Fires_Under_A_Larger_Runner_Value()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_table.sql", "create table if not exists public.lock_override_probe (id int primary key);");
        var owner = _fixture.OwnerConnectionString(database);
        MigratorHarness.Run(migrations.Path, owner).ExitCode.ShouldBe(MigratorExitCodes.Success);

        migrations.Add(
            "0002_probe_column.sql",
            "set local lock_timeout = '1s';\nalter table public.lock_override_probe add column note text;");

        await using var blocker = await _fixture.OpenSuperuserAsync(database);
        await using var transaction = (NpgsqlTransaction)await blocker.BeginTransactionAsync(token);
        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = "lock table public.lock_override_probe in access exclusive mode";
            await hold.ExecuteNonQueryAsync(token);
        }

        var stopwatch = Stopwatch.StartNew();
        var blocked = MigratorHarness.Run(
            migrations.Path,
            owner,
            MigrationCommand.Apply,
            lockTimeoutSeconds: 30);
        stopwatch.Stop();

        blocked.ExitCode.ShouldBe(MigratorExitCodes.LockTimeout);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1) + Bound);
        blocked.Transcript.ShouldContain("lock_timeout");
        blocked.Transcript.ShouldContain("runner setting 30 s");
        blocked.Transcript.ShouldNotContain("of 30 s");
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_table.sql"]);
        (await ColumnExistsAsync(database, "lock_override_probe", "note")).ShouldBeFalse();

        await transaction.RollbackAsync(token);

        var retried = MigratorHarness.Run(
            migrations.Path,
            owner,
            MigrationCommand.Apply,
            lockTimeoutSeconds: 30);

        retried.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", retried.Messages));
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe(["0001_probe_table.sql", "0002_probe_column.sql"]);
        (await ColumnExistsAsync(database, "lock_override_probe", "note")).ShouldBeTrue();
    }

    /// <summary>
    /// A file whose first statement succeeded but whose later statement cannot
    /// take its lock rolls back every statement in the file.
    /// </summary>
    [Fact]
    public async Task Multi_Statement_Lock_Timeout_Rolls_Back_The_Whole_File()
    {
        var token = TestContext.Current.CancellationToken;
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_table.sql", "create table if not exists public.multi_statement_held (id int primary key);");
        var owner = _fixture.OwnerConnectionString(database);
        MigratorHarness.Run(migrations.Path, owner).ExitCode.ShouldBe(MigratorExitCodes.Success);

        migrations.Add(
            "0002_probe_two_statements.sql",
            "create table if not exists public.multi_statement_created (id int primary key);\n" +
            "alter table public.multi_statement_held add column note text;");

        await using var blocker = await _fixture.OpenSuperuserAsync(database);
        await using var transaction = (NpgsqlTransaction)await blocker.BeginTransactionAsync(token);
        await using (var hold = blocker.CreateCommand())
        {
            hold.Transaction = transaction;
            hold.CommandText = "lock table public.multi_statement_held in access exclusive mode";
            await hold.ExecuteNonQueryAsync(token);
        }

        var blocked = MigratorHarness.Run(
            migrations.Path,
            owner,
            MigrationCommand.Apply,
            lockTimeoutSeconds: 1);

        blocked.ExitCode.ShouldBe(MigratorExitCodes.LockTimeout);
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_table.sql"]);
        (await _fixture.TableExistsAsync(database, "multi_statement_created")).ShouldBeFalse();
        (await ColumnExistsAsync(database, "multi_statement_held", "note")).ShouldBeFalse();

        await transaction.RollbackAsync(token);

        var retried = MigratorHarness.Run(migrations.Path, owner);

        retried.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", retried.Messages));
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe(["0001_probe_table.sql", "0002_probe_two_statements.sql"]);
        (await _fixture.TableExistsAsync(database, "multi_statement_created")).ShouldBeTrue();
        (await ColumnExistsAsync(database, "multi_statement_held", "note")).ShouldBeTrue();
    }

    /// <summary>An explicit <c>0</c> disables both server timeouts, not falls back to a default.</summary>
    [Fact]
    public async Task Explicit_Zero_Disables_Both_Server_Timeouts()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create().Add(
            "0001_probe_capture.sql",
            "create table public.timeout_capture_zero as select current_setting('lock_timeout') as lock_timeout, " +
            "current_setting('statement_timeout') as statement_timeout;");

        var outcome = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Apply,
            lockTimeoutSeconds: 0,
            statementTimeoutSeconds: 0);

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", outcome.Messages));
        (await _fixture.ScalarAsync<string>(
            database,
            "select lock_timeout from public.timeout_capture_zero")).ShouldBe("0");
        (await _fixture.ScalarAsync<string>(
            database,
            "select statement_timeout from public.timeout_capture_zero")).ShouldBe("0");
    }

    /// <summary>The defaults reach the server in PostgreSQL's own notation.</summary>
    [Fact]
    public async Task Defaults_Reach_Scripts_As_Five_Seconds_And_Fifteen_Minutes()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create().Add(
            "0001_probe_capture.sql",
            "create table public.timeout_capture_defaults as select current_setting('lock_timeout') as lock_timeout, " +
            "current_setting('statement_timeout') as statement_timeout;");

        var outcome = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", outcome.Messages));
        (await _fixture.ScalarAsync<string>(
            database,
            "select lock_timeout from public.timeout_capture_defaults")).ShouldBe("5s");
        (await _fixture.ScalarAsync<string>(
            database,
            "select statement_timeout from public.timeout_capture_defaults")).ShouldBe("15min");
    }

    /// <summary>
    /// The migration lock's wait is not governed by the session's
    /// <c>statement_timeout</c>: a runner waiting for another session's advisory
    /// lock succeeds even when the connection would abort any statement after
    /// half a second, because it resets both timeouts before waiting.
    /// </summary>
    [Fact]
    public async Task Advisory_Lock_Wait_Ignores_A_Session_Statement_Timeout()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_wait.sql", "create table if not exists public.advisory_wait_probe (id int primary key);");

        await using var blocker = await _fixture.OpenSuperuserAsync(database);
        await PostgresMigratorFixture.ExecuteAsync(
            blocker,
            $"select pg_advisory_lock({MigrationLock.Key.ToString(CultureInfo.InvariantCulture)})");

        var connectionString = new NpgsqlConnectionStringBuilder(_fixture.OwnerConnectionString(database))
        {
            Options = $"-c statement_timeout={SessionStatementTimeoutMs.ToString(CultureInfo.InvariantCulture)}",
        }.ConnectionString;

        var run = Task.Run(() => MigratorHarness.Run(migrations.Path, connectionString));
        var waiting = Stopwatch.StartNew();
        try
        {
            await WaitUntilAsync(
                () => AdvisoryLockWaiterExistsAsync(database),
                Bound,
                "the Migrator to queue on the migration lock");

            var appearedAfter = waiting.Elapsed;
            await WaitUntilAsync(
                () => Task.FromResult(waiting.Elapsed - appearedAfter >= WaitBeyondSessionTimeout),
                Bound,
                "the lock wait to outlast the session statement_timeout");
        }
        finally
        {
            await PostgresMigratorFixture.ExecuteAsync(
                blocker,
                $"select pg_advisory_unlock({MigrationLock.Key.ToString(CultureInfo.InvariantCulture)})");
        }

        var outcome = await run;

        waiting.Stop();
        waiting.Elapsed.ShouldBeGreaterThanOrEqualTo(WaitBeyondSessionTimeout);
        outcome.Messages.ShouldContain("another migration run holds the migration lock; waiting for it to finish");
        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success, string.Join(" | ", outcome.Messages));
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_wait.sql"]);
    }

    private async Task<bool> AdvisoryLockWaiterExistsAsync(string database) =>
        await _fixture.ScalarAsync<long>(
            database,
            """
            select count(*)
              from pg_stat_activity
             where datname = current_database()
               and pid <> pg_backend_pid()
               and wait_event_type = 'Lock'
               and query like '%pg_advisory_lock%'
            """) > 0;

    private async Task<bool> ColumnExistsAsync(string database, string table, string column) =>
        await _fixture.ScalarAsync<long>(
            database,
            $"select count(*) from information_schema.columns where table_schema = 'public' " +
            $"and table_name = '{table}' and column_name = '{column}'") > 0;

    /// <summary>Polls a database condition to a deadline; the delay is the poll interval, not a race window.</summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException(
                    $"{description} did not happen within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
            }

            await Task.Delay(PollInterval, TestContext.Current.CancellationToken);
        }
    }
}
