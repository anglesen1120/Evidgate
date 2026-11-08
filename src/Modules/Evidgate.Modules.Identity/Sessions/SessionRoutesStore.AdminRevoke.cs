using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The administrative bulk revoke, split out of <see cref="SessionRoutesStore"/> so neither file carries two
/// responsibilities: the admin route keeps its own statement and refusals. Everything it shares with the owner
/// routes — the data source, the clock, the proof parameter and the status strings — comes from the primary part.
/// </summary>
internal sealed partial class SessionRoutesStore
{
    /// <inheritdoc />
    public async ValueTask<(SessionRouteStatus Status, int Revoked)> RevokeAllForUserAsAdminAsync(
        byte[] sessionProof,
        Guid targetUserId,
        Guid? orgId,
        string? requestId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        if (sessionProof.Length == 0)
        {
            return (SessionRouteStatus.Unauthenticated, 0);
        }

        await using var command = dataSource.CreateCommand(
            "select status, revoked_count from evidgate_resolve.session_revoke_all_admin("
            + "cast(@cookie_hmacs as bytea[]), @target_user_id, @org_id, @now, @request_id, @trace_id)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("target_user_id", targetUserId);

        // Explicit type: a Guid? bound as DBNull has no CLR type to infer the OID from.
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid)
        {
            Value = (object?)orgId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        // Same two correlation sources as the single admin revoke; the definer validates and nulls them.
        command.Parameters.Add(new NpgsqlParameter("request_id", NpgsqlDbType.Text)
        {
            Value = (object?)requestId ?? DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("trace_id", NpgsqlDbType.Text)
        {
            Value = (object?)traceId ?? DBNull.Value,
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // One row per call, always: no row means the function and this build disagree, so the request is
            // refused rather than answered as "nothing was live".
            throw new InvalidOperationException(
                "evidgate_resolve.session_revoke_all_admin returned no row; migration 0085 and this build disagree");
        }

        var status = reader.GetString(0);
        var revoked = reader.GetInt32(1);

        if (string.Equals(status, RevokedStatus, StringComparison.Ordinal))
        {
            SessionTelemetry.Revoked(revoked);

            return (SessionRouteStatus.Done, revoked);
        }

        return status switch
        {
            NotFoundStatus => (SessionRouteStatus.NotFound, 0),
            UnauthenticatedStatus => (SessionRouteStatus.Unauthenticated, 0),
            _ => throw UnknownStatus("session_revoke_all_admin", status),
        };
    }
}
