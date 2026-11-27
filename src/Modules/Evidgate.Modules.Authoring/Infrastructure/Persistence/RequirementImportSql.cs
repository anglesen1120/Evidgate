namespace Evidgate.Modules.Authoring.Infrastructure.Persistence;

/// <summary>
/// The two reads a requirement import resolves a whole file with. Each statement names the organization and the project
/// and takes its values as one <c>text[]</c>, so 5 000 rows cost the same two round trips as one (the writes go through
/// <c>RequirementWriter</c>, the single path that appends a version).
/// </summary>
internal static class RequirementImportSql
{
    /// <summary>
    /// The live requirements of the project a file's rows can match: by external reference, or by title (the kind and the
    /// parent are compared in memory, where the file's own new parents are known). The head hash is joined so an
    /// unchanged body is recognised without reading a body.
    /// </summary>
    internal const string Candidates = """
        select r.id, r.kind, r.title, r.external_ref, r.feature_node_id, r.parent_requirement_id,
               r.version as row_version, h.content_hash
          from public.requirement r
          join public.requirement_version h
            on h.org_id = r.org_id and h.requirement_id = r.id and h.version_no = r.head_version_no
         where r.org_id = @org_id and r.project_id = @project_id and r.deleted_at is null
           and (r.external_ref = any(@refs::text[]) or r.title = any(@titles::text[]))
        """;

    /// <summary>The ids, among the file's feature node ids, of the live feature nodes of this project.</summary>
    internal const string LiveFeatureNodes = """
        select n.id
          from public.feature_node n
         where n.org_id = @org_id and n.project_id = @project_id and n.deleted_at is null
           and n.id = any(@ids::text[]::uuid[])
        """;

    /// <summary>One stored requirement a row of the file may name.</summary>
    internal sealed class StoredRequirement
    {
        /// <summary>The stable identity.</summary>
        public Guid Id { get; set; }

        /// <summary>The kind as stored.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The title as stored (NFC, trimmed).</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The external reference label, or null.</summary>
        public string? ExternalRef { get; set; }

        /// <summary>The linked feature node, or null.</summary>
        public Guid? FeatureNodeId { get; set; }

        /// <summary>The parent requirement, or null.</summary>
        public Guid? ParentRequirementId { get; set; }

        /// <summary>The identity row's version, the concurrency token of an update.</summary>
        public int RowVersion { get; set; }

        /// <summary>The hash of the head body.</summary>
        public string ContentHash { get; set; } = string.Empty;
    }
}
