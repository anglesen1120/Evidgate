using Evidgate.BuildingBlocks.Persistence.Resolve;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// Runs a <see cref="CompleteLoginCommand"/> in the order 02.3.1 §5 fixes — upsert the identity, require an
/// active membership, create the session — and answers the typed refusal of the step that refused.
/// </summary>
/// <remarks>
/// <para>
/// Each step is an independent statement with its own failure, and each refusal is final: a disabled user
/// gets 403 <c>account_disabled</c> with no session, an account with no active org gets 403
/// <c>no_membership</c> with no session and no cookie, and the rows step 1 wrote stay — the upsert is the
/// login's own bookkeeping, not a promise of access (02.3.1 §5, AC3).
/// </para>
/// <para>
/// Exceptions are not caught here: a database fault is not a business outcome, and the seam that owns the
/// response (<see cref="LoginCommandCompletion"/>) is what turns it into 503 <c>unavailable</c> with
/// <c>Retry-After</c>. Nothing in this class writes a log line, a metric label or a response body.
/// </para>
/// </remarks>
/// <param name="identities">The only writer of <c>user</c>/<c>user_identity</c> (02.3.1-T1.4's store).</param>
/// <param name="memberOrgs">The pre-tenant <c>evidgate_resolve.member_orgs</c> resolver (02.6.1-T3).</param>
/// <param name="sessions">The session-creation seam (02.3.2-T1's implementation, `session` row + cookie value).</param>
internal sealed class CompleteLoginCommandHandler(
    ILoginIdentityStore identities,
    IMemberOrgResolver memberOrgs,
    ISessions sessions)
{
    /// <summary>The org status that admits a login (02.3.1 AC3): a suspended org's membership never does.</summary>
    private const string ActiveOrgStatus = "active";

    /// <summary>Runs the command.</summary>
    /// <param name="command">The verified identity and the client metadata to store on the session.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The created session and the redirect target, or 403 <c>account_disabled</c>, 403 <c>no_membership</c>,
    /// or the session store's own failure (403 <c>account_disabled</c> / 503 <c>unavailable</c>) unchanged.
    /// </returns>
    public async ValueTask<Result<LoginResult>> HandleAsync(
        CompleteLoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. The identity: idempotent on (provider, subject), so a replayed or repeated login lands on the
        //    same user and never merges by email (D3, AC6).
        var user = await identities.UpsertAsync(command.Identity, cancellationToken).ConfigureAwait(false);

        if (!user.IsActive)
        {
            return Result.Failure<LoginResult>(LoginErrors.AccountDisabled);
        }

        // 2. Access: at least one org where the account holds an active membership. Nothing here binds a
        //    pending invite — that is 02.3.1-T2 — so an invited-but-unbound account is refused today.
        var orgs = await memberOrgs.MemberOrgsAsync(user.UserId, cancellationToken).ConfigureAwait(false);

        if (!AdmitsAny(orgs))
        {
            return Result.Failure<LoginResult>(LoginErrors.NoMembership);
        }

        // 3. The session: the store owns the row, the HMAC of the cookie value and the expiry pair.
        var ticket = await sessions
            .CreateAsync(
                new SessionCreation(user.UserId, ClientAddress.Sanitise(command.ClientIpAddress), command.UserAgent),
                cancellationToken)
            .ConfigureAwait(false);

        if (!ticket.IsSuccess)
        {
            return Result.Failure<LoginResult>(ticket.Error!);
        }

        return Result.Success(new LoginResult(
            user.UserId,
            ticket.Value.SessionId,
            ticket.Value.CookieValue,
            ticket.Value.ExpiresAt,
            ReturnUrlEncoder.ToLocation(command.ReturnUrl)));
    }

    /// <summary>Whether any of <paramref name="orgs"/> is active (a suspended org admits nobody).</summary>
    private static bool AdmitsAny(IReadOnlyList<MemberOrg> orgs)
    {
        foreach (var org in orgs)
        {
            if (string.Equals(org.Status, ActiveOrgStatus, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
