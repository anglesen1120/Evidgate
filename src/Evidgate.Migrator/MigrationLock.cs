using System.Data;
using Npgsql;

namespace Evidgate.Migrator;

/// <summary>
/// Serialises migration runs (01.4.1-T1, deploy gate 10.2.2): a second Migrator
/// that starts while another is applying the same database waits for the first to
/// finish instead of racing it into duplicate work or a half-seen journal.
/// </summary>
/// <remarks>
/// A <em>session</em>-level advisory lock on a dedicated connection: the apply
/// spans one transaction per file, so a transaction-level lock would be released
/// between files. The key is the ASCII text <c>evidgate</c> as a signed 64-bit
/// integer — one fixed, documented value per database, and a crashed runner
/// releases it as soon as its session ends. Read-only commands
/// (<c>--status</c>, <c>--dry-run</c>) never take it: they must not queue behind a
/// long migration.
/// </remarks>
internal sealed class MigrationLock : IDisposable
{
    /// <summary>Advisory lock key shared by every Migrator run, derived from "evidgate".</summary>
    internal const long Key = 0x6576_6964_6761_7465;

    private const string TryLockSql = "select pg_try_advisory_lock(@key)";
    private const string LockSql = "select pg_advisory_lock(@key)";
    private const string UnlockSql = "select pg_advisory_unlock(@key)";

    private readonly MigratorOptions _options;
    private readonly NpgsqlConnection _connection;

    private MigrationLock(MigratorOptions options, NpgsqlConnection connection)
    {
        _options = options;
        _connection = connection;
    }

    /// <summary>
    /// Takes the migration lock, waiting (and saying so) when another run holds it.
    /// </summary>
    /// <exception cref="NpgsqlException">The lock could not be taken (unreachable database, timeout).</exception>
    internal static MigrationLock Acquire(MigratorOptions options, Action<string> log)
    {
        var connection = SchemaMigrationStore.Open(options);
        try
        {
            // Waiting for another run is the point of this lock: a role- or database-level
            // lock_timeout/statement_timeout must not turn that wait into an error.
            using (var reset = connection.CreateCommand())
            {
                reset.CommandText = "set lock_timeout = 0; set statement_timeout = 0";
                reset.ExecuteNonQuery();
            }

            if (!TryAcquire(connection, options))
            {
                log("another migration run holds the migration lock; waiting for it to finish");
                Execute(connection, options, LockSql);
            }

            return new MigrationLock(options, connection);
        }
        catch (NpgsqlException)
        {
            connection.Dispose();
            throw;
        }
        catch (TimeoutException)
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Releases the lock explicitly before closing the session. Closing a session
    /// releases it anyway, but an explicit unlock keeps the guarantee independent of
    /// connection-pool reset behaviour: the connection may be reused by another
    /// runner in this process, and a lock left behind would block it forever.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                // Best effort: a failed unlock cannot be reported from Dispose, and
                // closing the session is the authoritative release.
                _ = Execute(_connection, _options, UnlockSql);

                // The session-level zeros must not survive on a connection a pooler hands out again.
                using var reset = _connection.CreateCommand();
                reset.CommandText = "reset lock_timeout; reset statement_timeout";
                reset.ExecuteNonQuery();
            }
        }
        catch (NpgsqlException)
        {
            // The session is going away; its locks go with it.
        }
        catch (InvalidOperationException)
        {
            // Already closed by the caller.
        }

        _connection.Dispose();
    }

    private static bool TryAcquire(NpgsqlConnection connection, MigratorOptions options) =>
        Execute(connection, options, TryLockSql) is true;

    private static object? Execute(NpgsqlConnection connection, MigratorOptions options, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        command.CommandText = sql;
        command.Parameters.AddWithValue("key", Key);
        return command.ExecuteScalar();
    }
}
