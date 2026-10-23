using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Evidgate.BuildingBlocks.Persistence.Readiness;

/// <summary>
/// The one readiness authority of the two processes (01.1.2-T1.3, AC3):
/// <c>SELECT max(name) FROM public.schema_migration</c> against the app-role data
/// source, compared ordinally with <see cref="ExpectedMigrationHead"/>. A head at
/// or beyond the expected head is ready — a newer journal is the normal state
/// during and after an expand/contract rollout (01.4.1 §11) — while a head
/// strictly older (or an empty/absent journal) is not. It only ever reads; a
/// missing journal reports unready and is never created here (01.4.1 owns it).
/// </summary>
public sealed partial class ReadinessProbe
{
    /// <summary>Journal head query, exactly as contracted (01.1.2 context, 01.4.1-T1).</summary>
    public const string JournalHeadSql = "SELECT max(name) FROM public.schema_migration";

    /// <summary>Seconds before an unreachable database counts as unready (00 §9.1: 503 with Retry-After).</summary>
    private const int CommandTimeoutSeconds = 5;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ReadinessProbe> _logger;

    /// <summary>Creates the probe over the app-role data source.</summary>
    public ReadinessProbe(NpgsqlDataSource dataSource, ILogger<ReadinessProbe> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>Runs one readiness check. Never throws for an unreachable or unmigrated database.</summary>
    public async ValueTask<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        var expected = ExpectedMigrationHead.Value;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(JournalHeadSql, connection)
            {
                CommandTimeout = CommandTimeoutSeconds,
            };

            var head = await command.ExecuteScalarAsync(cancellationToken) as string;
            var comparison = string.CompareOrdinal(head, expected);

            if (comparison > 0)
            {
                LogJournalAhead(head!, expected);
                return ReadinessResult.IsReadyAhead(expected, head!);
            }

            if (comparison == 0)
            {
                return ReadinessResult.IsReady(expected, head);
            }

            LogJournalBehind(head, expected);
            return ReadinessResult.NotReady(ReadinessReason.JournalBehind, expected, head);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            LogJournalMissing();
            return ReadinessResult.NotReady(ReadinessReason.JournalMissing, expected, null);
        }
        catch (PostgresException exception)
        {
            // A server-side failure after connecting — e.g. the application role
            // lost its SELECT grant on the journal (42501) — is a distinct,
            // actionable reason, not "unreachable". Failures that mean the session
            // itself could not be established (08xxx connection, 28xxx auth, 3Dxxx
            // missing database) stay "unreachable".
            if (exception.SqlState.StartsWith("08", StringComparison.Ordinal)
                || exception.SqlState.StartsWith("28", StringComparison.Ordinal)
                || exception.SqlState.StartsWith("3D", StringComparison.Ordinal))
            {
                LogDatabaseUnreachable(exception);
                return ReadinessResult.NotReady(ReadinessReason.DatabaseUnreachable, expected, null);
            }

            LogJournalUnreadable(exception);
            return ReadinessResult.NotReady(ReadinessReason.CheckFailed, expected, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A shutdown or an aborted request cancels the check; that is not an
            // unreachable database and must not be logged as one.
            return ReadinessResult.NotReady(ReadinessReason.CheckFailed, expected, null);
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
        {
            LogDatabaseUnreachable(exception);
            return ReadinessResult.NotReady(ReadinessReason.DatabaseUnreachable, expected, null);
        }
    }

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Readiness: schema_migration head {JournalHead} is older than this build's expected head {ExpectedHead}")]
    private partial void LogJournalBehind(string? journalHead, string? expectedHead);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Readiness: public.schema_migration does not exist; migrations have not been applied")]
    private partial void LogJournalMissing();

    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "Readiness: the database is unreachable")]
    private partial void LogDatabaseUnreachable(Exception exception);

    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Information,
        Message = "Readiness: schema_migration head {JournalHead} is newer than this build's expected head {ExpectedHead}; serving (expand/contract)")]
    private partial void LogJournalAhead(string journalHead, string expectedHead);

    [LoggerMessage(
        EventId = 1105,
        Level = LogLevel.Warning,
        Message = "Readiness: public.schema_migration cannot be read (privilege or shape)")]
    private partial void LogJournalUnreadable(Exception exception);
}
