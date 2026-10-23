namespace Evidgate.Migrator;

/// <summary>
/// Command-line surface of the Migrator (01.4.1 §5): one process, one command.
/// Kept separate from <see cref="Program"/> so the argument/environment
/// handling is testable without spawning a process.
/// </summary>
public static class MigratorHost
{
    private const string StatusArgument = "--status";
    private const string DryRunArgument = "--dry-run";
    private const string SeedDevArgument = "seed-dev";
    private const string HelpArgument = "--help";
    private const string HelpShortArgument = "-h";

    /// <summary>Usage text, also printed for <c>--help</c>.</summary>
    public static string Usage =>
        $"""
        evidgate-migrator — applies plain-SQL migrations as the owner role, journalled in {MigratorOptions.JournalTableName} (00 §5.2, ADR-0003).

        usage: Evidgate.Migrator [--status | --dry-run | seed-dev | --help]

          (no arguments)  apply every pending migration, one transaction per file
          --status        print applied, pending and drifted files; exit {MigratorExitCodes.ChecksumMismatch} on drift, applies nothing
          --dry-run       print the pending files without applying anything
          seed-dev        write the synthetic dev tenant (org, project, dev user + roles, CI binding); idempotent;
                          refuses unless DOTNET_ENVIRONMENT=Development and the database host is local (01.8.1-T3)

        environment:
          {MigratorOptions.ConnectionStringVariable}   (required) owner-role connection string
          {MigratorOptions.MigrationsPathVariable}     (optional) defaults to <repo>/migrations
          {MigratorOptions.CommandTimeoutVariable} (optional) seconds per statement, default {MigratorOptions.DefaultCommandTimeoutSeconds} = no timeout

        exit codes: {MigratorExitCodes.Success} ok · {MigratorExitCodes.InvalidInvocation} invalid invocation or migration set · {MigratorExitCodes.ChecksumMismatch} checksum mismatch · {MigratorExitCodes.MigrationFailed} migration failure · {MigratorExitCodes.DatabaseUnavailable} database unavailable
        """;

    /// <summary>
    /// Parses the arguments, reads the configuration and runs the command.
    /// </summary>
    /// <param name="args">Raw process arguments.</param>
    /// <param name="output">Standard output for the run transcript.</param>
    /// <param name="error">Errors that are not part of the run transcript (usage, configuration).</param>
    /// <param name="environment">Environment snapshot; defaults to the process environment.</param>
    public static int Run(
        string[] args,
        TextWriter output,
        TextWriter error,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!TryParse(args, out var command, out var usageError))
        {
            error.WriteLine($"E {usageError}");
            error.WriteLine(Usage);
            return MigratorExitCodes.InvalidInvocation;
        }

        if (command is null)
        {
            output.WriteLine(Usage);
            return MigratorExitCodes.Success;
        }

        MigratorOptions options;
        try
        {
            options = MigratorOptions.FromEnvironment(environment);
        }
        catch (MigratorSetupException ex)
        {
            foreach (var message in ex.Errors)
            {
                error.WriteLine($"E {message}");
            }

            error.WriteLine(Usage);
            return MigratorExitCodes.InvalidInvocation;
        }

        if (command == MigrationCommand.SeedDev)
        {
            return DevSeed.Run(options, environment, output, error);
        }

        return MigratorRunner.Run(options, command.Value, output).ExitCode;
    }

    private static bool TryParse(string[] args, out MigrationCommand? command, out string? error)
    {
        error = null;

        if (args.Length == 0)
        {
            command = MigrationCommand.Apply;
            return true;
        }

        if (args.Length > 1)
        {
            command = null;
            error = $"expected at most one argument, got {args.Length}";
            return false;
        }

        switch (args[0])
        {
            case StatusArgument:
                command = MigrationCommand.Status;
                return true;
            case DryRunArgument:
                command = MigrationCommand.DryRun;
                return true;
            case SeedDevArgument:
                command = MigrationCommand.SeedDev;
                return true;
            case HelpArgument:
            case HelpShortArgument:
                command = null;
                return true;
            default:
                command = null;
                error = $"unknown argument '{args[0]}'";
                return false;
        }
    }
}
