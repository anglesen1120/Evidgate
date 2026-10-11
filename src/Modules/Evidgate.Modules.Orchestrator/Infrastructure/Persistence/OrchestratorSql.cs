namespace Evidgate.Modules.Orchestrator.Infrastructure.Persistence;

/// <summary>The SQL of the manifest and environment-revision routes (05.1.1, 05.1.2); every statement runs in the request's tenant transaction.</summary>
internal static class OrchestratorSql
{
    /// <summary>Whether the project exists, and whether the named environment revision (if any) belongs to it — one round trip.</summary>
    internal const string ManifestTargets = """
        select exists (select 1 from public.project where id = @project_id and status <> 'deleted') as project_exists,
               (@environment_revision_id::uuid is null
                or exists (select 1 from public.environment_revision
                            where id = @environment_revision_id::uuid and project_id = @project_id)) as environment_known
        """;

    internal const string InsertManifest = """
        insert into public.manifest
               (id, org_id, project_id, manifest_hash, schema_version, suite_revision_id, environment_revision_id,
                shard_plan, matrix, env_needs, planned_count, document, verified_by)
        values (@id, @org_id, @project_id, @manifest_hash, 1, @suite_revision_id, @environment_revision_id::uuid,
                @shard_plan::jsonb, @matrix::jsonb, @env_needs::jsonb, @planned_count, @document::jsonb, @verified_by)
        on conflict (org_id, project_id, manifest_hash) do nothing
        returning id
        """;

    internal const string ManifestByHash = """
        select id from public.manifest where org_id = @org_id and project_id = @project_id and manifest_hash = @manifest_hash
        """;

    /// <summary>Creates the environment on its first revision; a project that does not exist (or is deleted) inserts nothing.</summary>
    internal const string EnsureEnvironment = """
        insert into public.environment (id, org_id, project_id, environment_key, name)
        select @id, @org_id, p.id, @environment_key, @name
          from public.project p
         where p.id = @project_id and p.status <> 'deleted'
        on conflict (org_id, project_id, environment_key) do nothing
        """;

    internal const string LockTimeout = "select set_config('lock_timeout', '5000', true)";

    /// <summary>The environment row, locked: it serialises the numbering of its revisions.</summary>
    internal const string EnvironmentForUpdate = """
        select e.id, e.current_revision_id, e.name
          from public.environment e
          join public.project p on p.org_id = e.org_id and p.id = e.project_id and p.status <> 'deleted'
         where e.project_id = @project_id and e.environment_key = @environment_key
           for no key update of e
        """;

    /// <summary>Dedupe and numbering in one round trip.</summary>
    internal const string RevisionSlot = """
        select same.id as existing_id, same.revision_no as existing_no,
               (select coalesce(max(revision_no), 0) + 1 from public.environment_revision
                 where org_id = @org_id and environment_id = @environment_id) as next_no
          from (select 1) as one
          left join lateral (
                select id, revision_no from public.environment_revision
                 where org_id = @org_id and environment_id = @environment_id and revision_hash = @revision_hash) as same on true
        """;

    internal const string InsertRevision = """
        insert into public.environment_revision
               (id, org_id, project_id, environment_id, revision_no, base_urls, dependency_versions, secret_refs, revision_hash, created_by)
        select @id, @org_id, @project_id, @environment_id, @revision_no, @base_urls::jsonb, @dependency_versions::jsonb,
               coalesce(array(select jsonb_array_elements_text(@secret_refs::jsonb)), '{}'), @revision_hash, @created_by
        """;

    /// <summary>Moves the pointer (and applies a new display name) only when something changes, so a replay does not bump the row version.</summary>
    internal const string MovePointer = """
        update public.environment
           set current_revision_id = @revision_id,
               name = coalesce(@name::text, name)
         where id = @environment_id
           and (current_revision_id is distinct from @revision_id or (@name::text is not null and name <> @name::text))
        """;

