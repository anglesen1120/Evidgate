using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Login;

/// <summary>
/// The one-time-use record of login <c>state</c> values: the pending-login cookie is deleted on every
/// callback, but a client that kept a copy could present the same cookie and <c>state</c> twice, and this set
/// is what makes the second attempt 401 <c>token_invalid</c> even then — on whichever replica receives it.
/// </summary>
/// <remarks>
/// The record lives in <c>public.login_state_replay</c> and is written by
/// <c>evidgate_resolve.login_state_replay_consume</c>: one insert-if-absent keyed by the SHA-256 of the key,
/// so two concurrent replays on different replicas cannot both succeed. Rows live for the cookie's own
/// lifetime and the function deletes a bounded batch of expired rows on every call, so the table stays at
/// the size of the live window. A database that cannot be reached throws: a state that cannot be recorded is
/// never trusted.
/// </remarks>
internal sealed class LoginReplayCache(NpgsqlDataSource dataSource, TimeProvider clock) : ILoginReplayCache
{
    /// <inheritdoc />
    public async ValueTask<bool> TryConsumeAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = clock.GetUtcNow().ToUniversalTime();

        await using var command = dataSource.CreateCommand(
            "select evidgate_resolve.login_state_replay_consume(@hash, @now, @expires)");
        command.Parameters.Add(new NpgsqlParameter("hash", NpgsqlDbType.Bytea) { Value = SHA256.HashData(Encoding.UTF8.GetBytes(key)) });
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("expires", now + lifetime);

        var consumed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return consumed is true;
    }
}

/// <summary>The one-time record of login <c>state</c> values the callback consults before it calls a provider.</summary>
internal interface ILoginReplayCache
{
    /// <summary>Records <paramref name="key"/> as used and reports whether this is the first time.</summary>
    /// <param name="key">The provider and <c>state</c>, joined (neither is a secret at this point: the
    /// state is already public in the redirect the browser carried).</param>
    /// <param name="lifetime">How long the record must live — the pending-login cookie's lifetime.</param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    /// <returns><c>true</c> on first use; <c>false</c> for a replay.</returns>
    ValueTask<bool> TryConsumeAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);
}
