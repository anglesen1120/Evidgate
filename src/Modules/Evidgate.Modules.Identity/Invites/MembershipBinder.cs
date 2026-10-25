using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Npgsql;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// The invite binder of 02.1.2-T2 (T75): the one place a <c>pending</c> membership becomes
/// <c>active</c>. It implements the public <see cref="IMembershipBinder"/> seam that
/// 02.3.1-T2's login completion calls after a provider callback has been validated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two paths, one contract.</b> A presented invite token is parsed with
/// <see cref="InviteTokenFormat.TryParse"/> (no lookup when it does not parse) and its prefix
/// is resolved pre-tenant through <c>evidgate_resolve.invite_by_secret</c>; the org's own
/// transaction then re-reads the row under RLS and requires <c>pending</c>, unexpired, an HMAC
/// of the presented secret matching the stored hash, and a stored address equal to the
/// provider-verified one. Without a token, an org that opted in to verified-domain auto-bind
/// binds its pending invite under the same checks minus the secret — but only for
/// <c>google</c>, with an <c>hd</c> claim naming the verified address's own domain.
/// </para>
/// <para>
/// <b>One transaction per org.</b> Each row is bound in its own <see cref="ITenantScope"/>
/// opened here — the binder runs at login, before any tenant exists — and committed before
/// the next row is touched. The seat check takes the org's row lock first, so concurrent
/// binds of one org serialise on <c>max_seats</c> and can never overfill it.
/// </para>
/// <para>
/// <b>No oracle, no secret.</b> Every refusal — unknown prefix, wrong secret, another address,
/// expired, cancelled, already used, user already a member — answers the same empty result
/// with the row untouched; the secret comparison is constant-time
/// (<see cref="InviteSecrets.Verify"/>). Nothing here logs, and the token, the secret and the
/// address never leave this object.
/// </para>
/// </remarks>
internal sealed class MembershipBinder : IMembershipBinder
{
    /// <summary><c>BoundMembership.Via</c> of the invite-secret path.</summary>
    internal const string ViaInviteSecret = "invite_secret";

    /// <summary><c>BoundMembership.Via</c> of the opt-in verified-domain auto-bind.</summary>
    internal const string ViaVerifiedDomain = "verified_domain";

    /// <summary>The only provider whose hosted domain the auto-bind trusts (T75).</summary>
    private const string GoogleProvider = "google";

    private const string PendingStatus = "pending";
    private const string ActiveStatus = "active";

    /// <summary>One answer shared by every refusal, so an ordinary login allocates at most one.</summary>
    private static readonly BindPendingMembershipsResult NothingBound = new BindPendingMembershipsResult([]);

    private readonly NpgsqlMembershipBinderResolver _resolver;
    private readonly ITenantScopeFactory _scopes;
    private readonly ITenantDb _db;
    private readonly InviteSecrets _secrets;
    private readonly TimeProvider _clock;

    /// <summary>Creates the binder over the app-role data source, the tenant scope factory and the invite key material.</summary>
    /// <param name="dataSource">The app-role data source the pre-tenant invite resolvers run on.</param>
    /// <param name="scopes">Opens the per-org tenant transaction; a singleton, as is the binder.</param>
    /// <param name="db">The Dapper path over the ambient scope.</param>
    /// <param name="secrets">The invite pepper; without one no secret can verify.</param>
    /// <param name="clock">The clock the expiry check reads, so a test can move it.</param>
    public MembershipBinder(
        NpgsqlDataSource dataSource,
        ITenantScopeFactory scopes,
        ITenantDb db,
        InviteSecrets secrets,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(clock);

        _resolver = new NpgsqlMembershipBinderResolver(dataSource);
        _scopes = scopes;
        _db = db;
        _secrets = secrets;
        _clock = clock;
    }

