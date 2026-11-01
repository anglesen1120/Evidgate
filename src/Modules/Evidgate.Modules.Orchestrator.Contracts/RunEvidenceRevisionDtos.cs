namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// One row of <c>GET /v1/runs/{run_id}/evidence-revisions</c>, read from <c>evidence_revision</c> (<c>0049</c>). The
/// table is append-only — late evidence appends the next <c>revision_no</c> instead of rewriting a row — so every item
/// is the verdict that revision was frozen with, and <c>missing</c> is reported verbatim rather than summarised.
/// </summary>
/// <param name="RevisionNo"><c>evidence_revision.revision_no</c>, 1-based and gap-free per run; the highest is newest.</param>
/// <param name="CreatedAt">When that revision was frozen.</param>
/// <param name="Completeness"><c>complete</c> or <c>incomplete</c> (00 §8.5); an incomplete revision is never a PASS.</param>
/// <param name="Missing">
/// The row's own 00 §8.5 reason codes, verbatim: empty exactly when <paramref name="Completeness"/> is <c>complete</c>.
/// </param>
/// <param name="EvaluationId">
/// The <c>evaluation.id</c> of this revision's <c>live</c> verdict, or null while none is stored. A <c>shadow</c>
/// evaluation is a second opinion about the same evidence and is never named here.
/// </param>
public sealed record EvidenceRevision(
    int RevisionNo,
    DateTimeOffset CreatedAt,
    string Completeness,
    IReadOnlyList<string> Missing,
    Guid? EvaluationId);

/// <summary>
/// Cursor page of <c>GET /v1/runs/{run_id}/evidence-revisions</c> (the 00 §9.1 envelope), keyset-ordered on
/// <c>revision_no</c> descending.
/// </summary>
/// <param name="Items">The run's revisions, newest first.</param>
/// <param name="NextCursor">Opaque cursor, or null on the last page.</param>
public sealed record EvidenceRevisionPage(IReadOnlyList<EvidenceRevision> Items, string? NextCursor);
