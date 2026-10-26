using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Evidgate.Modules.Authoring.Application.Import;

/// <summary>
/// The five reads of an import's resolution phase (13.1.1 AC13/AC15, <c>ImportPlanner</c>): the project's case prefix,
/// the live tree the file is matched against, the cases its codes name, and the last <c>content_hash</c> of every entity
/// it matched. One statement each, whatever the file size — the row-count-independent half of the import (P2).
/// </summary>
/// <remarks>
/// Every statement is parameter-bound and RLS-scoped by the caller's tenant transaction (C3, S1), and every predicate is
/// served by a declared index: <c>feature_node_tree_idx</c> for the live tree, <c>checklist_item_list_idx</c> for the
/// items of the touched nodes, <c>manual_test_case_code_key</c> for the codes and <c>authoring_version_entity_key</c> for
/// the hashes.
/// </remarks>
internal static class ImportSql
{
    /// <summary>
    /// The project's live tree: every live feature node with the parent, kind, title, position and version the identity
    /// rules of AC13/AC15 compare. Ordered by the tree's list order so the planner's appends see a stable sibling set.
    /// </summary>
    internal const string LiveNodes = """
        select n.id, n.parent_id, n.kind, n.title, n.position, n.version
          from public.feature_node n
         where n.org_id = @org_id
           and n.project_id = @project_id
           and n.deleted_at is null
         order by n.parent_id, n.position, n.id
        """;

    /// <summary>
    /// The live checklist items of the nodes this file touches: an item's identity is <c>(node, NFC title)</c> among the
    /// node's live siblings, so the read is scoped to the resolved node ids and never scans the project.
    /// </summary>
    internal const string LiveItems = """
        select i.id, i.feature_node_id, i.title, i.description, i.priority, i.state, i.position, i.version
          from public.checklist_item i
         where i.org_id = @org_id
           and i.project_id = @project_id
           and i.feature_node_id = any(@node_ids)
           and i.deleted_at is null
         order by i.feature_node_id, i.position, i.id
        """;

    /// <summary>
    /// The cases this file's codes name, soft-deleted ones included: a live code is the AC15 update/unchanged half and a
    /// soft-deleted code is the restore half, and <c>manual_test_case_code_key</c> makes one code one row per project.
    /// </summary>
    internal const string CasesByCode = """
        select m.id, m.checklist_item_id, m.code, m.title, m.description, m.precondition,
               m.steps::text as steps, m.priority, m.state, m.version, m.deleted_at
          from public.manual_test_case m
         where m.org_id = @org_id
           and m.project_id = @project_id
           and m.code = any(@codes)
        """;

    /// <summary>
    /// The last <c>content_hash</c> of every entity the plan matched, in one statement: AC15's "unchanged" is an exact
    /// comparison of the snapshot the file describes with the snapshot the last version recorded, through the same
    /// RFC 8785 hash the writer stores (<c>AuthoringHashes.ContentHash</c>), never a field-by-field guess.
    /// </summary>
    /// <remarks>
    /// The two arrays are passed element-for-element, so row <c>i</c> of one is the kind of row <c>i</c> of the other:
    /// a distinct-kind set zipped against a distinct-id set asks for entities the file never named (Perf F1). The
    /// lookup is a <c>lateral ... limit 1</c> per entity on <c>authoring_version_entity_key</c> rather than a
    /// <c>distinct on</c> over every version the project ever wrote (Perf F4).
    /// </remarks>
    internal const string LastContentHashes = """
        select h.entity_kind, h.entity_id, v.content_hash
          from unnest(@kinds::text[], @ids::uuid[]) as h(entity_kind, entity_id)
          cross join lateral (
              select v.content_hash
                from public.authoring_version v
               where v.org_id = @org_id
                 and v.project_id = @project_id
                 and v.entity_kind = h.entity_kind
                 and v.entity_id = h.entity_id
               order by v.version_no desc
               limit 1
          ) v
        """;

