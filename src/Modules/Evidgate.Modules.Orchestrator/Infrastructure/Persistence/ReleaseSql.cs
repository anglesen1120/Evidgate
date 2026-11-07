namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>
/// The release SQL: the create transaction's three statements, the reference probe that turns an unknown or foreign
/// environment revision into a 422 before any write, and the read path's statements. All are parameterised and run
/// on the ambient tenant scope, so RLS — not a `where` clause alone — hides another org's rows.
/// </summary>
internal static class ReleaseSql
{
    /// <summary>
    /// The release row, inserted `draft` (the column default) and moved to `active` by <see cref="Activate"/> in the
    /// same transaction when asked. The insert selects from the project row, so an unknown, deleted or other-org project
    /// inserts nothing and gets one 404; a duplicate name surfaces as 23505, which the handler maps to 409.
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
    /// `release_invalid` that its project id was recognised at all.
    /// </summary>
    internal const string ProjectVisible = """
        select 1
          from public.project p
         where p.id = @project_id and p.org_id = @org_id and p.status <> 'deleted'
        """;

    /// <summary>
    /// Every component of the release in one batch insert: the rows travel as ONE <c>jsonb</c> text
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
         where r.id = @release_id and r.org_id = @org_id
        """;

    /// <summary>
    /// The release's components in <c>component_key</c> order — a total order with no tie, so the read is deterministic
    /// and the response shape stable. The second of the read path's three statements; the bind path reads the same rows
    /// through the same statement, so a release is compared exactly as it is declared.
    /// </summary>
    internal const string ComponentsByRelease = """
        select c.component_key, c.digest, c.environment_revision_id
          from public.release_component c
         where c.org_id = @org_id and c.release_id = @release_id
         order by c.component_key
        """;

    /// <summary>
    /// The release a run create may bind to: one row when the id is in the caller's org and the run's project, nothing
    /// otherwise. The project predicate is what makes another project's release the same 404 an unknown id answers, so
    /// a run never binds to a sibling project's declaration.
    /// </summary>
    internal const string RunReleaseTarget = """
        select r.state
          from public.release r
         where r.id = @release_id and r.org_id = @org_id and r.project_id = @project_id
        """;

    /// <summary>
    /// The run's recorded <c>components</c> as text, plus the two identity facts a bind must refuse on, matched on the
    /// caller's org and the release's project. A fork run is advisory-only identity, and a replay run re-executes a
    /// stored run's inputs rather than this release's, so neither may be pinned to a release.
    /// </summary>
    internal const string RunComponents = """
        select r.components::text as components, r.is_fork, r.purpose
          from public.run r
         where r.id = @run_id and r.org_id = @org_id and r.project_id = @project_id
        """;

    /// <summary>
    /// Locks the run row of an explicit bind for the rest of its transaction. An evaluation row's composite foreign key
    /// takes <c>FOR KEY SHARE</c> on its run, which conflicts with <c>FOR UPDATE</c>: a concurrent live evaluation is
    /// therefore either committed before the guard reads it or blocked until this bind commits.
    /// </summary>
    internal const string LockRun = """
        select 1
          from public.run r
         where r.id = @run_id and r.org_id = @org_id
         for update
        """;

    /// <summary>
    /// One <c>release_run</c> row per matched component, in one batch. <c>on conflict do nothing</c> on
    /// <c>(org_id, release_id, run_id, component_key)</c> is what makes a re-bind of an existing pair a no-op instead of
    /// a 23505: the keys it already holds insert nothing, and the handler answers all rows the pair holds afterwards.
    /// </summary>
    internal const string InsertBindings = """
        insert into public.release_run (id, org_id, release_id, run_id, component_key)
        select b.id, @org_id, @release_id, @run_id, b.component_key
          from jsonb_to_recordset(@rows::jsonb) as b(id uuid, component_key text)
        on conflict (org_id, release_id, run_id, component_key) do nothing
        """;

    /// <summary>Every component bound for one pair, with its digest, ordered by component key.</summary>
    internal const string BoundComponentsByPair = """
        select c.component_key, c.digest
          from public.release_run rr
          join public.release_component c
            on c.org_id = rr.org_id and c.release_id = rr.release_id and c.component_key = rr.component_key
         where rr.org_id = @org_id and rr.release_id = @release_id and rr.run_id = @run_id
         order by c.component_key
        """;

    /// <summary>The bound component keys of one pair, ordered by key; the read seam other modules call.</summary>
    internal const string BoundKeys = """
        select rr.component_key
          from public.release_run rr
         where rr.org_id = @org_id and rr.release_id = @release_id and rr.run_id = @run_id
         order by rr.component_key
        """;

    /// <summary>
    /// The release's bindings ordered by <c>created_at</c> then id: insertion order over the
    /// <c>(org_id, release_id)</c> prefix of the binding key, never a "latest run" selection.
    /// </summary>
    internal const string BoundRuns = """
        select rr.run_id, rr.component_key, rr.created_at
          from public.release_run rr
         where rr.org_id = @org_id and rr.release_id = @release_id
         order by rr.created_at, rr.id
        """;

    /// <summary>
    /// The most bindings one run detail serves. A run may be bound to several releases and each bind writes up to
    /// <see cref="Evidgate.Modules.Orchestrator.Domain.ReleaseRules.MaxComponents"/> rows, so no writer constraint
    /// bounds the total; this is the read's own ceiling, ordered like the list so the oldest bindings are the ones kept.
    /// </summary>
    internal const int MaxBoundReleases = 500;

    /// <summary>
    /// The run-side mirror of <see cref="BoundRuns"/>, as one column of the statement that selects the run row
    /// (aliased <c>r</c>): one item per stored binding, joined to its release for the name, in the binding's own
    /// <c>created_at</c> then id order, capped by <c>@bound_release_limit</c>. It is a subquery of the run statement,
    /// so the bindings and the run's state come from one snapshot. The release's project is compared with the run's,
    /// the same predicate <see cref="MismatchSource"/> applies, so a binding row a bypassing writer pointed at a sibling
    /// project's release is not reported.
    /// </summary>
    internal const string BoundReleasesColumn = """
        (select coalesce(
                    jsonb_agg(
                        jsonb_build_object('release_id', b.release_id, 'release_name', b.release_name,
                                           'component_key', b.component_key)
                        order by b.created_at, b.id),
                    '[]'::jsonb)::text
           from (select rl.id as release_id, rl.name as release_name, rr.component_key, rr.created_at, rr.id
                   from public.release_run rr
                   join public.release rl
                     on rl.org_id = rr.org_id and rl.id = rr.release_id
                  where rr.org_id = r.org_id and rr.run_id = r.id and rl.project_id = r.project_id
                  order by rr.created_at, rr.id
                  limit @bound_release_limit) b) as bound_releases
        """;

    /// <summary>
    /// The two sides of the match for one pair in one row — the run's recorded <c>components</c> once, and the release's
    /// components aggregated into one document in <c>component_key</c> order — so the read seam derives a mismatch list
    /// in one round trip without repeating the run document per component.
    /// </summary>
    internal const string MismatchSource = """
        select ru.components::text as run_components,
               coalesce(
                   jsonb_agg(
                       jsonb_build_object('component_key', c.component_key, 'digest', c.digest)
                       order by c.component_key),
                   '[]'::jsonb)::text as release_components
          from public.release_component c
          join public.release rel
            on rel.org_id = c.org_id and rel.id = c.release_id
          join public.run ru
            on ru.org_id = c.org_id and ru.id = @run_id and ru.project_id = rel.project_id
         where c.org_id = @org_id and c.release_id = @release_id
         group by ru.components
        """;
}
