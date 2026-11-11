using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// Reads and prepares the migration journal table (01.4.1 contracts:
/// <c>name text primary key, checksum text not null,
/// applied_at timestamptz not null default now(), duration_ms int</c>). The
/// Migrator is its only writer; <c>evidgate_app</c> and <c>evidgate_operator</c>
/// hold SELECT on it (0001_foundation_roles.sql).
/// </summary>
internal static class SchemaMigrationStore
{
    /// <summary>
    /// Journal DDL. Also used by <see cref="SchemaMigrationJournal"/>, so the
    /// table shape exists exactly once. Idempotent: the Migrator prepares it
    /// before the first migration runs.
    /// </summary>
    internal static string CreateTableSql() =>
        $"""
        create table if not exists "{MigratorOptions.Schema}"."{MigratorOptions.JournalTableName}" (
            name text primary key,
            checksum text not null,
            applied_at timestamptz not null default now(),
            duration_ms integer
        );
        """;

    private static string SelectAppliedSql() =>
        $"""
        select name, checksum, applied_at, duration_ms
          from "{MigratorOptions.Schema}"."{MigratorOptions.JournalTableName}"
         order by name
        """;

    /// <summary>Opens an owner-role connection.</summary>
    /// <exception cref="NpgsqlException">The database could not be reached.</exception>
    internal static NpgsqlConnection Open(MigratorOptions options)
    {
        var connection = new NpgsqlConnection(options.SessionConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Creates the journal table when it does not exist yet. Called only on the
    /// apply path: the read-only commands (<c>--status</c>, <c>--dry-run</c>)
    /// must not write anything, and an absent journal table simply means nothing
    /// has been applied yet.
    /// </summary>
    internal static void EnsureTable(MigratorOptions options)
    {
        using var connection = Open(options);
        EnsureTable(connection, options.CommandTimeoutSeconds);
    }

    /// <summary>Creates the journal table on an existing connection.</summary>
    internal static void EnsureTable(NpgsqlConnection connection, int commandTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CreateTableSql();
        command.CommandTimeout = commandTimeoutSeconds;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the applied history in one short-lived connection. Read-only: a
    /// missing journal table (undefined_table) is an empty history, not an error.
    /// </summary>
    internal static IReadOnlyList<JournalEntry> ReadApplied(MigratorOptions options)
    {
        using var connection = Open(options);
        using var command = connection.CreateCommand();
        command.CommandText = SelectAppliedSql();
        command.CommandTimeout = options.CommandTimeoutSeconds;

        try
        {
            using var reader = command.ExecuteReader();

            var entries = new List<JournalEntry>();
            while (reader.Read())
            {
                entries.Add(new JournalEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3)));
            }

            return entries;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return [];
        }
    }
}
