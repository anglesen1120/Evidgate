using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.Pipeline.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Evidence.Endpoints;
using Mediator;

namespace Evidgate.Modules.Evidence.Application.Artifacts;

/// <summary>
/// One artifact's descriptive metadata. The base permission is <c>artifact:read</c> and is enforced by the
/// pipeline; the escalation for a sensitive row is data-dependent, so the handler asks the same permission
/// evaluator for <c>artifact:read_sensitive</c> and the shared read model masks the content-bearing members when
/// the answer is no. A gated read is a 200 with <c>content_gated: true</c>, never a 403: only content is gated.
/// </summary>
/// <param name="ArtifactId">The artifact the route addressed.</param>
internal sealed record GetArtifactMetadata(Guid ArtifactId) : IQuery<Result<ArtifactMetadataView>>;

/// <summary>Reads one artifact's metadata under the tenant scope (one statement, no N+1 over the policy).</summary>
/// <remarks>
/// The route names no project, so the artifact's own project decides the scope: a member's roles are org-level, so
/// any project of the org is readable; a request narrowed to another project is refused, and <c>artifact:read</c> is
/// outside the service ceiling, so no project-bound credential reaches the handler today.
/// RLS hides another tenant's row and the project check hides another project's row of the same org, so both are the
/// one 404 as an unknown id.
/// </remarks>
[Permission(Permissions.Artifact.Read)]
internal sealed class GetArtifactMetadataHandler(
    ITenantDb db,
    IRequestTenantContextAccessor tenant,
    IRequestPrincipalAccessor principals,
    IPermissionEvaluator permissions)
    : IQueryHandler<GetArtifactMetadata, Result<ArtifactMetadataView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<ArtifactMetadataView>> Handle(
        GetArtifactMetadata query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Evidence, nameof(GetArtifactMetadata));

        var context = tenant.Require();

        // The scope is the pipeline's: a query handler runs inside the tenant transaction the C2 stages opened, so
        // opening a second scope here would be refused as nesting. The permission evaluator reads the membership
        // rows through that same scope, so it is asked before the row is read, never in one of its own.
        var canReadSensitive = principals.Current is { } principal
            && (await permissions
                .EvaluateAsync(Permissions.Artifact.ReadSensitive, principal, context, cancellationToken)
                .ConfigureAwait(false)).IsGranted;

        var row = await ArtifactReadModel
            .ReadAsync(db, query.ArtifactId, canReadSensitive, cancellationToken)
            .ConfigureAwait(false);

        return row is null
            || EvidenceProjectScope.Project(row.ProjectId, principals.Current?.ProjectScope, context.ProjectId) is not null
            ? Result.Failure<ArtifactMetadataView>(EvidenceErrors.ArtifactNotFound)
            : Result.Success(ArtifactMetadataView.From(row));
    }
}
