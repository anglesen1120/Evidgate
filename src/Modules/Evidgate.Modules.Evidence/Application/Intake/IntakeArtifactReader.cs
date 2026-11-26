using Evidgate.BuildingBlocks.ObjectStorage;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.Modules.Evidence.Contracts;
using Evidgate.Modules.Evidence.Domain;

namespace Evidgate.Modules.Evidence.Application.Intake;

/// <summary>
/// <see cref="IIntakeArtifactReader"/> over the ambient tenant scope and the object store. The metadata read is one
/// statement and the policy is the effective one (<see cref="ArtifactKindDefaults.Resolve"/>), so a project without a
/// physical policy row reads exactly as the upload path does and an unknown kind fails closed.
/// </summary>
internal sealed class IntakeArtifactReader(ITenantDb db, IObjectStorage storage) : IIntakeArtifactReader
{
    /// <summary>The largest read <see cref="IObjectStorage.GetAsync"/> accepts.</summary>
    private const long StorageReadLimit = 268_435_456;

    private const string Sql = """
        select a.kind,
               a.media_type as MediaType,
               a.bytes,
               a.object_key as ObjectKey,
               kp.max_bytes as PolicyMaxBytes,
               kp.media_types as PolicyMediaTypes,
               kp.capture_enabled as PolicyCaptureEnabled,
               kp.default_sensitivity as PolicySensitivity
          from public.artifact a
          left join public.artifact_kind_policy kp
            on kp.org_id = a.org_id
           and kp.project_id = a.project_id
           and kp.kind = a.kind
         where a.id = @artifact_id
           and a.project_id = @project_id
           and a.org_id = @org_id
           and a.state = 'ready'
           and a.bytes is not null
        """;

    /// <inheritdoc />
    public async Task<IntakeArtifact?> ReadEligibleAsync(
        Guid orgId,
        Guid projectId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var row = await db
            .QuerySingleOrDefaultAsync<Row>(
                Sql,
                new { artifact_id = artifactId, project_id = projectId, org_id = orgId },
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var policy = ArtifactKindDefaults.Resolve(
            row.Kind,
            row.PolicyMaxBytes is { } maxBytes
                ? new KindPolicyRow(
                    maxBytes,
                    row.PolicyMediaTypes ?? [],
                    row.PolicyCaptureEnabled == true,
                    row.PolicySensitivity ?? ArtifactSensitivity.ForKind(row.Kind))
                : null);

        var allowed = policy is { CaptureEnabled: true }
            && policy.MediaTypes.Contains(row.MediaType, StringComparer.OrdinalIgnoreCase);

        return new IntakeArtifact(row.Kind, row.MediaType, row.Bytes, row.ObjectKey, allowed, policy?.MaxBytes ?? 0);
    }

    /// <inheritdoc />
    public async Task<byte[]?> FetchAsync(IntakeArtifact artifact, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // Clamped into the store's accepted range: a cap of zero or below is a refusal the caller already made, and a
        // cap above the store's own ceiling would otherwise throw an argument error instead of a bounded read.
        try
        {
            return await storage
                .GetAsync(artifact.ObjectKey, Math.Clamp(maxBytes, 1, StorageReadLimit), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectStorageException)
        {
            // A store that refuses the read is "no bytes" to the caller: the intake ends rejected rather than retried
            // against a store that is down, and the artifact itself is left as it is.
            return null;
        }
    }

    private sealed record Row(
        string Kind,
        string MediaType,
        long Bytes,
        string ObjectKey,
        long? PolicyMaxBytes,
        string[]? PolicyMediaTypes,
        bool? PolicyCaptureEnabled,
        string? PolicySensitivity);
}
