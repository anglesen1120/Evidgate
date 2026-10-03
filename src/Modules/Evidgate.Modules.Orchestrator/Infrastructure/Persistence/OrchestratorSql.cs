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
           for update of e
        """;

    /// <summary>Dedupe and numbering in one round trip.</summary>
    internal const string RevisionSlot = """
        select (select id from public.environment_revision
                 where org_id = @org_id and environment_id = @environment_id and revision_hash = @revision_hash) as existing_id,
               (select revision_no from public.environment_revision
                 where org_id = @org_id and environment_id = @environment_id and revision_hash = @revision_hash) as existing_no,
               (select coalesce(max(revision_no), 0) + 1 from public.environment_revision
                 where org_id = @org_id and environment_id = @environment_id) as next_no
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
         where r.id = @revision_id
        """;
}
