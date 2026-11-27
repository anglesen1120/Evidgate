namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The statements of the requirement head writer and its scoped reads. Every statement names the organization and the
/// project, so a foreign id matches no row on top of the row level security policy.
/// </summary>
internal static class RequirementSql
{
    /// <summary>A live feature node of the project, the only valid target of <c>feature_node_id</c>.</summary>
    internal const string FeatureNodeExists = """
        select exists (
          select 1 from public.feature_node
           where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null)
        """;

    /// <summary>A live requirement of the project, the only valid parent.</summary>
    internal const string RequirementExists = """
        select exists (
          select 1 from public.requirement
           where org_id = @org_id and project_id = @project_id and id = @id and deleted_at is null)
        """;

    /// <summary>The identity row; a new one starts at head version 1 and the first version follows in the same transaction.</summary>
    internal const string InsertRequirement = """
        insert into public.requirement
          (id, org_id, project_id, kind, title, external_ref, feature_node_id, parent_requirement_id, head_version_no)
        values
          (@id, @org_id, @project_id, @kind, @title, @external_ref, @feature_node_id, @parent_requirement_id, 1)
        returning version
        """;

    /// <summary>
    /// Moves the head one number forward if, and only if, the caller read the current row version. The row lock taken
    /// by this UPDATE is what serializes two writers: the loser re-evaluates the WHERE clause after the winner commits,
    /// matches nothing and is answered as stale instead of allocating the same number. With <c>@set_identity</c> the
    /// same statement also replaces the identity fields, so one edit moves the row version once, not twice.
    /// </summary>
    internal const string AdvanceHead = """
        update public.requirement
           set head_version_no = head_version_no + 1,
               kind = case when @set_identity then @kind::text else kind end,
               title = case when @set_identity then @title::text else title end,
               external_ref = case when @set_identity then @external_ref::text else external_ref end,
               feature_node_id = case when @set_identity then @feature_node_id::uuid else feature_node_id end,
               parent_requirement_id = case when @set_identity then @parent_requirement_id::uuid else parent_requirement_id end
         where org_id = @org_id and project_id = @project_id and id = @id
           and version = @expected_version and deleted_at is null
        returning head_version_no, version
        """;

    /// <summary>
    /// Hides the requirement from the live list when the caller read the current row version, and answers the head it
    /// hid (for the audit row). Versions, the head pointer and every row that names the requirement stay; only
    /// <c>deleted_at</c> moves.
    /// </summary>
    internal const string SoftDelete = """
        with hidden as (
          update public.requirement
             set deleted_at = now()
           where org_id = @org_id and project_id = @project_id and id = @id
             and version = @expected_version and deleted_at is null
          returning id, head_version_no, version
        )
        select hidden.version, hidden.head_version_no, h.id as head_version_id, h.content_hash
          from hidden
          join public.requirement_version h
            on h.org_id = @org_id and h.requirement_id = hidden.id and h.version_no = hidden.head_version_no
        """;

    /// <summary>Why <see cref="AdvanceHead"/> matched nothing: the row is missing or deleted, or its version moved.</summary>
    internal const string HeadState = """
        select version, deleted_at is not null as deleted
          from public.requirement
         where org_id = @org_id and project_id = @project_id and id = @id
        """;

    /// <summary>Appends the version the head now names.</summary>
    internal const string InsertVersion = """
        insert into public.requirement_version
          (id, org_id, project_id, requirement_id, version_no, body, content_hash, source, author_membership_id, reason)
        values
          (@id, @org_id, @project_id, @requirement_id, @version_no, @body, @content_hash, @source, @author_membership_id, @reason)
        """;

    /// <summary>One requirement with its head and the id of the head version.</summary>
    internal const string SelectRequirement = """
        select r.id, r.kind, r.title, r.external_ref, r.feature_node_id, r.parent_requirement_id,
               r.head_version_no, r.version, r.deleted_at, r.created_at, r.updated_at,
               h.id as head_version_id, h.body, h.content_hash
          from public.requirement r
          join public.requirement_version h
            on h.org_id = r.org_id and h.requirement_id = r.id and h.version_no = r.head_version_no
         where r.org_id = @org_id and r.project_id = @project_id and r.id = @id
        """;

    /// <summary>
    /// One keyset page of the live requirements in <c>(created_at, id)</c> order, served by
    /// <c>requirement_project_list_idx</c>. The list carries no body: a page of 200 stays one small statement.
    /// </summary>
    internal const string SelectLive = """
        select r.id, r.kind, r.title, r.external_ref, r.feature_node_id, r.parent_requirement_id,
               r.head_version_no, r.version, r.created_at, r.updated_at
          from public.requirement r
         where r.org_id = @org_id and r.project_id = @project_id and r.deleted_at is null
           and (@after_created_at::timestamptz is null or (r.created_at, r.id) > (@after_created_at::timestamptz, @after_id::uuid))
         order by r.created_at, r.id
         limit @limit
        """;

    /// <summary>One version of one requirement of the project, by its id.</summary>
    internal const string SelectVersion = """
        select v.id, v.version_no, v.body, v.content_hash, v.source, v.author_membership_id, v.reason, v.created_at
          from public.requirement_version v
         where v.org_id = @org_id and v.project_id = @project_id and v.requirement_id = @requirement_id and v.id = @id
        """;

    /// <summary>
    /// A page of history, newest first, keyed by version number. The unique key
    /// <c>(org_id, requirement_id, version_no)</c> is scanned backwards, so there is no sort. The page is cut by row
    /// count first, then by the running size of the bodies (<c>octet_length</c> reads the size from the TOAST pointer,
    /// so no body is fetched to be counted): a row is kept while the bytes before it are under <c>@max_bytes</c>, so
    /// the first row always comes back. The bodies are read only for the rows that survive the cut.
    /// </summary>
    internal const string SelectVersions = """
        select b.id, b.version_no, b.body, b.content_hash, b.source, b.author_membership_id, b.reason, b.created_at
          from (
            select s.version_no
              from (
                select page.version_no, page.body_bytes,
                       sum(page.body_bytes) over (order by page.version_no desc) as running_bytes
                  from (
                    select v.version_no, octet_length(v.body) as body_bytes
                      from public.requirement r
                      join public.requirement_version v
                        on v.org_id = r.org_id and v.requirement_id = r.id
                     where r.org_id = @org_id and r.project_id = @project_id and r.id = @requirement_id
                       and (@before_version_no::integer is null or v.version_no < @before_version_no)
                     order by v.version_no desc
                     limit @limit
                  ) page
              ) s
             where s.running_bytes - s.body_bytes < @max_bytes
          ) kept
          cross join lateral (
            select v.id, v.version_no, v.body, v.content_hash, v.source, v.author_membership_id, v.reason, v.created_at
              from public.requirement_version v
             where v.org_id = @org_id and v.requirement_id = @requirement_id and v.version_no = kept.version_no
          ) b
         order by kept.version_no desc
        """;
}
