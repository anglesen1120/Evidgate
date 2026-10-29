namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// One row of <c>GET /v1/projects/{p}/runs</c> (05.2.2 §5, §9.3; identity projection added by 05.2.2-T2; F43/S143
/// adds <c>completeness</c>, <c>parent_run_id</c> and <c>rerun_scope</c>). The wire names are <c>run_id</c>,
/// <c>state</c>, <c>created_at</c>, <c>ref</c>, <c>unverified_ref</c>, <c>completeness</c>, <c>parent_run_id</c> and
/// <c>rerun_scope</c> (the host's <c>SnakeCaseLower</c> policy). <c>decision</c>, <c>rerun_label</c> and
/// <c>reprocess_label</c> stay absent here — they are handed over to 07.2.2/07.5.2 and 05.5.1/05.5.2 and remain
/// optional in the schema until then.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="State">Its state, one of the 00 §8.1 vocabulary.</param>
/// <param name="CreatedAt">Creation, UTC — the primary sort key of the list.</param>
/// <param name="Ref">
/// What the run was created for (05.2.2 §5), the same block <c>GET /v1/runs/{r}</c> returns. Every member is
/// present, the ones no source supplied are null, and <see cref="RunRef.IsFork"/> is always a boolean.
/// </param>
/// <param name="UnverifiedRef">
/// True when at least one member of <see cref="Ref"/> came from the request body rather than a verified GitHub OIDC
/// claim, i.e. the identity is advisory input and never a gating one (05.2.2 §8, AC2).
/// </param>
/// <param name="Completeness">
/// The <c>evidence_revision.completeness</c> (<c>complete</c> or <c>incomplete</c>) of the revision
/// <c>run.current_evidence_revision_id</c> names, the <c>EvidenceBadge</c> verdict of 00 §8.5; null while the run has
/// no evidence revision yet. It is never reported as <c>incomplete</c> without a stored row.
/// </param>
/// <param name="ParentRunId">The run this one was created from by a rerun (<c>run.parent_run_id</c>, 0031); null for an original run.</param>
/// <param name="RerunScope">
/// The scope the run was created with (<c>run.rerun_scope</c>, 0031): <c>full</c> or <c>failed_subset</c>, null for
/// an original run. The column-backed form of the rerun label.
/// </param>
public sealed record RunSummary(
    Guid RunId,
    string State,
    DateTimeOffset CreatedAt,
    RunRef Ref,
    bool UnverifiedRef,
    string? Completeness,
    Guid? ParentRunId,
    string? RerunScope);

/// <summary>Cursor page of <c>GET /v1/projects/{p}/runs</c> (05.2.2 §5, AC6; the 00 §9.1 envelope).</summary>
/// <param name="Items">Runs in <c>(created_at, id)</c> ascending order.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record RunPage(IReadOnlyList<RunSummary> Items, string? NextCursor);
