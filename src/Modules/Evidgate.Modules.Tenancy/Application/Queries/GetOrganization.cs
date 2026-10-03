using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Tenancy.Contracts;
using Evidgate.Modules.Tenancy.Domain;
using Evidgate.Modules.Tenancy.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Tenancy.Application.Queries;

/// <summary>One organization (<c>GET /v1/orgs/{o}</c>, 02.1.1 §5).</summary>
/// <param name="OrgId">The org from the route; another org's id is 404.</param>
internal sealed record GetOrganizationQuery(Guid OrgId) : IQuery<Result<OrganizationDto>>;

/// <summary>
/// Handler of <see cref="GetOrganizationQuery"/>: one statement in the read-only tenant
/// transaction (C5). A suspended org still reads (02.1.1 §6); a deleted one is 404.
/// </summary>
[Permission(Permissions.Organization.Read)]
internal sealed class GetOrganizationQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<GetOrganizationQuery, Result<OrganizationDto>>
{
    /// <inheritdoc />
    public async ValueTask<Result<OrganizationDto>> Handle(
        GetOrganizationQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Tenancy, nameof(GetOrganizationQuery));

        if (query.OrgId != scopes.Require().OrgId)
        {
            return Result.Failure<OrganizationDto>(TenancyErrors.OrgNotFound);
        }

        var organization = await TenancyReader.OrganizationAsync(db, query.OrgId, cancellationToken)
            .ConfigureAwait(false);

        return organization is null || organization.Status == TenancyStatus.Deleted
            ? Result.Failure<OrganizationDto>(TenancyErrors.OrgNotFound)
            : Result.Success(organization);
    }
}
