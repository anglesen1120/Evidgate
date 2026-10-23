using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Catalog.Contracts;
using Evidgate.Modules.Ingestion.Contracts;
using Evidgate.Modules.Orchestrator.Domain;
using Mediator;

namespace Evidgate.Modules.Orchestrator.Application.Commands;

/// <summary>
/// Skeleton command of the sprint-1 slice: create a run for a registered
/// manifest. Replaced by the real run creation of 04 (permission
/// <c>run:create</c>, as <c>POST /v1/projects/{p}/runs</c>).
/// </summary>
internal sealed record CreateRun(string ProjectRef, string ManifestId) : ICommand<RunResult>;

/// <summary>Identity of the created run.</summary>
internal sealed record RunResult(string RunId, string ProjectRef, string ManifestId);

/// <summary>
/// Handler of <see cref="CreateRun"/>. It reaches the other modules only through
/// their <c>.Contracts</c> interfaces (00 §5.5, AC5): Ingestion for the manifest
/// and Catalog for the suite revisions the run may execute.
/// </summary>
[Permission("run:create")]
internal sealed class CreateRunHandler(IIngestion ingestion, ICatalog catalog, IRunRegistry runs)
    : ICommandHandler<CreateRun, RunResult>
{
    /// <inheritdoc />
    public async ValueTask<RunResult> Handle(CreateRun command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Orchestrator, nameof(CreateRun));

        var manifest = await ingestion.GetManifestAsync(command.ManifestId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Manifest {command.ManifestId} is not registered; the slice endpoint ingests it first.");

        var suites = await catalog.ListSuiteRevisionsAsync(command.ProjectRef, cancellationToken);

        var run = Run.Create(command.ProjectRef, manifest.ManifestId, suites.Select(suite => suite.Key));
        runs.Add(run);

        activity?.SetTag("evidgate.run_id", run.RunId);

        return new RunResult(run.RunId, run.ProjectRef, run.ManifestId);
    }
}
