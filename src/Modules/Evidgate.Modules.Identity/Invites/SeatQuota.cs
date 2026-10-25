using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Identity.Invites;

/// <summary>
/// Which memberships count against an org's <c>max_seats</c> (02.1.1-T3, 02.1.2-T2.3):
/// <see cref="Invite"/> counts the rows that already hold a seat or will hold one when bound
/// (<c>active + pending</c>), <see cref="Bind"/> counts the seated members only, because binding a
/// pending row moves it from one bucket to the other.
/// </summary>
internal enum SeatCountMode
{
    /// <summary>Invite creation: <c>active + pending</c> rows must stay below <c>max_seats</c>.</summary>
    /// <remarks>
    /// Never used on resend: re-minting the token of an existing <c>pending</c> row consumes no seat,
    /// and counting it would refuse the resend of a row that is itself part of the count (AC6).
    /// </remarks>
    Invite,

    /// <summary>Bind: <c>active</c> rows must stay below <c>max_seats</c>.</summary>
    Bind,
}

/// <summary>
/// The answer of <see cref="SeatQuota.CheckAsync"/>: room, the limit it was measured against, and
/// the org's status read by the same statement (F4: a bind into a non-<c>active</c> org is refused).
/// </summary>
/// <param name="HasRoom">True when one more seat fits (always true when the org sets no limit).</param>
/// <param name="Limit">The org's <c>max_seats</c>, or null when it is unlimited.</param>
/// <param name="IsOrgActive">
/// True when the org is <c>active</c> — its only status that may gain a member; a
/// <c>suspended</c> or <c>deleted</c> org binds nothing.
/// </param>
internal readonly record struct SeatCheck(bool HasRoom, int? Limit, bool IsOrgActive);

/// <summary>
/// The seat limit of 02.1.1-T3 measured inside the caller's tenant transaction (02.1.2-T2.3):
/// <c>select status, max_seats … for no key update</c> first, then the count, so concurrent
/// invites or binds of one org serialise on the org row and can never overfill it (the same
/// gate-before-count shape as Tenancy's <c>CreateProjectCommand</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>No key update, not update.</b> The lock is <c>FOR NO KEY UPDATE</c> for the reason Tenancy's
/// <c>ProjectCreateGate</c> records (<c>TenancyReadSql.cs:134</c>): <c>FOR UPDATE</c> conflicts with
/// the <c>FOR KEY SHARE</c> every FK check on <c>organization</c> takes, so it blocked membership,
/// project and <c>audit_log</c> inserts of the same org for the whole check and could deadlock
/// against the audit advisory lock; <c>NO KEY UPDATE</c> still conflicts with itself, so it still
/// serialises invites and binds of one org.
/// </para>
/// <para>
/// The identity module may not reference the Tenancy module project (02 §5.5, architecture tests),
/// so the limit is read by SQL here and the refusal is built by the caller
/// (<see cref="InviteErrors.QuotaExceeded"/>), with the same 429 <c>quota_exceeded</c> envelope
/// Tenancy answers for <c>max_projects</c>. A NULL <c>max_seats</c> is "no limit".
/// </para>
/// </remarks>
internal static class SeatQuota
{
    /// <summary>The limit's name in the 429 envelope (<c>error.details[].path</c>) and its message.</summary>
    internal const string LimitName = "max_seats";

    /// <summary>The org status that may gain a member (0004's <c>organization_status_check</c>).</summary>
    internal const string ActiveOrganizationStatus = "active";

    /// <summary>
    /// Reads the org's status and limit under a row lock, then counts the memberships the mode
    /// counts. Runs on the ambient <see cref="ITenantDb"/>, i.e. the caller's own tenant
    /// transaction; the lock only means anything inside one.
    /// </summary>
    /// <param name="db">The Dapper path over the caller's tenant scope.</param>
    /// <param name="orgId">The org whose seats are measured.</param>
    /// <param name="mode">Which rows count (<see cref="SeatCountMode"/>).</param>
    /// <param name="cancellationToken">Cancels the statements.</param>
    public static async ValueTask<SeatCheck> CheckAsync(
        ITenantDb db,
        Guid orgId,
        SeatCountMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // A separate statement after the lock on purpose: it starts once the lock is held, so its
        // snapshot sees every invite or bind that committed while this one waited.
        var gate = await db
            .QuerySingleOrDefaultAsync<SeatGateRow>(LockSql, new { org_id = orgId }, cancellationToken)
            .ConfigureAwait(false);

        // No row (an org this connection may not see) keeps the old "no limit" answer, so the 429
        // envelope of the invite path is unchanged; it is not `active`, so nothing binds into it.
        if (gate is null)
        {
            return new SeatCheck(HasRoom: true, Limit: null, IsOrgActive: false);
        }

        var isActive = string.Equals(gate.Status, ActiveOrganizationStatus, StringComparison.Ordinal);

        if (gate.MaxSeats is not { } max)
        {
            return new SeatCheck(HasRoom: true, Limit: null, IsOrgActive: isActive);
        }

        var count = await db
            .ScalarAsync<long>(
                mode == SeatCountMode.Bind ? ActiveCountSql : SeatedAndPendingCountSql,
                new { org_id = orgId, max_seats = max },
                cancellationToken)
            .ConfigureAwait(false);

        return new SeatCheck(HasRoom: count < max, Limit: max, IsOrgActive: isActive);
    }

    /// <summary>One row of <see cref="LockSql"/>, in select order (Dapper's positional materialiser).</summary>
    /// <param name="Status">The org's status: only <c>active</c> may gain a member.</param>
    /// <param name="MaxSeats">The seat limit, or null when unlimited.</param>
    private sealed record SeatGateRow(string Status, int? MaxSeats);

    /// <summary>
    /// The org's status and seat limit under the row lock. <c>FOR NO KEY UPDATE</c>: see the type
    /// remarks and <c>TenancyReadSql.cs:134</c>.
    /// </summary>
    private const string LockSql = """
        select status
             , max_seats
          from public.organization
         where id = @org_id
           for no key update
        """;

    /// <summary>
    /// Seated members only (bind). Bounded by <c>@max_seats</c> the way Tenancy's
    /// <c>ActiveProjectCount</c> is: the check only needs "is the count at the limit", so the count
    /// costs O(limit) rather than O(memberships) while the org row lock is held.
    /// </summary>
    private const string ActiveCountSql = """
        select count(*)
          from (select 1
                  from public.membership
                 where org_id = @org_id
                   and status = 'active'
                 limit @max_seats) as counted
        """;

    /// <summary>Seated and pending rows (invite creation), bounded by <c>@max_seats</c> as above.</summary>
    private const string SeatedAndPendingCountSql = """
        select count(*)
          from (select 1
                  from public.membership
                 where org_id = @org_id
                   and status in ('active', 'pending')
                 limit @max_seats) as counted
        """;
}
