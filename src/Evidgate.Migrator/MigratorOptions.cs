using System.Globalization;
using Evidgate.BuildingBlocks.SharedKernel.Secrets;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// Migrator configuration, read from the environment only (00 §5.7 S3): the
/// connection string never comes from a file in the repository, and it is never
/// logged — <see cref="DescribeTarget"/> prints host/database/user for the
/// operator without the password, and <see cref="ToString"/> is what keeps an
/// accidental interpolation of this object from leaking one (a record's
/// generated <c>ToString</c> would print every property, including the secret).
/// The connection string is resolved through <see cref="ISecretProvider"/>
/// (10.1.1-T2), so a managed secret store (O3) can replace the environment
/// without touching this type.
/// </summary>
public sealed class MigratorOptions
{
    public const string ConnectionStringVariable = SecretNames.MigratorConnectionString;
    public const string MigrationsPathVariable = "EVIDGATE_MIGRATOR__MIGRATIONSPATH";
    public const string CommandTimeoutVariable = "EVIDGATE_MIGRATOR__COMMANDTIMEOUTSECONDS";
    public const string LockTimeoutVariable = "EVIDGATE_MIGRATOR__LOCKTIMEOUTSECONDS";
    public const string StatementTimeoutVariable = "EVIDGATE_MIGRATOR__STATEMENTTIMEOUTSECONDS";

    /// <summary>
    /// Command timeout, in seconds, for every statement the Migrator runs:
    /// <c>0</c> (the default) means no timeout, which is the only safe default
    /// for a deploy-time tool — a long index build or backfill must not be cut in
    /// half, and the deploy gate bounds the run itself (10.2.2). A driver default
    /// of 30 s would otherwise abort such a statement.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 0;

    /// <summary>
    /// Default <c>lock_timeout</c> in seconds, sent as a startup option on every session of the run.
    /// DDL on a hot table queues behind any open transaction and every later query then
    /// queues behind the DDL, so a script that cannot get its lock quickly must fail and roll back.
    /// </summary>
    public const int DefaultLockTimeoutSeconds = 5;

    /// <summary>
    /// Default <c>statement_timeout</c> in seconds, applied like <see cref="DefaultLockTimeoutSeconds"/>.
    /// Generous on purpose: an index build or backfill must finish; the value only stops a runaway.
    /// </summary>
    public const int DefaultStatementTimeoutSeconds = 900;

    /// <summary>Largest value PostgreSQL accepts for a millisecond timeout (2^31-1 ms) expressed in whole seconds.</summary>
    public const int MaxServerTimeoutSeconds = int.MaxValue / 1000;

    /// <summary>Schema holding the journal table (01.4.1 contracts: <c>public.schema_migration</c>).</summary>
    public const string Schema = "public";

    /// <summary>Journal table name; it is part of the Api/Worker readiness contract, so it is not configurable.</summary>
    public const string JournalTableName = "schema_migration";

    /// <summary>Marker file used to resolve the repository root when the migrations path is not configured.</summary>
    public const string RepositoryMarkerFile = "Evidgate.slnx";

    /// <summary>
    /// Owner-role connection string. Never logged, never written to a file. The
    /// setter is not public (10.1.1-T2), so no configuration binder can write it:
    /// <see cref="FromEnvironment"/> resolves it through <see cref="ISecretProvider"/>,
    /// and the test fixtures reach it through <c>InternalsVisibleTo</c>.
    /// </summary>
    public string ConnectionString { get; internal init; } = string.Empty;

