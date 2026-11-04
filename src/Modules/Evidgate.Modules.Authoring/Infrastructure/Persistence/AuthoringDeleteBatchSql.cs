namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements of the set-based checklist-item delete: one lock of the whole selection, one read of its last
/// versions, one non-empty probe and one update, so the number of statements a batched apply issues does not grow with
/// the number of changes.
/// </summary>
/// <remarks>
/// The lock statement sorts by id before it locks, which keeps the writer-wide order (<c>checklist_item</c> rows by id)
/// for two batches that overlap; the last-version read is a statement of its own after that lock, because a lateral
/// join would answer from the statement's snapshot and hide a version the lock holder just committed.
/// </remarks>
internal static class AuthoringDeleteBatchSql
{
    /// <summary>The selected item rows, locked in ascending id order.</summary>
    internal const string LockItems = """
        select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state, i.source, i.position,
               i.deleted_at, i.version
          from public.checklist_item i
         where i.org_id = @org_id and i.project_id = @project_id and i.id = any(@ids)
         order by i.id
           for update
        """;

    /// <summary>The last version number of every selected item, read after the locks.</summary>
    internal const string LastVersions = """
        select distinct on (entity_id) entity_id, version_no
          from public.authoring_version
         where org_id = @org_id and project_id = @project_id
           and entity_kind = 'checklist_item'
           and entity_id = any(@ids)
         order by entity_id, version_no desc
        """;

    /// <summary>The first selected item that still holds a live manual case: the non-empty guard, one statement.</summary>
    internal const string FirstLiveCase = """
        select m.checklist_item_id
          from public.manual_test_case m
         where m.org_id = @org_id and m.project_id = @project_id
           and m.checklist_item_id = any(@ids) and m.deleted_at is null
         order by m.checklist_item_id
         limit 1
        """;

    /// <summary>The soft delete of the whole selection: <c>deleted_at</c> set, the rows kept, the trigger bumping each
    /// <c>version</c>.</summary>
    internal const string SoftDeleteItems = """
        update public.checklist_item
           set deleted_at = clock_timestamp()
         where org_id = @org_id and project_id = @project_id and id = any(@ids) and deleted_at is null
        returning id, version
        """;
}

/// <summary>The last version number of one selected item (0 when the item has no version row yet).</summary>
internal sealed class DeleteLastVersionRow
{
    public Guid EntityId { get; set; }

    public int VersionNo { get; set; }
}

/// <summary>One row of the set-based soft delete: the item and its <c>version</c> after the trigger bumped it.</summary>
internal sealed class DeletedItemRow
{
    public Guid Id { get; set; }

    public int Version { get; set; }
}
