using System.Globalization;
using Evidgate.Migrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// The sides of <c>seed-dev --with-token</c> that must never carry the one-time secret: what reaches a logger, what the
/// error text says when the mint or the print fails, and what the advisory-lock guard does when its session dies or its
/// wait outlasts the connection's command timeout.
/// </summary>
[Collection(MigratorPostgresSuite.Name)]
[Trait("Category", "Docker")]
public sealed partial class SeedDevTokenGuardTests
{
    private readonly SeedDevTokenHarness _harness;

    public SeedDevTokenGuardTests(PostgresMigratorFixture fixture) => _harness = new SeedDevTokenHarness(fixture);

    /// <summary>
    /// The token is printed to the caller's writer only: a logger that receives every entry of the seed's own container
    /// never sees the secret, and control messages at two levels prove the sink was really wired.
    /// </summary>
    [Fact]
    public async Task SeedDev_Token_NeverReachesALogger()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        var variables = _harness.Variables(database, pepper);

        // Positive control first: the seam hands a provider to the real composition, so an empty capture means silence
        // rather than a sink that was never registered — at Information and, because the sink sets Trace, at Debug.
        var control = new SeedDevTokenHarness.CapturingLoggerProvider();
        using (var probe = DevSeedServices.Compose(variables, SeedDevTokenHarness.ContractsDirectory(), control))
        {
            var logger = probe.GetRequiredService<ILoggerFactory>().CreateLogger("seed-dev-probe");
            Log.InformationControl(logger, "wired");
            Log.DebugControl(logger, "wired at debug");
        }

        control.Messages.ShouldContain(message => message.Contains("capture control wired", StringComparison.Ordinal));
        control.Messages.ShouldContain(
            message => message.Contains("capture control wired at debug", StringComparison.Ordinal));

        var capture = new SeedDevTokenHarness.CapturingLoggerProvider();
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        var code = DevSeedToken.Run(MigratorOptions.FromEnvironment(variables), variables, output, error, capture);

