using System.Globalization;
using Evidgate.Migrator;

namespace Migrator.IntegrationTests;

/// <summary>
/// Runs the Migrator in-process against a real database and hands the tests both
/// the structured result and the transcript, so an assertion can name an exit
/// code, a plan or the exact line an operator would read.
/// </summary>
internal static class MigratorHarness
{
    internal static RunOutcome Run(
        string migrationsPath,
        string connectionString,
        MigrationCommand command = MigrationCommand.Apply,
        int commandTimeoutSeconds = MigratorOptions.DefaultCommandTimeoutSeconds,
        int lockTimeoutSeconds = MigratorOptions.DefaultLockTimeoutSeconds,
        int statementTimeoutSeconds = MigratorOptions.DefaultStatementTimeoutSeconds)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        var result = MigratorRunner.Run(
            new MigratorOptions
            {
                MigrationsPath = migrationsPath,
                ConnectionString = connectionString,
                CommandTimeoutSeconds = commandTimeoutSeconds,
                LockTimeoutSeconds = lockTimeoutSeconds,
                StatementTimeoutSeconds = statementTimeoutSeconds,
            },
            command,
            writer);

        return new RunOutcome(result, writer.ToString());
    }
}

/// <summary>One Migrator run: structured result plus the console transcript.</summary>
/// <param name="Result">Exit code, plan and messages.</param>
/// <param name="Transcript">Everything the run wrote to its output.</param>
internal sealed record RunOutcome(MigrationRunResult Result, string Transcript)
{
    internal int ExitCode => Result.ExitCode;

    internal IReadOnlyList<string> Messages => Result.Messages;
}
