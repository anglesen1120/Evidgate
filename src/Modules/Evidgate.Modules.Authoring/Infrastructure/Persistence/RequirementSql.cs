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
    /// matches nothing and is answered as stale instead of allocating the same number.
    /// </summary>
    internal const string AdvanceHead = """
        update public.requirement
           set head_version_no = head_version_no + 1
         where org_id = @org_id and project_id = @project_id and id = @id
           and version = @expected_version and deleted_at is null
        returning head_version_no, version
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
               r.head_version_no, r.version, r.deleted_at, h.id as head_version_id
          from public.requirement r
          join public.requirement_version h
            on h.org_id = r.org_id and h.requirement_id = r.id and h.version_no = r.head_version_no
         where r.org_id = @org_id and r.project_id = @project_id and r.id = @id
        """;

    /// <summary>
    /// A page of history, newest first, keyed by version number. The unique key
    /// <c>(org_id, requirement_id, version_no)</c> is scanned backwards, so there is no sort.
    /// </summary>
    internal const string SelectVersions = """
        select v.id, v.version_no, v.body, v.content_hash, v.source, v.author_membership_id, v.reason, v.created_at
          from public.requirement r
          join public.requirement_version v
            on v.org_id = r.org_id and v.requirement_id = r.id
         where r.org_id = @org_id and r.project_id = @project_id and r.id = @requirement_id
           and (@before_version_no::integer is null or v.version_no < @before_version_no)
         order by v.version_no desc
         limit @limit
        """;
}
