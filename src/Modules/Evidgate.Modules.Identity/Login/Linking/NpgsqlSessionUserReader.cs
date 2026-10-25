using System.Text;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Npgsql;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// The only implementation of <see cref="ISessionUserReader"/> (02.3.1-T2.2): the session pepper of
/// 02.3.2-T1 hashes the presented cookie value, and <c>evidgate_resolve.session_user</c> of migration 0044
/// turns that hash into a <c>user.id</c>.
/// </summary>
/// <remarks>
/// <para>
/// The hash is the store's construction, not a second one: <see cref="SessionSecrets.HashCurrent"/> gives
/// the same HMAC-SHA256 the <c>session.cookie_hmac</c> column holds, with the pepper version beside it, which
/// is exactly the pair the function compares. Nothing is read from <c>public.session</c> directly — the app
/// role holds no privilege on it (0041) and the function is its only way in.
/// </para>
/// <para>
/// The cookie value is client input, so it is bounded before it is hashed and it is never logged: an
/// over-long value is no session at all rather than work the host does on demand.
/// </para>
/// </remarks>
/// <param name="dataSource">The app-role <see cref="NpgsqlDataSource"/> (the only role that may call the resolver).</param>
/// <param name="secrets">The pepper the session cookie values are hashed with.</param>
internal sealed class NpgsqlSessionUserReader(NpgsqlDataSource dataSource, SessionSecrets secrets) : ISessionUserReader
{
    /// <summary>
    /// Longest cookie value that is still hashed, in characters. A minted value is 43 base64url characters
    /// (<c>SessionStore.CookieValueBytes</c>), so anything longer cannot be one and is refused unhashed.
    /// </summary>
    internal const int MaxCookieValueLength = 128;

    /// <inheritdoc />
    public async ValueTask<Result<Guid>> ResolveAsync(string? cookieValue, CancellationToken cancellationToken)
    {
        // No pepper, no hash, no resolution: a deployment that has not provisioned the secret answers 503
        // rather than treat every caller as anonymous (the same fail-closed choice the store makes).
        if (!secrets.CanHash)
        {
            return Result.Failure<Guid>(LinkErrors.ServiceUnavailable);
        }

        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length > MaxCookieValueLength)
        {
            return Result.Failure<Guid>(LinkErrors.Unauthenticated);
        }

        var (pepperVersion, cookieHmac) = secrets.HashCurrent(Encoding.ASCII.GetBytes(cookieValue));

        try
        {
            await using var command = dataSource.CreateCommand(
                "select user_id from evidgate_resolve.session_user(@cookie_hmac, @pepper_version)");
            command.Parameters.AddWithValue("cookie_hmac", cookieHmac);
            command.Parameters.AddWithValue("pepper_version", pepperVersion);

            // `ExecuteScalarAsync` answers null for both "no row" and a NULL value; the function returns at
            // most one row and its user_id is NOT NULL, so the two cases are the same refusal here.
            var resolved = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return resolved is Guid userId
                ? Result.Success(userId)
                : Result.Failure<Guid>(LinkErrors.Unauthenticated);
        }
        catch (NpgsqlException)
        {
            return Result.Failure<Guid>(LinkErrors.ServiceUnavailable);
        }
        catch (TimeoutException)
        {
            return Result.Failure<Guid>(LinkErrors.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A pool or command timeout that surfaced as cancellation is an outage, not the caller leaving.
            return Result.Failure<Guid>(LinkErrors.ServiceUnavailable);
        }
    }
}
