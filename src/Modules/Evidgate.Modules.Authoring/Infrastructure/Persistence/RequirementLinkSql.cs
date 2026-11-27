namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements that create and remove a requirement link. Every statement names the organization and the project,
/// so a foreign id matches no row on top of the row level security policy.
/// </summary>
internal static class RequirementLinkSql
{
    /// <summary>
    /// Share-locks the live requirement row alone. An append moves the head with an UPDATE that conflicts with this
    /// lock, so the head read by <see cref="HeadVersionId"/> afterwards is still the head when the link commits. The
    /// head version is deliberately not joined here: a statement that waited on the lock keeps its old snapshot, so
    /// a join to a version the waited-for writer just inserted would drop the row and answer a spurious 404.
    /// </summary>
    internal const string LockRequirement = """
        select r.head_version_no
          from public.requirement r
         where r.org_id = @org_id and r.project_id = @project_id and r.id = @requirement_id and r.deleted_at is null
           for share of r
        """;

    /// <summary>The head version row of a requirement whose row <see cref="LockRequirement"/> holds, in a fresh statement snapshot.</summary>
    internal const string HeadVersionId = """
        select h.id
          from public.requirement_version h
         where h.org_id = @org_id and h.requirement_id = @requirement_id and h.version_no = @head_version_no
        """;

    /// <summary>A live checklist item of the project, a valid link target.</summary>
    internal const string ChecklistItemExists = """
        select exists (
          select 1 from public.checklist_item
           where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null)
        """;

    /// <summary>A live manual test case of the project, a valid link target.</summary>
    internal const string ManualCaseExists = """
        select exists (
          select 1 from public.manual_test_case
           where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null)
        """;

    /// <summary>
    /// Inserts the pinned link. The single <c>unique nulls not distinct</c> constraint
    /// <c>requirement_link_target_key</c> is the arbiter of "one link per requirement and target", so a repeat or a
    /// concurrent twin writes nothing and returns no id.
    /// </summary>
    internal const string InsertLink = """
        insert into public.requirement_link
          (id, org_id, project_id, requirement_id, requirement_version_id, checklist_item_id, manual_test_case_id,
           linked_by_membership_id)
        values
          (@id, @org_id, @project_id, @requirement_id, @requirement_version_id, @checklist_item_id, @manual_test_case_id,
           @membership_id)
        on conflict do nothing
        returning id
        """;

    /// <summary>The link that stopped <see cref="InsertLink"/>, with the number of the version it pins.</summary>
    internal const string ExistingLink = """
        select l.id as link_id, l.requirement_version_id as version_id, v.version_no
          from public.requirement_link l
          join public.requirement_version v
            on v.org_id = l.org_id and v.requirement_id = l.requirement_id and v.id = l.requirement_version_id
         where l.org_id = @org_id and l.project_id = @project_id and l.requirement_id = @requirement_id
           and l.checklist_item_id is not distinct from @checklist_item_id::uuid
           and l.manual_test_case_id is not distinct from @manual_test_case_id::uuid
        """;

    /// <summary>
    /// Removes one link of the requirement and answers what it was (its pinned version and target), so the audit row
    /// can record the removed association. A soft-deleted requirement keeps its links removable.
    /// </summary>
    internal const string DeleteLink = """
        with removed as (
          delete from public.requirement_link
           where org_id = @org_id and project_id = @project_id and requirement_id = @requirement_id and id = @id
          returning id, requirement_version_id, checklist_item_id, manual_test_case_id
        )
        select d.id as link_id, d.requirement_version_id as version_id, v.version_no,
               d.checklist_item_id, d.manual_test_case_id
          from removed d
          join public.requirement_version v
            on v.org_id = @org_id and v.requirement_id = @requirement_id and v.id = d.requirement_version_id
        """;
}
