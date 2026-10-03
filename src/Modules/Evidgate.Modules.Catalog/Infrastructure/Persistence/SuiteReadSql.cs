namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>The Dapper statements of the suite read side (00 §5.5 C5), one statement per handler step (P3).</summary>
internal static class SuiteReadSql
{
    private const string SuiteColumns = """
               s.id, s.org_id, s.project_id, s.name, s.slug, s.status, s.head_revision_id,
               h.revision_no as head_revision_no, s.created_at, s.updated_at, s.version
        """;

    /// <summary>One suite with its head's number; the head falls back to the highest revision when the column is empty.</summary>
    internal const string SuiteById = $"""
        select {SuiteColumns}
          from public.suite s
          left join public.suite_revision h on h.org_id = s.org_id and h.id = s.head_revision_id
         where s.id = @suite_id
        """;

    internal const string SuitesFirstPage = $"""
        select {SuiteColumns}
          from public.suite s
          left join public.suite_revision h on h.org_id = s.org_id and h.id = s.head_revision_id
         where s.org_id = @org_id
           and s.project_id = @project_id
         order by s.created_at,
                  s.id
         limit @limit
        """;

    internal const string SuitesNextPage = $"""
        select {SuiteColumns}
          from public.suite s
          left join public.suite_revision h on h.org_id = s.org_id and h.id = s.head_revision_id
         where s.org_id = @org_id
           and s.project_id = @project_id
           and (s.created_at, s.id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by s.created_at,
                  s.id
         limit @limit
        """;

    /// <summary>The suite row a revision write locks (serialising revision_no allocation).</summary>
    internal const string SuiteForUpdate = """
        select id, project_id, status
          from public.suite
         where id = @suite_id
           for update
        """;

    internal const string SuiteForRead = "select id, project_id, status from public.suite where id = @suite_id";

    internal const string ExistingByHash = """
        select id, revision_no, status
          from public.suite_revision
         where org_id = @org_id
           and suite_id = @suite_id
           and revision_hash = @revision_hash
        """;

    internal const string NextRevisionNo = """
        select coalesce(max(revision_no), 0) + 1
          from public.suite_revision
         where org_id = @org_id
           and suite_id = @suite_id
        """;

    internal const string InsertVariants = """
        insert into public.variant (id, org_id, project_id, variant_hash, params)
        select r.id, @org_id, @project_id, r.variant_hash, r.params
          from jsonb_to_recordset(@rows::jsonb) as r(id uuid, variant_hash text, params jsonb)
        on conflict (org_id, project_id, variant_hash) do nothing
        """;

    internal const string InsertRevision = """
        insert into public.suite_revision
               (id, org_id, suite_id, revision_no, revision_hash, shard_strategy, retry_policy, environment_requirements, created_by)
        values (@id, @org_id, @suite_id, @revision_no, @revision_hash, @shard::jsonb, @retry::jsonb, @environment::jsonb, @created_by)
        """;

    internal const string InsertMembers = """
        insert into public.suite_member (id, org_id, suite_revision_id, case_revision_id, variant_id, ordinal, required)
        select r.id, @org_id, @revision_id, r.case_revision_id, v.id, r.ordinal, r.required
          from jsonb_to_recordset(@rows::jsonb) as r(id uuid, case_revision_id uuid, variant_hash text, ordinal integer, required boolean)
          join public.variant v on v.org_id = @org_id and v.project_id = @project_id and v.variant_hash = r.variant_hash
        """;

    internal const string InsertEdges = """
        insert into public.case_dependency (id, org_id, suite_revision_id, parent_member_id, child_member_id)
        select r.id, @org_id, @revision_id, r.parent, r.child
          from jsonb_to_recordset(@rows::jsonb) as r(id uuid, parent uuid, child uuid)
        """;

    internal const string SetHead = """
        update public.suite
           set head_revision_id = @revision_id
         where id = @suite_id
        """;

    /// <summary>A revision with its members and edges as JSON text, in one statement; the suite row supplies the project for the scope rule.</summary>
    internal const string RevisionDetail = """
        select r.id, r.suite_id, s.project_id, r.revision_no, r.revision_hash, r.status,
               r.shard_strategy::text as shard_strategy, r.retry_policy::text as retry_policy,
               r.environment_requirements::text as environment_requirements, r.created_at,
               (select coalesce(jsonb_agg(jsonb_build_object(
                          'id', m.id, 'case_revision_id', m.case_revision_id, 'variant_hash', v.variant_hash,
                          'params', v.params, 'required', m.required, 'ordinal', m.ordinal) order by m.ordinal), '[]'::jsonb)::text
                  from public.suite_member m
                  join public.variant v on v.org_id = m.org_id and v.id = m.variant_id
                 where m.org_id = r.org_id and m.suite_revision_id = r.id) as members_json,
               (select coalesce(jsonb_agg(jsonb_build_object(
                          'parent_member_id', d.parent_member_id, 'child_member_id', d.child_member_id)
                        order by d.parent_member_id, d.child_member_id), '[]'::jsonb)::text
                  from public.case_dependency d
                 where d.org_id = r.org_id and d.suite_revision_id = r.id) as edges_json
          from public.suite_revision r
          join public.suite s on s.org_id = r.org_id and s.id = r.suite_id
         where r.id = @revision_id
        """;

    internal const string Publish = "update public.suite_revision set status = 'published' where id = @revision_id and status = 'draft'";

    internal const string RevisionExists = "select 1 from public.suite_revision where id = @revision_id";

    internal const string DataIsolation = """
        select coalesce(environment_requirements ->> 'data_isolation', 'isolated')
          from public.suite_revision
         where id = @revision_id
        """;

    internal const string InsertSuite = """
        insert into public.suite (id, org_id, project_id, name, slug)
        values (@id, @org_id, @project_id, @name, @slug)
        """;
}
