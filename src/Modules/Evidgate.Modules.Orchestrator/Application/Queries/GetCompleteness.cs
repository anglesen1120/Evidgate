using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Orchestrator.Domain;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Queries;

/// <summary>
/// Skeleton query of the sprint-1 slice: the evidence completeness of a run.
/// Read-only by construction (00 §5.5 C1); it computes no state and writes
/// nothing.
/// </summary>
internal sealed record GetCompleteness(string RunId) : IQuery<CompletenessResult>;

/// <summary>Completeness of a run (00 §8.5 value set).</summary>
internal sealed record CompletenessResult(string RunId, string Completeness)
{
    /// <summary>Fixed stub of the slice: nothing computes completeness until 05/06.</summary>
    internal const string Stub = "incomplete";
}

/// <summary>Handler of <see cref="GetCompleteness"/>: reports the run's stub completeness.</summary>
[Permission("run:read")]
internal sealed class GetCompletenessHandler(IRunRegistry runs) : IQueryHandler<GetCompleteness, CompletenessResult>
{
    /// <inheritdoc />
    public ValueTask<CompletenessResult> Handle(GetCompleteness query, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(GetCompleteness));

        var run = runs.Find(query.RunId)
            ?? throw new InvalidOperationException($"Run {query.RunId} is unknown; create it before reading it.");

        return ValueTask.FromResult(new CompletenessResult(run.RunId, CompletenessResult.Stub));
    }
}
