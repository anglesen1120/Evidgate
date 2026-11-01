using System.Globalization;
using System.Text.Json;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The stable names of the frozen-evidence event: the outbox task type a finished reconcile attempt enqueues once per
/// inserted <c>evidence_revision</c>, its aggregate, its idempotency key and the payload it carries (00 §5.5 C7) — one
/// home so a consumer in another module reads exactly what the finisher wrote instead of a second copy that could drift
/// (00 §5.5 C6).
/// <para>
/// One row per inserted revision, never per attempt: an attempt whose evidence a recorded revision already counts appends
/// nothing and enqueues nothing, and the key names the revision id, so two rows can never describe the same revision.
/// The row is written in the attempt's own transaction, next to the insert and the pointer move it announces, so the
/// revision and its event commit or roll back together (00 §5.7 P5).
/// </para>
/// <para>
/// The payload carries ids and the allocated revision number, nothing else: a consumer re-reads what it needs from the
/// revision it names, so a value copied here could only drift from the row. Those re-reads are tenant-scoped by the
/// consumer's own <c>org_id</c>, never by a bare id: <c>run.project_id</c> has no <c>(org_id, project_id)</c> foreign key
/// (0031), so <c>project_id</c> is trustworthy only because the run-create path admits a project the tenant can see and
/// that is not deleted — a consumer that looked the id up outside its org scope would be the one crossing tenants.
/// </para>
/// </summary>
public static class EvidenceRevisionFrozenContract
{
    /// <summary>The outbox task type of a frozen revision: <c>orchestrator.evidence.revision_frozen</c>.</summary>
    public const string OutboxTaskType = "orchestrator.evidence.revision_frozen";

    /// <summary>The aggregate type every row of this event names: the revision (<c>evidence_revision</c>).</summary>
    public const string AggregateType = "evidence_revision";

    /// <summary>snake_case, the wire convention every payload of this platform writes (00 §9.4).</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The outbox idempotency key of one frozen revision:
    /// <c>orchestrator:evidence-revision:{revision}:frozen</c>.
    /// <para>
    /// The revision id is allocated before the insert, so the key is a function of the row it announces: a retried
    /// attempt that inserts no revision writes no key, and two revisions of one run carry two keys. A reused key with a
    /// different payload stays a conflict at <c>outbox_org_idem_uq</c> (D6), never a silent overwrite.
    /// </para>
    /// </summary>
    /// <param name="evidenceRevisionId">The <c>evidence_revision.id</c> the event announces.</param>
    public static string IdempotencyKey(Guid evidenceRevisionId) =>
        string.Create(CultureInfo.InvariantCulture, $"orchestrator:evidence-revision:{evidenceRevisionId:D}:frozen");

    /// <summary>The payload of one frozen revision as JSON: the tenant, the project, the run and the revision it names.</summary>
    /// <param name="orgId">The tenant the revision belongs to, from the attempt's scope.</param>
    /// <param name="projectId">The project of the run, read from the run row.</param>
    /// <param name="runId">The run whose evidence was frozen.</param>
    /// <param name="evidenceRevisionId">The inserted <c>evidence_revision.id</c>.</param>
    /// <param name="evidenceRevisionNo">The <c>revision_no</c> the insert allocated (1 for a run's first revision).</param>
    public static string PayloadJson(
        Guid orgId,
        Guid projectId,
        Guid runId,
        Guid evidenceRevisionId,
        int evidenceRevisionNo) =>
        JsonSerializer.Serialize(
            new EvidenceRevisionFrozenPayload(orgId, projectId, runId, evidenceRevisionId, evidenceRevisionNo),
            PayloadOptions);
}

/// <summary>
/// The payload members of one <see cref="EvidenceRevisionFrozenContract.OutboxTaskType"/> row, as written (snake_case on
/// the wire).
/// </summary>
/// <param name="OrgId"><c>org_id</c>: the tenant of the revision, copied from the attempt's scope.</param>
/// <param name="ProjectId"><c>project_id</c>: the project of the run whose evidence was frozen.</param>
/// <param name="RunId"><c>run_id</c>: the run whose evidence was frozen.</param>
/// <param name="EvidenceRevisionId"><c>evidence_revision_id</c>: the inserted revision.</param>
/// <param name="EvidenceRevisionNo"><c>evidence_revision_no</c>: its <c>revision_no</c>, 1 for the run's first revision.</param>
public sealed record EvidenceRevisionFrozenPayload(
    Guid OrgId,
    Guid ProjectId,
    Guid RunId,
    Guid EvidenceRevisionId,
    int EvidenceRevisionNo);