        code.ShouldBe(MigratorExitCodes.Success, error.ToString());
        var token = SeedDevTokenHarness.TokenOf(output.ToString());
        capture.Messages.ShouldNotContain(message => message.Contains(token, StringComparison.Ordinal));
        capture.Messages.ShouldNotContain(message => message.Contains("egt_", StringComparison.Ordinal));
        error.ToString().ShouldNotContain(token);
    }

    /// <summary>
    /// The writer fails on the one-time secret line and echoes both the line and the pepper into the exception, so only
    /// the redactor keeps them out of the error text; the token row was already committed when that write failed.
    /// </summary>
    [Fact]
    public async Task SeedDev_Token_OutputWriteFails_ErrorTextCarriesNoSecret()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        var error = new StringWriter(CultureInfo.InvariantCulture);

        var code = MigratorHost.Run(
            ["seed-dev", "--with-token"],
            new SeedDevTokenHarness.SecretEchoingWriter(pepper),
            error,
            _harness.Variables(database, pepper));

        code.ShouldBe(MigratorExitCodes.MigrationFailed, error.ToString());
        error.ToString().ShouldContain("seed-dev --with-token failed");
        error.ToString().ShouldContain("<redacted>");
        error.ToString().ShouldNotContain("egt_");
        error.ToString().ShouldNotContain(pepper);
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
    }

    /// <summary>
    /// The real command cannot write the row (the app role lost INSERT), so the mint fails after every other check has
    /// passed: no token line, no secret in the error, and nothing stored.
    /// </summary>
    [Fact]
    public async Task SeedDev_Token_MintRefused_ErrorTextCarriesNoSecret()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();

        await using (var connection = await _harness.Fixture.OpenSuperuserAsync(database))
        {
            await PostgresMigratorFixture.ExecuteAsync(
                connection,
                "revoke insert on public.ingest_token from evidgate_app");
        }

        var run = _harness.RunWithToken(database, pepper);

        run.ExitCode.ShouldBe(MigratorExitCodes.MigrationFailed, run.Error);
        run.Error.ShouldContain("seed-dev");
        run.Error.ShouldNotContain("egt_");
        run.Output.ShouldNotContain(SeedDevTokenHarness.DevTokenLine);
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(0);
    }

    /// <summary>
    /// The guard's session dies before it releases the lock: disposal must stay quiet, because the secret may already be
    /// printed and a broken session must not turn a successful run into a failure exit code.
    /// </summary>
    [Fact]
    public async Task SeedGuard_DisposedAfterItsSessionIsTerminated_DoesNotThrow()
    {
        var (database, _) = await _harness.SeededDatabaseAsync();
        await using var dataSource = new NpgsqlDataSourceBuilder(
            SeedDevTokenHarness.Unpooled(_harness.Fixture.AppConnectionString(database))).Build();
        var guard = await DevSeedToken.SeedGuard.AcquireAsync(
            dataSource, TestContext.Current.CancellationToken);

        await using (var connection = await _harness.Fixture.OpenSuperuserAsync(database))
        {
            await PostgresMigratorFixture.ExecuteAsync(
                connection,
                $"select pg_terminate_backend(pid) from pg_stat_activity where usename = 'evidgate_app' and datname = '{database}'");
        }

        // The backend is gone only once pg_stat_activity drops it, and that is the state the unlock has to survive.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await _harness.Fixture.ScalarAsync<long>(
            database,
            $"select count(*) from pg_stat_activity where usename = 'evidgate_app' and datname = '{database}'") > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"the terminated app-role session on '{database}' is still listed");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await guard.DisposeAsync();
    }

    /// <summary>
    /// The session is already closed when the guard is disposed: Npgsql refuses the unlock before it reaches the
    /// server, and that refusal must stay inside the disposal rather than escape as a failure exit code.
    /// </summary>
    [Fact]
    public async Task SeedGuard_DisposedAfterItsSessionClosed_DoesNotThrow()
    {
        var (database, _) = await _harness.SeededDatabaseAsync();
        await using var dataSource = new NpgsqlDataSourceBuilder(
            SeedDevTokenHarness.Unpooled(_harness.Fixture.AppConnectionString(database))).Build();
        var guard = await DevSeedToken.SeedGuard.AcquireAsync(
            dataSource, TestContext.Current.CancellationToken);

        await guard.DisposeAsync();

        // The connection is closed now, so this second disposal is the already-closed path.
        await guard.DisposeAsync();
    }

    /// <summary>
    /// A second run waits for the holder even though its own connection cuts every command off after two seconds, and
    /// then completes: the advisory lock is not a statement with a deadline, so a seed that outlasts the connection's
    /// command timeout must not become a failed run.
    /// </summary>
    [Fact]
    public async Task SeedDev_Token_SecondRun_WaitsPastTheConnectionsCommandTimeout()
    {
        var (database, pepper) = await _harness.SeededDatabaseAsync();
        await using var dataSource = new NpgsqlDataSourceBuilder(
            SeedDevTokenHarness.Unpooled(_harness.Fixture.AppConnectionString(database))).Build();
        await using var holder = await DevSeedToken.SeedGuard.AcquireAsync(
            dataSource, TestContext.Current.CancellationToken);
        var variables = _harness.Variables(
            database,
            pepper,
            appConnectionString: SeedDevTokenHarness.WithCommandTimeout(
                SeedDevTokenHarness.Unpooled(_harness.Fixture.AppConnectionString(database)),
                seconds: 2));

        // A thread, not Task.Run: the real command blocks inside the lock wait, and a task started elsewhere and joined
        // here is the pattern VSTHRD003 forbids.
        var result = new SeedDevTokenHarness.RunResult[1];
        var run = new Thread(() => result[0] = SeedDevTokenHarness.Run(["seed-dev", "--with-token"], variables));
        run.Start();

        try
        {
            // The second run is really in the lock wait, so the hold below is what the assertion measures; then the
            // wait runs past the two-second command timeout before the holder lets go.
            await WaitForAdvisoryWaiterAsync(database);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            run.IsAlive.ShouldBeTrue("the second run must still wait for the lock, not have failed after two seconds");
        }
        finally
        {
            await holder.DisposeAsync();
        }

        run.Join(TimeSpan.FromSeconds(60)).ShouldBeTrue("the second run did not finish after the lock was released");

        result[0].ExitCode.ShouldBe(MigratorExitCodes.Success, result[0].Error);
        SeedDevTokenHarness.CountOf(result[0].Output, SeedDevTokenHarness.DevTokenLine).ShouldBe(1);
        (await _harness.Fixture.ScalarAsync<long>(database, "select count(*) from public.ingest_token")).ShouldBe(1);
    }

    /// <summary>The second run's backend sits in an advisory-lock wait; without that the hold would prove nothing.</summary>
    private async Task WaitForAdvisoryWaiterAsync(string database)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await _harness.Fixture.ScalarAsync<long>(
            database,
            "select count(*) from pg_stat_activity where wait_event = 'advisory' and usename = 'evidgate_app' " +
            $"and datname = '{database}'") == 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"no seed run is waiting on the advisory lock in '{database}'");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The probe's log calls (CA1848/CA1873: no format strings at the call site).</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "capture control {Marker}")]
        public static partial void InformationControl(ILogger logger, string marker);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "capture control {Marker}")]
        public static partial void DebugControl(ILogger logger, string marker);
    }
}