    /// <summary>One live feature node of the project (the AC13 identity and sibling-position read).</summary>
    internal sealed class NodeRow
    {
        /// <summary>The node's id.</summary>
        public Guid Id { get; set; }

        /// <summary>The parent node, or null for a root module.</summary>
        public Guid? ParentId { get; set; }

        /// <summary>The stored kind.</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>The stored, NFC-composed title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The sibling position.</summary>
        public int Position { get; set; }

        /// <summary>The row's current <c>version</c>.</summary>
        public int Version { get; set; }
    }

    /// <summary>One live checklist item of a touched node (the AC13 identity and sibling-position read).</summary>
    internal sealed class ItemRow
    {
        /// <summary>The item's id.</summary>
        public Guid Id { get; set; }

        /// <summary>The owning node.</summary>
        public Guid FeatureNodeId { get; set; }

        /// <summary>The stored, NFC-composed title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The stored description.</summary>
        public string? Description { get; set; }

        /// <summary>The stored priority.</summary>
        public string Priority { get; set; } = string.Empty;

        /// <summary>The stored state.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>The sibling position.</summary>
        public int Position { get; set; }

        /// <summary>The row's current <c>version</c>.</summary>
        public int Version { get; set; }
    }

    /// <summary>One case a code of the file names, soft-deleted or not (the AC15 identity read).</summary>
    internal sealed class CaseRow
    {
        /// <summary>The case's id.</summary>
        public Guid Id { get; set; }

        /// <summary>The owning checklist item.</summary>
        public Guid ChecklistItemId { get; set; }

        /// <summary>The stored code.</summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>The stored title.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The stored description.</summary>
        public string? Description { get; set; }

        /// <summary>The stored precondition.</summary>
        public string? Precondition { get; set; }

        /// <summary>The stored steps document as JSON text.</summary>
        public string Steps { get; set; } = string.Empty;

        /// <summary>The stored priority.</summary>
        public string Priority { get; set; } = string.Empty;

        /// <summary>The stored state.</summary>
        public string State { get; set; } = string.Empty;

        /// <summary>The row's current <c>version</c>.</summary>
        public int Version { get; set; }

        /// <summary>The soft-delete stamp, or null for a live case.</summary>
        public DateTimeOffset? DeletedAt { get; set; }
    }

    /// <summary>One entity's last stored <c>content_hash</c>.</summary>
    internal sealed class VersionHashRow
    {
        /// <summary>The stored entity kind (<c>feature_node</c>, <c>checklist_item</c>, <c>manual_test_case</c>).</summary>
        public string EntityKind { get; set; } = string.Empty;

        /// <summary>The entity.</summary>
        public Guid EntityId { get; set; }

        /// <summary>The <c>sha256:&lt;hex&gt;</c> of the last stored snapshot.</summary>
        public string ContentHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// A <c>text[]</c> parameter of one of the reads above, typed explicitly.
    /// </summary>
    /// <remarks>
    /// Dapper cannot send a <c>string[]</c> on this path: the tenant read model registers a read-only <c>string[]</c>
    /// type handler (<see cref="Evidgate.BuildingBlocks.Persistence.Tenancy.StringArrayTypeHandler"/> — writes are EF
    /// Core's job), so Dapper's type-handler registry throws before the command is built. Dapper's custom-parameter
    /// hook runs before that registry. This is the module-local twin of
    /// <c>Evidgate.BuildingBlocks.Persistence.Tasks.TextArrayParameter</c>, which is internal to its own assembly and so
    /// is not visible to a module (00 §5.5: a module reaches another only through its <c>.Contracts</c>).
    /// </remarks>
    internal sealed class TextArrayParameter(string[] values) : SqlMapper.ICustomQueryParameter
    {
        /// <inheritdoc />
        public void AddParameter(IDbCommand command, string name)
        {
            ArgumentNullException.ThrowIfNull(command);

            var parameter = (NpgsqlParameter)command.CreateParameter();
            parameter.ParameterName = name;
            parameter.NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text;
            parameter.Value = values;

            command.Parameters.Add(parameter);
        }
    }
}
