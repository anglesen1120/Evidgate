namespace Evidgate.Modules.Catalog.Infrastructure.Persistence;

/// <summary>
/// The Dapper statements of the catalog's read side (00 §5.5 C5). Each is a single statement (P3); the list is
/// keyset-paged on <c>(created_at, id)</c> over <c>test_case_list_idx</c> (D8), with one extra row read to decide
/// whether a next page exists.
/// </summary>
internal static class CatalogReadSql
{
    private const string CaseColumns = """
               id, org_id, project_id, annotation_id, name, framework, language, file_path, status,
               owner_membership_id, labels, first_seen_commit, last_seen_commit, created_at, updated_at, version
        """;

    /// <summary>One case by id.</summary>
    internal const string CaseById = $"""
        select {CaseColumns}
          from public.test_case
         where id = @case_id
        """;

    /// <summary>The project's status, for the project-existence check of the list route.</summary>
    internal const string ProjectExists = "select 1 from public.project where id = @project_id and status <> 'deleted'";

    /// <summary>First page of a project's cases; without a status filter archived cases are left out.</summary>
    internal const string CasesFirstPage = $"""
        select {CaseColumns}
          from public.test_case
         where org_id = @org_id
           and project_id = @project_id
           and ((@status::text is null and status <> 'archived') or status = @status)
           and (@framework::text is null or framework = @framework)
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>The next page: the same projection and order with the row-comparison cursor (00 §9.1).</summary>
    internal const string CasesNextPage = $"""
        select {CaseColumns}
          from public.test_case
         where org_id = @org_id
           and project_id = @project_id
           and ((@status::text is null and status <> 'archived') or status = @status)
           and (@framework::text is null or framework = @framework)
           and (created_at, id) > (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at,
                  id
         limit @limit
        """;

    /// <summary>One case with its identity trail (newest 50 sources and 50 renames) as JSON text columns, in one statement.</summary>
    internal const string CaseDetail = $"""
        select {CaseColumns},
               (select coalesce(jsonb_agg(jsonb_build_object(
                          'source', s.source, 'raw_key', s.raw_key, 'commit_sha', s.commit_sha, 'created_at', s.created_at)
                        order by s.created_at desc, s.id desc), '[]'::jsonb)::text
                  from (select * from public.case_identity_source
                         where org_id = c.org_id and case_id = c.id
                         order by created_at desc, id desc limit 50) s) as sources_json,
               (select coalesce(jsonb_agg(jsonb_build_object(
                          'from_path', r.from_path, 'to_path', r.to_path, 'commit_sha', r.commit_sha, 'detected_at', r.detected_at)
                        order by r.detected_at desc, r.id desc), '[]'::jsonb)::text
                  from (select * from public.case_rename_history
                         where org_id = c.org_id and case_id = c.id
                         order by detected_at desc, id desc limit 50) r) as renames_json
          from public.test_case c
         where c.id = @case_id
        """;

    private const string RevisionColumns = """
               id, case_id, revision_hash, commit_sha, file_path, framework, language, content_sha256, assertion_hash, created_at
        """;

    /// <summary>First page of a case's revisions, newest first; the index is scanned backwards.</summary>
    internal const string RevisionsFirstPage = $"""
        select {RevisionColumns}
          from public.test_revision
         where org_id = @org_id
           and case_id = @case_id
         order by created_at desc,
                  id desc
         limit @limit
        """;

    /// <summary>The next page: the row comparison runs the other way because the order is descending.</summary>
    internal const string RevisionsNextPage = $"""
        select {RevisionColumns}
          from public.test_revision
         where org_id = @org_id
           and case_id = @case_id
           and (created_at, id) < (@cursor_created_at::timestamptz, @cursor_id::uuid)
         order by created_at desc,
                  id desc
         limit @limit
        """;
}
