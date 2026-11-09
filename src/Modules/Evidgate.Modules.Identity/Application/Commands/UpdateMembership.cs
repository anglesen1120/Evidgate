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
/// Change a membership's status (<c>PATCH /v1/orgs/{o}/memberships/{m}</c>, 02.1.2
/// §5): the admin disable path, which sets <c>active → removed</c> and is the only
/// transition this route performs (activating an invite is the binding flow of
/// 02.1.2-T2). <paramref name="ExpectedVersion"/> is the row's <c>version</c> the
/// caller's <c>If-Match</c> carried; a stale value is 412 (00 §9.1/§5.5 C4), and the
/// EF concurrency token is what enforces it in the UPDATE statement itself.
/// </summary>
/// <param name="OrgId">The org from the route; it must equal the request tenant (otherwise 404).</param>
/// <param name="MembershipId">The membership from the route.</param>
/// <param name="Status">The target status; the validator accepts only <see cref="MembershipStatus.Removed"/>.</param>
/// <param name="ExpectedVersion">The <c>version</c> the caller last read (from <c>If-Match</c>).</param>
/// <param name="IdempotencyKey">The client's <c>Idempotency-Key</c> (00 §9.1).</param>
internal sealed record UpdateMembershipCommand(
    Guid OrgId,
    Guid MembershipId,
    string Status,
    int ExpectedVersion,
    string IdempotencyKey)
    : ICommand<Result<MembershipView>>, IIdempotentCommand;

/// <summary>
/// Shape validation of <see cref="UpdateMembershipCommand"/> (C2 stage 2). Only
/// <c>removed</c> is a legal target on this route, and the precondition must name a
/// real version — an absent or unparsable <c>If-Match</c> is the endpoint's 422, so
/// the validator only has to refuse a version that cannot exist.
/// </summary>
internal sealed class UpdateMembershipCommandValidator : AbstractValidator<UpdateMembershipCommand>
{
    /// <summary>Builds the rules.</summary>
    public UpdateMembershipCommandValidator()
    {
        RuleFor(command => command.Status)
            .Must(status => string.Equals(status, MembershipStatus.Removed, StringComparison.Ordinal))
            .WithErrorCode("unsupported_status");

        RuleFor(command => command.ExpectedVersion)
            .GreaterThan(0)
            .WithErrorCode("required");
    }
}

/// <summary>
/// Handler of <see cref="UpdateMembershipCommand"/>. A membership the tenant cannot
/// see is 404 (RLS decides, AC7); an already <c>removed</c> row answers 200 with its
/// current state so a retry is not an error; any other current status is 409
/// <c>invalid_transition</c> (02.1.2 §6 lists <c>active → removed</c> only, and a
/// pending invite is cancelled through 02.1.2-T2's route); a target whose
/// administrative rank is above the caller's is 403 <c>forbidden</c>; removing the
/// org's last active <c>tenant_admin</c> is 409 <c>last_admin</c> (AC4).
/// </summary>
/// <remarks>
/// <c>ISessionRevocation</c> is called in this path in the same transaction (AC3).
/// </remarks>
[AuditedAction("membership.update")]
[Permission(Permissions.Membership.Write)]
internal sealed class UpdateMembershipCommandHandler(
    ITenantDbContextFactory<IdentityDbContext> contexts,
    ITenantDb db,
    ITenantScopeAccessor scopes,
    IRequestPrincipalAccessor principals,
    IAuditTrail audit,
    ISessionRevocation sessions)
    : ICommandHandler<UpdateMembershipCommand, Result<MembershipView>>
{
    /// <inheritdoc />
    public async ValueTask<Result<MembershipView>> Handle(
        UpdateMembershipCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = EvidgateTelemetry.StartModuleActivity(ModuleName.Identity, nameof(UpdateMembershipCommand));

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

        // Checked before the version and retry branches: the hierarchy is a property of the caller and
        // the target. Rank counts active memberships only, so a removed or pending target ranks zero.
        if (!await MemberRankGuard.ActorOutranksOrEqualsAsync(
                db, orgId, principals.Current, membership.UserId, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result.Failure<MembershipView>(MemberRankGuard.Refused);
        }

        if (membership.Version != command.ExpectedVersion)
        {
            return Result.Failure<MembershipView>(Stale(membership.Version));
        }

        if (string.Equals(membership.Status, MembershipStatus.Removed, StringComparison.Ordinal))
        {
            // Already in the requested state: the retry is a no-op, not a conflict.
            audit.Suppress();

            var current = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
                .ConfigureAwait(false);

            return Result.Success(current!);
        }

        if (!membership.IsActive)
        {
            return Result.Failure<MembershipView>(Error.Conflict(
                ApiErrorCode.InvalidTransition,
                $"a {membership.Status} membership cannot be removed; a pending invite is cancelled through its own route"));
        }

        if (await LastAdminGuard.WouldOrphanOrgAsync(db, orgId, membership.Id, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<MembershipView>(Error.Conflict(
                ApiErrorCode.LastAdmin,
                "the organization must keep at least one active tenant_admin (AC4)"));
        }

        // The guard's row lock is where a concurrent demotion of the actor can land, so the
        // rank is compared again after it. This narrows the window; it is not atomic.
        if (!await MemberRankGuard.ActorOutranksOrEqualsAsync(
                db, orgId, principals.Current, membership.UserId, cancellationToken)
            .ConfigureAwait(false))
        {
            return Result.Failure<MembershipView>(MemberRankGuard.Refused);
        }

        membership.Remove();

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The version token moved between the read above and the UPDATE, or a
            // concurrent writer won: the caller's precondition is what failed.
            var latest = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
                .ConfigureAwait(false);

            return Result.Failure<MembershipView>(Stale(latest?.Version ?? membership.Version));
        }

        // Disabling the membership ends every live session of the user in this same transaction (AC3);
        // a failed revocation rolls the status change back with it.
        if (membership.UserId is { } changedUser)
        {
            _ = await sessions.RevokeAllForUserAsync(changedUser, cancellationToken)
                .ConfigureAwait(false);
        }

        var view = await MembershipReader.ReadAsync(db, orgId, membership.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"the membership {membership.Id} was updated in this transaction yet cannot be read back");

        audit.Describe(new AuditObject("membership", membership.Id.ToString("D"), Metadata: new Dictionary<string, string> { ["status"] = command.Status }));

        return Result.Success(view);
    }

    /// <summary>The 412 a stale or missing precondition answers (00 §9.1).</summary>
    private static Error Stale(int currentVersion) => new(
        ApiErrorCode.PreconditionFailed,
        $"the membership changed since it was read (current version {currentVersion})",
        StatusCodes.Status412PreconditionFailed,
        Retryable: false,
        Details: [new ErrorDetail("If-Match", "stale")]);
}
