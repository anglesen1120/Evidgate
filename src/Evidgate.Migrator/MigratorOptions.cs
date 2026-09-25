using System.Globalization;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// Migrator configuration, read from the environment only (00 §5.7 S3): the
/// connection string never comes from a file in the repository, and it is never
/// logged — <see cref="DescribeTarget"/> prints host/database/user for the
/// operator without the password, and <see cref="ToString"/> is what keeps an
/// accidental interpolation of this object from leaking one (a record's
/// generated <c>ToString</c> would print every property, including the secret).
/// </summary>
public sealed class MigratorOptions
{
    public const string ConnectionStringVariable = "EVIDGATE_MIGRATOR__CONNECTIONSTRING";
    public const string MigrationsPathVariable = "EVIDGATE_MIGRATOR__MIGRATIONSPATH";
    public const string CommandTimeoutVariable = "EVIDGATE_MIGRATOR__COMMANDTIMEOUTSECONDS";

    /// <summary>
    /// Command timeout, in seconds, for every statement the Migrator runs:
    /// <c>0</c> (the default) means no timeout, which is the only safe default
    /// for a deploy-time tool — a long index build or backfill must not be cut in
    /// half, and the deploy gate bounds the run itself (10.2.2). A driver default
    /// of 30 s would otherwise abort such a statement.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 0;

    /// <summary>Schema holding the journal table (01.4.1 contracts: <c>public.schema_migration</c>).</summary>
    public const string Schema = "public";

    /// <summary>Journal table name; it is part of the Api/Worker readiness contract, so it is not configurable.</summary>
    public const string JournalTableName = "schema_migration";

    /// <summary>Marker file used to resolve the repository root when the migrations path is not configured.</summary>
    public const string RepositoryMarkerFile = "Evidgate.slnx";

    /// <summary>Owner-role connection string. Never logged, never written to a file.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>Directory holding the migration files.</summary>
    public required string MigrationsPath { get; init; }

    /// <summary>Seconds before a statement is aborted; <c>0</c> disables the command timeout.</summary>
    public int CommandTimeoutSeconds { get; init; } = DefaultCommandTimeoutSeconds;

    /// <summary>
    /// Reads the <c>EVIDGATE_MIGRATOR__*</c> variables.
    /// </summary>
    /// <param name="environment">Environment snapshot; defaults to the process environment.</param>
    /// <param name="startDirectory">
    /// Directory the default migrations path is resolved from; defaults to the
    /// application base directory, then walks up to the repository root.
    /// </param>
    /// <exception cref="MigratorSetupException">A required variable is missing or a value is invalid.</exception>
    public static MigratorOptions FromEnvironment(
        IReadOnlyDictionary<string, string?>? environment = null,
        string? startDirectory = null)
    {
        var values = environment ?? ProcessEnvironment();
        var errors = new List<string>();

        var connectionString = Read(values, ConnectionStringVariable);
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

        if (errors.Count > 0)
        {
            throw new MigratorSetupException(errors);
        }

        return new MigratorOptions
        {
            ConnectionString = connectionString!,
            MigrationsPath = Path.GetFullPath(migrationsPath!),
            CommandTimeoutSeconds = commandTimeout,
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
        $"{nameof(MigratorOptions)} {{ MigrationsPath = {MigrationsPath}, Target = {DescribeTarget()}, CommandTimeoutSeconds = {CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} }}";

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

    private static Dictionary<string, string?> ProcessEnvironment()
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
