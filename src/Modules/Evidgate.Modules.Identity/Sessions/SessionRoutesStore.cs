using Evidgate.BuildingBlocks.Persistence.Tasks;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>The outcome of one session route, as the store found it.</summary>
internal enum SessionRouteStatus
{
    /// <summary>The statement ran and answered: a list, a revocation, or a cleanup count.</summary>
    Done,

    /// <summary>No such session for this caller: unknown, another account's, another org's, or dead — one answer (AC6).</summary>
    NotFound,

    /// <summary>The presented proof matches no live session, so the request has no acting user.</summary>
    Unauthenticated,
}

/// <summary>One row of <c>GET /v1/auth/sessions</c> (02.3.2 §5, AC6).</summary>
/// <param name="Id">The session's id — the value <c>DELETE /v1/auth/sessions/{s}</c> takes.</param>
/// <param name="Current">Whether this is the session the request was authenticated with.</param>
/// <param name="CreatedAt">When the session was minted.</param>
/// <param name="LastSeenAt">The last request that slid the idle deadline.</param>
/// <param name="ExpiresAt">The idle deadline (sliding).</param>
/// <param name="AbsoluteExpiresAt">The hard deadline no activity extends.</param>
/// <param name="Ip">The client address recorded at minting, or null.</param>
/// <param name="UserAgent">The user agent recorded at minting, or null.</param>
internal sealed record SessionListItem(
    Guid Id,
    bool Current,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset AbsoluteExpiresAt,
    string? Ip,
    string? UserAgent);

/// <summary>What one <c>session-cleanup</c> call did (02.3.2 §7).</summary>
/// <param name="Stamped">Rows that became <c>expired</c> in this call.</param>
/// <param name="Deleted">Rows hard-deleted in this call.</param>
internal sealed record SessionCleanupResult(int Stamped, int Deleted);

/// <summary>
/// The persistence seam of the four session routes and the cleanup sweep (02.3.2-T2.1):
/// list, logout, revoke one, revoke all but the current, and one bounded cleanup batch —
/// all through the <c>evidgate_resolve</c> definers of migration 0048.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pre-tenant, like the routes.</b> These calls run before any tenant scope exists: a
/// user may belong to several orgs or to none, and the acting user is resolved from the
/// presented credential INSIDE the definer, so no method takes a user id. The four route
/// statements use the app-role <see cref="NpgsqlDataSource"/>; the cleanup uses the
/// <c>evidgate_worker_sched</c> connection of
/// <see cref="IWorkerSchedDataSource"/>, because EXECUTE on
/// <c>evidgate.session_cleanup</c> is granted to the sweep role alone (TL T80).
/// </para>
/// <para>
/// <b>What is passed.</b> <c>sessionProof</c> is the HMAC of the LIVE cookie value —
/// <c>SessionRequestOutcome.GetSessionProof()</c> — never the cookie itself and never a
/// second hashing done here. A missing or empty proof answers
/// <see cref="SessionRouteStatus.Unauthenticated"/> without a statement, the
/// <see cref="NpgsqlLinkIdentityStore"/> rule.
/// </para>
/// <para>
/// <b>What is returned.</b> A business outcome is a status, never a failure: a dead
/// session, a foreign id and an unknown id are all
/// <see cref="SessionRouteStatus.NotFound"/>, so the route's 404 cannot be turned into an
/// oracle. A database fault, a timeout and a status this build does not know are
/// <see cref="InvalidOperationException"/> or the driver's own exception and PROPAGATE:
/// the caller answers 503/500 and never 401, because a lookup that did not run must not
/// sign a healthy user out (D-S85-D2).
/// </para>
/// <para>
/// <b>Nothing is logged here.</b> The cookie value, the proof hash, the session id, the
/// address and the user agent are personal data or credentials (02.3.2 §8); the only
/// telemetry this class adds is the revocation count, through
/// <see cref="SessionTelemetry.Revoked"/>.
/// </para>
/// </remarks>
internal interface ISessionRoutesStore
{
    /// <summary>The caller's live sessions, current one flagged, newest first.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns><see cref="SessionRouteStatus.Done"/> with the rows, or <see cref="SessionRouteStatus.Unauthenticated"/>.</returns>
    ValueTask<(SessionRouteStatus Status, IReadOnlyList<SessionListItem> Items)> ListAsync(
        byte[] sessionProof,
        CancellationToken cancellationToken);

    /// <summary>Logout: revokes the session the proof belongs to.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns><see cref="SessionRouteStatus.Done"/> when the row is revoked, else <see cref="SessionRouteStatus.Unauthenticated"/>.</returns>
    ValueTask<SessionRouteStatus> LogoutAsync(byte[] sessionProof, CancellationToken cancellationToken);

