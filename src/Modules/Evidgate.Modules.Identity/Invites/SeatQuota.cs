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

/// <summary>The answer of <see cref="SeatQuota.CheckAsync"/>: room, and the limit it was measured against.</summary>
/// <param name="HasRoom">True when one more seat fits (always true when the org sets no limit).</param>
/// <param name="Limit">The org's <c>max_seats</c>, or null when it is unlimited.</param>
internal readonly record struct SeatCheck(bool HasRoom, int? Limit);

/// <summary>
/// The seat limit of 02.1.1-T3 measured inside the caller's tenant transaction (02.1.2-T2.3):
/// <c>select max_seats … for update</c> first, then the count, so concurrent invites or binds of
/// one org serialise on the org row and can never overfill it (the same gate-before-count shape as
/// Tenancy's <c>CreateProjectCommand</c>).
/// </summary>
/// <remarks>
/// The identity module may not reference the Tenancy module project (02 §5.5, architecture tests),
/// so the limit is read by SQL here and the refusal is built by the caller
/// (<see cref="InviteErrors.QuotaExceeded"/>), with the same 429 <c>quota_exceeded</c> envelope
/// Tenancy answers for <c>max_projects</c>. A NULL <c>max_seats</c> is "no limit".
/// </remarks>
internal static class SeatQuota
{
    /// <summary>The limit's name in the 429 envelope (<c>error.details[].path</c>) and its message.</summary>
    internal const string LimitName = "max_seats";

    /// <summary>
    /// Reads the org's limit under a row lock, then counts the memberships the mode counts. Runs on
    /// the ambient <see cref="ITenantDb"/>, i.e. the caller's own tenant transaction; the lock only
    /// means anything inside one.
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
        var limit = await db
            .ScalarAsync<int?>(LockSql, new { org_id = orgId }, cancellationToken)
            .ConfigureAwait(false);

        if (limit is not { } max)
        {
            return new SeatCheck(HasRoom: true, Limit: null);
        }

        var count = await db
            .ScalarAsync<long>(
                mode == SeatCountMode.Bind ? ActiveCountSql : SeatedAndPendingCountSql,
                new { org_id = orgId },
                cancellationToken)
            .ConfigureAwait(false);

        return new SeatCheck(HasRoom: count < max, Limit: max);
    }

    private const string LockSql = """
        select max_seats
          from public.organization
         where id = @org_id
           for update
        """;

    private const string ActiveCountSql = """
        select count(*)
          from public.membership
         where org_id = @org_id
           and status = 'active'
        """;

    private const string SeatedAndPendingCountSql = """
        select count(*)
          from public.membership
         where org_id = @org_id
           and status in ('active', 'pending')
        """;
}
