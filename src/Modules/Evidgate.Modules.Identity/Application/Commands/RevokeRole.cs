using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Revoke one role from one membership (<c>DELETE …/{m}/roles/{r}</c>, 02.1.2 §5).
/// Revoking the <c>tenant_admin</c> role of the org's last active admin is 409
/// <c>last_admin</c> (AC4): the same guard as removal, because demoting the last
/// admin locks the tenant out exactly as removing them would. AC5: the next request
/// evaluates the reduced role set — nothing is cached.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership from the route.</param>
/// <param name="RoleId">The role to revoke; the pair must currently be granted.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record RevokeRoleCommand(Guid OrgId, Guid MembershipId, Guid RoleId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="RevokeRoleCommand"/> (C2 stage 2).</summary>
internal sealed class RevokeRoleCommandValidator : AbstractValidator<RevokeRoleCommand>
{
    /// <summary>Builds the rules.</summary>
    public RevokeRoleCommandValidator() =>
        RuleFor(command => command.RoleId)
            .Must(roleId => roleId != Guid.Empty)
            .WithErrorCode("required");
}

/// <summary>
/// Handler of <see cref="RevokeRoleCommand"/>. A membership the tenant cannot see is
/// 404, and so is a pair that was never granted (00 §9.2: the addressed resource does
/// not exist) — the route is not an upsert. A target whose administrative rank is above
/// the caller's is 403 <c>forbidden</c>. Only an <b>active</b> membership's
/// <c>tenant_admin</c> grant can trip the last-admin guard: a pending or removed row
/// grants nothing, so revoking from it is harmless.
/// </summary>
[AuditedAction("membership.role_revoke")]
[Permission(Permissions.Membership.Write)]
internal sealed class RevokeRoleCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IAuditTrail audit,
    ISessionPrivilegeChange privilegeChanges)
    : ICommandHandler<RevokeRoleCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(
        RevokeRoleCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(RevokeRoleCommand));

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

        // Before the pair's 404 on purpose: after it, a 404-versus-403 difference would tell the
        // caller whether a higher-ranked member holds the role. Rank counts active memberships only,
        // so a removed or pending target ranks zero here.
        if (!await MemberRankGuard.ActorOutranksOrEqualsAsync(
                db, orgId, principals.Current, membership.UserId, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result.Failure(MemberRankGuard.Refused);
        }

        var grant = await context.MembershipRoles
            .SingleOrDefaultAsync(
                row => row.MembershipId == membership.Id && row.RoleId == command.RoleId,
                cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return Result.Failure(Error.NotFound("that membership does not hold that role"));
        }

        if (membership.IsActive)
        {
            // The role's code is what the guard keys on; a role the tenant cannot see
            // cannot be the built-in admin role, so an unreadable code skips the guard
            // rather than inventing a refusal.
            var roleCode = await context.Roles
                .Where(role => role.Id == command.RoleId)
                .Select(role => role.Code)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(roleCode, LastAdminGuard.TenantAdminRoleCode, StringComparison.Ordinal)
                && await LastAdminGuard.WouldOrphanOrgAsync(db, orgId, membership.Id, cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict(
                    ApiErrorCode.LastAdmin,
                    "the organization must keep at least one active tenant_admin (AC4)"));
            }
        }

        // The guard's row lock is where a concurrent demotion of the actor can land, so the
        // rank is compared again after it. This narrows the window; it is not atomic.
        if (!await MemberRankGuard.ActorOutranksOrEqualsAsync(
                db, orgId, principals.Current, membership.UserId, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result.Failure(MemberRankGuard.Refused);
        }

        context.MembershipRoles.Remove(grant);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The grant row moved (its `version` token, 00 §5.5 C4): a concurrent
            // revoke took the same pair, so the DELETE matched no row. The serial
            // replay would now find no grant and answer 404, which is what the route
            // documents for a pair that is not granted — not a 500.
            var stillGranted = await db.QuerySingleOrDefaultAsync<Guid>(
                MembershipReadSql.MembershipRolePair,
                new { org_id = orgId, membership_id = membership.Id, role_id = command.RoleId },
                cancellationToken).ConfigureAwait(false);

            return stillGranted == Guid.Empty
                ? Result.Failure(Error.NotFound("that membership does not hold that role"))
                : Result.Failure(Error.PreconditionFailed(
                    "the role grant changed since it was read; re-read the membership and retry"));
        }

        audit.Describe(new AuditObject("membership", membership.Id.ToString("D"), Metadata: new Dictionary<string, string> { ["role_id"] = command.RoleId.ToString("D") }));

        // 02.3.2-T1 (AC7, D5b): the revoke committed, so every live session of the member is flagged for
        // rotation in the same transaction — otherwise a session opened with the revoked role keeps it until
        // it expires. Revoking ALL the user's sessions is 02.1.2-T3's (ISessionRevocation); this is only the
        // rotation trigger and is safe to call now.
        if (membership.UserId is { } changedUser)
        {
            _ = await privilegeChanges.RequireRotationForUserAsync(changedUser, cancellationToken)
                .ConfigureAwait(false);
        }

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
