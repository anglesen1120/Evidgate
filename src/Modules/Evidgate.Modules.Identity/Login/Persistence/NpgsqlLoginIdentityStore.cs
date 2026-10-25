using Npgsql;

namespace Evidgate.Modules.Identity.Login.Persistence;

/// <summary>
/// The only implementation of <see cref="ILoginIdentityStore"/> (02.3.1-T1.4): the upsert of
/// <c>user</c>/<c>user_identity</c> on <c>(provider, subject)</c> runs through the owner-scoped
/// <c>evidgate_resolve.login_upsert_identity</c> function of migration 0042, because the app role holds no
/// privilege on either table (0041) — never by a direct INSERT, which RLS and the missing grant would refuse.
/// </summary>
/// <remarks>
/// <para>
/// The statement is one call with parameters and no transaction: the FKs and the unique key make it
/// replay-safe and race-safe, so the login flow needs no scope of its own and no lock (P5 — every provider
/// call has already finished, and nothing external runs while this is in flight).
/// </para>
/// <para>
/// The two ids are app-generated UUIDv7 (00 §7, <c>Guid.CreateVersion7()</c>): a first login's ids travel
/// with the call, so a concurrent loser can delete exactly the row it created and leave no orphan account.
/// The provider name is validated by <c>user_identity_provider_check</c> inside the same statement, so an
/// unknown provider is refused by the server rather than by a list in this class.
/// </para>
/// <para>
/// Nothing is logged here: an email, a subject and a display name are personal data (02.3.1 §8), and the
/// caller's audit/metric layer never receives them either.
/// </para>
/// </remarks>
internal sealed class NpgsqlLoginIdentityStore(NpgsqlDataSource dataSource) : ILoginIdentityStore
{
    /// <summary><c>user.status</c> of an account that may sign in; every other value is disabled (02.3.1 §6).</summary>
    private const string ActiveStatus = "active";

    /// <summary>The display-name cap of <c>user_display_name_length_check</c> (0041).</summary>
    private const int MaxDisplayNameLength = 200;

    /// <inheritdoc />
    public async ValueTask<LoginUser> UpsertAsync(VerifiedIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        await using var command = dataSource.CreateCommand(
            "select user_id, status from evidgate_resolve.login_upsert_identity("
            + "@provider, @subject, @email, @display_name, @user_id, @identity_id)");
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("subject", identity.Subject);
        command.Parameters.AddWithValue("email", identity.Email);
        command.Parameters.AddWithValue("display_name", (object?)BoundDisplayName(identity.DisplayName) ?? DBNull.Value);
        command.Parameters.AddWithValue("user_id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The function returns exactly one row or raises; an empty result means the contract moved and
            // a session must not be issued on a guess.
            throw new InvalidOperationException("evidgate_resolve.login_upsert_identity returned no row");
        }

        return new LoginUser(
            reader.GetGuid(0),
            string.Equals(reader.GetString(1), ActiveStatus, StringComparison.Ordinal));
    }

    /// <summary>
    /// The provider's profile name bounded to the column's 200 characters, or null when the provider sent
    /// none: whitespace is ABSENT, and a truncated name never ends on half a surrogate pair. `user.email` is
    /// bounded by its own check and is never truncated — a shortened address would be a different account.
    /// </summary>
    /// <param name="value">The display name the provider reported, or null.</param>
    private static string? BoundDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= MaxDisplayNameLength)
        {
            return trimmed;
        }

        var truncated = trimmed[..MaxDisplayNameLength];

        return char.IsHighSurrogate(truncated[^1]) ? truncated[..^1] : truncated;
    }
}
