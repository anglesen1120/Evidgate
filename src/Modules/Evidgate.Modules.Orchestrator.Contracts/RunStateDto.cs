namespace Evidgate.Modules.Orchestrator.Contracts;

/// <summary>
/// The body of <c>GET /v1/runs/{r}</c> (05.3.1 §5, AC7, 00 §8.1): where one run stands in the state machine, which
/// evidence revision its current verdict was frozen from, its optimistic-concurrency token and its purpose. The wire
/// names are <c>run_id</c>, <c>state</c>, <c>current_evidence_revision_id</c>, <c>version</c> and <c>purpose</c> (the
/// host's <c>SnakeCaseLower</c> policy). Another tenant's — or, for a project-scoped credential, another project's —
/// run is <c>404 not_found</c>, the same code and body as an unknown id (00 §5.4).
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="State">Its state, one of the 00 §8.1 vocabulary.</param>
/// <param name="CurrentEvidenceRevisionId">
/// The evidence revision the run's current verdict is computed from; null while no revision has been frozen yet
/// (05.3.2-T5 owns the pointer and the write side, this story exposes the column).
/// </param>
/// <param name="Version">The optimistic-concurrency token (00 §5.5 C4); a transition against a stale value is 412.</param>
/// <param name="Purpose">
/// The run's purpose, <c>regular</c> or <c>replay</c> (T67, 05.2.1-T5). A replay run is 05.3.1's read-model hook for
/// 09's regression-count exclusion; the column is written once by the create handler and never updated.
/// </param>
public sealed record RunStateDto(
    Guid RunId,
    string State,
    Guid? CurrentEvidenceRevisionId,
    int Version,
    string Purpose);
