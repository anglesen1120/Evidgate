using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;

namespace Evidgate.Modules.Identity.Application.Queries;

/// <summary>
/// One membership of one org (<c>GET /v1/orgs/{o}/memberships/{m}</c>, 02.1.2 §5).
/// The tenant filter is row level security, which is what makes another org's
/// membership id indistinguishable from a missing one — the RLS half of AC7.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership from the route.</param>
internal sealed record GetMembershipQuery(Guid OrgId, Guid MembershipId)
    : IQuery<Result<MembershipView>>;

/// <summary>Handler of <see cref="GetMembershipQuery"/>.</summary>
[Permission(Permissions.Membership.Read)]
internal sealed class GetMembershipQueryHandler(ITenantDb db, ITenantScopeAccessor scopes)
    : IQueryHandler<GetMembershipQuery, Result<MembershipView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MembershipView>> Handle(
        GetMembershipQuery query,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(GetMembershipQuery));

        var orgId = scopes.Require().OrgId;

        if (query.OrgId != orgId)
        {
            return Result.Failure<MembershipView>(Error.NotFound(OrgMismatch.Message));
        }

        var view = await MembershipReader.ReadAsync(db, orgId, query.MembershipId, cancellationToken)
            .ConfigureAwait(false);

        return view is null
            ? Result.Failure<MembershipView>(Error.NotFound("no membership with that id is visible to this tenant"))
            : Result.Success(view);
    }
}
