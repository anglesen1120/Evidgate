using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Evidgate.Modules.Identity.Ci;

namespace Evidgate.Migrator;

/// <summary>
/// Command-line surface of the Migrator: one process, one command.
/// Kept separate from <see cref="Program"/> so the argument/environment
/// handling is testable without spawning a process.
/// </summary>
public static class MigratorHost
{
    private const string StatusArgument = "--status";
    private const string DryRunArgument = "--dry-run";
    private const string SeedDevArgument = "seed-dev";
    private const string WithTokenArgument = "--with-token";
    private const string HelpArgument = "--help";
    private const string HelpShortArgument = "-h";

    /// <summary>Usage text, also printed for <c>--help</c>.</summary>
    public static string Usage =>
        $"""
        evidgate-migrator — applies plain-SQL migrations as the owner role, journalled in {MigratorOptions.JournalTableName} (00 §5.2, ADR-0003).

        usage: Evidgate.Migrator [--status | --dry-run | seed-dev [--with-token] | --help]

          (no arguments)  apply every pending migration, one transaction per file
          --status        print applied, pending and drifted files; exit {MigratorExitCodes.ChecksumMismatch} on drift, applies nothing
          --dry-run       print the pending files without applying anything
          seed-dev        write the synthetic dev tenant (org, project, dev user + roles, CI binding) and the dev
                          project's sample case, suite and suite revision, through the real Catalog commands on the
                          app-role connection; idempotent; refuses unless DOTNET_ENVIRONMENT=Development and every
                          database host is local
          seed-dev --with-token
                          also mint ONE dev ingest token (name dev-seed, no expiry) and ONE sample policy revision for the
                          dev project, through the real Identity/Policy commands on the app-role connection; the token
                          is printed once on stdout as {DevSeedToken.TokenVariable}=…, and a rerun prints how to recover instead;
                          refuses unless Development, every database host is local, {SecretNames.CiPepper} is configured
                          and {SecretNames.DatabaseConnectionString} carries the app role

        environment:
          {MigratorOptions.ConnectionStringVariable}   (required) owner-role connection string
          {SecretNames.DatabaseConnectionString}   (required) app-role (evidgate_app) connection string
          {SecretNames.CiPepper}   (required by --with-token) base64 pepper, at least {CiSecrets.MinKeyBytes} bytes
          {MigratorOptions.MigrationsPathVariable}     (optional) defaults to <repo>/migrations
          {MigratorOptions.CommandTimeoutVariable} (optional) seconds per statement, default {MigratorOptions.DefaultCommandTimeoutSeconds} = no timeout
          {MigratorOptions.LockTimeoutVariable} (optional) lock_timeout per script, default {MigratorOptions.DefaultLockTimeoutSeconds} s, 0 = disabled
          {MigratorOptions.StatementTimeoutVariable} (optional) statement_timeout per script, default {MigratorOptions.DefaultStatementTimeoutSeconds} s, 0 = disabled

        exit codes: {MigratorExitCodes.Success} ok · {MigratorExitCodes.InvalidInvocation} invalid invocation or migration set · {MigratorExitCodes.ChecksumMismatch} checksum mismatch · {MigratorExitCodes.MigrationFailed} migration failure · {MigratorExitCodes.DatabaseUnavailable} database unavailable · {MigratorExitCodes.LockTimeout} lock timeout · {MigratorExitCodes.StatementTimeout} statement timeout
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

        if (!TryParse(args, out var command, out var withToken, out var usageError))
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
            return DevSeed.Run(options, environment, output, error, withToken);
        }

        return MigratorRunner.Run(options, command.Value, output).ExitCode;
    }

    private static bool TryParse(string[] args, out MigrationCommand? command, out bool withToken, out string? error)
    {
        error = null;
        withToken = false;

        if (args.Length == 0)
        {
            command = MigrationCommand.Apply;
            return true;
        }

        if (args.Length > 2)
        {
            command = null;
            error = $"expected at most two arguments, got {args.Length}";
            return false;
        }

        if (args.Length == 2)
        {
            // The only two-argument form is the seed plus its one switch. Every other pair keeps the wording a
            // one-argument command line has always answered, so callers that match on it are unaffected.
            var seed = args[0] == SeedDevArgument || args[1] == SeedDevArgument;
            var token = args[0] == WithTokenArgument || args[1] == WithTokenArgument;

            if (seed && token)
            {
                command = MigrationCommand.SeedDev;
                withToken = true;
                return true;
            }

            command = null;
            error = token
                ? $"'{WithTokenArgument}' is only valid with {SeedDevArgument}"
                : "expected at most one argument, got 2";
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
            case WithTokenArgument:
                command = null;
                error = $"'{WithTokenArgument}' is only valid with {SeedDevArgument}";
                return false;
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
