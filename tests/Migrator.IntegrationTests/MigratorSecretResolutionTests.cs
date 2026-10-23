using Evidgate.Migrator;
using Shouldly;
using Xunit;

namespace Migrator.IntegrationTests;

/// <summary>
/// 10.1.1-T2 T2.2: the Migrator resolves its connection string through
/// <c>ISecretProvider</c> at process start, so changing the environment variable
/// changes the resolved secret with no code change — and the secret value never
/// reaches the output.
/// </summary>
/// <remarks>
/// Docker-free: the target is unreachable on purpose, so each run stops at the
/// redacted <c>database unavailable</c> line (exit code
/// <see cref="MigratorExitCodes.DatabaseUnavailable"/>) without a database. The
/// line names that run's host, database and user, which is exactly the observable
/// proof that the value came from the environment of that start.
/// </remarks>
public sealed class MigratorSecretResolutionTests
{
    private const string AlphaConnectionString =
        "Host=127.0.0.1;Port=1;Database=db_alpha;Username=user_alpha;Password=alpha-secret;Timeout=2;Command Timeout=2";

    private const string BetaConnectionString =
        "Host=127.0.0.1;Port=1;Database=db_beta;Username=user_beta;Password=beta-secret;Timeout=2;Command Timeout=2";

    /// <summary>
    /// Two starts of the same binary with two different values of
    /// <c>EVIDGATE_MIGRATOR__CONNECTIONSTRING</c>: each resolves the value of its
    /// own start, and neither leaks the password.
    /// </summary>
    [Fact]
    public async Task Connection_String_Is_Resolved_From_The_Environment_At_Each_Process_Start()
    {
        var alpha = await RunMigratorProcessAsync(AlphaConnectionString);
        var beta = await RunMigratorProcessAsync(BetaConnectionString);

        alpha.ExitCode.ShouldBe(MigratorExitCodes.DatabaseUnavailable);
        beta.ExitCode.ShouldBe(MigratorExitCodes.DatabaseUnavailable);

        // Each run's redacted target description reflects the value of its own start.
        // Every stream assertion compares a boolean with a value-free message, so a
        // failure can never print captured process output (which could hold a secret).
        alpha.StandardOutput.Contains("database unavailable for 127.0.0.1:1/db_alpha as 'user_alpha'", StringComparison.Ordinal)
            .ShouldBeTrue("the redacted target of run 1 must be printed");
        beta.StandardOutput.Contains("database unavailable for 127.0.0.1:1/db_beta as 'user_beta'", StringComparison.Ordinal)
            .ShouldBeTrue("the redacted target of run 2 must be printed");

        // ...and not the other start's value.
        (alpha.StandardOutput.Contains("db_beta", StringComparison.Ordinal)
                || alpha.StandardOutput.Contains("user_beta", StringComparison.Ordinal))
            .ShouldBeFalse("run 1 must not print the target of run 2");
        (beta.StandardOutput.Contains("db_alpha", StringComparison.Ordinal)
                || beta.StandardOutput.Contains("user_alpha", StringComparison.Ordinal))
            .ShouldBeFalse("run 2 must not print the target of run 1");

        // The password is a secret: it must never appear in either stream. The
        // assertions compare booleans, so a failure cannot print the value.
        alpha.StandardOutput.Contains("alpha-secret", StringComparison.Ordinal)
            .ShouldBeFalse("the connection-string password must never reach stdout");
        alpha.StandardError.Contains("alpha-secret", StringComparison.Ordinal)
            .ShouldBeFalse("the connection-string password must never reach stderr");
        beta.StandardOutput.Contains("beta-secret", StringComparison.Ordinal)
            .ShouldBeFalse("the connection-string password must never reach stdout");
        beta.StandardError.Contains("beta-secret", StringComparison.Ordinal)
            .ShouldBeFalse("the connection-string password must never reach stderr");

        string.IsNullOrEmpty(alpha.StandardError).ShouldBeTrue("run 1 stderr must be empty");
        string.IsNullOrEmpty(beta.StandardError).ShouldBeTrue("run 2 stderr must be empty");
    }

    /// <summary>
    /// The provider treats an empty or whitespace value as unset, so a blank
    /// variable is the same "missing" setup error as an absent one — the value is
    /// never parsed as a connection string.
    /// </summary>
    [Fact]
    public void Blank_Connection_String_Is_Reported_As_Missing()
    {
        var error = Should.Throw<MigratorSetupException>(() => MigratorOptions.FromEnvironment(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [MigratorOptions.ConnectionStringVariable] = "   ",
            }));

        error.Errors.Any(message => message.Contains(MigratorOptions.ConnectionStringVariable, StringComparison.Ordinal))
            .ShouldBeTrue("a blank connection string must be reported as missing");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunMigratorProcessAsync(
        string connectionString)
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
        startInfo.ArgumentList.Add("--status");
        startInfo.Environment[MigratorOptions.ConnectionStringVariable] = connectionString;
        startInfo.Environment[MigratorOptions.MigrationsPathVariable] = PostgresMigratorFixture.RepoMigrationsPath;

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
