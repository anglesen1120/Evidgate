using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Mediator;

namespace Evidgate.Modules.Policy.Application.Queries;

/// <summary>
/// Skeleton query of the sprint-1 slice: the release decision of a run. Read-only
/// by construction (00 §5.5 C1) and a stub until 07 evaluates policies.
/// </summary>
internal sealed record GetDecision(string RunId) : IQuery<DecisionResult>;

/// <summary>Decision of a run (00 §8.5 value set: PASS/FAIL/INCONCLUSIVE).</summary>
internal sealed record DecisionResult(string RunId, string Decision)
{
    /// <summary>Fixed stub of the slice: no policy is evaluated until 07 (00 §8.6).</summary>
    internal const string Stub = "pending";
}

/// <summary>Handler of <see cref="GetDecision"/>: reports the run's stub decision.</summary>
[Permission("decision:read")]
internal sealed class GetDecisionHandler : IQueryHandler<GetDecision, DecisionResult>
{
    /// <inheritdoc />
    public ValueTask<DecisionResult> Handle(GetDecision query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Policy, nameof(GetDecision));

        return ValueTask.FromResult(new DecisionResult(query.RunId, DecisionResult.Stub));
    }
}