    /// <summary>Revokes one session: the caller's own, or a member's when the caller is an admin of <paramref name="orgId"/>.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="sessionId">The target session id from the route.</param>
    /// <param name="orgId">The org named by <c>X-Evidgate-Org</c>, or null for the owner path only.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns><see cref="SessionRouteStatus.Done"/>, <see cref="SessionRouteStatus.NotFound"/> or <see cref="SessionRouteStatus.Unauthenticated"/>.</returns>
    ValueTask<SessionRouteStatus> RevokeOneAsync(
        byte[] sessionProof,
        Guid sessionId,
        Guid? orgId,
        CancellationToken cancellationToken);

    /// <summary>Revokes every live session of the caller, keeping the current one unless told otherwise.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="keepCurrent">True keeps the session the request was authenticated with.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>The status and how many rows this call revoked.</returns>
    ValueTask<(SessionRouteStatus Status, int Revoked)> RevokeOthersAsync(
        byte[] sessionProof,
        bool keepCurrent,
        CancellationToken cancellationToken);

    /// <summary>
    /// One bounded cleanup batch: stamps dead rows <c>expired</c> once and deletes rows whose
    /// last deadline passed more than 30 days ago, leaf first.
    /// </summary>
    /// <param name="now">The instant the sweep judges every deadline against.</param>
    /// <param name="limit">Rows per part; clamped to 0..10000, and 0 sweeps nothing.</param>
    /// <param name="cancellationToken">Task abort.</param>
    /// <returns>How many rows this batch stamped and deleted.</returns>
    ValueTask<SessionCleanupResult> CleanupAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// One instance per host, over the app-role <see cref="NpgsqlDataSource"/> and the sweep
/// <see cref="IWorkerSchedDataSource"/>; the clock is the deployment's
/// <see cref="TimeProvider"/>, so a test moves every deadline without waiting.
/// </remarks>
/// <param name="dataSource">The app-role data source the four route statements run on.</param>
/// <param name="sweep">The <c>evidgate_worker_sched</c> connection the cleanup batch runs on.</param>
/// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
internal sealed class SessionRoutesStore(
    NpgsqlDataSource dataSource,
    IWorkerSchedDataSource sweep,
    TimeProvider clock) : ISessionRoutesStore
{
    /// <summary>The largest batch the cleanup function accepts, in rows per part.</summary>
    internal const int MaxCleanupBatch = 10_000;

    private const string OkStatus = "ok";
    private const string RevokedStatus = "revoked";
    private const string NotFoundStatus = "not_found";
    private const string UnauthenticatedStatus = "unauthenticated";

    /// <inheritdoc />
    public async ValueTask<(SessionRouteStatus Status, IReadOnlyList<SessionListItem> Items)> ListAsync(
        byte[] sessionProof,
        CancellationToken cancellationToken)
    {
        if (sessionProof.Length == 0)
        {
            return (SessionRouteStatus.Unauthenticated, []);
        }

        await using var command = dataSource.CreateCommand(
            "select session_id, is_current, created_at, last_seen_at, expires_at, absolute_expires_at,"
            + " ip, user_agent, status from evidgate_resolve.session_list(cast(@cookie_hmacs as bytea[]), @now)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<SessionListItem>();
        string? status = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The function answers ONE row per live session, or exactly one
            // `unauthenticated` row; a caller with a live session always has at least one.
            status ??= reader.GetString(8);
            if (!string.Equals(status, OkStatus, StringComparison.Ordinal))
            {
                break;
            }

            items.Add(new SessionListItem(
                reader.GetGuid(0),
                reader.GetBoolean(1),
                await reader.GetFieldValueAsync<DateTimeOffset>(2, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(3, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(4, cancellationToken).ConfigureAwait(false),
                await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken).ConfigureAwait(false),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7)));
        }

        if (status is null)
        {
            // The statement always answers at least one row; no row means the function and this
            // build disagree, so the request is refused (never answered as "no sessions").
            throw new InvalidOperationException(
                "evidgate_resolve.session_list returned no row; migration 0048 and this build disagree");
        }

        return string.Equals(status, OkStatus, StringComparison.Ordinal)
            ? (SessionRouteStatus.Done, items)
            : (SessionRouteStatus.Unauthenticated, []);
    }

    /// <inheritdoc />
    public async ValueTask<SessionRouteStatus> LogoutAsync(
        byte[] sessionProof,
        CancellationToken cancellationToken)
    {
        if (sessionProof.Length == 0)
        {
            return SessionRouteStatus.Unauthenticated;
        }

        await using var command = dataSource.CreateCommand(
            "select status from evidgate_resolve.session_revoke_current(cast(@cookie_hmacs as bytea[]), @now)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        var status = await ReadStatusAsync(command, "session_revoke_current", cancellationToken).ConfigureAwait(false);
        if (string.Equals(status, RevokedStatus, StringComparison.Ordinal))
        {
            SessionTelemetry.Revoked(1);

            return SessionRouteStatus.Done;
        }

        return status switch
        {
            UnauthenticatedStatus => SessionRouteStatus.Unauthenticated,
            NotFoundStatus => SessionRouteStatus.NotFound,
            _ => throw UnknownStatus("session_revoke_current", status),
        };
    }

    /// <inheritdoc />
    public async ValueTask<SessionRouteStatus> RevokeOneAsync(
        byte[] sessionProof,
        Guid sessionId,
        Guid? orgId,
        CancellationToken cancellationToken)
    {
        if (sessionProof.Length == 0)
        {
            return SessionRouteStatus.Unauthenticated;
        }

        await using var command = dataSource.CreateCommand(
            "select status from evidgate_resolve.session_revoke_one("
            + "cast(@cookie_hmacs as bytea[]), @session_id, @org_id, @now)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("session_id", sessionId);

        // Explicit type: a Guid? bound as DBNull has no CLR type to infer the OID from.
        command.Parameters.Add(new NpgsqlParameter("org_id", NpgsqlDbType.Uuid)
        {
            Value = (object?)orgId ?? DBNull.Value,
        });
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        var status = await ReadStatusAsync(command, "session_revoke_one", cancellationToken).ConfigureAwait(false);
        if (string.Equals(status, RevokedStatus, StringComparison.Ordinal))
        {
            SessionTelemetry.Revoked(1);

            return SessionRouteStatus.Done;
        }

        return status switch
        {
            NotFoundStatus => SessionRouteStatus.NotFound,
            UnauthenticatedStatus => SessionRouteStatus.Unauthenticated,
            _ => throw UnknownStatus("session_revoke_one", status),
        };
    }

    /// <inheritdoc />
    public async ValueTask<(SessionRouteStatus Status, int Revoked)> RevokeOthersAsync(
        byte[] sessionProof,
        bool keepCurrent,
        CancellationToken cancellationToken)
    {
        if (sessionProof.Length == 0)
        {
            return (SessionRouteStatus.Unauthenticated, 0);
        }

        await using var command = dataSource.CreateCommand(
            "select status, revoked_count from evidgate_resolve.session_revoke_others("
            + "cast(@cookie_hmacs as bytea[]), @keep_current, @now)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("keep_current", keepCurrent);
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "evidgate_resolve.session_revoke_others returned no row; migration 0048 and this build disagree");
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
            UnauthenticatedStatus => (SessionRouteStatus.Unauthenticated, 0),
            NotFoundStatus => (SessionRouteStatus.NotFound, 0),
            _ => throw UnknownStatus("session_revoke_others", status),
        };
    }

    /// <inheritdoc />
    public async ValueTask<SessionCleanupResult> CleanupAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        // The function clamps too; clamping here keeps a caller's zero from becoming an
        // unbounded call and a negative one from becoming a no-op loop in the task.
        var bounded = Math.Clamp(limit, 0, MaxCleanupBatch);
        if (bounded == 0)
        {
            return new SessionCleanupResult(0, 0);
        }

        await using var connection = await sweep.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "select stamped, deleted from evidgate.session_cleanup(@now, @limit)";
        command.Parameters.AddWithValue("now", now.ToUniversalTime());
        command.Parameters.AddWithValue("limit", bounded);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "evidgate.session_cleanup returned no row; migration 0048 and this build disagree");
        }

        return new SessionCleanupResult(reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>The <c>cookie_hmacs</c> array of one proof — one element, the authenticator's hash.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value.</param>
    private static NpgsqlParameter ProofParameter(byte[] sessionProof) =>
        new("cookie_hmacs", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = new[] { sessionProof } };

    /// <summary>Runs a statement that answers one <c>status</c> row and returns it.</summary>
    /// <param name="command">The prepared statement, parameters already bound.</param>
    /// <param name="function">The function name, for the mismatch message.</param>
    /// <param name="cancellationToken">Request abort.</param>
    private static async Task<string> ReadStatusAsync(
        NpgsqlCommand command,
        string function,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"evidgate_resolve.{function} returned no row; migration 0048 and this build disagree");
        }

        return reader.GetString(0);
    }

    /// <summary>The refusal of a status string this build does not know — a migration/code mismatch.</summary>
    /// <param name="function">The function that answered it.</param>
    /// <param name="status">The stored status.</param>
    private static InvalidOperationException UnknownStatus(string function, string status) =>
        new($"evidgate_resolve.{function} answered the unknown session route status '{status}'");
}
