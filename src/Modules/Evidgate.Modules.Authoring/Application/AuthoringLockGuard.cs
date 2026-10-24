using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The bounded lock wait and the contention vocabulary an authoring command shares with
/// <see cref="AuthoringVersionWriter"/> (13.1.1 §5, AC2/AC19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a command needs it.</b> The writer bounds <em>its own</em> statements with a 5 s <c>lock_timeout</c> and answers
/// the two contention SQLSTATEs (55P03 lock timeout, 40P01 deadlock) with the retryable <c>503 unavailable</c> of §5. A
/// T2 command also takes row locks <em>before</em> it reaches the writer — the <c>for update</c> read of the node a
/// <c>PATCH</c>/<c>DELETE</c> addresses, and the group lock of AC2's sibling set — and those statements ran with no bound
/// and no mapping at all: a holder that never commits parked the request forever, and a deadlock surfaced as a 500. This
/// type is the one place both are fixed: the same 5 s bound the writer uses
/// (<see cref="AuthoringSql.SetLockTimeout"/>) and the same two SQLSTATEs it maps.
/// </para>
/// <para>
/// <b>The sibling-group lock.</b> AC2's unique index makes two live siblings of one group unable to share a position, but
/// a renumber parks the moved rows <em>above</em> the group's maximum and then assigns the final values, so a row that
/// enters the group between the renumber's read and its park can meet a parked value and the partial index refuses an
/// <c>UPDATE</c> the caller never made. Every writer that can add or move a row into a group therefore has to be
/// serialised with a renumber of that group, and the transaction-scoped advisory lock keyed on
/// <c>(project, parent)</c> is that serialisation — the pattern Catalog's project sync already uses
/// (<c>CaseIdentityService.cs:30</c>), chosen over a <c>for update</c> lock of the parent row because a row lock there
/// would join the writer's own rank-then-id order and could form a cycle with a batch that locks a child before its
/// parent. An advisory lock is never taken by the batch path, so no such cycle can include it.
/// </para>
/// </remarks>
internal static class AuthoringLockGuard
{
    /// <summary>
    /// Applies the writer's bound (a 5 s, transaction-local <c>lock_timeout</c>) to the caller's transaction, so every
    /// later lock of the command — handler-level and writer-level alike — gives up instead of parking the request.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task SetLockTimeoutAsync(ITenantDb db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.ExecuteAsync(AuthoringSql.SetLockTimeout, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// True for the two SQLSTATEs that mean "another writer holds what this call needs", so the route answers the
    /// retryable <c>503 unavailable</c> of §5 instead of a 500: a lock timeout (55P03) and a deadlock (40P01).
    /// </summary>
    /// <param name="exception">The database refusal.</param>
    internal static bool IsContention(PostgresException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected;
    }

    /// <summary>
    /// Takes the transaction-scoped advisory lock of one sibling group, so a renumber of that group and a write that adds
    /// or moves a row into it run one after the other instead of colliding in the partial unique index of AC2.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="projectId">The project of the group; a project's groups never contend with another project's.</param>
    /// <param name="parentId">The node whose live children are the group, or null for the project's root group.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task LockSiblingGroupAsync(
        ITenantDb db,
        Guid projectId,
        Guid? parentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.ExecuteAsync(
            AuthoringSql.LockSiblingGroup,
            new { group_key = SiblingGroupKey(projectId, parentId) },
            cancellationToken);
    }

    /// <summary>
    /// The advisory key of one group: <c>authoring_group:&lt;project&gt;:&lt;parent or root&gt;</c>. Built here, as one
    /// bound text parameter, so the key of a root group (<c>root</c>) can never meet the key of a node called "root" and
    /// no statement has to guess the type of a null uuid.
    /// </summary>
    /// <param name="projectId">The project.</param>
    /// <param name="parentId">The parent node, or null for the root group.</param>
    internal static string SiblingGroupKey(Guid projectId, Guid? parentId) =>
        "authoring_group:" + projectId.ToString("D") + ":" + (parentId?.ToString("D") ?? "root");
}
