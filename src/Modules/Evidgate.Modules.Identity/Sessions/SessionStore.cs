using System.Security.Cryptography;
using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Evidgate.Modules.Identity.Sessions;

/// <summary>
/// The only implementation of <see cref="ISessions"/> (02.3.1-T1.4): it mints the session cookie value,
/// stores its HMAC and nothing else, and writes the row through the owner-scoped
/// <c>evidgate_resolve.session_create</c> function of migration 0042 — the app role holds no privilege on
/// <c>session</c> (0041), so a direct insert is not an option and never becomes one.
/// </summary>
/// <remarks>
/// <para>
/// The value handed back in <see cref="SessionTicket.CookieValue"/> is 32 random bytes as base64url and is
/// returned exactly once: the column holds HMAC-SHA256(pepper_vN, value), so reading the table yields no
/// usable cookie (<c>RawValueNeverPersisted</c>). The pepper version travels with the hash so 02.3.2-T1's
/// rotation can keep an old pepper alive while its sessions drain.
/// </para>
/// <para>
/// Client metadata is not rewritten here, except for the user agent's length. The client address is stored
/// exactly as it was handed in: <c>ClientAddress</c> in the completion handler is the one owner of what an
/// <c>inet</c> literal may be, and the <c>session_create</c> function nulls a literal the column still
/// refuses (its exception block) instead of failing a login every other gate admitted. The user agent is
/// bounded surrogate-safely here, ahead of the column's own 512-character bound.
/// </para>
/// <para>
/// Failures are values, not exceptions: a disabled account is 403 <c>account_disabled</c>, and a missing
/// pepper, an unknown user or a database fault (including a timeout) is 503 <c>unavailable</c> with
/// <c>Retryable: true</c>. Nothing here opens a transaction that spans an outbound call — the login flow
/// has already finished every provider call before it asks for a session (P5).
/// </para>
/// </remarks>
internal sealed class SessionStore : ISessions
{
    /// <summary>Entropy of the cookie value, in bytes (256 bits: the hash is only as strong as the value).</summary>
    internal const int CookieValueBytes = 32;

    /// <summary>The user-agent cap of <c>session_user_agent_length_check</c> (0041).</summary>
    internal const int MaxUserAgentLength = 512;

    private const string OkStatus = "ok";
    private const string DisabledStatus = "disabled";

    private readonly NpgsqlDataSource _dataSource;
    private readonly SessionOptions _options;
    private readonly SessionSecrets _secrets;
    private readonly TimeProvider _clock;

    /// <summary>Binds the app-role data source, the session options, the pepper and the clock.</summary>
    /// <param name="dataSource">The app-role <see cref="NpgsqlDataSource"/> (the only DML role, 00 §5.4.3).</param>
    /// <param name="options">The bound session options (lifetimes).</param>
    /// <param name="secrets">The pepper the cookie value is hashed with.</param>
    /// <param name="clock">The clock the two expiries are computed from.</param>
    public SessionStore(
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
    public async ValueTask<Result<SessionTicket>> CreateAsync(
        SessionCreation request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No pepper, no hash, no session: a deployment that has not provisioned the secret answers 503
        // instead of storing a credential nothing can verify.
        if (!_secrets.CanHash)
        {
            return Result.Failure<SessionTicket>(SessionErrors.Unavailable);
        }

        var cookieValue = NewCookieValue();
        var (pepperVersion, cookieHmac) = _secrets.HashCurrent(Encoding.ASCII.GetBytes(cookieValue));

        var now = _clock.GetUtcNow();
        var expiresAt = now + _options.IdleLifetime;
        var absoluteExpiresAt = now + _options.AbsoluteLifetime;
        var sessionId = Guid.CreateVersion7();

        var storedId = sessionId;
        var storedExpiresAt = expiresAt;
        string status;
        try
        {
            await using var command = _dataSource.CreateCommand(
                "select session_id, expires_at, status from evidgate_resolve.session_create("
                + "@session_id, @user_id, @cookie_hmac, @pepper_version, @ip, @user_agent, @expires_at, @absolute_expires_at)");
            command.Parameters.AddWithValue("session_id", sessionId);
            command.Parameters.AddWithValue("user_id", request.UserId);
            command.Parameters.AddWithValue("cookie_hmac", cookieHmac);
            command.Parameters.AddWithValue("pepper_version", pepperVersion);
            // The address is handed through unchanged: ClientAddress.Sanitise (the completion handler) is the
            // one owner of what an inet literal may be, and the function's exception block nulls a literal the
            // column still refuses instead of failing a login every other gate admitted.
            command.Parameters.AddWithValue("ip", (object?)request.IpAddress ?? DBNull.Value);
            command.Parameters.AddWithValue("user_agent", (object?)BoundUserAgent(request.UserAgent) ?? DBNull.Value);
            command.Parameters.AddWithValue("expires_at", expiresAt);
            command.Parameters.AddWithValue("absolute_expires_at", absoluteExpiresAt);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<SessionTicket>(SessionErrors.Unavailable);
            }

            status = reader.GetString(2);

            if (string.Equals(status, OkStatus, StringComparison.Ordinal))
            {
                // The stored row is the truth about what was written; the caller renders its expiry.
                storedId = reader.GetGuid(0);
                storedExpiresAt = await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (NpgsqlException)
        {
            return Result.Failure<SessionTicket>(SessionErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            return Result.Failure<SessionTicket>(SessionErrors.Unavailable);
        }

        if (string.Equals(status, OkStatus, StringComparison.Ordinal))
        {
            return Result.Success<SessionTicket>(new SessionTicket(storedId, cookieValue, storedExpiresAt));
        }

        return Result.Failure<SessionTicket>(
            string.Equals(status, DisabledStatus, StringComparison.Ordinal)
                ? SessionErrors.AccountDisabled
                // `unknown_user` — and any status this build does not know — fails closed as unavailable.
                : SessionErrors.Unavailable);
    }

    /// <summary>A fresh cookie value: 32 random bytes, base64url without padding (43 characters).</summary>
    private static string NewCookieValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(CookieValueBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// The user agent bounded to the column's 512 characters (0041), without splitting a surrogate pair: the
    /// value is client input and the column is not the place to discover that.
    /// </summary>
    /// <param name="value">The user agent the callback read, or null.</param>
    private static string? BoundUserAgent(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length <= MaxUserAgentLength)
        {
            return value;
        }

        var truncated = value[..MaxUserAgentLength];

        return char.IsHighSurrogate(truncated[^1]) ? truncated[..^1] : truncated;
    }
}