    /// <summary>
    /// <see cref="ConnectionString"/> with <c>lock_timeout</c> and <c>statement_timeout</c> as startup
    /// options, so every session the run opens carries both limits. An operator-supplied
    /// <c>Options</c> value is kept and ours is appended; PostgreSQL applies the last duplicate,
    /// so the configured limits win. A script's own <c>set local</c> still overrides them.
    /// </summary>
    internal string SessionConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            var limits = string.Create(
                CultureInfo.InvariantCulture,
                $"-c lock_timeout={LockTimeoutSeconds * 1000L}ms -c statement_timeout={StatementTimeoutSeconds * 1000L}ms");
            builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? limits : $"{builder.Options} {limits}";
            return builder.ConnectionString;
        }
    }

    /// <summary>Directory holding the migration files.</summary>
    public required string MigrationsPath { get; init; }

    /// <summary>Seconds before a statement is aborted; <c>0</c> disables the command timeout.</summary>
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    /// <summary>Seconds a script may wait for a lock before it fails; <c>0</c> disables <c>lock_timeout</c> explicitly.</summary>
    public int LockTimeoutSeconds { get; init; } = DefaultLockTimeoutSeconds;

    /// <summary>Seconds one statement of a script may run before it fails; <c>0</c> disables <c>statement_timeout</c> explicitly.</summary>
    public int StatementTimeoutSeconds { get; init; } = DefaultStatementTimeoutSeconds;

    /// <summary>
    /// Reads the <c>EVIDGATE_MIGRATOR__*</c> variables. The connection string is
    /// a secret and is resolved through <see cref="ISecretProvider"/> (10.1.1-T2);
    /// the other variables are not secret and are read from the snapshot directly.
    /// </summary>
    /// <param name="environment">Environment snapshot; defaults to the process environment.</param>
    /// <param name="startDirectory">
    /// Directory the default migrations path is resolved from; defaults to the
    /// application base directory, then walks up to the repository root.
    /// </param>
    /// <param name="secrets">
    /// Resolves the connection string; defaults to an <see cref="EnvironmentSecretProvider"/>
    /// over the same snapshot. This is the swap point for a managed secret store (O3).
    /// </param>
    /// <exception cref="MigratorSetupException">A required variable is missing or a value is invalid.</exception>
    public static MigratorOptions FromEnvironment(
        IReadOnlyDictionary<string, string?>? environment = null,
        string? startDirectory = null,
        ISecretProvider? secrets = null)
    {
        var values = environment ?? ProcessEnvironment();
        var errors = new List<string>();

        // 10.1.1-T2: the connection string is a secret, so it is resolved through
        // the one secret interface instead of being read from the dictionary
        // directly. The default provider is built over the same snapshot, so the
        // environment stays the source; a managed store (O3) is passed in instead.
        var provider = secrets ?? new EnvironmentSecretProvider(
            name => values.TryGetValue(name, out var value) ? value : null);
        var connectionString = provider.GetSecret(SecretNames.MigratorConnectionString);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            errors.Add($"missing {ConnectionStringVariable}: the owner-role connection string must come from the environment (00 §5.7 S3)");
        }
        else
        {
            try
            {
                _ = new NpgsqlConnectionStringBuilder(connectionString);
            }
            catch (ArgumentException ex)
            {
                // The driver's message can quote the input, so it is redacted too.
                errors.Add(
                    $"invalid {ConnectionStringVariable}: not a PostgreSQL connection string " +
                    $"({SecretRedactor.Redact(ex.Message, SecretRedactor.PasswordOf(connectionString))})");
                connectionString = null;
            }
        }

        var migrationsPath = Read(values, MigrationsPathVariable);
        if (string.IsNullOrWhiteSpace(migrationsPath))
        {
            migrationsPath = DefaultMigrationsPath(startDirectory);
        }

        var commandTimeout = DefaultCommandTimeoutSeconds;
        var commandTimeoutText = Read(values, CommandTimeoutVariable);
        if (!string.IsNullOrWhiteSpace(commandTimeoutText)
            && (!int.TryParse(commandTimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out commandTimeout)
                || commandTimeout < 0))
        {
            errors.Add($"invalid {CommandTimeoutVariable}: '{commandTimeoutText}' is not a non-negative number of seconds (0 = no command timeout)");
            commandTimeout = DefaultCommandTimeoutSeconds;
        }

        var lockTimeout = ReadServerTimeout(values, LockTimeoutVariable, DefaultLockTimeoutSeconds, errors);
        var statementTimeout = ReadServerTimeout(values, StatementTimeoutVariable, DefaultStatementTimeoutSeconds, errors);

        if (errors.Count > 0)
        {
            throw new MigratorSetupException(errors);
        }

        return new MigratorOptions
        {
            ConnectionString = connectionString!,
            MigrationsPath = Path.GetFullPath(migrationsPath!),
            CommandTimeoutSeconds = commandTimeout,
            LockTimeoutSeconds = lockTimeout,
            StatementTimeoutSeconds = statementTimeout,
        };
    }

    /// <summary>
    /// Operator-facing description of the target: host, port, database and user.
    /// The password never appears (00 §5.7 S3).
    /// </summary>
    public string DescribeTarget()
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
            var port = builder.Port.ToString(CultureInfo.InvariantCulture);
            return $"{builder.Host}:{port}/{builder.Database} as '{builder.Username}'";
        }
        catch (ArgumentException)
        {
            return "<unparsable connection string>";
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{nameof(MigratorOptions)} {{ MigrationsPath = {MigrationsPath}, Target = {DescribeTarget()}, CommandTimeoutSeconds = {CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}, " +
        $"LockTimeoutSeconds = {LockTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}, StatementTimeoutSeconds = {StatementTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} }}";

    /// <summary>Reads a server-side timeout; an unset value takes the default, anything unparsable, negative or too large fails closed.</summary>
    private static int ReadServerTimeout(IReadOnlyDictionary<string, string?> values, string key, int fallback, List<string> errors)
    {
        var text = Read(values, key);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= MaxServerTimeoutSeconds)
        {
            return seconds;
        }

        errors.Add($"invalid {key}: '{text}' is not a whole number of seconds between 0 and {MaxServerTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} (0 = disabled)");
        return fallback;
    }

    /// <summary>Default migrations path: <c>&lt;repo root&gt;/migrations</c>, falling back to the application directory.</summary>
    private static string DefaultMigrationsPath(string? startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, RepositoryMarkerFile)))
            {
                return Path.Combine(directory.FullName, "migrations");
            }

            directory = directory.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "migrations");
    }

    private static string? Read(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    /// <summary>The process environment as a snapshot, the same shape the tests pass in.</summary>
    internal static Dictionary<string, string?> ProcessEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value as string;
            }
        }

        return values;
    }
}
