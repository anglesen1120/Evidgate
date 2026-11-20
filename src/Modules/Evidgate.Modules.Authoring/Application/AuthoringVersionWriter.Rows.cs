using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

internal sealed partial class AuthoringVersionWriter
{
    // ------------------------------------------------------------------ rows

    /// <summary>
    /// The row of <c>public.feature_node</c> as the writer locks it. A mutable class, like the Catalog read models: Dapper
    /// binds the seven selected columns by property name (<c>DefaultTypeMap.MatchNamesWithUnderscores</c>) and converts
    /// <c>timestamptz</c> to <see cref="DateTimeOffset"/>, while a positional record would need a constructor whose
    /// parameters match the result columns exactly. The last version of the history is filled in after the lock, because
    /// it is a statement of its own that must run after it.
    /// </summary>
    internal sealed class NodeRow
    {
        public Guid Id { get; set; }

        public Guid? ParentId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int Position { get; set; }

        /// <summary>The <c>deleted_at</c> of the row; the reader hands <c>timestamptz</c> over as <see cref="DateTime"/>.</summary>
        public DateTime? DeletedAt { get; set; }

        public int Version { get; set; }

        public int LastVersionNo { get; set; }

        public string LastContentHash { get; set; } = string.Empty;
    }

    /// <summary>The row of <c>public.checklist_item</c> as the writer locks it; see <see cref="NodeRow"/>.</summary>
    internal sealed class ItemRow
    {
        public Guid Id { get; set; }

        public Guid FeatureNodeId { get; set; }

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string Priority { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public int Position { get; set; }

        /// <summary>The <c>deleted_at</c> of the row; the reader hands <c>timestamptz</c> over as <see cref="DateTime"/>.</summary>
        public DateTime? DeletedAt { get; set; }

        public int Version { get; set; }

        public int LastVersionNo { get; set; }

        public string LastContentHash { get; set; } = string.Empty;
    }

    /// <summary>The row of <c>public.manual_test_case</c> as the writer locks it; see <see cref="NodeRow"/>.</summary>
    internal sealed class CaseRow
    {
        public Guid Id { get; set; }

        public Guid ChecklistItemId { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public string? Precondition { get; set; }

        public string Steps { get; set; } = string.Empty;

        public string Priority { get; set; } = string.Empty;

        public string State { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        /// <summary>The <c>deleted_at</c> of the row; the reader hands <c>timestamptz</c> over as <see cref="DateTime"/>.</summary>
        public DateTime? DeletedAt { get; set; }

        public int Version { get; set; }

        public int LastVersionNo { get; set; }

        public string LastContentHash { get; set; } = string.Empty;
    }

    /// <summary>The <c>version</c> a write returned.</summary>
    internal sealed class VersionRow
    {
        public int Version { get; set; }
    }

    /// <summary>The last <c>authoring_version</c> row of one entity, read after its lock.</summary>
    internal sealed class LastVersionRow
    {
        public int VersionNo { get; set; }

        public string ContentHash { get; set; } = string.Empty;
    }

    /// <summary>The two answers of the AC7 guard.</summary>
    internal sealed class NodeContentsRow
    {
        public bool HasChildren { get; set; }

        public bool HasItems { get; set; }
    }

    /// <summary>The AC7 guard of a checklist item.</summary>
    internal sealed class ItemContentsRow
    {
        public bool HasCases { get; set; }
    }

    /// <summary>AC8's position answers.</summary>
    internal sealed class SiblingPositionRow
    {
        public bool Taken { get; set; }

        public int MaxPosition { get; set; }
    }

    /// <summary>One stored version (the <c>fromVersionId</c> restore).</summary>
    internal sealed class StoredVersion
    {
        public Guid Id { get; set; }

        public string EntityKind { get; set; } = string.Empty;

        public Guid EntityId { get; set; }

        public int VersionNo { get; set; }

        public string Content { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;
    }

    /// <summary>A version row still to be inserted.</summary>
    internal sealed record PendingVersion(
        Guid VersionId,
        AuthoringEntityKind EntityKind,
        Guid EntityId,
        int VersionNo,
        string ChangeKind,
        string Content,
        string ContentHash,
        string? Reason);

    /// <summary>One row of the batched <c>authoring_version</c> insert.</summary>
    private sealed record VersionRowJson(
        Guid Id,
        string EntityKind,
        Guid EntityId,
        int VersionNo,
        string ChangeKind,
        string Content,
        string ContentHash,
        string Source,
        string? Reason,
        Guid? AiRunId);

    /// <summary>One locked live sibling of the group a reorder renumbers.</summary>
    internal sealed class ReorderSiblingRow
    {
        public Guid Id { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public int Position { get; set; }

        public int Version { get; set; }
    }

    /// <summary>One row of the post-renumber version read.</summary>
    internal sealed class ReorderVersionRow
    {
        public Guid Id { get; set; }

        public int Version { get; set; }
    }

    /// <summary>One <c>(id, position)</c> pair of the set-based assignment (<c>jsonb_to_recordset</c>).</summary>
    private sealed record ReorderTargetRow(Guid Id, int Position);
}