    /// <inheritdoc />
    public async ValueTask<Result<BindPendingMembershipsResult>> BindAsync(
        BindPendingMembershipsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 02.1.2 §2 (T75): an unverified or absent address binds nothing, and nothing is
        // queried — the provider's word alone is never enough (FR-02-4: identity ≠ email).
        // The same holds for a user id that is not one: no row may be bound to Guid.Empty.
        if (!request.EmailVerified
            || request.UserId == Guid.Empty
            || string.IsNullOrEmpty(request.VerifiedEmail))
        {
            return Result.Success(NothingBound);
        }

        var email = InviteEmail.Normalize(request.VerifiedEmail);
        if (email.Length == 0)
        {
            return Result.Success(NothingBound);
        }

        var bound = new List<BoundMembership>(1);
        var attempted = new HashSet<Guid>();

        // (a) The invite secret: the only path that binds with any provider. A token that
        // does not parse is skipped like an absent one, and so is every secret while no
        // pepper is configured — nothing could verify it (InviteSecrets.CanHash false).
        if (_secrets.CanHash
            && InviteTokenFormat.TryParse(request.InviteToken, out var prefix, out var secret))
        {
            var candidate = await _resolver
                .ResolveBySecretAsync(prefix, cancellationToken)
                .ConfigureAwait(false);

            if (candidate is { } resolved)
            {
                attempted.Add(resolved.MembershipId);
                var attempt = await BindOneAsync(
                    resolved,
                    request.UserId,
                    email,
                    secret,
                    cancellationToken).ConfigureAwait(false);

                if (attempt.Error is { } error)
                {
                    return Result.Failure<BindPendingMembershipsResult>(error);
                }

                if (attempt.IsBound)
                {
                    bound.Add(new BoundMembership(resolved.OrgId, resolved.MembershipId, ViaInviteSecret));
                }
            }
        }

        // (b) The org's opt-in auto-bind: a Google Workspace account whose verified address
        // sits in the account's own hosted domain. Never for another provider, never without
        // `hd`, never for an org that did not opt in (the resolver filters both), and never
        // twice for one row.
        if (AutoBindAllowed(request, email))
        {
            var candidates = await _resolver
                .AutoBindCandidatesAsync(email, cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in candidates)
            {
                if (!attempted.Add(candidate.MembershipId))
                {
                    continue;
                }

                var attempt = await BindOneAsync(
                    candidate,
                    request.UserId,
                    email,
                    secret: null,
                    cancellationToken).ConfigureAwait(false);

                if (attempt.Error is { } error)
                {
                    return Result.Failure<BindPendingMembershipsResult>(error);
                }

                if (attempt.IsBound)
                {
                    bound.Add(new BoundMembership(candidate.OrgId, candidate.MembershipId, ViaVerifiedDomain));
                }
            }
        }

        return Result.Success(new BindPendingMembershipsResult(bound));
    }

