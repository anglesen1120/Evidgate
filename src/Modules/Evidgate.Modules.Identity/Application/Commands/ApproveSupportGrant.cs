using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// <c>POST /v1/support-grants/{g}/approve</c>: a tenant admin approves an operator's request. The approver is the
/// authenticated principal and nothing else — no field of the request can name one — and the database re-checks
/// that they are an active built-in <c>tenant_admin</c> under locks, so a concurrent membership removal or role
/// revocation either wins the race or waits for this transaction.
/// </summary>
/// <param name="GrantId">The grant from the route.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c>: the openapi operation declares it, so a replay of
/// the same approval answers the stored 200 instead of running the handler and the trigger again.</param>
internal sealed record ApproveSupportGrantCommand(Guid GrantId, string IdempotencyKey) : ICommand<Result>, IIdempotentCommand;

/// <summary>Handler of <see cref="ApproveSupportGrantCommand"/>; RLS decides which grants exist for the tenant.</summary>
[Permission(Permissions.Support.Approve)]
[AuditedAction("support_grant.approve")]
internal sealed class ApproveSupportGrantCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    TimeProvider clock,
    IAuditTrail audit)
    : ICommandHandler<ApproveSupportGrantCommand, Result>
{
    /// <summary>The built-in role an approver must hold, by catalogue code.</summary>
    private const string TenantAdminRole = "tenant_admin";

    /// <summary>The tail of the trigger's refusal message; the row id in front of it varies.</summary>
    private const string ApproverRefusal = "is not an active tenant_admin of the organization";

    /// <inheritdoc />
    public async ValueTask<Result> Handle(ApproveSupportGrantCommand command, CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ApproveSupportGrantCommand));

        if (principals.Current is not { Kind: PrincipalKind.Member, UserId: { } approver })
        {
            return Result.Failure(Error.Forbidden("support grants are approved by an authenticated member"));
        }

        var org = scopes.Require().OrgId;
        var now = clock.GetUtcNow();
        await using var context = contexts.Create();

        var grant = await context.SupportGrants
            .SingleOrDefaultAsync(row => row.Id == command.GrantId, cancellationToken)
            .ConfigureAwait(false);

        if (grant is null)
        {
            return Result.Failure(SupportGrantErrors.NotFound);
        }

        if (await RefusalAsync(context, grant, org, approver, now, cancellationToken).ConfigureAwait(false)
            is { } refusal)
        {
            return Result.Failure(refusal);
        }

        grant.Approve(approver, now);

        if (await SaveAsync(context, cancellationToken).ConfigureAwait(false) is { } saveRefusal)
        {
            return Result.Failure(saveRefusal);
        }

        audit.Describe(new AuditObject("support_grant", grant.Id.ToString("D")));

        return Result.Success(StatusCodes.Status200OK);
    }

    /// <summary>
    /// The answers that stop the request before it writes: the requester may not approve, the grant must still be
    /// awaiting approval, and the approver must hold the built-in <c>tenant_admin</c> role of this org. They run in
    /// that order so the answer is the same one the trigger raises when the same condition breaks under its locks.
    /// </summary>
    private static async ValueTask<Error?> RefusalAsync(
        IdentityDbContext context,
        Domain.SupportGrant grant,
        Guid org,
        Guid approver,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (grant.OperatorUserId == approver)
        {
            return SupportGrantErrors.ApproverMustDiffer;
        }

        if (!grant.IsApprovableAt(now))
        {
            return SupportGrantErrors.GrantNotRequested;
        }

        return await IsTenantAdminAsync(context, org, approver, cancellationToken).ConfigureAwait(false)
            ? null
            : SupportGrantErrors.ApproverNotTenantAdmin;
    }

    /// <summary>
    /// Saves the approval, translating each database refusal into the answer the pre-check would have given: the
    /// trigger re-reads membership and role state under locks, so a condition can change between that read and this
    /// write, and a client must not see a 500 for losing the race.
    /// </summary>
    private static async ValueTask<Error?> SaveAsync(IdentityDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.PreconditionFailed("the support grant changed; retry");
        }
        catch (DbUpdateException exception) when (MembershipRoleRefusal.IsContention(exception))
        {
            // A concurrent membership change deadlocked with the trigger's share locks (or a lock timeout set outside this request fired); nothing was written.
            return MembershipRoleRefusal.Contention;
        }
        catch (DbUpdateException exception) when (PgErrors.IsCheckViolation(exception, ApproverRefusal))
        {
            return SupportGrantErrors.ApproverNotTenantAdmin;
        }
        catch (DbUpdateException exception) when (PgErrors.IsCheckViolation(exception, "has expired and cannot be approved"))
        {
            return SupportGrantErrors.GrantNotRequested;
        }
        catch (DbUpdateException exception) when (PgErrors.IsCheckViolation(exception, "is revoked; a revoked grant accepts no change"))
        {
            return SupportGrantErrors.GrantNotRequested;
        }
    }

    /// <summary>
    /// Whether the member holds the built-in <c>tenant_admin</c> role of this org through an active membership.
    /// Built-ins have <c>org_id is null</c>, so the role half of the predicate is what makes a same-coded custom
    /// role ineligible.
    /// </summary>
    private static Task<bool> IsTenantAdminAsync(
        IdentityDbContext context,
        Guid org,
        Guid user,
        CancellationToken cancellationToken) =>
        (from membership in context.Memberships
         join granted in context.MembershipRoles on membership.Id equals granted.MembershipId
         join role in context.Roles on granted.RoleId equals role.Id
         where membership.OrgId == org
             && membership.UserId == user
             && membership.Status == MembershipStatus.Active
             && granted.OrgId == org
             && role.OrgId == null
             && role.IsBuiltin
             && role.Code == TenantAdminRole
         select membership.Id).AnyAsync(cancellationToken);
}
