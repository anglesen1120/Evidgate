using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// <c>POST /v1/support-grants/{g}/revoke</c> (02.8.1 §5): any holder of <c>support:approve</c> in the grant's org
/// may end a requested or approved grant, because the person who approved it may be unavailable during the very
/// incident the access exists for. Revocation is final: a second call answers 409 and writes no second audit row.
/// </summary>
/// <param name="GrantId">The grant from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1): the openapi operation declares it, so a
/// replay answers the stored 200 rather than a second 409 or a second audit row.</param>
internal sealed record RevokeSupportGrantCommand(Guid GrantId, string IdempotencyKey) : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="RevokeSupportGrantCommand"/>; the tenant factory's RLS scope decides visibility.</summary>
[Permission(Permissions.Support.Approve)]
[AuditedAction("support_grant.revoke")]
internal sealed class RevokeSupportGrantCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<RevokeSupportGrantCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(RevokeSupportGrantCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(RevokeSupportGrantCommand));

        var now = clock.GetUtcNow();
        await using var context = contexts.Create();
        var grant = await context.SupportGrants
            .SingleOrDefaultAsync(row => row.Id == command.GrantId, cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return Result.Failure(SupportGrantErrors.NotFound);
        }

        if (grant.RevokedAt is not null)
        {
            // Nothing changed, so there is nothing to audit; the first revocation already recorded it.
            audit.Suppress();

            return Result.Failure(SupportGrantErrors.GrantAlreadyRevoked);
        }

        grant.Revoke(now);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Error.PreconditionFailed("the support grant changed; retry"));
        }
        catch (DbUpdateException exception) when (PgErrors.IsCheckViolation(exception, "is revoked; a revoked grant accepts no change"))
        {
            return Result.Failure(SupportGrantErrors.GrantAlreadyRevoked);
        }

        audit.Describe(new AuditObject("support_grant", grant.Id.ToString("D")));

        return Result.Success(StatusCodes.Status200OK);
    }
}
