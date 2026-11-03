using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Profile;

/// <summary>The account's own profile, as the definer projects it.</summary>
/// <param name="Id">The <c>user</c> row — the account the presented session belongs to.</param>
/// <param name="Email">The provider-verified address stored on the row.</param>
/// <param name="DisplayName">The provider profile name, or null when the provider supplied none.</param>
/// <param name="PreferredLocale">The chosen language (<c>vi</c> / <c>en</c>), or null until the account has chosen.</param>
/// <param name="Version">The row's optimistic-concurrency token, which the route publishes as its <c>ETag</c>.</param>
internal sealed record MyProfile(Guid Id, string Email, string? DisplayName, string? PreferredLocale, int Version);

/// <summary>How one profile statement ended.</summary>
internal enum MyProfileStatus
{
    /// <summary>The statement ran and answered the caller's own row.</summary>
    Ok,

    /// <summary>The write's version predicate matched no row, so nothing changed.</summary>
    Stale,

    /// <summary>The presented proof matches no live session, so the request has no acting user.</summary>
    Unauthenticated,
}

/// <summary>
/// The persistence seam of the own-profile read and the locale write: two statements over
/// the owner-owned <c>evidgate_resolve</c> functions of migration 0061.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pre-tenant, like the routes.</b> Both calls run before any tenant scope exists and
/// the acting user is resolved from the presented credential INSIDE the function, so no
/// method takes a user id. The runtime roles hold no privilege on <c>public."user"</c>, so
/// these functions are the only door to the row. Both functions also require the owning
/// account to be <c>active</c>, so a session that outlives a disable resolves no acting user
/// (the same door the request authenticator's 403 <c>account_disabled</c> closes one layer up).
/// </para>
/// <para>
/// <b>What is passed.</b> <c>sessionProof</c> is the HMAC of the LIVE cookie value — derived
/// by the request authenticator, never the cookie itself and never a second hashing done
/// here. A missing or empty proof answers <see cref="MyProfileStatus.Unauthenticated"/>
/// without a statement.
/// </para>
/// <para>
/// <b>What is returned.</b> A business outcome is a status, never a failure. A database
/// fault or a status this build does not know throws, so the caller answers 503/500 and
/// never signs a healthy account out.
/// </para>
/// </remarks>
internal interface IMyProfileStore
{
    /// <summary>The caller's own profile.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns><see cref="MyProfileStatus.Ok"/> with the row, or <see cref="MyProfileStatus.Unauthenticated"/>.</returns>
    ValueTask<(MyProfileStatus Status, MyProfile? Profile)> ReadAsync(
        byte[] sessionProof,
        CancellationToken cancellationToken);

    /// <summary>Writes the chosen language against the version the caller read.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value; empty when the request carried no cookie.</param>
    /// <param name="preferredLocale">The validated language (<c>vi</c> / <c>en</c>).</param>
    /// <param name="expectedVersion">The <c>version</c> the caller's <c>If-Match</c> named.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// <see cref="MyProfileStatus.Ok"/> with the row the write produced; <see cref="MyProfileStatus.Stale"/> with
    /// no profile and the version the next read carries, because the write matched nothing; or
    /// <see cref="MyProfileStatus.Unauthenticated"/>.
    /// </returns>
    ValueTask<(MyProfileStatus Status, MyProfile? Profile, int CurrentVersion)> UpdateAsync(
        byte[] sessionProof,
        string preferredLocale,
        int expectedVersion,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// One instance per host, over the app-role <see cref="NpgsqlDataSource"/>; the clock is the
/// deployment's <see cref="TimeProvider"/>, so a test moves every session deadline without
/// waiting.
/// </remarks>
/// <param name="dataSource">The app-role data source both statements run on.</param>
/// <param name="clock">The clock the statements' <c>p_now</c> comes from.</param>
internal sealed class NpgsqlMyProfileStore(NpgsqlDataSource dataSource, TimeProvider clock) : IMyProfileStore
{
    private const string OkStatus = "ok";
    private const string StaleStatus = "stale";
    private const string UnauthenticatedStatus = "unauthenticated";

    /// <inheritdoc />
    public async ValueTask<(MyProfileStatus Status, MyProfile? Profile)> ReadAsync(
        byte[] sessionProof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionProof);

        if (sessionProof.Length == 0)
        {
            return (MyProfileStatus.Unauthenticated, null);
        }

        await using var command = dataSource.CreateCommand(
            "select status, user_id, email, display_name, preferred_locale, version"
            + " from evidgate_resolve.user_profile_read(cast(@cookie_hmacs as bytea[]), @now)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "evidgate_resolve.user_profile_read returned no row; migration 0061 and this build disagree");
        }

        var status = reader.GetString(0);
        if (string.Equals(status, UnauthenticatedStatus, StringComparison.Ordinal))
        {
            return (MyProfileStatus.Unauthenticated, null);
        }

        if (!string.Equals(status, OkStatus, StringComparison.Ordinal))
        {
            throw UnknownStatus("user_profile_read", status);
        }

        // A live session always resolves one user row (the session carries a foreign key to
        // it), so an `ok` without columns is the same migration/build disagreement, not an
        // empty profile.
        if (await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "evidgate_resolve.user_profile_read answered ok without a profile; migration 0061 and this build disagree");
        }

        var profile = new MyProfile(
            reader.GetGuid(1),
            reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
            reader.GetInt32(5));

        return (MyProfileStatus.Ok, profile);
    }

