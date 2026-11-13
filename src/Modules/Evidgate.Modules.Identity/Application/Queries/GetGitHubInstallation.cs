using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Application.Commands;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary>
/// <c>GET /v1/github/installations</c>: the caller's org installation. A suspended or deleted installation answers
/// 409 <c>installation_unavailable</c>, because a binding that cannot mint tokens must not read as healthy.
/// </summary>
internal sealed record GetGitHubInstallationQuery : IQuery<Result<GitHubInstallationDto>>;

/// <summary>Handler of <see cref="GetGitHubInstallationQuery"/>; RLS scopes the row to the caller's tenant.</summary>
[Permission(Permissions.Project.Read)]
internal sealed class GetGitHubInstallationQueryHandler(
    ITenantScopeAccessor scopes,
    IGitHubInstallationStore installations)
    : IQueryHandler<GetGitHubInstallationQuery, Result<GitHubInstallationDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<GitHubInstallationDto>> Handle(
        GetGitHubInstallationQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(GetGitHubInstallationQuery));

        var row = await installations
            .FindCurrentAsync(scopes.Require().OrgId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return Result.Failure<GitHubInstallationDto>(Error.NotFound("this org has no github installation"));
        }

        if (!string.Equals(row.State, "active", StringComparison.Ordinal))
        {
            return Result.Failure<GitHubInstallationDto>(GitHubInstallationErrors.Unavailable);
        }

        return Result.Success(CompleteGitHubInstallCommandHandler.ToDto(row));
    }
}
