using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The only implementation of <see cref="ISessionAuthentication"/> (02.3.2-T1.1): it hashes the presented
/// <c>__Host-</c> cookie value under the current pepper (and the previous one while a rotation window is open)
/// and asks <c>evidgate_resolve.session_authenticate</c> of migration 0045 — ONE statement that resolves the
/// row, decides its status, slides the idle deadline when the 60 s throttle allows it, and re-hashes a session
/// that was signed with the retired pepper.
/// </summary>
/// <remarks>
/// <para>
/// The HMAC is computed here and the COMPARISON happens in the database against the unique index: the app role
/// holds no privilege on <c>session</c> (0041), and an app-side comparison of a secret is exactly the timing
/// side channel this design removes. The presented value is bounded before it is hashed — a value longer than
/// any this server mints cannot be a session, so it is refused without a database round trip.
/// </para>
/// <para>
/// A session's state is a SUCCESS value (<see cref="SessionAuthResult"/>), never a failure: the caller renders
/// 401/403 from <see cref="SessionAuthResult.Status"/> and can report telemetry per code. Only a deployment
/// without a pepper, or a database fault, is a failure (503 <c>unavailable</c>) — the request must not be
/// admitted on a lookup that never ran.
/// </para>
/// </remarks>
internal sealed class SessionAuthenticationStore : ISessionAuthentication
{
    /// <summary>
    /// Longest cookie value that is still hashed, in characters. A minted value is 43 base64url characters
    /// (<c>SessionStore.CookieValueBytes</c>), so anything longer cannot be one and is refused unhashed.
    /// </summary>
    internal const int MaxCookieValueLength = 128;

    private const string ActiveStatus = "active";
    private const string UnknownStatus = "unknown";
    private const string ExpiredStatus = "expired";
    private const string RevokedStatus = "revoked";
    private const string DisabledStatus = "disabled";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SessionOptions _options;
    private readonly SessionSecrets _secrets;
    private readonly TimeProvider _clock;

    /// <summary>Binds the app-role data source, the session options, the peppers and the clock.</summary>
    /// <param name="dataSource">The app-role <see cref="NpgsqlDataSource"/> (the only role that may call the resolver).</param>
    /// <param name="options">The bound session options (lifetimes).</param>
    /// <param name="secrets">The peppers the presented cookie value is hashed with.</param>
    /// <param name="clock">The clock every instant of the decision comes from.</param>
    public SessionAuthenticationStore(
        NpgsqlDataSource dataSource,
        IOptions<SessionOptions> options,
        SessionSecrets secrets,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clock);

        _dataSource = dataSource;
        _options = options.Value;
        _secrets = secrets;
        _clock = clock;
    }

    /// <inheritdoc />
    public async ValueTask<Result<SessionAuthResult>> AuthenticateAsync(
        string? cookieValue,
        CancellationToken cancellationToken)
    {
        // No pepper, no hash, no lookup: a deployment that has not provisioned the secret answers 503 instead of
        // treating every caller as anonymous (which would look like a working login that forgets people).
        if (!_secrets.CanHash)
        {
            return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
        }

        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length > MaxCookieValueLength)
        {
            // No usable cookie value: no session can carry it, and hashing it would be work an attacker chose.
            SessionTelemetry.AuthOutcome(ApiErrorCode.Unauthenticated);
            SessionTelemetry.SlideWrite(false);

            return Result.Success(new SessionAuthResult(
                SessionAuthStatus.Unknown,
                Guid.Empty,
                Guid.Empty,
                RotationRequired: false));
        }

        var candidates = _secrets.Candidates(Encoding.ASCII.GetBytes(cookieValue));
        if (candidates.Count == 0)
        {
            return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
        }

        try
        {
            await using var command = _dataSource.CreateCommand(
                "select status, session_id, user_id, rotation_required, slid "
                + "from evidgate_resolve.session_authenticate("
                + "@cookie_hmacs, @current_hmac, @current_version, @now, @idle_seconds)");

            // Every candidate in one array, so the lookup is one index probe whatever the rotation window holds.
            command.Parameters.AddWithValue(
                "cookie_hmacs",
                candidates.Select(candidate => candidate.Hash).ToArray());
            command.Parameters.AddWithValue("current_hmac", candidates[0].Hash);
            command.Parameters.AddWithValue("current_version", candidates[0].Version);
            command.Parameters.AddWithValue("now", _clock.GetUtcNow().ToUniversalTime());
            command.Parameters.AddWithValue("idle_seconds", _options.IdleSeconds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The statement always answers one row; no row means the function and this build disagree, so
                // the request is refused rather than admitted.
                return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
            }

            var status = reader.GetString(0);
            var sessionId = reader.GetGuid(1);
            var userId = reader.GetGuid(2);
            var rotationRequired = reader.GetBoolean(3);
            var slid = reader.GetBoolean(4);

            SessionTelemetry.SlideWrite(slid);

            if (string.Equals(status, ActiveStatus, StringComparison.Ordinal))
            {
                SessionTelemetry.AuthOutcome(SessionTelemetry.ActiveOutcome);

                return Result.Success(new SessionAuthResult(
                    SessionAuthStatus.Active,
                    sessionId,
                    userId,
                    rotationRequired));
            }

            var mapped = status switch
            {
                UnknownStatus => (Status: SessionAuthStatus.Unknown, Code: ApiErrorCode.Unauthenticated),
                ExpiredStatus => (Status: SessionAuthStatus.Expired, Code: ApiErrorCode.SessionExpired),
                RevokedStatus => (Status: SessionAuthStatus.Revoked, Code: ApiErrorCode.SessionRevoked),
                DisabledStatus => (Status: SessionAuthStatus.Disabled, Code: ApiErrorCode.AccountDisabled),
                _ => (Status: SessionAuthStatus.Unknown, Code: string.Empty),
            };

            if (mapped.Code.Length == 0)
            {
                // A status this build does not know is a migration/code mismatch: refuse the request rather than
                // answer "unknown" (or worse, admit it) for something the database did describe.
                return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
            }

            SessionTelemetry.AuthOutcome(mapped.Code);

            // `rotation_required` is read above but can only be true for an active session, which returned
            // already — a dead session is never rotated, it is answered.
            return Result.Success(new SessionAuthResult(
                mapped.Status,
                mapped.Status == SessionAuthStatus.Unknown ? Guid.Empty : sessionId,
                mapped.Status == SessionAuthStatus.Unknown ? Guid.Empty : userId,
                RotationRequired: false));
        }
        catch (NpgsqlException)
        {
            return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A pool or command timeout that surfaced as cancellation is an outage, not the caller leaving.
            return Result.Failure<SessionAuthResult>(SessionErrors.Unavailable);
        }
    }
}
