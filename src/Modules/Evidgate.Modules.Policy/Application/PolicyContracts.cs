using Evidgate.Modules.Policy.Application.Queries;
using Evidgate.Modules.Policy.Contracts;

namespace Evidgate.Modules.Policy.Application;

/// <summary>
/// The module's in-process surface (00 §5.5, 07.1.1-T4.2): the adapter that exposes
/// <see cref="IPolicyContracts"/> to other modules. Catalog's <c>CatalogContracts</c> is the precedent —
/// a thin, transaction-neutral wrapper that opens no transaction of its own and lets an absent answer stand as
/// "nothing there" (a null read is "no active policy", an empty list is "no required artifact kind",
/// owner decision Q1) instead of surfacing an error.
/// </summary>
internal sealed class PolicyContracts(
    GetActivePolicyQuery query,
    GetRequiredArtifactKindsQuery requiredArtifactKinds,
    GetLiveEvaluationsQuery liveEvaluations,
    GetLatestLiveDecisionsQuery latestLiveDecisions) : IPolicyContracts
{
    /// <summary>Resolves the revision a new run pins, or null when the project has no active policy.</summary>
    /// <param name="projectId">The project the run is created in.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async ValueTask<ActivePolicy?> GetActivePolicyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var result = await query.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? result.Value : null;
    }

    /// <summary>Reads a revision's required artifact kinds, ordinal and de-duplicated; empty when it has none.</summary>
    /// <param name="policyRevisionId">The revision to read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public ValueTask<IReadOnlyList<string>> GetRequiredArtifactKindsAsync(Guid policyRevisionId, CancellationToken cancellationToken) =>
        requiredArtifactKinds.GetAsync(policyRevisionId, cancellationToken);

    /// <summary>Answers which of the named revisions of one run already have a stored live evaluation.</summary>
    /// <param name="runId">The run the revisions belong to.</param>
    /// <param name="evidenceRevisions">The revision numbers to look up.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public ValueTask<IReadOnlyList<LiveEvaluation>> GetLiveEvaluationsAsync(
        Guid runId,
        IReadOnlyList<int> evidenceRevisions,
        CancellationToken cancellationToken) =>
        liveEvaluations.GetAsync(runId, evidenceRevisions, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RunLiveDecision>> GetLatestLiveDecisionsAsync(
        IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken) =>
        latestLiveDecisions.GetAsync(runIds, cancellationToken);
}
