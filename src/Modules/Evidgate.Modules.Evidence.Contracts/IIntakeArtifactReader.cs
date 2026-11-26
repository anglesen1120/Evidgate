namespace Evidgate.Modules.Evidence.Contracts;

/// <summary>
/// What a requirement intake may know about one artifact it is built from: the facts of a <c>ready</c>, project-visible
/// artifact and the effective capture policy of its kind. The object key is the only handle to the stored bytes.
/// </summary>
/// <param name="Kind">The artifact kind.</param>
/// <param name="MediaType">The stored media type.</param>
/// <param name="DeclaredBytes">The verified length of the stored object.</param>
/// <param name="ObjectKey">The storage key of the final object.</param>
/// <param name="MediaAllowed">True when the kind's effective policy is enabled and lists the media type.</param>
/// <param name="MaxBytes">The effective byte cap of the kind; 0 when the kind has no effective policy.</param>
public sealed record IntakeArtifact(string Kind, string MediaType, long DeclaredBytes, string ObjectKey, bool MediaAllowed, long MaxBytes);

/// <summary>
/// The one cross-module read a requirement intake makes of the evidence store: the metadata of a ready artifact in the
/// caller's tenant scope, and a bounded fetch of its bytes outside any scope.
/// </summary>
public interface IIntakeArtifactReader
{
    /// <summary>
    /// Reads the artifact inside the caller's open tenant scope. A policy that forbids the artifact still returns it,
    /// with <see cref="IntakeArtifact.MediaAllowed"/> false, so the caller decides the refusal.
    /// </summary>
    /// <param name="orgId">The caller's tenant.</param>
    /// <param name="projectId">The project the artifact must belong to.</param>
    /// <param name="artifactId">The artifact.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The artifact, or null when it is absent, not ready, or not in that project (one answer, no oracle).</returns>
    Task<IntakeArtifact?> ReadEligibleAsync(Guid orgId, Guid projectId, Guid artifactId, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the object once, bounded by <paramref name="maxBytes"/>. Opens no transaction and needs no scope.
    /// </summary>
    /// <param name="artifact">The artifact the metadata read returned.</param>
    /// <param name="maxBytes">The most bytes accepted.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes, or null when the object is absent or the store refused the read.</returns>
    /// <exception cref="InvalidOperationException">The object is longer than <paramref name="maxBytes"/>.</exception>
    Task<byte[]?> FetchAsync(IntakeArtifact artifact, long maxBytes, CancellationToken cancellationToken);
}
