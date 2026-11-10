using System.Globalization;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// Applies, inspects and previews the migration set (01.4.1-T1). The
/// <see cref="MigrationCommand"/> decides what to do; the return value's
/// <see cref="MigrationRunResult.ExitCode"/> is the process contract of §5.
/// </summary>
/// <remarks>
/// Order of operations for <see cref="MigrationCommand.Apply"/>:
/// load and validate the set → read the journal → compare checksums → refuse on
/// drift (nothing is applied, nothing is written) → create the journal table when
/// it is missing → hand the pending scripts to DbUp with one transaction per
/// script and the checksum journal. <see cref="MigrationCommand.Status"/> and
/// <see cref="MigrationCommand.DryRun"/> stop after the comparison: they only
/// read. Connection-string secrets never reach the output.
/// </remarks>
public static class MigratorRunner
{
    /// <summary>Runs one command and returns its outcome, already shaped as an exit code.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="output"/> is null.</exception>
    public static MigrationRunResult Run(MigratorOptions options, MigrationCommand command, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);

        var run = new RunContext(options, command, output);

        IReadOnlyList<MigrationScript> scripts;
        try
        {
            scripts = MigrationScriptLoader.Load(options.MigrationsPath);
        }
        catch (MigratorSetupException ex)
        {
            foreach (var error in ex.Errors)
            {
                run.Emit($"E {error}");
            }

            return run.Result(MigratorExitCodes.InvalidInvocation, plan: null);
        }