    /// <inheritdoc />
    public async ValueTask<(MyProfileStatus Status, MyProfile? Profile, int CurrentVersion)> UpdateAsync(
        byte[] sessionProof,
        string preferredLocale,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionProof);
        ArgumentException.ThrowIfNullOrEmpty(preferredLocale);

        if (sessionProof.Length == 0)
        {
            return (MyProfileStatus.Unauthenticated, null, 0);
        }

        await using var command = dataSource.CreateCommand(
            "select status, user_id, email, display_name, preferred_locale, version"
            + " from evidgate_resolve.user_profile_update("
            + "cast(@cookie_hmacs as bytea[]), @preferred_locale, @expected_version, @now)");
        command.Parameters.Add(ProofParameter(sessionProof));
        command.Parameters.AddWithValue("preferred_locale", preferredLocale);
        command.Parameters.AddWithValue("expected_version", expectedVersion);
        command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "evidgate_resolve.user_profile_update returned no row; migration 0061 and this build disagree");
        }

        var status = reader.GetString(0);
        if (string.Equals(status, UnauthenticatedStatus, StringComparison.Ordinal))
        {
            return (MyProfileStatus.Unauthenticated, null, 0);
        }

        if (string.Equals(status, StaleStatus, StringComparison.Ordinal))
        {
            // Nothing was written: the version the client held is no longer the row's.
            return (MyProfileStatus.Stale, null, await NullableIntAsync(reader, 5, cancellationToken).ConfigureAwait(false) ?? 0);
        }

        if (!string.Equals(status, OkStatus, StringComparison.Ordinal))
        {
            throw UnknownStatus("user_profile_update", status);
        }

        var profile = new MyProfile(
            reader.GetGuid(1),
            reader.GetString(2),
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
            reader.GetInt32(5));

        return (MyProfileStatus.Ok, profile, profile.Version);
    }

    /// <summary>One nullable integer column: null when the function answered no value for it.</summary>
    /// <param name="reader">The open reader, positioned on the row.</param>
    /// <param name="ordinal">The column's position.</param>
    /// <param name="cancellationToken">Request abort.</param>
    private static async ValueTask<int?> NullableIntAsync(
        NpgsqlDataReader reader,
        int ordinal,
        CancellationToken cancellationToken) =>
        await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetInt32(ordinal);

    /// <summary>The <c>cookie_hmacs</c> array of one proof — one element, the authenticator's hash.</summary>
    /// <param name="sessionProof">The HMAC of the live cookie value.</param>
    private static NpgsqlParameter ProofParameter(byte[] sessionProof) =>
        new("cookie_hmacs", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = new[] { sessionProof } };

    /// <summary>The refusal of a status string this build does not know — a migration/code mismatch.</summary>
    /// <param name="function">The function that answered it.</param>
    /// <param name="status">The stored status.</param>
    private static InvalidOperationException UnknownStatus(string function, string status) =>
        new($"evidgate_resolve.{function} answered the unknown profile status '{status}'");
}
