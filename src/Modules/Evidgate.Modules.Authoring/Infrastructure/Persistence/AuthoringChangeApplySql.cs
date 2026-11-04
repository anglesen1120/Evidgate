using Evidgate.BuildingBlocks.Persistence.Tenancy;

namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The SQL of the batched apply and the discard (13.1.2 §6): the two lock statements in the order every applier shares,
/// the apply-time non-empty re-check, the one state flip of each route, and the reads both routes need before writing.
/// </summary>
/// <remarks>
/// Every statement is tenant- and project-scoped in its own predicate as well as by row level security, and both lock
/// statements sort <b>before</b> they lock (<c>order by … for update</c>), so two appliers of overlapping selections take
/// the same rows in the same order and cannot form a deadlock cycle. The change rows are locked first, in
/// <c>(target_id, id)</c> order, and the affected items second in one statement over the union of every selected change's
/// affected set, sorted by id.
/// </remarks>
internal static class AuthoringChangeApplySql
{
    /// <summary>The lock set of a discard: the state of one change row of this project, or nothing.</summary>
    internal const string LockOneChange = """
        select state
          from public.authoring_change
         where org_id = @org_id and project_id = @project_id and id = @change_id
           for update
        """;

    /// <summary>The selected change rows, locked in the shared <c>(target_id, id)</c> order.</summary>
    internal const string LockChanges = """
        select id, target_id, change_type, state, reason, affected_versions::text as affected_versions
          from public.authoring_change
         where org_id = @org_id and project_id = @project_id and id = any(@ids)
         order by target_id, id
           for update
        """;

    /// <summary>
    /// One statement over the union of every affected item — the targets <b>and</b> every payload item a merge names —
    /// locked in ascending id order after the change rows, so the batch ETag and the lock set cover the same rows.
    /// </summary>
    internal const string LockItems = """
        select id, version, deleted_at
          from public.checklist_item
         where org_id = @org_id and project_id = @project_id and id = any(@ids)
         order by id
           for update
        """;

    /// <summary>
    /// The apply-time half of the non-empty guard: the first item of the selection that still holds a live manual case.
    /// A case created after the change was queued bumps the case's version, not the item's, so the batch ETag cannot see
    /// it and this statement is the only check that can.
    /// </summary>
    internal const string FirstLiveCase = """
        select checklist_item_id
          from public.manual_test_case
         where org_id = @org_id and project_id = @project_id
           and checklist_item_id = any(@ids) and deleted_at is null
         limit 1
        """;

    /// <summary>The one state flip of an apply: every selected row, and only while it is still `pending`.</summary>
    internal const string ApplyChanges = """
        update public.authoring_change
           set state = 'applied',
               applied_at = now(),
               applied_batch_id = @batch_id,
               applied_by_membership_id = @actor
         where org_id = @org_id and project_id = @project_id and id = any(@ids) and state = 'pending'
        """;

    /// <summary>The one state flip of a discard, answering the stamp the response carries.</summary>
    internal const string DiscardChange = """
        update public.authoring_change
           set state = 'discarded',
               discarded_at = now(),
               discarded_by_membership_id = @actor
         where org_id = @org_id and project_id = @project_id and id = @change_id and state = 'pending'
        returning discarded_at
        """;
}

/// <summary>One locked <c>authoring_change</c> row of a selection.</summary>
internal sealed class AuthoringChangeRow
{
    /// <summary>The change id.</summary>
    public Guid Id { get; init; }

    /// <summary>The change's target item.</summary>
    public Guid TargetId { get; init; }

    /// <summary>The stored <c>change_type</c> text.</summary>
    public string ChangeType { get; init; } = string.Empty;

    /// <summary>The stored <c>state</c> text.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The caller's reason, recorded on the version row of the change it applies.</summary>
    public string? Reason { get; init; }

