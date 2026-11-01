using System.Globalization;
using System.Text.Json;

namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The frozen-evidence event's stable names and payload: one home, so a consumer in another module reads exactly what the
/// finisher wrote — one row per inserted revision, in that revision's transaction, keyed by the revision id, so the row and
/// its event commit or roll back together. The payload carries ids only; a consumer re-reads by its own <c>org_id</c>.
/// </summary>
public static class EvidenceRevisionFrozenContract
{
    /// <summary>The outbox task type of a frozen revision: <c>orchestrator.evidence.revision_frozen</c>.</summary>
    public const string OutboxTaskType = "orchestrator.evidence.revision_frozen";

    /// <summary>The aggregate type every row of this event names: the revision (<c>evidence_revision</c>).</summary>
    public const string AggregateType = "evidence_revision";

    /// <summary>snake_case, the wire convention every payload of this platform writes.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The outbox idempotency key of one frozen revision: <c>orchestrator:evidence-revision:{revision}:frozen</c>. The id
    /// is allocated before the insert, so the key is a function of the row it announces and a reused key with another
    /// payload stays a unique-constraint conflict, never a silent overwrite.
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
