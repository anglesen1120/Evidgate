using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Remove a member (<c>DELETE /v1/orgs/{o}/memberships/{m}</c>, 02.1.2 §5): the
/// terminal <c>active → removed</c> transition of 02.1.2 §6. The row is kept — it is
/// the audit history of the membership (02.1.2 §8 "Retention/deletion impact") — so
/// the route never hard-deletes. Idempotent under the client's
/// <c>Idempotency-Key</c>; a repeat on an already removed membership is 204 again.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record RemoveMemberCommand(Guid OrgId, Guid MembershipId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="RemoveMemberCommand"/>. Order of refusals: route org vs
/// request tenant (404), unknown membership (404, RLS decides — another org's id is
/// indistinguishable from a missing one, AC7), illegal source state (409
/// <c>invalid_transition</c>: <c>pending → removed</c> is not one of 02.1.2 §6's
/// edges, a pending invite is cancelled through 02.1.2-T2's route), last admin (409
/// <c>last_admin</c>, AC4), then the write.
/// </summary>
/// <remarks>
/// 02.1.2-T3.2 adds the <c>ISessionRevocation.RevokeAllForUserAsync</c> call inside
/// this same transaction, which is what makes removal end live sessions immediately
/// (AC3). The interface is defined by 02.1.2-T1.3; the <c>session</c> table behind it
/// does not exist yet (02.3.2-T1), so the call is recorded as deferred in the feature
/// plan §3 rather than stubbed.
/// </remarks>
[AuditedAction("membership.remove")]
[Permission(Permissions.Membership.Write)]
internal sealed class RemoveMemberCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IAuditTrail audit)
    : ICommandHandler<RemoveMemberCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(
        RemoveMemberCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(RemoveMemberCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure(Error.NotFound(OrgMismatch.Message));
        }

        await using var context = contexts.Create();

        var membership = await context.Memberships
            .SingleOrDefaultAsync(row => row.Id == command.MembershipId, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return Result.Failure(Error.NotFound("no membership with that id is visible to this tenant"));
        }

        if (string.Equals(membership.Status, MembershipStatus.Removed, StringComparison.Ordinal))
        {
            // Already removed: nothing changed, so no audit row.
            audit.Suppress();

            return Result.Success(StatusCodes.Status204NoContent);
        }

        if (!membership.IsActive)
        {
            return Result.Failure(Error.Conflict(
                ApiErrorCode.InvalidTransition,
                $"a {membership.Status} membership cannot be removed; a pending invite is cancelled through its own route"));
        }

        if (await LastAdminGuard.WouldOrphanOrgAsync(db, orgId, membership.Id, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure(Error.Conflict(
                ApiErrorCode.LastAdmin,
                "the organization must keep at least one active tenant_admin (AC4)"));
        }

        membership.Remove();

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The `version` token (00 §5.5 C4) says the row moved between the read
            // above and this UPDATE: a concurrent writer took the same transition (two
            // admins removing/disable-clicking the same member, or a retry carrying a
            // new Idempotency-Key). Answer what the serial replay would have answered
            // for the row's current state instead of letting an expected conflict surface
            // as a 500 — an already removed membership is the route's idempotent 204.
            var latest = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
                .ConfigureAwait(false);

            return latest switch
            {
                null => Result.Failure(Error.NotFound("no membership with that id is visible to this tenant")),
                { Status: MembershipStatus.Removed } => Result.Success(StatusCodes.Status204NoContent),
                { } current => Result.Failure(Error.Conflict(
                    ApiErrorCode.InvalidTransition,
                    $"a {current.Status} membership cannot be removed")),
            };
        }

        audit.Describe(new AuditObject("membership", membership.Id.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