    internal const string RevisionById = """
        select r.id as revision_id, r.environment_id, e.environment_key, r.project_id, r.revision_no, r.revision_hash,
               r.base_urls::text as base_urls, r.dependency_versions::text as dependency_versions,
               array_to_json(r.secret_refs)::text as secret_refs, r.created_at
          from public.environment_revision r
          join public.environment e on e.org_id = r.org_id and e.id = r.environment_id
          join public.project p on p.org_id = r.org_id and p.id = r.project_id and p.status <> 'deleted'
         where r.id = @revision_id
        """;

    // ------------------------------------------------------------------ 05.2.1 run creation

    /// <summary>
    /// The manifest a run is created from, by id and project, in one statement (05.2.1 §5). RLS makes another
    /// tenant's manifest invisible, so an unknown or foreign id is the same 404. The handler derives the planned
    /// executions and the shard ranges from the immutable document's own copy of <c>shard_plan</c>: the manifest
    /// verifier already validated that copy at upload (05.1.1 §5), so <c>shard_plan</c> and <c>planned_count</c> are
    /// not projected a second time.
    /// </summary>
    internal const string RunManifest = """
        select suite_revision_id, environment_revision_id, document::text as document
          from public.manifest
         where org_id = @org_id and project_id = @project_id and id = @manifest_id
        """;

    /// <summary>
    /// Whether the manifest's suite and environment revisions resolve in this project, in one statement and with no
    /// row lock (05.2.1 AC5): the composite FKs enforce existence at insert, but the check runs first so an
    /// unresolved reference is a 422 <c>manifest_invalid</c> before any write. The suite revision is project-scoped
    /// through its suite, exactly like the manifest upload's check.
    /// </summary>
    internal const string RunRevisionTargets = """
        select exists (select 1
                         from public.suite_revision r
                         join public.suite s on s.org_id = r.org_id and s.id = r.suite_id
                        where r.id = @suite_revision_id and s.project_id = @project_id) as suite_known,
               exists (select 1 from public.environment_revision
                        where id = @environment_revision_id and project_id = @project_id) as environment_known
        """;

    /// <summary>
    /// The run row (05.2.1 §4, T13/T88/T93). <c>state</c> is <c>queued</c> because the producer slots commit in this
    /// same transaction; <c>collection_deadline_at</c> is computed on the database clock (<c>now()</c>, the same clock
    /// <c>created_at</c> defaults to) so the two can never disagree; <c>policy_revision_id</c> is the active revision
    /// 07.1.1-T4.2 pins (null when the project has no active policy); <c>ci_run_ref</c> comes from the caller's OIDC
    /// claims, never the body.
    /// </summary>
    internal const string InsertRun = """
        insert into public.run
               (id, org_id, project_id, manifest_id, policy_revision_id, suite_revision_id, environment_revision_id,
                ci_run_ref, components, purpose, state, collection_deadline_at, late_window_minutes)
        values (@id, @org_id, @project_id, @manifest_id, @policy_revision_id::uuid, @suite_revision_id, @environment_revision_id,
                @ci_run_ref::jsonb, @components::jsonb, 'regular', 'queued',
                now() + @collection_timeout::interval, @late_window_minutes)
        """;

    /// <summary>
    /// Every planned execution of the run in one batch insert (05.2.1 §5, 00 §5.7 P2): the rows travel as ONE
    /// <c>jsonb</c> text parameter expanded by <c>jsonb_to_recordset</c>, so the statement count is constant in the
    /// manifest's size and no array parameter reaches the read-only tenant Dapper path (the
    /// <c>StringArrayTypeHandler</c> refuses array writes; precedent <c>SuiteReadSql.InsertMembers</c>).
    /// <c>required</c> defaults true (the column default).
    /// </summary>
    internal const string InsertPlannedExecutions = """
        insert into public.planned_execution
               (id, org_id, run_id, execution_key, case_revision_id, variant_hash, target_env, shard_id, required)
        select r.id, @org_id, @run_id, r.execution_key, r.case_revision_id, r.variant_hash, r.target_env, r.shard_id, true
          from jsonb_to_recordset(@rows::jsonb)
               as r(id uuid, execution_key text, case_revision_id uuid, variant_hash text, target_env text, shard_id text)
        """;
}
