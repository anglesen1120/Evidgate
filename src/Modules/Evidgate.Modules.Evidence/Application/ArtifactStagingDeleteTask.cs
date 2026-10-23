using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tasks;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Domain;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// Payload of the <c>artifact_staging_delete</c> task (06.1.2 §5, T86), as the completion's outbox row writes it.
/// </summary>
/// <param name="ArtifactId">The artifact whose staging object is deleted.</param>
/// <param name="StagingKey">The staging key the producer derived; the handler re-derives it and refuses anything else.</param>
internal sealed record ArtifactStagingDeletePayload(Guid ArtifactId, string StagingKey);

/// <summary>
/// Deletes the <c>pending/</c> staging object of one artifact after its completion committed (06.1.2 §5, T86): once
/// the verified bytes sit at the final key the staging object is dead weight, and the 24 h bucket lifecycle would
/// only collect it a day later. Tenant-bound (the interface default): the key is re-derived from the scope's org
/// and the payload's artifact id, so a payload naming any other key — a crafted row, a replayed one from another
/// tenant — is refused and can never delete an object outside its own tenant. Idempotent on a missing key, because
/// the lifecycle may already have collected it, and the final object is never touched. The task queue's own row is
/// the record of what ran, and a refusal reaches the dead-letter list through the exception's first line, which
/// carries no key (S3).
/// </summary>
internal sealed class ArtifactStagingDeleteTaskHandler(IObjectStorage storage)
    : ITaskHandler<ArtifactStagingDeletePayload>
{
    /// <inheritdoc />
    public string TaskType => EvidenceTaskTypes.StagingDelete;

    /// <inheritdoc />
    public async ValueTask HandleAsync(
        ArtifactStagingDeletePayload payload,
        ITenantScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (scope is null)
        {
            throw new InvalidOperationException(
                "artifact_staging_delete is tenant-bound: a claimed row without an org_id is dead-lettered as missing_tenant, never run unscoped");
        }

        var stagingKey = ArtifactKeyMinter.StagingKey(scope.OrgId, payload.ArtifactId);

        if (!string.Equals(stagingKey, payload.StagingKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "the artifact_staging_delete payload does not name the staging key of its own tenant");
        }

        await storage.DeleteAsync(stagingKey, cancellationToken).ConfigureAwait(false);
    }
}
