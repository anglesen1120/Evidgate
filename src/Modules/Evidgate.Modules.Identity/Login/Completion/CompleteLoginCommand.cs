namespace Evidgate.Modules.Identity.Login.Completion;

/// <summary>
/// The business half of a federated login (02.3.1 §5, 02.3.1-T1.4): the callback verified the provider
/// identity and allowlisted the <c>returnUrl</c>, and this command turns that verified identity into a
/// session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a Mediator message, deliberately.</b> The C2 pipeline opens a tenant transaction for every
/// <c>Result</c>-carrying message (<c>TenantTransactionBehaviour</c>) and its authorization stage reads that
/// tenant, so a dispatched command needs a resolved tenant before the handler runs. A login has none: the
/// browser has no session yet and the orgs of the account are exactly what the handler has still to resolve,
/// so the callback publishes no tenant and must not invent one. The pre-tenant CI exchanges are dispatched
/// only because their endpoints publish a tenant and a service principal first. This command therefore runs
/// from <see cref="LoginCommandCompletion"/> (the <see cref="ILoginCompletion"/> the protocol calls) and
/// writes no tenant-scoped row: <c>user</c>, <c>user_identity</c> and <c>session</c> are platform-scoped
/// tables reached through <c>evidgate_resolve</c>, and the membership question is the pre-tenant resolver
/// call, exactly as 02.3.1 §5 and 00 §5.4.8 prescribe.
/// </para>
/// <para>
/// <b>No outbound call, no shared transaction (P5).</b> Every provider call already happened in the
/// callback; these are independent statements in the order of 02.3.1 §5, each with its own failure — and the
/// bind and the audit of 02.3.1-T2 open their own per-org transaction, so this command owns none.
/// </para>
/// </remarks>
/// <param name="Identity">The provider-verified identity; <c>(Provider, Subject)</c> is the only key (D3).</param>
/// <param name="ReturnUrl">The allowlisted app path the browser lands on after the login.</param>
/// <param name="ClientIpAddress">The client address as the request reported it (sanitised before the write).</param>
/// <param name="UserAgent">The client's user agent, already bounded by the callback (never logged).</param>
/// <param name="InviteToken">
/// The invite token the browser staged before the login (<c>POST /v1/auth/invite-token</c>, 02.3.1-T2 Q1), or
/// null when it staged none. It is the single-use half an invite binds with (AC4) and reaches the binder and
/// nothing else: never the session row, a log line, a metric label, an error body or the <c>returnUrl</c>.
/// </param>
/// <param name="PredecessorCookieValue">
/// The <c>__Host-evidgate_session</c> value the browser PRESENTED to the callback (02.3.2-T1, D5a), or null when
/// it presented none. The session store revokes that row in the same transaction as the new insert and chains
/// <c>rotated_from</c> to it when it belongs to the same user, so a sign-in always kills whatever session the
/// browser was carrying (AC7). It is a credential value: it reaches the store and nothing else — never the
/// session row, a log line, a metric, a body or the redirect. A client-supplied session ID is never honoured
/// here, which is what makes fixation impossible: only the value the browser already held can die.
/// </param>
internal sealed record CompleteLoginCommand(
    VerifiedIdentity Identity,
    string ReturnUrl,
    string? ClientIpAddress,
    string? UserAgent,
    string? InviteToken = null,
    string? PredecessorCookieValue = null);

/// <summary>What a completed login hands back to the protocol: the session to cookie and where to redirect.</summary>
/// <param name="UserId">The platform user the identity resolved to (the same one a later login reuses).</param>
/// <param name="SessionId">The new <c>session</c> row (never written to a log, a metric or a body).</param>
/// <param name="SessionCookieValue">The one-time cookie value; the store keeps only its HMAC.</param>
/// <param name="ExpiresAt">The session's sliding deadline, which becomes the cookie's <c>Expires</c>.</param>
/// <param name="RedirectPath">A <c>Location</c>-safe app path (percent-encoded, allowlist re-checked).</param>
internal sealed record LoginResult(
    Guid UserId,
    Guid SessionId,
    string SessionCookieValue,
    DateTimeOffset ExpiresAt,
    string RedirectPath);
