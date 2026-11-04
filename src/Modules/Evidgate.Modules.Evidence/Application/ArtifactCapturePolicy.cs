using Evidgate.Modules.Evidence.Domain;
using Evidgate.Modules.Evidence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Evidence.Application;

/// <summary>
/// What a background read of one artifact needs from its kind's capture policy: the byte cap the read must not
/// exceed, and whether a secret in the object makes the artifact unusable.
/// </summary>
/// <param name="MaxBytes">The effective cap, after the project's row was resolved against the static defaults.</param>
/// <param name="RejectOnSecret">The project's switch: true means a matched secret rejects the artifact.</param>
internal sealed record ArtifactCaptureSettings(long MaxBytes, bool RejectOnSecret);

/// <summary>
/// The one resolution of an artifact's effective capture policy over the tenant write model, shared by the two
/// background readers (<c>artifact_parse</c> and <c>artifact_secret_scan</c>): both must bound their object read by the
/// cap the artifact was admitted under (D-A9), and neither may invent a cap of its own. A kind with no effective
/// policy is an error rather than a guess — such an artifact cannot exist as <c>ready</c>, so a background task that
/// meets one is reading a corrupt row, and it fails closed instead of reading unbounded bytes.
/// </summary>
internal static class ArtifactCapturePolicy
{
    /// <summary>Resolves the effective policy of one artifact's kind in one project.</summary>
    /// <param name="context">This attempt's write model, inside its caller's tenant scope.</param>
    /// <param name="projectId">The project the artifact belongs to.</param>
    /// <param name="kind">The artifact's kind.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The effective cap and the project's rejection switch.</returns>
    /// <exception cref="InvalidOperationException">The kind has neither a policy row nor a default, or its capture switch is off.</exception>
    public static async Task<ArtifactCaptureSettings> ResolveAsync(
        EvidenceDbContext context,
        Guid projectId,
        string kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var row = await context.KindPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(policy => policy.ProjectId == projectId && policy.Kind == kind, cancellationToken)
            .ConfigureAwait(false);

        var resolved = ArtifactKindDefaults.Resolve(
            kind,
            row is null
                ? null
                : new KindPolicyRow(row.MaxBytes, row.MediaTypes, row.CaptureEnabled, row.DefaultSensitivity));

        return resolved is { CaptureEnabled: true } policy
            ? new ArtifactCaptureSettings(policy.MaxBytes, row?.RejectOnSecret ?? false)
            : throw new InvalidOperationException(
                "the artifact's kind has no effective capture policy, so there is no byte cap for a background read");
    }
}
