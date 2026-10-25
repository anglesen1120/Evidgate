using Evidgate.BuildingBlocks.Observability;
using Evidgate.BuildingBlocks.Persistence.Audit;
using Evidgate.BuildingBlocks.Persistence.Outbox;
using Evidgate.BuildingBlocks.Persistence.Tenancy;
using Evidgate.BuildingBlocks.SharedKernel.Authorization;
using Evidgate.BuildingBlocks.SharedKernel.Messaging;
using Evidgate.BuildingBlocks.SharedKernel.Modules;
using Evidgate.BuildingBlocks.SharedKernel.Results;
using Evidgate.Modules.Identity.Authorization;
using Evidgate.Modules.Identity.Contracts;
using Evidgate.Modules.Identity.Infrastructure.Persistence;
using Evidgate.Modules.Identity.Invites;
using Mediator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Evidgate.Modules.Identity.Application.Commands;

/// <summary>
/// Rotate the invite of a pending membership (<c>POST /v1/orgs/{o}/memberships/{m}/resend</c>,
/// 02.1.2 §5/§6, AC6): the same row, a new prefix and secret, a new expiry, and one notification
/// outbox row of kind <c>resend</c>. Idempotent under the client's <c>Idempotency-Key</c> (T11).
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership whose invite is rotated; another org's id is 404.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record ResendInviteCommand(Guid OrgId, Guid MembershipId, string IdempotencyKey)
    : ICommand<Result<InviteIssuedView>>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="ResendInviteCommand"/>. Refusals in order: route org vs request
/// tenant (404), minting configured (503), unknown membership (404), not <c>pending</c>
/// (409 <c>invite_not_pending</c>). Only the tokens are rotated — the address, the status and
/// the roles stay, which is what makes a resend invisible to the client's view of the row.
/// <para>
/// The old token stops resolving because the prefix is overwritten: the resolver
/// (<c>evidgate_resolve.invite_by_secret</c>) looks a membership up by its stored prefix, and the
/// previous one is gone from every live row. The stored hash is replaced in the same statement, so
/// not even a leaked prefix+hash pair can be replayed.
/// </para>
/// </summary>
[AuditedAction("membership.invite_resend")]
[Permission(Permissions.Membership.Write)]
internal sealed class ResendInviteCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IAuditTrail audit,
    IOutboxWriter outbox,
    InviteSecrets inviteSecrets,
    InviteSecretCarrier inviteSecretCarrier,
    TimeProvider clock)
    : ICommandHandler<ResendInviteCommand, Result<InviteIssuedView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<InviteIssuedView>> Handle(
        ResendInviteCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(ResendInviteCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure<InviteIssuedView>(Error.NotFound(OrgMismatch.Message));
        }

        if (!inviteSecrets.CanHash)
        {
            return Result.Failure<InviteIssuedView>(InviteErrors.NotConfigured);
        }

        await using var context = contexts.Create();

        // RLS already hides another org's row; the composite key keeps the lookup honest even if it
        // did not (AC7).
        var membership = await context.Memberships
            .FirstOrDefaultAsync(
                row => row.Id == command.MembershipId && row.OrgId == orgId,
                cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return Result.Failure<InviteIssuedView>(Error.NotFound(NotFoundMessage));
        }

        if (!string.Equals(membership.Status, MembershipStatus.Pending, StringComparison.Ordinal))
        {
            return Result.Failure<InviteIssuedView>(InviteErrors.NotPending);
        }

        var mint = await InviteMints.MintAsync(context, inviteSecrets, cancellationToken).ConfigureAwait(false);

        if (mint is null)
        {
            return Result.Failure<InviteIssuedView>(InviteErrors.MintingUnavailable);
        }

        mint.ApplyTo(membership, clock);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The platform-wide prefix index again: the aborted transaction cannot mint again.
            return Result.Failure<InviteIssuedView>(InviteErrors.MintingUnavailable);
        }

        var view = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the membership {membership.Id} was updated in this transaction yet cannot be read back");

        await InviteNotifications.EnqueueAsync(
            outbox,
            view.Id,
            orgId,
            InviteNotifications.ResendKind,
            view.Version,
            cancellationToken).ConfigureAwait(false);

        inviteSecretCarrier.Token = mint.Full;

        audit.Describe(new AuditObject("membership", view.Id.ToString("D")));

        return Result.Success(InviteIssuedView.From(view, inviteToken: null), status: 202);
    }

    /// <summary>The 404 of an id this tenant cannot see (the same sentence the read routes use).</summary>
    private const string NotFoundMessage = "no membership with that id is visible to this tenant";
}

/// <summary>
/// Cancel a pending invite (<c>POST /v1/orgs/{o}/memberships/{m}/cancel-invite</c>, 02.1.2 §5/§6):
/// <c>pending → revoked</c>. The stored prefix and hash stay on the row for audit, and the resolver
/// only ever returns <c>pending</c> rows, so a cancelled invite can never bind.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership whose invite is cancelled; another org's id is 404.</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record CancelInviteCommand(Guid OrgId, Guid MembershipId, string IdempotencyKey)
    : ICommand<Result>, IIdempotentCommand;

/// <summary>
/// Handler of <see cref="CancelInviteCommand"/>: 204 on the transition, 404 for an unknown or
/// other-tenant id, 409 <c>invite_not_pending</c> when the row is not <c>pending</c>. No outbox row:
/// cancelling an invite tells nobody anything, and there is no email to withdraw in V1.
/// </summary>
[AuditedAction("membership.invite_cancel")]
[Permission(Permissions.Membership.Write)]
internal sealed class CancelInviteCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantScopeAccessor scopes,
    IAuditTrail audit)
    : ICommandHandler<CancelInviteCommand, Result>
{
    /// <inheritdoc />
    public async ValueTask<Result> Handle(
        CancelInviteCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(CancelInviteCommand));

        var orgId = scopes.Require().OrgId;

        if (command.OrgId != orgId)
        {
            return Result.Failure(Error.NotFound(OrgMismatch.Message));
        }

        await using var context = contexts.Create();

        var membership = await context.Memberships
            .FirstOrDefaultAsync(
                row => row.Id == command.MembershipId && row.OrgId == orgId,
                cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            return Result.Failure(Error.NotFound("no membership with that id is visible to this tenant"));
        }

        if (!string.Equals(membership.Status, MembershipStatus.Pending, StringComparison.Ordinal))
        {
            return Result.Failure(InviteErrors.NotPending);
        }

        // The transition of 02.1.2 §6. The token columns stay: the resolver filters on
        // `status = 'pending'`, so a revoked row can never bind, and the audit trail keeps what was
        // issued.
        membership.Status = MembershipStatus.Revoked;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A concurrent bind or cancel won: the client's view is stale, which is exactly the 409
            // the serial path answers.
            return Result.Failure(InviteErrors.NotPending);
        }

        audit.Describe(new AuditObject("membership", membership.Id.ToString("D")));

        return Result.Success(StatusCodes.Status204NoContent);
    }
}
