using System.Security.Cryptography;
using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// 01.4.1-T1/T1.2 and AC1–AC4: the Migrator applies the real migrations in order
/// and journals them with their checksums, refuses a drifted history before it
/// applies anything, is atomic per file, and previews without writing.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed class MigratorTests
{
    private readonly PostgresMigratorFixture _fixture;

    public MigratorTests(PostgresMigratorFixture fixture) => _fixture = fixture;

    /// <summary>AC1: fresh database → head in one command, one journal row per file, in order.</summary>
    [Fact]
    public async Task Applies_In_Order_And_Journals()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var outcome = MigratorHarness.Run(
            PostgresMigratorFixture.RepoMigrationsPath,
            _fixture.OwnerConnectionString(database));

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success);
        outcome.Result.AppliedNow.ShouldBe(PostgresMigratorFixture.RealMigrationFileNames);
        outcome.Result.Plan!.Pending.Select(script => script.Name)
            .ShouldBe(PostgresMigratorFixture.RealMigrationFileNames);
        outcome.Messages.ShouldContain(line => line.StartsWith($"summary: applied {PostgresMigratorFixture.RealMigrationFileNames.Count}, failed 0", StringComparison.Ordinal));

        var rows = await _fixture.ReadJournalAsync(database);
        rows.Select(row => row.Name).ShouldBe(PostgresMigratorFixture.RealMigrationFileNames);

        foreach (var row in rows)
        {
            var bytes = await File.ReadAllBytesAsync(
                Path.Combine(PostgresMigratorFixture.RepoMigrationsPath, row.Name),
                TestContext.Current.CancellationToken);
            row.Checksum.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            row.DurationMs.ShouldNotBeNull();
            row.DurationMs!.Value.ShouldBeGreaterThanOrEqualTo(0);
            row.AppliedAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-10));
        }

        // One transaction per file, applied in order: the journal timestamps never go backwards.
        rows.Select(row => row.AppliedAt).ShouldBe(rows.Select(row => row.AppliedAt).OrderBy(at => at));

        // The real migrations really did what they say: template table, tenant helpers, roles.
        (await _fixture.TableExistsAsync(database, "rls_probe")).ShouldBeTrue();
        (await _fixture.ScalarAsync<bool>(database, "select evidgate.current_org_id() is null")).ShouldBeTrue();
        (await _fixture.ScalarAsync<long>(database, "select count(*) from pg_roles where rolname like 'evidgate_%'"))
            .ShouldBe(4);

        var status = MigratorHarness.Run(
            PostgresMigratorFixture.RepoMigrationsPath,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Status);

        status.ExitCode.ShouldBe(MigratorExitCodes.Success);
        status.Result.Plan!.Pending.ShouldBeEmpty();
        status.Result.Plan!.Drifted.ShouldBeEmpty();
    }

    /// <summary>AC2: a failing script rolls back its whole transaction and is never journaled.</summary>
    [Fact]
    public async Task Failure_Rolls_Back_Whole_File()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_ok.sql", "create table if not exists public.failure_probe_first (id int primary key);")
            .Add(
                "0002_probe_fail.sql",
                "create table if not exists public.failure_probe_half (id int primary key);\nselect 1 / 0;");

        var outcome = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        outcome.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        outcome.Result.AppliedNow.ShouldBe(["0001_probe_ok.sql"]);
        outcome.Messages.ShouldContain(line =>
            line.StartsWith("migration failed in 0002_probe_fail.sql:", StringComparison.Ordinal));

        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_ok.sql"]);
        (await _fixture.TableExistsAsync(database, "failure_probe_half")).ShouldBeFalse();
        (await _fixture.TableExistsAsync(database, "failure_probe_first")).ShouldBeTrue();
    }

    /// <summary>AC3: editing an applied file stops the run before anything is applied; <c>--status</c> names it.</summary>
    [Fact]
    public async Task Checksum_Mismatch_Refuses_Start()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_first.sql", "create table if not exists public.drift_probe_first (id int primary key);");

        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        // Edit the applied file and add a pending one: the pending file must NOT be applied.
        migrations
            .Add("0001_probe_first.sql", "create table if not exists public.drift_probe_first (id int primary key);\n-- edited\n")
            .Add("0002_probe_pending.sql", "create table if not exists public.drift_probe_pending (id int primary key);");

        var apply = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        apply.ExitCode.ShouldBe(MigratorExitCodes.ChecksumMismatch);
        apply.Messages.ShouldContain(line =>
            line.StartsWith("drifted 0001_probe_first.sql: checksum mismatch", StringComparison.Ordinal));
        apply.Result.AppliedNow.ShouldBeEmpty();
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_first.sql"]);
        (await _fixture.TableExistsAsync(database, "drift_probe_pending")).ShouldBeFalse();

        var status = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Status);

        status.ExitCode.ShouldBe(MigratorExitCodes.ChecksumMismatch);
        status.Messages.ShouldContain(line =>
            line.StartsWith("drifted 0001_probe_first.sql: checksum mismatch", StringComparison.Ordinal));
        status.Messages.ShouldContain("pending 0002_probe_pending.sql");
    }

    /// <summary>
    /// A migration set may contain notes next to the scripts; only files that
    /// look like migrations are validated.
    /// </summary>
    [Fact]
    public async Task Non_Sql_Files_Next_To_The_Scripts_Are_Ignored()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("README.md", "# notes for this directory\n")
            .Add("0001_probe_ok.sql", "create table if not exists public.notes_probe (id int primary key);");

        var outcome = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success);
        outcome.Result.AppliedNow.ShouldBe(["0001_probe_ok.sql"]);
    }

    /// <summary>
    /// A statement that outruns the configured command timeout is reported as a
    /// timeout (not as a syntax or connection error), rolled back and not
    /// journalled; the same script applies once the timeout allows it.
    /// </summary>
    [Fact]
    public async Task Script_Over_The_Command_Timeout_Fails_And_Is_Not_Journalled()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create().Add(
            "0001_probe_slow.sql",
            "create table if not exists public.slow_probe (id int primary key);\nselect pg_sleep(3);");

        var timedOut = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Apply,
            commandTimeoutSeconds: 2);

        timedOut.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed);
        timedOut.Messages.ShouldContain(line => line.Contains("command timeout", StringComparison.Ordinal));
        (await _fixture.ReadJournalAsync(database)).ShouldBeEmpty();
        (await _fixture.TableExistsAsync(database, "slow_probe")).ShouldBeFalse();

        var allowed = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Apply,
            commandTimeoutSeconds: 5);

        allowed.ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_slow.sql"]);
    }

    /// <summary>
    /// Two runners started at the same time must not apply the same file twice:
    /// the second waits on the migration lock and then finds nothing to do.
    /// </summary>
    [Fact]
    public async Task Concurrent_Runs_Serialise_On_The_Migration_Lock()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add(
                "0001_probe_slow.sql",
                "create table if not exists public.concurrent_first (id int primary key);\nselect pg_sleep(1);")
            .Add("0002_probe_second.sql", "create table if not exists public.concurrent_second (id int primary key);");
        var owner = _fixture.OwnerConnectionString(database);

        var runs = await Task.WhenAll(
            Task.Run(() => MigratorHarness.Run(migrations.Path, owner)),
            Task.Run(() => MigratorHarness.Run(migrations.Path, owner)));

        runs.ShouldAllBe(outcome => outcome.ExitCode == MigratorExitCodes.Success);

        // Creating a table twice would have failed, so exactly one runner applied.
        runs.Count(outcome => outcome.Result.AppliedNow.Count == 2).ShouldBe(1);
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe(["0001_probe_slow.sql", "0002_probe_second.sql"]);
    }

    /// <summary>AC4: <c>--dry-run</c> lists the pending files and applies nothing.</summary>
    [Fact]
    public async Task DryRun_Applies_Nothing()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_a.sql", "create table if not exists public.dryrun_probe_a (id int primary key);")
            .Add("0002_probe_b.sql", "create table if not exists public.dryrun_probe_b (id int primary key);");

        // A read-only command does not even create the journal table on a pristine database.
        var pristineStatus = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Status);
        pristineStatus.ExitCode.ShouldBe(MigratorExitCodes.Success);
        pristineStatus.Messages.ShouldContain("summary: 0 applied, 2 pending, 0 drifted");

        var outcome = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.DryRun);

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success);
        outcome.Result.Plan!.Pending.Select(script => script.Name).ShouldBe(["0001_probe_a.sql", "0002_probe_b.sql"]);
        outcome.Result.AppliedNow.ShouldBeEmpty();
        outcome.Messages.ShouldContain("pending 0001_probe_a.sql");
        outcome.Messages.ShouldContain("pending 0002_probe_b.sql");
        outcome.Messages.ShouldContain("dry run: 2 migration(s) pending, nothing applied");

        // Journal unchanged — and, on a pristine database, not even created:
        // --status and --dry-run issue no DDL.
        (await _fixture.TableExistsAsync(database, "schema_migration")).ShouldBeFalse();
        (await _fixture.TableExistsAsync(database, "dryrun_probe_a")).ShouldBeFalse();
        (await _fixture.TableExistsAsync(database, "dryrun_probe_b")).ShouldBeFalse();

        // The same directory still applies afterwards — the preview left no state behind.
        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name)
            .ShouldBe(["0001_probe_a.sql", "0002_probe_b.sql"]);
    }

    /// <summary>Pending versus applied is what <c>--status</c> exists for; it never writes.</summary>
    [Fact]
    public async Task Status_Reports_Pending()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_a.sql", "create table if not exists public.status_probe_a (id int primary key);");

        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);
        migrations.Add("0002_probe_b.sql", "create table if not exists public.status_probe_b (id int primary key);");

        var outcome = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Status);

        outcome.ExitCode.ShouldBe(MigratorExitCodes.Success);
        outcome.Result.Plan!.Applied.Select(row => row.Name).ShouldBe(["0001_probe_a.sql"]);
        outcome.Result.Plan!.Pending.Select(script => script.Name).ShouldBe(["0002_probe_b.sql"]);
        outcome.Messages.ShouldContain(line =>
            line.StartsWith("applied 0001_probe_a.sql at ", StringComparison.Ordinal));
        outcome.Messages.ShouldContain("pending 0002_probe_b.sql");
        outcome.Messages.ShouldContain("summary: 1 applied, 1 pending, 0 drifted");

        // A read-only command: the pending file is still pending afterwards.
        (await _fixture.ReadJournalAsync(database)).Select(row => row.Name).ShouldBe(["0001_probe_a.sql"]);
        (await _fixture.TableExistsAsync(database, "status_probe_b")).ShouldBeFalse();
    }

    /// <summary>
    /// Names, duplicate numbers and gaps are validated before the database is
    /// touched at all — a branch merge that produced the same number twice must
    /// not be discovered half-way through an apply (01 §5, 01.4.1-T1.1).
    /// </summary>
    [Fact]
    public async Task Invalid_Migration_Set_Is_Rejected_Before_The_Database()
    {
        var database = await _fixture.CreateDatabaseAsync();
        var owner = _fixture.OwnerConnectionString(database);

        using var gap = TempMigrations.Create()
            .Add("0001_probe_first.sql", "select 1;")
            .Add("0003_probe_third.sql", "select 1;");
        var gapOutcome = MigratorHarness.Run(gap.Path, owner);
        gapOutcome.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        gapOutcome.Messages.ShouldContain(line => line.Contains("missing migration number 0002", StringComparison.Ordinal));

        using var duplicate = TempMigrations.Create()
            .Add("0001_probe_first.sql", "select 1;")
            .Add("0001_probe_other.sql", "select 1;");
        var duplicateOutcome = MigratorHarness.Run(duplicate.Path, owner);
        duplicateOutcome.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        duplicateOutcome.Messages.ShouldContain(line => line.Contains("duplicate migration number 0001", StringComparison.Ordinal));

        using var badName = TempMigrations.Create().Add("1_probe_short.sql", "select 1;");
        var nameOutcome = MigratorHarness.Run(badName.Path, owner);
        nameOutcome.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        nameOutcome.Messages.ShouldContain(line => line.Contains("invalid migration file name", StringComparison.Ordinal));

        // A case-sensitive *.sql glob would silently skip this file on Linux.
        using var wrongCase = TempMigrations.Create().Add("0001_probe_UPPER.SQL", "select 1;");
        var caseOutcome = MigratorHarness.Run(wrongCase.Path, owner);
        caseOutcome.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        caseOutcome.Messages.ShouldContain(line =>
            line.Contains("0001_probe_UPPER.SQL", StringComparison.Ordinal)
            && line.Contains("invalid migration file name", StringComparison.Ordinal));

        using var empty = TempMigrations.Create();
        var emptyOutcome = MigratorHarness.Run(empty.Path, owner);
        emptyOutcome.ExitCode.ShouldBe(MigratorExitCodes.InvalidInvocation);
        emptyOutcome.Messages.ShouldContain(line => line.Contains("no *.sql migration files", StringComparison.Ordinal));

        // Nothing reached the database: not even the journal table was created.
        (await _fixture.TableExistsAsync(database, "schema_migration")).ShouldBeFalse();
    }

    /// <summary>An unreachable database is exit <c>4</c> and never leaks the credential (00 §5.7 S3).</summary>
    [Fact]
    public async Task Unreachable_Database_Reports_Exit_Code_4()
    {
        const string Password = "super-secret-password";
        var unreachable =
            $"Host=127.0.0.1;Port=1;Database=nowhere;Username=evidgate_owner;Password={Password};Timeout=2;Command Timeout=2";

        var outcome = MigratorHarness.Run(
            PostgresMigratorFixture.RepoMigrationsPath,
            unreachable,
            MigrationCommand.Status);

        outcome.ExitCode.ShouldBe(MigratorExitCodes.DatabaseUnavailable);
        outcome.Messages.ShouldContain(line => line.StartsWith("database unavailable for ", StringComparison.Ordinal));
        outcome.Transcript.ShouldNotContain(Password);
        outcome.Messages.ShouldAllBe(line => !line.Contains(Password, StringComparison.Ordinal));
    }

    /// <summary>
    /// An applied file that is gone from the directory is drift too — the usual
    /// cause is a branch that lost a migration, or two branches that invented the
    /// same number. The run must stop before applying the replacement.
    /// </summary>
    [Fact]
    public async Task Removed_Applied_File_Is_Reported_As_Drift()
    {
        var database = await _fixture.CreateDatabaseAsync();
        using var migrations = TempMigrations.Create()
            .Add("0001_probe_alpha.sql", "create table if not exists public.alpha_probe (id int primary key);")
            .Add("0002_probe_beta.sql", "create table if not exists public.beta_probe (id int primary key);")
            .Add("0003_probe_gamma.sql", "create table if not exists public.gamma_probe (id int primary key);");

        MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database))
            .ExitCode.ShouldBe(MigratorExitCodes.Success);

        // The branch switched: 0003 is gone, and a different 0003 arrived instead.
        File.Delete(Path.Combine(migrations.Path, "0003_probe_gamma.sql"));
        migrations.Add("0003_probe_delta.sql", "create table if not exists public.delta_probe (id int primary key);");

        var status = MigratorHarness.Run(
            migrations.Path,
            _fixture.OwnerConnectionString(database),
            MigrationCommand.Status);

        status.ExitCode.ShouldBe(MigratorExitCodes.ChecksumMismatch);
        status.Messages.ShouldContain(line =>
            line.StartsWith("drifted 0003_probe_gamma.sql: applied but the file is gone", StringComparison.Ordinal));
        status.Messages.ShouldContain("pending 0003_probe_delta.sql");

        var apply = MigratorHarness.Run(migrations.Path, _fixture.OwnerConnectionString(database));
        apply.ExitCode.ShouldBe(MigratorExitCodes.ChecksumMismatch);
        (await _fixture.TableExistsAsync(database, "delta_probe")).ShouldBeFalse();
    }

    /// <summary>
    /// The exit-code contract is a process contract (10.2.2-T1 decides on the
    /// child process's code), so one path is proven through the real executable.
    /// </summary>
    [Fact]
    public async Task Status_Through_Real_Process_Uses_Exit_Code_Contract()
    {
        var database = await _fixture.CreateDatabaseAsync();

        var run = await RunMigratorProcessAsync(
            database,
            PostgresMigratorFixture.RepoMigrationsPath,
            "--status");

        run.ExitCode.ShouldBe(MigratorExitCodes.Success);
        run.StandardOutput.ShouldContain(
            $"summary: 0 applied, {PostgresMigratorFixture.RealMigrationFileNames.Count} pending, 0 drifted");
        run.StandardError.ShouldBeEmpty();
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunMigratorProcessAsync(
        string database,
        string migrationsPath,
        string argument)
    {
        var assembly = Path.Combine(AppContext.BaseDirectory, "Evidgate.Migrator.dll");
        File.Exists(assembly).ShouldBeTrue(
            $"the Migrator assembly must be copied next to the tests so the real process can be spawned: {assembly}");

        var token = TestContext.Current.CancellationToken;
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // The Migrator has no runtimeconfig.json of its own in this output
        // directory, so it runs on the test host's framework description.
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Migrator.IntegrationTests.runtimeconfig.json"));
        startInfo.ArgumentList.Add(assembly);
        startInfo.ArgumentList.Add(argument);
        startInfo.Environment[MigratorOptions.ConnectionStringVariable] = _fixture.OwnerConnectionString(database);
        startInfo.Environment[MigratorOptions.MigrationsPathVariable] = migrationsPath;

        var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start the Migrator process");

        using (process)
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(token);
            var standardError = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);

            return (process.ExitCode, await standardOutput, await standardError);
        }
    }
}
