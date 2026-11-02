using Evidgate.BuildingBlocks.Persistence.Resolve;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Login.Audit;

namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// Runs a <see cref="CompleteLoginCommand"/> in the order 02.3.1 §5 fixes — upsert the identity, bind a
/// pending invite, require an active membership, audit the login, create the session — and answers the typed
/// refusal of the step that refused.
/// </summary>
/// <remarks>
/// <para>
/// Each step is an independent statement with its own failure, and each refusal is final: a disabled user
/// gets 403 <c>account_disabled</c> with no session, no bind and no audit row; an account with no active org
/// gets 403 <c>no_membership</c> with no session and no cookie, and the rows step 1 wrote stay — the upsert is
/// the login's own bookkeeping, not a promise of access (02.3.1 §5, AC3).
/// </para>
/// <para>
/// <b>The bind runs before the membership question, and outside any tenant scope.</b> A pending invite is the
/// only way an invited-but-unbound account reaches its org, so the binder of 02.1.2-T2 is called right after
/// the upsert with the provider's own verdict on the address and the token the login staged. The binder opens
/// and commits one tenant transaction per org itself, so this handler opens none: the login has no tenant at
/// all (00 §5.4.8), and the membership question is the pre-tenant resolver, run after the bind so a bind of
/// this very login already counts. A reached seat limit (429 <c>quota_exceeded</c>) is not fatal — the binder
/// still attempts every other candidate org before it reports the refusal, so whatever bound is committed —
/// so the handler continues to the membership check; any other failure
/// value is returned unchanged and admits nobody. 02.3.1-T2 Q2/Q3 (the alias ceiling and the per-org Google
/// domain allowlist) are the binder's own rules; this call site adds none.
/// </para>
/// <para>
/// <b>Audited before the session, fail-closed.</b> The <c>auth.login</c> row is appended to the chain of every
/// active org of the user (02.3.1-T2.2, Q5) before the session row exists, so a login that cannot be audited
/// issues nothing: the writer's fault propagates and the seam above answers 503 <c>unavailable</c>.
/// </para>
/// <para>
/// Exceptions are not caught here: a database fault is not a business outcome, and the seam that owns the
/// response (<see cref="LoginCommandCompletion"/>) is what turns it into 503 <c>unavailable</c> with
/// <c>Retry-After</c>. Nothing in this class writes a log line, a metric label or a response body, and the
/// invite token reaches the binder and nothing else.
/// </para>
/// </remarks>
/// <param name="identities">The only writer of <c>user</c>/<c>user_identity</c> (02.3.1-T1.4's store).</param>
/// <param name="binder">The invite-binding seam (02.1.2-T2), called outside any tenant scope.</param>
/// <param name="memberOrgs">The pre-tenant <c>evidgate_resolve.member_orgs</c> resolver (02.6.1-T3).</param>
/// <param name="audit">The <c>auth.login</c> writer of 02.3.1-T2.2 (one tenant transaction per org).</param>
/// <param name="sessions">The session-creation seam (02.3.2-T1's implementation, `session` row + cookie value).</param>
internal sealed class CompleteLoginCommandHandler(
    ILoginIdentityStore identities,
    IMembershipBinder binder,
    IMemberOrgResolver memberOrgs,
    IIdentityAuditWriter audit,
    ISessions sessions)
{
    /// <summary>The org status that admits a login (02.3.1 AC3): a suspended org's membership never does.</summary>
    private const string ActiveOrgStatus = "active";

    /// <summary>Runs the command.</summary>
    /// <param name="command">The verified identity, the staged invite token and the client metadata to store on the session.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The created session and the redirect target, or 403 <c>account_disabled</c>, 403 <c>no_membership</c>,
    /// the binder's own non-quota failure unchanged, or the session store's own failure (403
    /// <c>account_disabled</c> / 503 <c>unavailable</c>) unchanged.
    /// </returns>
    public async ValueTask<Result<LoginResult>> HandleAsync(
        CompleteLoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var identity = command.Identity;

        // 1. The identity: idempotent on (provider, subject), so a replayed or repeated login lands on the
        //    same user and never merges by email (D3, AC6). A disabled account stops here: no bind, no audit
        //    row, no session (02.3.1 §6).
        var user = await identities.UpsertAsync(identity, cancellationToken).ConfigureAwait(false);

        if (!user.IsActive)
        {
            return Result.Failure<LoginResult>(LoginErrors.AccountDisabled);
        }

        // 2. The pending invite (AC4): only a provider-VERIFIED address may bind, and only when it has the
        //    shape invite creation accepts — a provider that reports something else binds nothing, which is
        //    not an error. The token is the single-use half the login staged; the binder decides every match,
        //    the org allowlist and the alias ceiling included.
        if (identity.EmailVerified && !string.IsNullOrEmpty(identity.Email) && InviteEmail.IsValid(identity.Email))
        {
            var bound = await binder
                .BindAsync(
                    new BindPendingMembershipsRequest(
                        user.UserId,
                        identity.Provider,
                        identity.Email,
                        EmailVerified: true,
                        command.InviteToken,
                        identity.HostedDomain),
                    cancellationToken)
                .ConfigureAwait(false);

            // A full org (429 quota_exceeded) refuses only its own seat: the binder has already tried every
            // other candidate org and committed what bound, so the login continues to the membership check
            // instead of failing. Any other failure admits nobody and is answered as the binder reported it.
            if (!bound.IsSuccess
                && !string.Equals(bound.Error!.Code, ApiErrorCode.QuotaExceeded, StringComparison.Ordinal))
            {
                return Result.Failure<LoginResult>(bound.Error!);
            }
        }

        // 3-5. Access, audit and the session: the same three ordered steps a development sign-in runs, so one
        //    method owns the "audited before the session" invariant instead of two copies of it.
        var issued = await IssueKnownUserAsync(
                user.UserId,
                identity.Provider,
                command.ClientIpAddress,
                command.UserAgent,
                command.PredecessorCookieValue,
                cancellationToken)
            .ConfigureAwait(false);

        if (!issued.IsSuccess)
        {
            return Result.Failure<LoginResult>(issued.Error!);
        }

        return Result.Success(new LoginResult(
            user.UserId,
            issued.Value.SessionId,
            issued.Value.CookieValue,
            issued.Value.ExpiresAt,
            ReturnUrlEncoder.ToLocation(command.ReturnUrl)));
    }

    /// <summary>
    /// The last three steps of a sign-in for an account that is already identified: at least one active
    /// membership, the <c>auth.login</c> row of every active org, and only then the session row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Extracted so the federated login and the development-only sign-in share ONE implementation of the order
    /// and of the fail-closed rule: a sign-in that cannot be audited issues nothing, and the writer's fault
    /// propagates to the seam that answers 503 <c>unavailable</c>.
    /// </para>
    /// <para>
    /// No identity is written here: the caller has already resolved the account (the provider upsert, or the
    /// seeded dev user), and a session is only ever created for the id it names.
    /// </para>
    /// </remarks>
    /// <param name="userId">The account the session belongs to.</param>
    /// <param name="provider">The provider name the audit row records (<c>google</c>, <c>github</c>, <c>dev</c>).</param>
    /// <param name="clientIpAddress">The client address as the request reported it (sanitised before the write).</param>
    /// <param name="userAgent">The client's user agent, already bounded (never logged).</param>
    /// <param name="predecessorCookieValue">The session cookie value the browser presented, or null.</param>
    /// <param name="cancellationToken">Request abort.</param>
    /// <returns>
    /// The created ticket, or 403 <c>no_membership</c>, or the session store's own failure
    /// (403 <c>account_disabled</c> / 503 <c>unavailable</c>) unchanged.
    /// </returns>
    internal async ValueTask<Result<SessionTicket>> IssueKnownUserAsync(
        Guid userId,
        string provider,
        string? clientIpAddress,
        string? userAgent,
        string? predecessorCookieValue,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        // Access: at least one org where the account holds an active membership.
        var orgs = await memberOrgs.MemberOrgsAsync(userId, cancellationToken).ConfigureAwait(false);

        var activeOrgs = new List<Guid>(orgs.Count);
        foreach (var org in orgs)
        {
            if (string.Equals(org.Status, ActiveOrgStatus, StringComparison.Ordinal))
            {
                activeOrgs.Add(org.OrgId);
            }
        }

        if (activeOrgs.Count == 0)
        {
            return Result.Failure<SessionTicket>(LoginErrors.NoMembership);
        }

        // The audit row of this sign-in, one per active org, before the session exists (Q5): a login that
        // cannot be recorded does not happen, so the writer's fault propagates and no session is issued.
        await audit
            .WriteAsync(
                new IdentityAuditEvent(IdentityAuditActions.Login, userId, provider),
                activeOrgs,
                cancellationToken)
            .ConfigureAwait(false);

        // The session: the store owns the row, the HMAC of the cookie value and the expiry pair. D5a: the value
        // the browser presented travels with the creation, so the store revokes that row in the same statement
        // and chains `rotated_from` — a sign-in never leaves the previous session alive.
        return await sessions
            .CreateAsync(
                new SessionCreation(
                    userId,
                    ClientAddress.Sanitise(clientIpAddress),
                    userAgent,
                    predecessorCookieValue),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
