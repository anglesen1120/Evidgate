using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.Modules.Ingestion.Contracts;
using Mediator;

namespace Evidgate.Modules.Ingestion.Application.Commands;

/// <summary>
/// Skeleton command of the sprint-1 slice: register a manifest for a project.
/// Replaced by the real manifest upload of 04 (permission <c>run:create</c>, as
/// <c>POST /v1/projects/{p}/manifests</c> in contracts/openapi.yaml).
/// </summary>
internal sealed record IngestManifest(string ProjectRef, string SourceRef, string Content)
    : ICommand<ManifestResult>;

/// <summary>Identity and hash of the stored manifest.</summary>
internal sealed record ManifestResult(string ManifestId, string ManifestHash);

/// <summary>Handler of <see cref="IngestManifest"/>: delegates to the module's own service interface.</summary>
[Permission("run:create")]
internal sealed class IngestManifestHandler(IIngestion ingestion) : ICommandHandler<IngestManifest, ManifestResult>
{
    /// <inheritdoc />
    public async ValueTask<ManifestResult> Handle(IngestManifest command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Ingestion, nameof(IngestManifest));

        var manifest = await ingestion.StoreManifestAsync(
            new ManifestSubmission(command.ProjectRef, command.SourceRef, command.Content),
            cancellationToken);

        return new ManifestResult(manifest.ManifestId, manifest.Hash);
    }
}
