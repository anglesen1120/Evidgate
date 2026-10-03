using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;

namespace Evidgate.Modules.Identity.Application;

/// <summary>Maps CI entities to their API views. Secrets and hashes never reach a view.</summary>
internal static class CiViews
{
    public static CiBindingView ToView(this CiBinding binding) => new(
        binding.Id,
        binding.ProjectId,
        binding.Provider,
        binding.RepositoryId,
        binding.RepositoryFullName,
        binding.AllowedRefPatterns,
        binding.AllowedWorkflowRefs,
        binding.Status,
        binding.ActivatedAt,
        binding.CreatedAt,
        binding.Version);

    public static IngestTokenView ToView(this IngestToken token, DateTimeOffset now) => new(
        token.Id,
        token.ProjectId,
        token.Name,
        token.Prefix,
        token.StatusAt(now),
        token.Scopes,
        token.ExpiresAt,
        token.OverlapEndsAt,
        token.LastUsedAt,
        token.RotatedToId,
        token.CreatedAt);
}