    /// <summary>
    /// Whether the auto-bind path may run at all: Google, a non-empty <c>hd</c>, and an
    /// address whose own domain that <c>hd</c> names (ordinal, case-insensitive). A mismatch
    /// of either kind means the address alone binds nothing (T75).
    /// </summary>
    /// <param name="request">What the login reported.</param>
    /// <param name="email">The normalised verified address.</param>
    private static bool AutoBindAllowed(BindPendingMembershipsRequest request, string email)
    {
        if (!string.Equals(request.Provider, GoogleProvider, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(request.HostedDomain))
        {
            return false;
        }

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
        {
            return false;
        }

        return string.Equals(
            request.HostedDomain.Trim(),
            email[(at + 1)..],
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Binds one pending membership inside its org's own transaction: re-read under RLS,
    /// check every condition, take the seat lock, then the single-use update. Nothing is
    /// committed unless the update changed exactly one row.
    /// </summary>
    /// <param name="candidate">The org and membership the pre-tenant resolver named.</param>
    /// <param name="userId">The signed-in user.</param>
    /// <param name="email">The normalised verified address.</param>
    /// <param name="secret">The presented invite secret, or null for the auto-bind path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    private async ValueTask<BindAttempt> BindOneAsync(
        InviteCandidate candidate,
        Guid userId,
        string email,
        string? secret,
        CancellationToken cancellationToken)
    {
        await using var scope = await _scopes
            .BeginAsync(candidate.OrgId, projectId: null, readOnlyTransaction: false, cancellationToken)
            .ConfigureAwait(false);

        var row = await _db.QuerySingleOrDefaultAsync<PendingInviteRow>(
            """
            select status, invited_email, invite_token_hash, invite_pepper_version, invite_expires_at
              from public.membership
             where org_id = @org_id and id = @membership_id
            """,
            new { org_id = candidate.OrgId, membership_id = candidate.MembershipId },
            cancellationToken).ConfigureAwait(false);

        if (row is null || !string.Equals(row.Status, PendingStatus, StringComparison.Ordinal))
        {
            return BindAttempt.NotBound;
        }

        var expires = row.InviteExpiresAt;
        if (expires is null || expires.Value <= _clock.GetUtcNow())
        {
            return BindAttempt.NotBound;
        }

        // The HMAC compare runs before the address compare, so a wrong secret and a wrong
        // address cost the same work (T75: no timing oracle beyond the unavoidable).
        if (secret is not null)
        {
            var hash = row.InviteTokenHash;
            var pepperVersion = row.InvitePepperVersion;
            if (hash is not { Length: > 0 }
                || pepperVersion is null
                || !_secrets.Verify(pepperVersion.Value, hash, secret))
            {
                return BindAttempt.NotBound;
            }
        }

        if (!string.Equals(row.InvitedEmail, email, StringComparison.Ordinal))
        {
            return BindAttempt.NotBound;
        }

        // The org row lock taken here is what serialises concurrent binds of one org; a full
        // org fails the whole call, with this org's transaction rolled back uncommitted.
        var seats = await SeatQuota
            .CheckAsync(_db, candidate.OrgId, SeatCountMode.Bind, cancellationToken)
            .ConfigureAwait(false);

        if (!seats.HasRoom)
        {
            return BindAttempt.Refused(InviteErrors.QuotaExceeded(seats.Limit));
        }

        var changed = await _db.ExecuteAsync(
            """
            update public.membership
               set status = @active
                 , user_id = @user_id
             where id = @membership_id
               and org_id = @org_id
               and status = @pending
               and not exists (
                   select 1
                     from public.membership existing
                    where existing.org_id = @org_id
                      and existing.user_id = @user_id)
            """,
            new
            {
                active = ActiveStatus,
                user_id = userId,
                membership_id = candidate.MembershipId,
                org_id = candidate.OrgId,
                pending = PendingStatus,
            },
            cancellationToken).ConfigureAwait(false);

        if (changed != 1)
        {
            // Two zero-row cases, one answer (T75, single use, no oracle): the row stopped
            // being `pending` between the resolver and this statement (consumed, cancelled,
            // expired), or the user already holds a membership of this org (0011's
            // membership_org_user_key) - the invite is then satisfied and this row stays
            // pending. The duplicate test sits in the statement rather than in a caught
            // unique violation, so a refusal never aborts the tenant transaction.
            return BindAttempt.NotBound;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);

        return BindAttempt.Bound;
    }

    /// <summary>
    /// The <c>membership</c> columns the bind decision reads, in select order (Dapper builds a
    /// positional materialiser, so the order is load-bearing — <c>MembershipRow</c>'s rule).
    /// </summary>
    /// <param name="Status">The row's status; only <c>pending</c> can bind.</param>
    /// <param name="InvitedEmail">The stored, normalised address.</param>
    /// <param name="InviteTokenHash">The HMAC of the secret half, or null while the invite has none.</param>
    /// <param name="InvitePepperVersion">The pepper version of that hash, or null.</param>
    /// <param name="InviteExpiresAt">When the invite stops being bindable, or null.</param>
    private sealed record PendingInviteRow(
        string Status,
        string InvitedEmail,
        byte[]? InviteTokenHash,
        int? InvitePepperVersion,
        DateTimeOffset? InviteExpiresAt);

    /// <summary>What one row's attempt produced: bound, not bound, or the refusal that ends the call.</summary>
    /// <param name="IsBound">True when the row became <c>active</c> and its org committed.</param>
    /// <param name="Error">The refusal that ends the whole call, or null.</param>
    private readonly record struct BindAttempt(bool IsBound, Error? Error)
    {
        /// <summary>The row stayed <c>pending</c> and the caller must not learn why.</summary>
        internal static BindAttempt NotBound => default;

        /// <summary>The row became <c>active</c> and its org's transaction committed.</summary>
        internal static BindAttempt Bound => new(true, null);

        /// <summary>The org is full: the whole call answers 429, nothing in this org is bound.</summary>
        internal static BindAttempt Refused(Error error) => new(false, error);
    }
}