    /// <summary>The <c>affected_versions</c> object, as stored (item id → version at queue time).</summary>
    public string AffectedVersions { get; init; } = "{}";
}

/// <summary>One locked <c>checklist_item</c> row of the affected set.</summary>
internal sealed class AuthoringAffectedItemRow
{
    /// <summary>The item id.</summary>
    public Guid Id { get; init; }

    /// <summary>The item's version now.</summary>
    public int Version { get; init; }

    /// <summary>When the item was soft-deleted, or null while it is live.</summary>
    public DateTimeOffset? DeletedAt { get; init; }
}

/// <summary>The reader shared by the apply's preconditions: it only reads, and its two locks are ordered.</summary>
internal static class AuthoringChangeApplyReader
{
    /// <summary>
    /// Locks and reads the selected change rows. A missing id — another tenant's, another project's or a random one —
    /// is indistinguishable from the rest and answered by the caller as <c>404 not_found</c>.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="changeIds">The selection.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<IReadOnlyList<AuthoringChangeRow>> LockChangesAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        IReadOnlyList<Guid> changeIds,
        CancellationToken cancellationToken) =>
        db.QueryAsync<AuthoringChangeRow>(
            AuthoringChangeApplySql.LockChanges,
            new { org_id = orgId, project_id = projectId, ids = changeIds.ToArray() },
            cancellationToken);

    /// <summary>Locks the affected items in ascending id order and answers their current version.</summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="itemIds">The union of the selection's affected items.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<IReadOnlyList<AuthoringAffectedItemRow>> LockItemsAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        IReadOnlyList<Guid> itemIds,
        CancellationToken cancellationToken) =>
        db.QueryAsync<AuthoringAffectedItemRow>(
            AuthoringChangeApplySql.LockItems,
            new { org_id = orgId, project_id = projectId, ids = itemIds.ToArray() },
            cancellationToken);

    /// <summary>
    /// The first soft-delete target of the batch that holds a live manual case, or null when every one of them is
    /// empty — the apply-time re-check of the non-empty guard.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project.</param>
    /// <param name="deleteTargets">The items the batch would soft-delete.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<Guid?> FirstLiveCaseAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        IReadOnlyList<Guid> deleteTargets,
        CancellationToken cancellationToken) =>
        db.ScalarAsync<Guid?>(
            AuthoringChangeApplySql.FirstLiveCase,
            new { org_id = orgId, project_id = projectId, ids = deleteTargets.ToArray() },
            cancellationToken);

    /// <summary>
    /// Locks one change row and answers its state, or null when the id is not one of this project's rows — another
    /// tenant's, another project's and a random id all answer null, which the caller turns into the same 404.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="changeId">The change from the route.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<string?> LockOneStateAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid changeId,
        CancellationToken cancellationToken) =>
        db.ScalarAsync<string?>(
            AuthoringChangeApplySql.LockOneChange,
            new { org_id = orgId, project_id = projectId, change_id = changeId },
            cancellationToken);

    /// <summary>
    /// Moves the locked row to <c>discarded</c> and answers the stamp the response carries. The predicate repeats
    /// <c>state = 'pending'</c>, so a row that somehow left the state between the lock and the update writes nothing.
    /// </summary>
    /// <param name="db">The tenant-bound database of the open command scope.</param>
    /// <param name="orgId">The organization of the open scope.</param>
    /// <param name="projectId">The project from the route.</param>
    /// <param name="changeId">The change from the route.</param>
    /// <param name="actor">The caller's membership.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    internal static Task<DateTimeOffset> DiscardAsync(
        ITenantDb db,
        Guid orgId,
        Guid projectId,
        Guid changeId,
        Guid actor,
        CancellationToken cancellationToken) =>
        db.ScalarAsync<DateTimeOffset>(
            AuthoringChangeApplySql.DiscardChange,
            new { org_id = orgId, project_id = projectId, change_id = changeId, actor },
            cancellationToken);
}
