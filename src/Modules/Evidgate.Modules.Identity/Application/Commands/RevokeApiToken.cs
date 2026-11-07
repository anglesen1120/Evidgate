using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
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
/// <c>DELETE /v1/api-tokens/{t}</c>: revocation is immediate and final. A token another tenant owns is not
/// visible, so it answers 404 exactly like an unknown id; revoking an already revoked token is a no-op that
/// writes no second audit row.
/// </summary>
/// <param name="TokenId">The token from the route.</param>
internal sealed record RevokeApiTokenCommand(Guid TokenId) : ICommand<Result>;

/// <summary>Handler of <see cref="RevokeApiTokenCommand"/>; the tenant factory's RLS scope decides visibility.</summary>
[Permission(Permissions.ApiToken.Write)]
[AuditedAction("api_token.revoke")]
internal sealed class RevokeApiTokenCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<RevokeApiTokenCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(RevokeApiTokenCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(RevokeApiTokenCommand));

        var now = clock.GetUtcNow();
        await using var context = contexts.Create();
        var token = await context.ApiTokens
            .SingleOrDefaultAsync(row => row.Id == command.TokenId, cancellationToken)
            .ConfigureAwait(false);

        if (token is null)
        {
            return Result.Failure(Error.NotFound("no api token with that id is visible to this tenant"));
        }

        if (token.RevokedAt is not null)
        {
            // Nothing changed, so there is nothing to audit; the first revocation already recorded it.
            audit.Suppress();

            return Result.Success(StatusCodes.Status204NoContent);
        }

        token.Revoke(now);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(Error.PreconditionFailed("the token changed; retry"));
        }

        audit.Describe(new AuditObject("api_token", token.Id.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
