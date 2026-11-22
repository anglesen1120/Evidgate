using System.Text.Json;
using System.Text.Json.Serialization;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Authoring.Application.Commands;
using Evidgate.Modules.Authoring.Contracts;
using Evidgate.Modules.Authoring.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Evidgate.Modules.Authoring.Application;

/// <summary>
/// The one code path that mutates an authoring entity and appends an <c>authoring_version</c> row (13.1.1 §5, T1.4,
/// AC5 and AC7): <see cref="AppendAsync"/> over the three <see cref="EntityChange"/> kinds,
/// <see cref="MoveManualTestCaseAsync"/> for AC22 and <see cref="RestoreAsync"/> for AC8.
/// </summary>
/// <remarks>
/// <para>
/// <b>The writer owns both rows of a change.</b> §5 states that every mutation of the three entities goes through this
/// type in the same tenant transaction, so a create inserts the entity row, an update writes the new snapshot, a soft
/// delete sets <c>deleted_at</c> and a restore clears it — each next to the version row it describes. No caller edits an
/// entity row and then asks for a version.
/// </para>
/// <para>
/// <b>It never opens, commits or rolls back a transaction.</b> Every statement runs on the ambient tenant scope
/// (<see cref="ITenantDb"/>), so a refusal leaves the caller's transaction to abort: that is what AC5's and AC7's
/// "nothing changes" means for a batch that already wrote an earlier entity of the same call.
/// </para>
/// <para>
/// <b>Serialisation.</b> Every mutation locks its entity row <c>for update</c> first and derives
/// <c>version_no</c> from the <c>version</c> the trigger returns, so two writers of one entity serialise on the row
/// lock and the unique key <c>(org, project, entity_kind, entity_id, version_no)</c> never surfaces as a 23505. If the
/// stored history is out of step with the row it refuses with <c>409 invalid_transition</c> instead.
/// </para>
/// <para>
/// <b>Lock order, one total order for every path:</b> <c>feature_node</c> → <c>checklist_item</c> →
/// <c>manual_test_case</c>, and within a rank by id. A batch takes <em>every</em> lock it can need — the rows it mutates
/// <c>for update</c> and the parents its content names <c>for share</c> — in that one sorted phase before its first
/// change; a move locks the target item before the case; a restore takes the sibling group's advisory lock, then peeks
/// its parent without a lock, takes the parent's share lock, then locks the entity, and answers <c>503</c> if the row
/// moved to another parent in between rather than taking a parent lock after the child. A parent probe is always a
/// share lock — it exists to block a concurrent soft delete of the parent, never to serialise siblings. Siblings are
/// serialised instead by one transaction-scoped advisory lock per group
/// (<see cref="AuthoringLockGuard.LockSiblingGroupAsync"/>, the pattern <c>CaseIdentityService</c> uses for
/// a project's sync), because a <c>for update</c> lock of the parent row would join this order and could form a cycle with
/// a batch that locks a child before its parent. Every path that takes that lock takes it <em>before</em> its first row
/// lock (the node create and move-into-group in their handler, the reorder and the restore in their core), so it is always
/// the outermost lock of the call. Every lock of a call is bounded by one 5 s <c>lock_timeout</c>,
/// and a lock timeout (55P03) or a deadlock (40P01) answers the retryable <c>503 unavailable</c> of §5 rather than a
/// 500, so even an order this comment missed degrades to a retry.
/// </para>
/// </remarks>
internal sealed partial class AuthoringVersionWriter(
    ITenantDb db,
    ITenantScopeAccessor scopes,
    ManualCaseStepsValidator steps)
{
    private const int MaximumReasonLength = 500;

    /// <summary>The partial sibling index of AC2: the one unique violation that is a <c>position_taken</c>, not a 23505.</summary>
    private const string SiblingPositionIndex = "feature_node_sibling_position_uq";

    /// <summary>AC15's one-case-per-code key: a caller-supplied code that is already taken is a 422, not a 23505.</summary>
    private const string ManualCaseCodeIndex = "manual_test_case_code_key";

    private static readonly JsonSerializerOptions VersionRowJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The highest sibling position a caller may name (§5 route 2/3; create validates the ladder, this validates the
    /// number). It is 10^9 — the same order of magnitude as the code allocator's cap (migrations/0039, <c>AllocateCode</c>)
    /// — and it exists because AC2's renumber parks a moved row at <c>position + max + 1</c>: with
    /// <c>max &lt;= 10^9</c> the parked value is at most <c>2*10^9 + 1</c>, still inside <c>integer</c>, so a caller can never
    /// push route 4 into a silent <c>int</c> overflow and a 500. A position past the cap is <c>422 out_of_range</c>.
    /// </summary>
    internal const int MaximumSiblingPosition = 1_000_000_000;
}

/// <summary>
/// What <see cref="AuthoringVersionWriter.ReorderNodesAsync"/> did: whether the group moved at all (AC5's no-op rule —
/// a list that already describes the stored order appends nothing) and the <b>one</b> <c>update</c> row per moved node
/// (AC2). It is module-internal, unlike <see cref="AuthoringAppendOutcome"/>: the reorder is a route of this module's
/// API, not a cross-module write, so 13.1.2 and 13.3.1 never see it.
/// </summary>
/// <param name="Changed">True when at least one sibling's position changed.</param>
/// <param name="Versions">One ref per moved node, in the order the ids were moved.</param>
internal sealed record AuthoringReorderOutcome(bool Changed, IReadOnlyList<AuthoringVersionRef> Versions);
