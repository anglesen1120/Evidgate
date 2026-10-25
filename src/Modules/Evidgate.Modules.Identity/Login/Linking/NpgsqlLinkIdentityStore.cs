using Evidgate.BuildingBlocks.SharedKernel.Results;
using Npgsql;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// The only implementation of <see cref="ILinkIdentityStore"/> (02.3.1-T2.2): one call to
/// <c>evidgate_resolve.login_link_identity</c> (migration 0044) on the app-role data source, because the app
/// role holds no privilege on <c>public.user_identity</c> and <c>public."user"</c> (0041) — never a direct
/// INSERT or UPDATE, which RLS and the missing grant would refuse.
/// </summary>
/// <remarks>
/// <para>
/// The function always returns one row and never raises for a business outcome, so an empty result means the
/// stored contract moved: this class then fails closed (503) instead of guessing that the identity was
/// attached. An outcome string this build does not know is
/// <see cref="LinkResult.Unrecognised"/> — also a 503, never a silent success.
/// </para>
/// <para>
/// Nothing here is logged: the address and the subject are personal data (02.3.1 §8), and the caller's audit
/// row carries the provider name and, for a move, the previous user id only.
/// </para>
/// </remarks>
/// <param name="dataSource">The app-role <see cref="NpgsqlDataSource"/> (the only role that may call the resolver).</param>
internal sealed class NpgsqlLinkIdentityStore(NpgsqlDataSource dataSource) : ILinkIdentityStore
{
    /// <summary>The outcome strings of the 0044 contract.</summary>
    private const string Linked = "linked";

    private const string AlreadyLinked = "already_linked";

    private const string Moved = "moved";

    private const string Conflict = "conflict";

    private const string UnknownUser = "unknown_user";

    private const string Disabled = "disabled";

    /// <inheritdoc />
    public async ValueTask<Result<LinkOutcome>> LinkAsync(
        Guid userId,
        string provider,
        string subject,
        string email,
        string? displayName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(subject);
        ArgumentOutOfRangeException.ThrowIfEqual(userId, Guid.Empty);

        try
        {
            await using var command = dataSource.CreateCommand(
                "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity("
                + "@user_id, @provider, @subject, @email, @display_name, @identity_id)");
            command.Parameters.AddWithValue("user_id", userId);
            command.Parameters.AddWithValue("provider", provider);
            command.Parameters.AddWithValue("subject", subject);
            command.Parameters.AddWithValue("email", email);
            // Accepted for the signature and never written by the function: a second provider's profile name
            // must not rename an account whose name the login upsert (0042) owns.
            command.Parameters.AddWithValue("display_name", (object?)displayName ?? DBNull.Value);
            command.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<LinkOutcome>(LinkErrors.ServiceUnavailable);
            }

            var outcome = await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false);
            var movedFrom = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? (Guid?)null
                : await reader.GetFieldValueAsync<Guid>(1, cancellationToken).ConfigureAwait(false);

            return Result.Success(Outcome(outcome, movedFrom));
        }
        catch (NpgsqlException)
        {
            return Result.Failure<LinkOutcome>(LinkErrors.ServiceUnavailable);
        }
        catch (TimeoutException)
        {
            return Result.Failure<LinkOutcome>(LinkErrors.ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A pool or command timeout that surfaced as cancellation is an outage, not the caller leaving.
            return Result.Failure<LinkOutcome>(LinkErrors.ServiceUnavailable);
        }
    }

    /// <summary>
    /// The stored outcome as this build's answer. An unknown string, or a <c>moved</c> without the account it
    /// came from, is <see cref="LinkResult.Unrecognised"/> — the caller answers 503 and keeps the staged link,
    /// rather than reporting a success it cannot audit.
    /// </summary>
    /// <param name="outcome">The <c>outcome</c> column of the function's row.</param>
    /// <param name="movedFrom">The <c>moved_from_user_id</c> column, or null.</param>
    private static LinkOutcome Outcome(string outcome, Guid? movedFrom) => outcome switch
    {
        Linked => new LinkOutcome(LinkResult.Linked, null),
        AlreadyLinked => new LinkOutcome(LinkResult.AlreadyLinked, null),
        Moved when movedFrom is not null => new LinkOutcome(LinkResult.Moved, movedFrom),
        Conflict => new LinkOutcome(LinkResult.Conflict, null),
        UnknownUser => new LinkOutcome(LinkResult.UnknownUser, null),
        Disabled => new LinkOutcome(LinkResult.Disabled, null),
        _ => new LinkOutcome(LinkResult.Unrecognised, null),
    };
}
