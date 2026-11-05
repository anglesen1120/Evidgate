namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The release SQL of 05.6.1: the create transaction's three statements, the one reference probe that turns an unknown
/// or foreign environment revision into a 422 before any write, and the two statements of the read path. Every statement
/// is parameterised (00 §5.7 S1) and runs on the ambient tenant scope, so RLS — not a `where` clause alone — is what
/// makes another org's rows invisible.
/// </summary>
internal static class ReleaseSql
{
    /// <summary>
    /// The release row, inserted `draft` (the column default) and moved to `active` by <see cref="Activate"/> in the same
    /// transaction when the request asked for it. The row is inserted from the project row so a project that does not
    /// exist — or is deleted, or belongs to another org — inserts nothing and the handler answers the one 404. A duplicate
    /// name raises the unique key's 23505, which the handler maps to 409 rather than checking first (00 §5.7: no
    /// check-then-insert).
    /// </summary>
    internal const string InsertRelease = """
        insert into public.release (id, org_id, project_id, name, created_by)
        select @id, @org_id, p.id, @name, @created_by
          from public.project p
         where p.id = @project_id and p.org_id = @org_id and p.status <> 'deleted'
        """;

    /// <summary>
    /// The project-visibility probe that runs before any body rule can answer: one row when the project exists, is not
    /// soft-deleted and belongs to the caller's org, nothing otherwise. It is one statement, and it puts the 404 in
    /// front of every 422 — an unknown, soft-deleted, other-tenant or sibling-project target must not learn from a
    /// `release_invalid` that its project id was recognised at all (00 §9.2).
    /// </summary>
    internal const string ProjectVisible = """
        select 1
          from public.project p
         where p.id = @project_id and p.org_id = @org_id and p.status <> 'deleted'
        """;

    /// <summary>
    /// Every component of the release in one batch insert (00 §5.7 P2): the rows travel as ONE <c>jsonb</c> text
    /// parameter expanded by <c>jsonb_to_recordset</c>, so the statement count is constant in the component count. The
    /// migration's composite FK to <c>environment_revision</c> is the last line of defence; the handler's own probe
    /// (<see cref="UnresolvedRevisions"/>) answers the 422 before this statement runs.
    /// </summary>
    internal const string InsertComponents = """
        insert into public.release_component (id, org_id, release_id, component_key, digest, environment_revision_id)
        select r.id, @org_id, @release_id, r.component_key, r.digest, r.environment_revision_id
          from jsonb_to_recordset(@rows::jsonb)
               as r(id uuid, component_key text, digest text, environment_revision_id uuid)
        """;

    /// <summary>The one transition the guard permits: a `draft` release becomes `active`; a replay of it is a no-op.</summary>
    internal const string Activate = """
        update public.release set state = 'active' where id = @release_id and state = 'draft'
        """;

    /// <summary>
    /// The components whose environment revision does not resolve inside the project, by their 0-based request index:
    /// one statement for the whole array. RLS makes another tenant's revision invisible, so a foreign id and an unknown
    /// one answer the same 422 and existence never leaks across orgs; a revision of a sibling project of the same org is
    /// refused by the `project_id` predicate, exactly like the run-create path's revision probe.
    /// </summary>
    internal const string UnresolvedRevisions = """
        select r.idx as index
          from jsonb_to_recordset(@rows::jsonb) as r(idx integer, environment_revision_id uuid)
         where not exists (select 1
                             from public.environment_revision er
                            where er.id = r.environment_revision_id
                              and er.project_id = @project_id)
        """;

    /// <summary>The release header of the read path; RLS makes another tenant's id the same null an unknown one gets.</summary>
    internal const string ReleaseById = """
        select r.id as release_id, r.org_id, r.project_id, r.name, r.state, r.created_by, r.created_at, r.version
          from public.release r
         where r.id = @release_id
        """;

    /// <summary>
    /// The release's components in <c>component_key</c> order — a total order with no tie, so the read is deterministic
    /// and the response shape stable. The second and last statement of the read path (00 §5.7 P3): the bound-runs section
    /// is 05.6.1-T2's and is not read here.
    /// </summary>
    internal const string ComponentsByRelease = """
        select c.component_key, c.digest, c.environment_revision_id
          from public.release_component c
         where c.org_id = @org_id and c.release_id = @release_id
         order by c.component_key
        """;
}
