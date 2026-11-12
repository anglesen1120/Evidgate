using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Sessions;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Identity.Login.Linking;

/// <summary>
/// The only implementation of <see cref="ILinkIdentityStore"/> (02.3.1-T2.2): one call to
/// <c>evidgate_resolve.login_link_identity_audited</c> (migration 0101, which wraps the 0045 link and writes its
/// audit rows in the same transaction) on the app-role data source, because the app
/// role holds no privilege on <c>public.user_identity</c> and <c>public."user"</c> (0041) — never a direct
/// INSERT or UPDATE, which RLS and the missing grant would refuse.
/// </summary>
/// <remarks>
/// <para>
/// <b>The function resolves the target account (02.3.2-T1, D8 / residual SEC2).</b> It takes the HMAC of a live
/// session's cookie and resolves the session and its active user itself, so a caller running as
/// <c>evidgate_app</c> can no longer name an arbitrary user id to attach an identity to. The hash comes from the
/// deployment's session authenticator (<c>SessionRequestOutcome.GetSessionProof()</c>): the HTTP layer already spent
/// the cookie to prove the request, so no raw credential and no second hashing live in this class.
/// </para>
/// <para>
/// The function always returns one row and raises only when the audit cannot be written (the link then rolls
/// back and this class answers 503). An empty result means the
/// stored contract moved: this class then fails closed (503) instead of guessing that the identity was
/// attached. An outcome string this build does not know is <see cref="LinkResult.Unrecognised"/> — also a 503,
/// never a silent success.
/// </para>
/// <para>
/// Nothing here is logged: the address and the subject are personal data (02.3.1 §8), the proof is a credential
/// hash, and the audit rows written by the function carry the provider name and, for a move, the previous user id only.
/// </para>
/// </remarks>
/// <param name="dataSource">The app-role <see cref="NpgsqlDataSource"/> (the only role that may call the resolver).</param>
/// <param name="clock">The clock the function's <c>p_now</c> is read from, so the session's liveness is judged on the deployment's own clock.</param>
internal sealed class NpgsqlLinkIdentityStore(NpgsqlDataSource dataSource, TimeProvider clock) : ILinkIdentityStore
{
    /// <summary>The outcome strings of the 0045 contract.</summary>
    private const string Linked = "linked";

    private const string AlreadyLinked = "already_linked";

    private const string Moved = "moved";

    private const string Conflict = "conflict";

    private const string Disabled = "disabled";

    private const string Unauthenticated = "unauthenticated";

    /// <inheritdoc />
    public async ValueTask<Result<LinkOutcome>> LinkAsync(
        byte[]? sessionProof,
        string provider,
        string subject,
        string email,
        string? requestId,
        string? traceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(subject);

        // No proof, no lookup: an absent or empty hash cannot name a session, and the function would answer the
        // same `unauthenticated` — without a database round trip.
        if (sessionProof is not { Length: > 0 })
        {
            return Result.Success(new LinkOutcome(LinkResult.Unauthenticated, null));
        }

        try
        {
            await using var command = dataSource.CreateCommand(
                "select outcome, moved_from_user_id from evidgate_resolve.login_link_identity_audited("
                + "cast(@cookie_hmacs as bytea[]), @now, @provider, @subject, @email, @identity_id, @request_id, @trace_id)");

            // One element: the authenticator's proof of the live session, which after a lazy rotation is already
            // the hash of the newly issued cookie.
            command.Parameters.Add(new NpgsqlParameter(
                "cookie_hmacs",
                NpgsqlDbType.Array | NpgsqlDbType.Bytea)
            {
                Value = new[] { sessionProof },
            });

            // timestamptz takes a UTC instant: Npgsql refuses a non-UTC DateTimeOffset, so the clock's value is
            // normalised here rather than at the database.
            command.Parameters.AddWithValue("now", clock.GetUtcNow().ToUniversalTime());
            command.Parameters.AddWithValue("provider", provider);
            command.Parameters.AddWithValue("subject", subject);
            command.Parameters.AddWithValue("email", email);

            // No profile name: the function has no argument for one, because a second provider's display name must
            // not rename an account whose name the login upsert (0042) owns.
            command.Parameters.AddWithValue("identity_id", Guid.CreateVersion7());

            // Explicit text type: "the request carried none" is legal and a DBNull has no CLR type to infer from.
            // The definer nulls a value that is not a safe request id or a 32-hex trace id.
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
                return Result.Failure<LinkOutcome>(SessionErrors.Unavailable);
            }

            var outcome = await reader.GetFieldValueAsync<string>(0, cancellationToken).ConfigureAwait(false);
            var movedFrom = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? (Guid?)null
                : await reader.GetFieldValueAsync<Guid>(1, cancellationToken).ConfigureAwait(false);

            return Result.Success(Outcome(outcome, movedFrom));
        }
        catch (NpgsqlException)
        {
            return Result.Failure<LinkOutcome>(SessionErrors.Unavailable);
        }
        catch (TimeoutException)
        {
            return Result.Failure<LinkOutcome>(SessionErrors.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A pool or command timeout that surfaced as cancellation is an outage, not the caller leaving.
            return Result.Failure<LinkOutcome>(SessionErrors.Unavailable);
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
        Disabled => new LinkOutcome(LinkResult.Disabled, null),
        Unauthenticated => new LinkOutcome(LinkResult.Unauthenticated, null),
        _ => new LinkOutcome(LinkResult.Unrecognised, null),
    };
}
