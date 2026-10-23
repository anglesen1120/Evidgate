using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.Pipeline.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Api;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Domain;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using FluentValidation;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Grant one role to one membership (<c>POST /v1/orgs/{o}/memberships/{m}/roles</c>,
/// 02.1.2 §5). Granting a role the membership already holds is a no-op answered 200
/// with the current state — the grant is idempotent by
/// <c>unique (membership_id, role_id)</c>, and the client's
/// <c>Idempotency-Key</c> covers the retry (T11). AC5: the next request evaluates the
/// new role set — nothing is cached.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership from the route.</param>
/// <param name="RoleId">The role to grant: a built-in catalogue role or one of the caller's org roles.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record GrantRoleCommand(Guid OrgId, Guid MembershipId, Guid RoleId, string IdempotencyKey)
    : ICommand<Result<MembershipView>>, IIdempotentCommand;

/// <summary>Shape validation of <see cref="GrantRoleCommand"/> (C2 stage 2).</summary>
internal sealed class GrantRoleCommandValidator : AbstractValidator<GrantRoleCommand>
{
    /// <summary>Builds the rules.</summary>
    public GrantRoleCommandValidator() =>
        RuleFor(command => command.RoleId)
            .Must(roleId => roleId != Guid.Empty)
            .WithErrorCode("required");
}

/// <summary>
/// Handler of <see cref="GrantRoleCommand"/>. RLS decides which roles resolve, so a
/// role id the tenant cannot see — including another org's custom role — is 409
/// <c>unknown_role</c> (02.1.2 §5), and a membership it cannot see is 404 (AC7).
/// <para>
/// Two further refusals, both from W7-SEC-03/W7-SEC-02 and both using codes 00 §9.2
/// already names: a role whose <c>kind</c> is not <c>member</c> — the staff
/// (<c>platform_operator</c>, <c>billing_operator</c>) and service
/// (<c>ci_producer</c>, <c>agent</c>) catalogue rows, which belong to a staff account or
/// a service token and never to a membership — is the same 409 <c>unknown_role</c>; and
/// granting the caller's <b>own</b> membership a role that carries a permission the
/// caller does not hold is 403 <c>forbidden</c> (a role is not a way around the role you
/// hold, 00 §2).
/// </para>
/// </summary>
[AuditedAction("membership.role_grant")]
[Permission(Permissions.Membership.Write)]
internal sealed class GrantRoleCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    ICallerPermissionSet permissions,
    IAuditTrail audit)
    : ICommandHandler<GrantRoleCommand, Result<MembershipView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MembershipView>> Handle(
        GrantRoleCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(GrantRoleCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure<MembershipView>(Error.NotFound(OrgMismatch.Message));
        }

        await using var context = contexts.Create();

        var membership = await context.Memberships
            .SingleOrDefaultAsync(row => row.Id == command.MembershipId, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return Result.Failure<MembershipView>(Error.NotFound("no membership with that id is visible to this tenant"));
        }

        var role = await context.Roles
            .SingleOrDefaultAsync(
                candidate => candidate.Id == command.RoleId && candidate.Kind == RoleKind.Member,
                cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            return Result.Failure<MembershipView>(Error.Conflict(
                ApiErrorCode.UnknownRole,
                "the role is neither a member role of the catalogue nor a role of this organization"));
        }

        var caller = principals.Current;

        // W7-SEC-02: a caller may grant its own membership — or a pending row addressed to
        // its own address — only what it already holds. Checked before the idempotency
        // check on purpose — a refusal must not depend on whether the row happens to exist
        // (a corrupt or filtered grant set would then decide it).
        if (await SelfEscalation
                .IsSelfTargetAsync(context, orgId, caller, membership.UserId, membership.InvitedEmail, cancellationToken)
                .ConfigureAwait(false))
        {
            var missing = await SelfEscalation.MissingAsync(
                permissions,
                orgId,
                caller,
                await SelfEscalation.RoleCodesAsync(context, [role.Id], cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (missing.Count > 0)
            {
                return Result.Failure<MembershipView>(SelfEscalation.Refused);
            }
        }

        var alreadyGranted = await context.MembershipRoles
            .AnyAsync(
                grant => grant.MembershipId == membership.Id && grant.RoleId == role.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (!alreadyGranted)
        {
            await context.MembershipRoles.AddAsync(
                new MembershipRole
                {
                    Id = Guid.CreateVersion7(),
                    OrgId = orgId,
                    MembershipId = membership.Id,
                    RoleId = role.Id,
                    GrantedBy = caller?.UserId,
                },
                cancellationToken).ConfigureAwait(false);

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // `unique (membership_id, role_id)` is the arbiter: a concurrent grant
                // that passed the check above still leaves exactly one row, and the
                // request's intent (the role is held) is satisfied.
            }
        }

        var view = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the membership {membership.Id} was updated in this transaction yet cannot be read back");

        audit.Describe(new AuditObject("membership", membership.Id.ToString("D"), Metadata: new Dictionary<string, string> { ["role_id"] = command.RoleId.ToString("D") }));

        return Result.Success(view);
    }

    /// <summary>True when the failure is a unique violation (SQLSTATE 23505).</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