        try
        {
            // An apply serialises against other runners and is the only command
            // that writes; --status and --dry-run read without waiting and without
            // creating anything.
            using var migrationLock = command == MigrationCommand.Apply
                ? MigrationLock.Acquire(options, run.Emit)
                : null;

            var plan = BuildPlan(scripts, SchemaMigrationStore.ReadApplied(options));

            return command switch
            {
                MigrationCommand.Status => Report(run, plan),
                MigrationCommand.DryRun => Preview(run, plan),
                _ => Apply(run, plan),
            };
        }
        catch (NpgsqlException ex)
        {
            return run.Unavailable(ex);
        }
        catch (TimeoutException ex)
        {
            return run.Unavailable(ex);
        }
        catch (IOException ex)
        {
            return run.Unavailable(ex);
        }
    }

    /// <summary>Compares the validated script set with the journal; detects drift without touching the schema.</summary>
    private static MigrationPlan BuildPlan(IReadOnlyList<MigrationScript> scripts, IReadOnlyList<JournalEntry> applied)
    {
        var appliedByName = applied.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var pending = scripts.Where(script => !appliedByName.ContainsKey(script.Name)).ToList();

        var drifted = new List<MigrationDrift>();
        foreach (var script in scripts)
        {
            if (appliedByName.TryGetValue(script.Name, out var entry) && !string.Equals(entry.Checksum, script.Checksum, StringComparison.Ordinal))
            {
                drifted.Add(new MigrationDrift(script.Name, MigrationDriftKind.Modified, script.Checksum, entry.Checksum));
            }
        }

        foreach (var entry in applied)
        {
            if (!scripts.Any(script => string.Equals(script.Name, entry.Name, StringComparison.Ordinal)))
            {
                drifted.Add(new MigrationDrift(entry.Name, MigrationDriftKind.MissingOnDisk, FileChecksum: null, entry.Checksum));
            }
        }

        return new MigrationPlan(scripts, applied, pending, drifted);
    }

    private static MigrationRunResult Report(RunContext run, MigrationPlan plan)
    {
        foreach (var entry in plan.Applied)
        {
            run.Emit(
                $"applied {entry.Name} at {entry.AppliedAt.ToString("u", CultureInfo.InvariantCulture)}" +
                (entry.DurationMs is { } ms ? $" in {ms.ToString(CultureInfo.InvariantCulture)} ms" : " (duration unknown)"));
        }

        foreach (var script in plan.Pending)
        {
            run.Emit($"pending {script.Name}");
        }

        EmitDrift(run, plan);
        run.Emit(
            $"summary: {Count(plan.Applied)} applied, {Count(plan.Pending)} pending, {Count(plan.Drifted)} drifted");

        return run.Result(
            plan.Drifted.Count == 0 ? MigratorExitCodes.Success : MigratorExitCodes.ChecksumMismatch,
            plan);
    }

    private static MigrationRunResult Preview(RunContext run, MigrationPlan plan)
    {
        foreach (var script in plan.Pending)
        {
            run.Emit($"pending {script.Name}");
        }

        EmitDrift(run, plan);
        run.Emit($"dry run: {Count(plan.Pending)} migration(s) pending, nothing applied");

        return run.Result(MigratorExitCodes.Success, plan);
    }

    private static MigrationRunResult Apply(RunContext run, MigrationPlan plan)
    {
        if (plan.Drifted.Count > 0)
        {
            EmitDrift(run, plan);
            run.Emit($"refusing to apply: {Count(plan.Drifted)} applied migration(s) no longer match the repository; nothing was applied");
            return run.Result(MigratorExitCodes.ChecksumMismatch, plan);
        }

        if (plan.Pending.Count == 0)
        {
            run.Emit($"nothing to apply: {Count(plan.Applied)} migration(s) already applied");
            return run.Result(MigratorExitCodes.Success, plan);
        }

        foreach (var script in plan.Pending)
        {
            run.Emit($"pending {script.Name}");
        }

        // Only an apply creates the journal: `--status` and `--dry-run` never write.
        SchemaMigrationStore.EnsureTable(run.Options);

        run.Emit($"applying {Count(plan.Pending)} pending migration(s), one transaction each");

        var timing = new ScriptTimingPreprocessor();
        var checksums = plan.Scripts.ToDictionary(script => script.Name, script => script.Checksum, StringComparer.Ordinal);
        var log = new MigratorUpgradeLog(run.Emit, run.Secret);
        var journal = new SchemaMigrationJournal(run.Options, checksums, timing, line => run.Emit(line));

        var engine = DeployChanges.To
            .PostgresqlDatabase(run.Options.ConnectionString)
            .WithExecutionTimeout(TimeSpan.FromSeconds(run.Options.CommandTimeoutSeconds))
            .WithVariablesDisabled()
            .WithScripts(plan.Pending.Select(script => new SqlScript(script.Name, script.Contents)))
            .WithTransactionPerScript()
            .WithPreprocessor(new ScriptTimeoutPreprocessor(run.Options))
            .WithPreprocessor(timing)
            .JournalTo(journal)
            .LogTo(log)
            .Build();

        var result = engine.PerformUpgrade();
        var appliedNow = result.Scripts.Select(script => script.Name).ToList();

        if (!result.Successful)
        {
            var failed = result.ErrorScript?.Name ?? "unknown script";
            run.Emit($"migration failed in {failed}: {log.Redact(result.Error?.Message)}");
            var timeoutExit = ServerTimeout(result.Error);
            if (timeoutExit is { } serverTimeout)
            {
                EmitServerTimeout(run, log, failed, serverTimeout, result.Error);
            }
            else if (IsCommandTimeout(result.Error))
            {
                run.Emit(
                    $"{failed} exceeded the configured command timeout of {DescribeTimeout(run.Options.CommandTimeoutSeconds)} " +
                    $"({MigratorOptions.CommandTimeoutVariable}); raise it or split the migration");
            }

            run.Emit("the failed script's transaction was rolled back and it was not journalled");
            return run.Result(timeoutExit?.ExitCode ?? MigratorExitCodes.MigrationFailed, plan, appliedNow);
        }

        foreach (var name in appliedNow)
        {
            run.Emit($"applied {name}");
        }

        run.Emit($"summary: applied {Count(appliedNow)}, failed 0");
        return run.Result(MigratorExitCodes.Success, plan, appliedNow);
    }

    private static void EmitDrift(RunContext run, MigrationPlan plan)
    {
        foreach (var drift in plan.Drifted)
        {
            var reason = drift.Kind == MigrationDriftKind.Modified
                ? $"checksum mismatch (journal {Short(drift.AppliedChecksum)}, file {Short(drift.FileChecksum)})"
                : $"applied but the file is gone (journal {Short(drift.AppliedChecksum)})";
            run.Emit($"drifted {drift.Name}: {reason}");
        }
    }

    private const string LockNotAvailable = "55P03";
    private const string QueryCanceled = "57014";

    /// <summary>Finds a server-side lock or statement timeout in the failure chain.</summary>
    private static ServerTimeoutKind? ServerTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: LockNotAvailable })
            {
                return new ServerTimeoutKind(MigratorExitCodes.LockTimeout, "lock_timeout", MigratorOptions.LockTimeoutVariable);
            }

            if (current is PostgresException { SqlState: QueryCanceled })
            {
                return new ServerTimeoutKind(MigratorExitCodes.StatementTimeout, "statement_timeout", MigratorOptions.StatementTimeoutVariable);
            }
        }

        return null;
    }

    private static void EmitServerTimeout(RunContext run, MigratorUpgradeLog log, string script, ServerTimeoutKind kind, Exception? error)
    {
        var seconds = kind.ExitCode == MigratorExitCodes.LockTimeout
            ? run.Options.LockTimeoutSeconds
            : run.Options.StatementTimeoutSeconds;
        var relation = error is PostgresException { TableName: { Length: > 0 } table }
            ? $" (relation {log.Redact(table)})"
            : error is PostgresException { Where: { Length: > 0 } where } ? $" ({log.Redact(where.ReplaceLineEndings(" "))})" : string.Empty;
        // The runner value is a default: a script's own `set local` may have shortened or lengthened the limit that fired.
        var configured = seconds == 0 ? "disabled" : $"{seconds.ToString(CultureInfo.InvariantCulture)} s";
        run.Emit(
            $"{script} hit {kind.Setting} (runner setting {configured}; the script may set its own){relation}; " +
            (kind.ExitCode == MigratorExitCodes.LockTimeout
                ? "another session holds a conflicting lock: find it in pg_locks / pg_stat_activity, end or wait for it, then rerun"
                : "the statement ran longer than allowed: raise the limit or split the migration") +
            $" ({kind.Variable}; exit {kind.ExitCode.ToString(CultureInfo.InvariantCulture)})");
    }

    private sealed record ServerTimeoutKind(int ExitCode, string Setting, string Variable);

    /// <summary>True when the failure chain carries a driver timeout rather than a server error.</summary>
    private static bool IsCommandTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeTimeout(int seconds) =>
        seconds == 0 ? "0 (no command timeout)" : $"{seconds.ToString(CultureInfo.InvariantCulture)} s";

    private static string Count<T>(IReadOnlyCollection<T> items) =>
        items.Count.ToString(CultureInfo.InvariantCulture);

    private static string Short(string? checksum) =>
        checksum is { Length: >= 8 } ? checksum[..8] + "…" : checksum ?? "none";

    /// <summary>Collects the transcript and shapes the result.</summary>
    private sealed class RunContext
    {
        private readonly List<string> _lines = [];
        private readonly string? _secret;

        internal RunContext(MigratorOptions options, MigrationCommand command, TextWriter output)
        {
            Options = options;
            Command = command;
            Output = output;
            _secret = SecretRedactor.PasswordOf(options.ConnectionString);
        }

        internal MigratorOptions Options { get; }

        internal MigrationCommand Command { get; }

        internal TextWriter Output { get; }

        internal string? Secret => _secret;

        internal void Emit(string line)
        {
            Output.WriteLine(line);
            _lines.Add(line);
        }

        internal MigrationRunResult Unavailable(Exception exception)
        {
            Emit($"database unavailable for {Options.DescribeTarget()}: {SecretRedactor.Redact(exception.Message, _secret)}");
            if (IsCommandTimeout(exception))
            {
                Emit(
                    $"no response within the configured command timeout of {DescribeTimeout(Options.CommandTimeoutSeconds)} " +
                    $"({MigratorOptions.CommandTimeoutVariable})");
            }

            return Result(MigratorExitCodes.DatabaseUnavailable, plan: null);
        }

        internal MigrationRunResult Result(int exitCode, MigrationPlan? plan, IReadOnlyList<string>? appliedNow = null) =>
            new(exitCode, Command, [.. _lines], plan, appliedNow ?? []);
    }
}
